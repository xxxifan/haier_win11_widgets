"""海尔智家（U+ 云）非官方 Python 客户端。

接口逻辑参考 https://github.com/banto6/haier，去除了对 HomeAssistant 的依赖。
"""

from .client import (
    APP_SOURCE_APP,
    APP_SOURCE_WXAPP,
    APP_SOURCES,
    HaierClient,
    HaierDevice,
    TokenInfo,
)
from .exceptions import HaierAuthError, HaierClientError, HaierGatewayError
from .gateway import HaierGateway
from .session import HaierSession

__all__ = [
    "APP_SOURCE_APP",
    "APP_SOURCE_WXAPP",
    "APP_SOURCES",
    "HaierClient",
    "HaierDevice",
    "TokenInfo",
    "HaierAuthError",
    "HaierClientError",
    "HaierGatewayError",
    "HaierGateway",
    "HaierSession",
]
