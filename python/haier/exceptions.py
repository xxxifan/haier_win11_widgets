class HaierClientError(Exception):
    """接口调用失败（网络错误或服务端返回非 00000 的 retCode）。"""

    def __init__(self, message: str, ret_code: str | None = None):
        super().__init__(message)
        self.ret_code = ret_code


class HaierAuthError(HaierClientError):
    """登录、刷新 token 或 token 校验失败。"""


class HaierGatewayError(Exception):
    """WebSocket 设备网关异常。"""
