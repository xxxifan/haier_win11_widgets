"""海尔智家 HTTP 接口封装。

接口列表（均参考 banto6/haier 的实现）：

* 登录 / 刷新 token   -> https://zj.haier.net/api-gw/oauthserver/account/v1/*
* 用户信息            -> https://account-api.haier.net/v2/haier/userinfo
* 设备列表            -> https://uws.haier.net/uds/v1/protected/deviceinfos
* 设备属性（数字模型）-> https://uws.haier.net/shadow/v1/devdigitalmodels
* 分配 WebSocket 网关 -> https://uws.haier.net/gmsWS/wsag/assign

uws / zj 接口都要求带 appId/appKey/clientId/timestamp/sequenceId/sign 这一组公共 header，
sign = sha256(path + 去空白后的 body + appId + appKey + timestamp)。
"""

from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import random
import time
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional
from urllib.parse import urlparse

import aiohttp

from .exceptions import HaierAuthError, HaierClientError

_LOGGER = logging.getLogger(__name__)

# token 来源客户端。refresh_token 与签发它的 appId 绑定，
# 用其他客户端的 appId 去刷新会返回 43005 授权异常，所以刷新时必须与来源一致。
APP_SOURCE_WXAPP = "wxapp"
APP_SOURCE_APP = "app"
DEFAULT_APP_SOURCE = APP_SOURCE_APP

APP_SOURCES: Dict[str, tuple[str, str]] = {
    # 微信小程序
    APP_SOURCE_WXAPP: ("MB-SHEZJAPPWXXCX-0000", "79ce99cc7f9804663939676031b8a427"),
    # 海尔智家 App
    APP_SOURCE_APP: ("MB-UZHSH-0001", "5dfca8714eb26e3a776e58a8273c8752"),
}

PHONE_LOGIN_API = "https://zj.haier.net/api-gw/oauthserver/account/v1/login"
REFRESH_TOKEN_API = "https://zj.haier.net/api-gw/oauthserver/account/v1/refreshToken"
GET_USER_INFO_API = "https://account-api.haier.net/v2/haier/userinfo"
GET_DEVICES_API = "https://uws.haier.net/uds/v1/protected/deviceinfos"
GET_DIGITAL_MODEL_API = "https://uws.haier.net/shadow/v1/devdigitalmodels"
GET_WSS_GW_API = "https://uws.haier.net/gmsWS/wsag/assign"

SUCCESS_CODE = "00000"
DEFAULT_TIMEOUT = aiohttp.ClientTimeout(total=30)


def sign(app_id: str, app_key: str, timestamp: str, body: str, url: str) -> str:
    """计算 uws 接口签名。body 需为序列化后的 JSON 字符串，会先去掉所有空白。"""
    compact_body = (
        str(body).replace("\t", "").replace("\r", "").replace("\n", "").replace(" ", "")
    )
    content = urlparse(url).path + compact_body + str(app_id) + str(app_key) + str(timestamp)
    return hashlib.sha256(content.encode("utf-8")).hexdigest()


def sequence_id() -> str:
    """20 位交易流水号：14 位 yyyyMMddHHmmss + 6 位随机数。"""
    return time.strftime("%Y%m%d%H%M%S") + str(random.randint(100000, 999999))


@dataclass(frozen=True)
class TokenInfo:
    token: str
    refresh_token: str
    expires_in: int
    issued_at: int = field(default_factory=lambda: int(time.time()))

    @property
    def expires_at(self) -> int:
        return self.issued_at + self.expires_in


