"""不依赖网络的单元测试：签名、流水号、网关消息编解码、会话持久化。

运行: python -m pytest tests -q   或   python -m unittest discover tests
"""

import asyncio
import base64
import gzip
import json
import re
import sys
import tempfile
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from haier.client import APP_SOURCES, HaierClient, sequence_id, sign  # noqa: E402
from haier.gateway import HaierGateway, build_batch_cmd, decode_digital_model  # noqa: E402
from haier.session import HaierSession  # noqa: E402


class SignTest(unittest.TestCase):
    def test_sign_matches_reference_algorithm(self):
        # 与 banto6/haier 的 _sign 逐字节一致：sha256(path + body去空白 + appId + appKey + timestamp)
        import hashlib

        app_id, app_key = APP_SOURCES["app"]
        ts = "1700000000000"
        body = '{"username": "13800000000", "password": "x y"}'
        url = "https://zj.haier.net/api-gw/oauthserver/account/v1/login"
        expected = hashlib.sha256(
            ("/api-gw/oauthserver/account/v1/login" + '{"username":"13800000000","password":"xy"}' + app_id + app_key + ts).encode()
        ).hexdigest()
        self.assertEqual(sign(app_id, app_key, ts, body, url), expected)

    def test_sequence_id_format(self):
        sid = sequence_id()
        self.assertEqual(len(sid), 20)
        self.assertTrue(re.fullmatch(r"\d{20}", sid))
        self.assertEqual(sid[:8], time.strftime("%Y%m%d"))

    def test_common_headers_body_consistency(self):
        client = HaierClient(session=None, client_id="cid", token="tok", app_source="wxapp")
        api = "https://uws.haier.net/uds/v1/protected/deviceinfos"
        headers = client.common_headers(api, "")
        for key in ("accessToken", "appId", "appKey", "clientId", "sequenceId", "sign", "timestamp"):
            self.assertIn(key, headers)
        self.assertEqual(headers["appId"], APP_SOURCES["wxapp"][0])
        self.assertEqual(headers["accessToken"], "tok")
        self.assertEqual(
            headers["sign"],
            sign(APP_SOURCES["wxapp"][0], APP_SOURCES["wxapp"][1], headers["timestamp"], "", api),
        )

    def test_invalid_app_source(self):
        with self.assertRaises(ValueError):
            HaierClient(session=None, client_id="cid", app_source="nope")


class GatewayCodecTest(unittest.TestCase):
    def test_decode_digital_model(self):
        model = {"attributes": [{"name": "targetTemp", "value": "40"}]}
        args = base64.b64encode(gzip.compress(json.dumps(model).encode())).decode()
        self.assertEqual(decode_digital_model(args), model)

    def test_build_batch_cmd(self):
        msg = build_batch_cmd("ag", "dev1", {"onOffStatus": True, "targetTemperature": 26}, sn="s" * 32)
        self.assertEqual(msg["topic"], "BatchCmdReq")
        self.assertEqual(msg["content"]["sn"], "s" * 32)
        item = msg["content"]["data"][0]
        self.assertEqual(item["deviceId"], "dev1")
        self.assertEqual(item["subSn"], "s" * 32 + ":0")
        # 所有值转为字符串，和参考实现保持一致
        self.assertEqual(item["cmdArgs"], {"onOffStatus": "True", "targetTemperature": "26"})

    def test_handle_digital_model_message_dispatches_callbacks(self):
        received = []
        client = HaierClient(session=None, client_id="cid", token="tok")
        gw = HaierGateway(session=None, client=client, on_data=lambda d, v: received.append((d, v)))

        model = {"attributes": [{"name": "targetTemp", "value": "40"}, {"name": "noValue"}]}
        inner = {
            "dev": "dev1",
            "args": base64.b64encode(gzip.compress(json.dumps(model).encode())).decode(),
        }
        msg = {
            "agClientId": "tok",
            "topic": "GenMsgDown",
            "content": {
                "businType": "DigitalModel",
                "data": base64.b64encode(json.dumps(inner).encode()).decode(),
                "dataFmt": "",
                "sn": "x",
            },
        }

        async def run():
            waiter = asyncio.ensure_future(gw.wait_data("dev1", timeout=2))
            await asyncio.sleep(0)
            await gw._handle_message(json.dumps(msg))
            return await waiter

        values = asyncio.run(run())
        self.assertEqual(values, {"targetTemp": "40"})
        self.assertEqual(received, [("dev1", {"targetTemp": "40"})])

    def test_online_notify(self):
        events = []
        client = HaierClient(session=None, client_id="cid", token="tok")
        gw = HaierGateway(session=None, client=client, on_online=lambda d, o: events.append((d, o)))
        inner = {"devs": ["dev1", "dev2"]}
        msg = {
            "topic": "GenMsgDown",
            "content": {
                "businType": "DevOfflineNotify",
                "data": base64.b64encode(json.dumps(inner).encode()).decode(),
            },
        }
        asyncio.run(gw._handle_message(json.dumps(msg)))
        self.assertEqual(events, [("dev1", False), ("dev2", False)])

    def test_command_response_resolves_pending(self):
        client = HaierClient(session=None, client_id="cid", token="tok")
        gw = HaierGateway(session=None, client=client)

        async def run():
            loop = asyncio.get_running_loop()
            fut = loop.create_future()
            gw._pending["sn123"] = fut
            await gw._handle_message(json.dumps({"topic": "BatchCmdResp", "content": {"sn": "sn123", "retCode": "00000"}}))
            return await asyncio.wait_for(fut, 1)

        resp = asyncio.run(run())
        self.assertEqual(resp["topic"], "BatchCmdResp")


class SessionTest(unittest.TestCase):
    def test_round_trip(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "s.json"
            s = HaierSession("cid", "tok", "rt", int(time.time()) + 3600, "wxapp", {"mobile": "138"})
            s.save(path)
            loaded = HaierSession.load(path)
            self.assertEqual(loaded.client_id, "cid")
            self.assertEqual(loaded.token, "tok")
            self.assertEqual(loaded.refresh_token, "rt")
            self.assertEqual(loaded.app_source, "wxapp")
            self.assertEqual(loaded.user, {"mobile": "138"})
            self.assertFalse(loaded.expired)
            self.assertEqual(loaded.path, path)
            # 文件必须是 UTF-8 无 BOM
            self.assertFalse(path.read_bytes().startswith(b"\xef\xbb\xbf"))


if __name__ == "__main__":
    unittest.main()
