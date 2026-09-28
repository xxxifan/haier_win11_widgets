"""海尔智家 WebSocket 设备网关。

流程（参考 banto6/haier）：

1. 调用 ``/gmsWS/wsag/assign`` 拿到网关地址 ``agAddr``；
2. 连接 ``{agAddr}/userag?token={token}&agClientId={token}``；
3. 发送 ``BoundDevs`` 订阅需要监听的设备；
4. 每 60 秒发送一次 ``HeartBeat``；
5. 通过 ``BatchCmdReq`` 下发控制命令（cmdArgs 即属性键值对）；
6. 网关只在属性变化时推送 ``GenMsgDown``（businType=DigitalModel，数据为
   base64 + gzip 压缩的 JSON），以及设备上下线通知（DevOnlineNotify / DevOfflineNotify）。
"""

from __future__ import annotations

import asyncio
import base64
import json
import logging
import random
import zlib
from typing import Any, Awaitable, Callable, Dict, Iterable, List, Optional

import aiohttp

from .client import HaierClient
from .exceptions import HaierGatewayError

_LOGGER = logging.getLogger(__name__)

HEARTBEAT_INTERVAL = 60

DataCallback = Callable[[str, Dict[str, Any]], Any]
OnlineCallback = Callable[[str, bool], Any]
RawCallback = Callable[[dict], Any]


def random_str(length: int = 32) -> str:
    return "".join(random.choice("abcdef1234567890") for _ in range(length))


def decode_digital_model(args: str) -> dict:
    """解码 GenMsgDown/DigitalModel 中的 args 字段（base64 -> gzip -> JSON）。"""
    raw = zlib.decompress(base64.b64decode(args), 16 + zlib.MAX_WBITS)
    return json.loads(raw.decode("utf-8"))


def build_batch_cmd(ag_client_id: str, device_id: str, attributes: Dict[str, Any], sn: str | None = None) -> dict:
    sn = sn or random_str(32)
    return {
        "agClientId": ag_client_id,
        "topic": "BatchCmdReq",
        "content": {
            "trace": random_str(32),
            "sn": sn,
            "data": [
                {
                    "sn": sn,
                    "index": 0,
                    "delaySeconds": 0,
                    "subSn": sn + ":0",
                    "deviceId": device_id,
                    "cmdArgs": {k: str(v) for k, v in attributes.items()},
                }
            ],
        },
    }