@dataclass
class HaierDevice:
    """设备基础信息（来自 deviceinfos 接口）。"""

    raw: dict

    @property
    def id(self) -> str:
        return self.raw["deviceId"]

    @property
    def name(self) -> str:
        return self.raw.get("deviceName") or self.id

    @property
    def type(self) -> Optional[str]:
        return self.raw.get("deviceType")

    @property
    def product_code(self) -> Optional[str]:
        return self.raw.get("productCodeT")

    @property
    def product_name(self) -> Optional[str]:
        return self.raw.get("productNameT")

    @property
    def wifi_type(self) -> Optional[str]:
        return self.raw.get("wifiType")

    @property
    def online(self) -> bool:
        return bool(self.raw.get("online"))

    @property
    def room(self) -> Optional[str]:
        return self.raw.get("roomName") or self.raw.get("room")

    def __str__(self) -> str:
        return json.dumps(
            {
                "id": self.id,
                "name": self.name,
                "type": self.type,
                "product_code": self.product_code,
                "product_name": self.product_name,
                "wifi_type": self.wifi_type,
                "online": self.online,
            },
            ensure_ascii=False,
        )


class HaierClient:
    """HTTP 接口客户端。

    :param session: aiohttp.ClientSession，由调用方管理生命周期
    :param client_id: 客户端标识。手机号登录时参考实现直接使用手机号
    :param token: accountToken，未登录时可为空字符串
    :param app_source: token 来源（app / wxapp），决定 appId/appKey
    """

    def __init__(
        self,
        session: aiohttp.ClientSession,
        client_id: str,
        token: str = "",
        app_source: str = DEFAULT_APP_SOURCE,
        max_retries: int = 3,
    ):
        if app_source not in APP_SOURCES:
            raise ValueError(f"未知的 app_source: {app_source}，可选 {list(APP_SOURCES)}")
        self._session = session
        self._client_id = client_id
        self._token = token
        self._app_source = app_source
        self._app_id, self._app_key = APP_SOURCES[app_source]
        self._max_retries = max_retries

    # ---------------------------------------------------------------- 属性

    @property
    def client_id(self) -> str:
        return self._client_id

    @property
    def token(self) -> str:
        return self._token

    @token.setter
    def token(self, value: str) -> None:
        self._token = value

    @property
    def app_source(self) -> str:
        return self._app_source

    # ---------------------------------------------------------------- 认证

    async def phone_login(self, phone: str, password: str) -> TokenInfo:
        """手机号 + 密码登录。参考实现使用 App 来源的 appId/appKey。"""
        payload = {"username": phone, "password": password}
        content = await self._post_json(PHONE_LOGIN_API, payload, auth_error=True)
        return self._parse_token_info(content)

    async def refresh_token(self, refresh_token: str) -> TokenInfo:
        """用 refreshToken 换取新 token。必须使用签发该 token 的 app_source。"""
        payload = {"refreshToken": refresh_token}
        content = await self._post_json(REFRESH_TOKEN_API, payload, auth_error=True)
        return self._parse_token_info(content)

    async def get_user_info(self) -> dict:
        """根据 token 获取用户信息，同时也可用来校验 token 是否有效。"""
        headers = {"Authorization": f"Bearer {self._token}"}

        async def _do():
            async with self._session.get(
                GET_USER_INFO_API, headers=headers, timeout=DEFAULT_TIMEOUT
            ) as resp:
                content = await resp.json(content_type=None)
                if (
                    not isinstance(content, dict)
                    or "error_description" in content
                    or "userId" not in content
                ):
                    desc = content.get("error_description") if isinstance(content, dict) else content
                    raise HaierAuthError(f"获取用户信息失败: {desc}")
                return content

        return await self._retry(_do)

    # ---------------------------------------------------------------- 设备

    async def get_devices_raw(self) -> List[dict]:
        content = await self._get_json(GET_DEVICES_API)
        return content.get("deviceinfos", [])

    async def get_devices(self) -> List[HaierDevice]:
        """获取账号下绑定的所有设备。"""
        return [HaierDevice(raw) for raw in await self.get_devices_raw()]

    async def get_devices_online_status(self) -> Dict[str, bool]:
        return {d["deviceId"]: bool(d.get("online")) for d in await self.get_devices_raw()}

    async def get_digital_model(self, device_id: str) -> dict:
        """获取设备数字模型（attributes / alarms / businessAttr）。"""
        payload = {"deviceInfoList": [{"deviceId": device_id}]}
        content = await self._post_json(GET_DIGITAL_MODEL_API, payload)
        detail = content.get("detailInfo") or {}
        if device_id not in detail:
            _LOGGER.warning(
                "设备 %s 未返回数字模型: %s", device_id, json.dumps(content, ensure_ascii=False)
            )
            return {}
        model = detail[device_id]
        if isinstance(model, str):
            model = json.loads(model)
        return model

    async def get_attributes(self, device_id: str) -> List[dict]:
        """获取设备属性定义列表（含当前值、可写性、取值范围）。"""
        return (await self.get_digital_model(device_id)).get("attributes", [])

    async def get_snapshot(self, device_id: str) -> Dict[str, Any]:
        """获取设备当前所有属性值 {name: value}。"""
        return {
            a["name"]: a["value"] for a in await self.get_attributes(device_id) if "value" in a
        }

    async def get_device_gateway(self) -> str:
        """申请 WebSocket 网关地址（wss://...）。"""
        payload = {"clientId": self._client_id, "token": self._token}
        content = await self._post_json(GET_WSS_GW_API, payload)
        addr = content["agAddr"]
        return addr.replace("http://", "wss://").replace("https://", "wss://")

    # ---------------------------------------------------------------- 内部

    def common_headers(self, api: str, body: str = "") -> Dict[str, str]:
        timestamp = str(int(time.time() * 1000))
        return {
            "accessToken": self._token,
            "appId": self._app_id,
            "appKey": self._app_key,
            "clientId": self._client_id,
            "sequenceId": sequence_id(),
            "sign": sign(self._app_id, self._app_key, timestamp, body, api),
            "timestamp": timestamp,
            "timezone": "+8",
            "language": "zh-CN",
            "Content-Type": "application/json",
        }

    async def _post_json(self, api: str, payload: dict, auth_error: bool = False) -> dict:
        # 签名基于去空白后的 body，所以发送的 body 也必须是同一串，不能再让 aiohttp 重新序列化
        body = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))

        async def _do():
            headers = self.common_headers(api, body)
            async with self._session.post(
                api, headers=headers, data=body.encode("utf-8"), timeout=DEFAULT_TIMEOUT
            ) as resp:
                content = await resp.json(content_type=None)
                self._assert_success(content, auth_error)
                return content

        return await self._retry(_do)

    async def _get_json(self, api: str) -> dict:
        async def _do():
            headers = self.common_headers(api)
            async with self._session.get(api, headers=headers, timeout=DEFAULT_TIMEOUT) as resp:
                content = await resp.json(content_type=None)
                self._assert_success(content)
                return content

        return await self._retry(_do)

    async def _retry(self, func):
        last: Optional[BaseException] = None
        for attempt in range(1, self._max_retries + 1):
            try:
                return await func()
            except (aiohttp.ClientError, asyncio.TimeoutError) as err:
                last = err
                _LOGGER.warning(
                    "请求失败 %s，第 %s/%s 次重试", type(err).__name__, attempt, self._max_retries
                )
                await asyncio.sleep(min(2 ** (attempt - 1), 5))
        raise HaierClientError(f"请求失败，已重试 {self._max_retries} 次: {last}") from last

    @staticmethod
    def _assert_success(content: Any, auth_error: bool = False) -> None:
        if not isinstance(content, dict):
            raise HaierClientError(f"接口返回了非 JSON 对象: {content!r}")
        ret_code = content.get("retCode")
        if ret_code is not None and ret_code != SUCCESS_CODE:
            msg = f"接口返回异常 [{ret_code}]: {content.get('retInfo')}"
            if auth_error:
                raise HaierAuthError(msg, ret_code)
            raise HaierClientError(msg, ret_code)

    @staticmethod
    def _parse_token_info(content: dict) -> TokenInfo:
        try:
            info = content["data"]["tokenInfo"]
            return TokenInfo(info["accountToken"], info["refreshToken"], int(info["expiresIn"]))
        except (KeyError, TypeError) as err:
            raise HaierAuthError(
                f"登录响应缺少 tokenInfo: {json.dumps(content, ensure_ascii=False)}"
            ) from err
