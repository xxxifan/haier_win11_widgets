"""登录会话持久化与自动续期。

会话文件（JSON）字段::

    {
        "client_id": "138xxxxxxxx",
        "token": "...",
        "refresh_token": "...",
        "expires_at": 1700000000,
        "app_source": "app",
        "user": {"userId": "...", "mobile": "...", "username": "..."}
    }
"""

from __future__ import annotations

import json
import logging
import os
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Optional

import aiohttp

from .client import DEFAULT_APP_SOURCE, HaierClient, TokenInfo
from .exceptions import HaierAuthError

_LOGGER = logging.getLogger(__name__)

DEFAULT_SESSION_FILE = Path(os.environ.get("HAIER_SESSION", "session.json"))
# 距离过期不足 1 天时提前刷新
REFRESH_AHEAD_SECONDS = 86400


@dataclass
class HaierSession:
    client_id: str
    token: str
    refresh_token: str
    expires_at: int
    app_source: str = DEFAULT_APP_SOURCE
    user: dict = field(default_factory=dict)
    path: Optional[Path] = field(default=None, repr=False, compare=False)

    # ---------------------------------------------------------------- 持久化

    @classmethod
    def load(cls, path: Path | str = DEFAULT_SESSION_FILE) -> "HaierSession":
        path = Path(path)
        if not path.exists():
            raise HaierAuthError(f"会话文件不存在: {path}，请先登录")
        with path.open("r", encoding="utf-8") as fp:
            data = json.load(fp)
        return cls(
            client_id=data["client_id"],
            token=data["token"],
            refresh_token=data["refresh_token"],
            expires_at=int(data.get("expires_at", 0)),
            app_source=data.get("app_source", DEFAULT_APP_SOURCE),
            user=data.get("user") or {},
            path=path,
        )

    def save(self, path: Path | str | None = None) -> Path:
        path = Path(path) if path else self.path
        if path is None:
            path = DEFAULT_SESSION_FILE
        self.path = path
        data = asdict(self)
        data.pop("path", None)
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("w", encoding="utf-8", newline="\n") as fp:
            json.dump(data, fp, ensure_ascii=False, indent=2)
        return path

    # ---------------------------------------------------------------- 状态

    @property
    def expires_in(self) -> int:
        return self.expires_at - int(time.time())

    @property
    def expired(self) -> bool:
        return self.expires_in <= 0

    def apply_token(self, info: TokenInfo) -> None:
        self.token = info.token
        self.refresh_token = info.refresh_token
        self.expires_at = info.expires_at

    def client(self, http: aiohttp.ClientSession) -> HaierClient:
        return HaierClient(http, self.client_id, self.token, self.app_source)

    # ---------------------------------------------------------------- 登录 / 续期

    @classmethod
    async def login(
        cls,
        http: aiohttp.ClientSession,
        phone: str,
        password: str,
        path: Path | str | None = None,
    ) -> "HaierSession":
        """手机号 + 密码登录并保存会话。"""
        from .client import APP_SOURCE_APP

        client = HaierClient(http, phone, "", APP_SOURCE_APP)
        info = await client.phone_login(phone, password)
        client.token = info.token
        user = await client.get_user_info()
        session = cls(
            client_id=phone,
            token=info.token,
            refresh_token=info.refresh_token,
            expires_at=info.expires_at,
            app_source=APP_SOURCE_APP,
            user={k: user.get(k) for k in ("userId", "mobile", "username")},
            path=Path(path) if path else None,
        )
        session.save()
        return session

    @classmethod
    async def from_refresh_token(
        cls,
        http: aiohttp.ClientSession,
        client_id: str,
        refresh_token: str,
        app_source: str = DEFAULT_APP_SOURCE,
        path: Path | str | None = None,
    ) -> "HaierSession":
        """用抓包得到的 refresh_token 建立会话（app_source 需与 token 来源一致）。"""
        client = HaierClient(http, client_id, "", app_source)
        info = await client.refresh_token(refresh_token)
        client.token = info.token
        user = await client.get_user_info()
        session = cls(
            client_id=client_id,
            token=info.token,
            refresh_token=info.refresh_token,
            expires_at=info.expires_at,
            app_source=app_source,
            user={k: user.get(k) for k in ("userId", "mobile", "username")},
            path=Path(path) if path else None,
        )
        session.save()
        return session

    async def refresh(self, http: aiohttp.ClientSession) -> None:
        """强制刷新 token 并保存。"""
        client = HaierClient(http, self.client_id, "", self.app_source)
        info = await client.refresh_token(self.refresh_token)
        self.apply_token(info)
        self.save()
        _LOGGER.info("token 已刷新，有效期至 %s", time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(self.expires_at)))

    async def ensure_valid(self, http: aiohttp.ClientSession, force_check: bool = False) -> HaierClient:
        """返回可用的客户端；token 临近过期或校验失败时自动刷新。"""
        need_refresh = self.expires_in < REFRESH_AHEAD_SECONDS
        if not need_refresh and force_check:
            try:
                await self.client(http).get_user_info()
            except HaierAuthError:
                need_refresh = True
        if need_refresh:
            await self.refresh(http)
        return self.client(http)