class HaierGateway:
    """单条 WebSocket 连接的封装，可作为异步上下文管理器使用::

        async with HaierGateway(session, client, on_data=print) as gw:
            await gw.subscribe([device_id])
            await gw.control(device_id, {"onOffStatus": "true"})
    """

    def __init__(
        self,
        session: aiohttp.ClientSession,
        client: HaierClient,
        on_data: Optional[DataCallback] = None,
        on_online: Optional[OnlineCallback] = None,
        on_message: Optional[RawCallback] = None,
    ):
        self._session = session
        self._client = client
        self._on_data = on_data
        self._on_online = on_online
        self._on_message = on_message

        self._ws: Optional[aiohttp.ClientWebSocketResponse] = None
        self._ag_client_id = client.token
        self._tasks: List[asyncio.Task] = []
        self._pending: Dict[str, asyncio.Future] = {}
        self._data_waiters: Dict[str, List[asyncio.Future]] = {}
        self._closed = asyncio.Event()

    # ---------------------------------------------------------------- 生命周期

    async def __aenter__(self) -> "HaierGateway":
        await self.connect()
        return self

    async def __aexit__(self, *exc) -> None:
        await self.close()

    @property
    def connected(self) -> bool:
        return self._ws is not None and not self._ws.closed

    async def connect(self) -> None:
        server = await self._client.get_device_gateway()
        url = f"{server}/userag?token={self._client.token}&agClientId={self._ag_client_id}"
        _LOGGER.debug("连接设备网关: %s", server)
        try:
            self._ws = await self._session.ws_connect(url, heartbeat=None)
        except aiohttp.ClientError as err:
            raise HaierGatewayError(f"连接网关失败: {err}") from err

        self._closed.clear()
        self._tasks.append(asyncio.create_task(self._reader(), name="haier-ws-reader"))
        self._tasks.append(asyncio.create_task(self._heartbeat(), name="haier-ws-heartbeat"))

    async def close(self) -> None:
        for task in self._tasks:
            task.cancel()
        for task in self._tasks:
            try:
                await task
            except (asyncio.CancelledError, Exception):
                pass
        self._tasks.clear()
        if self._ws is not None and not self._ws.closed:
            await self._ws.close()
        self._ws = None
        self._closed.set()
        for fut in list(self._pending.values()):
            if not fut.done():
                fut.set_exception(HaierGatewayError("网关已关闭"))
        self._pending.clear()

    async def wait_closed(self) -> None:
        """阻塞直到连接断开（用于常驻监听）。"""
        await self._closed.wait()

    # ---------------------------------------------------------------- 发送

    async def _send(self, msg: dict) -> None:
        if not self.connected:
            raise HaierGatewayError("网关未连接")
        text = json.dumps(msg, ensure_ascii=False)
        _LOGGER.debug(">> %s", text)
        await self._ws.send_str(text)

    async def subscribe(self, device_ids: Iterable[str]) -> None:
        """订阅设备状态推送（BoundDevs）。"""
        await self._send(
            {
                "agClientId": self._ag_client_id,
                "topic": "BoundDevs",
                "content": {"devs": list(device_ids)},
            }
        )

    async def send_heartbeat(self) -> None:
        await self._send(
            {
                "agClientId": self._ag_client_id,
                "topic": "HeartBeat",
                "content": {"sn": random_str(32), "duration": 0},
            }
        )

    async def send_command(self, device_id: str, attributes: Dict[str, Any]) -> str:
        """发送控制命令，不等待结果，返回流水号 sn。"""
        msg = build_batch_cmd(self._ag_client_id, device_id, attributes)
        await self._send(msg)
        return msg["content"]["sn"]

    async def control(
        self,
        device_id: str,
        attributes: Dict[str, Any],
        timeout: float = 10.0,
    ) -> Optional[dict]:
        """发送控制命令并等待网关对该 sn 的响应。

        返回网关响应消息（若超时未收到响应返回 None，命令本身可能已生效，
        可再通过 :meth:`wait_data` 或 HTTP 快照确认）。
        """
        loop = asyncio.get_running_loop()
        sn = random_str(32)
        fut: asyncio.Future = loop.create_future()
        self._pending[sn] = fut
        try:
            await self._send(build_batch_cmd(self._ag_client_id, device_id, attributes, sn))
            return await asyncio.wait_for(fut, timeout)
        except asyncio.TimeoutError:
            return None
        finally:
            self._pending.pop(sn, None)

    async def request_all_property(self, device_id: str) -> str:
        """部分设备需要主动请求才会推送全部属性。"""
        return await self.send_command(device_id, {"getAllProperty": "getAllProperty"})

    async def wait_data(self, device_id: str, timeout: float = 10.0) -> Optional[Dict[str, Any]]:
        """等待指定设备的下一次属性推送。"""
        loop = asyncio.get_running_loop()
        fut: asyncio.Future = loop.create_future()
        self._data_waiters.setdefault(device_id, []).append(fut)
        try:
            return await asyncio.wait_for(fut, timeout)
        except asyncio.TimeoutError:
            return None
        finally:
            waiters = self._data_waiters.get(device_id, [])
            if fut in waiters:
                waiters.remove(fut)

    # ---------------------------------------------------------------- 接收

    async def _heartbeat(self) -> None:
        while True:
            await asyncio.sleep(HEARTBEAT_INTERVAL)
            try:
                await self.send_heartbeat()
                _LOGGER.debug("已发送心跳")
            except Exception:  # noqa: BLE001
                _LOGGER.exception("发送心跳失败")

    async def _reader(self) -> None:
        assert self._ws is not None
        try:
            async for msg in self._ws:
                if msg.type == aiohttp.WSMsgType.TEXT:
                    try:
                        await self._handle_message(msg.data)
                    except Exception:  # noqa: BLE001
                        _LOGGER.exception("处理网关消息失败: %s", msg.data)
                elif msg.type in (aiohttp.WSMsgType.CLOSED, aiohttp.WSMsgType.CLOSING):
                    break
                elif msg.type == aiohttp.WSMsgType.ERROR:
                    _LOGGER.error("WebSocket 异常: %s", self._ws.exception())
                    break
                else:
                    _LOGGER.warning("收到未知类型的消息: %s", msg.type)
        finally:
            _LOGGER.debug("网关连接已断开")
            self._closed.set()
            for fut in list(self._pending.values()):
                if not fut.done():
                    fut.set_exception(HaierGatewayError("网关连接已断开"))

    async def _handle_message(self, text: str) -> None:
        _LOGGER.debug("<< %s", text)
        msg = json.loads(text)
        if self._on_message:
            await _maybe_await(self._on_message(msg))

        topic = msg.get("topic")
        content = msg.get("content") or {}

        # 命令响应：按 sn 匹配（响应的 sn 可能在 content.sn 或 content.data[].sn）
        sns = set()
        if isinstance(content, dict):
            if content.get("sn"):
                sns.add(content["sn"])
            for item in content.get("data") or []:
                if isinstance(item, dict) and item.get("sn"):
                    sns.add(item["sn"])
        for sn in sns:
            fut = self._pending.get(sn)
            if fut and not fut.done() and topic != "BatchCmdReq":
                fut.set_result(msg)

        if topic != "GenMsgDown":
            return

        busin_type = content.get("businType")
        data_b64 = content.get("data")
        if not data_b64:
            return
        data = json.loads(base64.b64decode(data_b64))

        if busin_type == "DigitalModel":
            device_id = data["dev"]
            model = decode_digital_model(data["args"])
            values = {a["name"]: a["value"] for a in model.get("attributes", []) if "value" in a}
            for fut in self._data_waiters.get(device_id, []):
                if not fut.done():
                    fut.set_result(values)
            if self._on_data:
                await _maybe_await(self._on_data(device_id, values))
        elif busin_type in ("DevOfflineNotify", "DevOnlineNotify"):
            online = busin_type == "DevOnlineNotify"
            for device_id in data.get("devs", []):
                _LOGGER.info("设备 %s %s", device_id, "上线" if online else "离线")
                if self._on_online:
                    await _maybe_await(self._on_online(device_id, online))
        else:
            _LOGGER.debug("未处理的 GenMsgDown businType=%s data=%s", busin_type, data)


async def _maybe_await(result: Any) -> None:
    if asyncio.iscoroutine(result) or isinstance(result, Awaitable):
        await result
