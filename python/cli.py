"""海尔智家命令行工具。

用法示例::

    python cli.py login --phone 138xxxxxxxx            # 密码会交互式提示
    python cli.py token --client-id xxx --refresh-token yyy --app-source wxapp
    python cli.py whoami
    python cli.py devices
    python cli.py attrs  <设备ID或名称> [--writable]
    python cli.py status <设备ID或名称>
    python cli.py set    <设备ID或名称> onOffStatus=true targetTemperature=26
    python cli.py watch  [设备ID或名称 ...]

会话默认保存到当前目录 session.json（可用 --session 或环境变量 HAIER_SESSION 指定）。
"""

from __future__ import annotations

import argparse
import asyncio
import getpass
import json
import logging
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, Optional

import aiohttp

from haier import (
    APP_SOURCE_APP,
    APP_SOURCES,
    HaierAuthError,
    HaierClient,
    HaierClientError,
    HaierDevice,
    HaierGateway,
    HaierGatewayError,
    HaierSession,
)
from haier.session import DEFAULT_SESSION_FILE

_LOGGER = logging.getLogger("haier.cli")


# ---------------------------------------------------------------- 工具函数

def _print_json(obj: Any) -> None:
    print(json.dumps(obj, ensure_ascii=False, indent=2))


def _fmt_range(attr: dict) -> str:
    vr = attr.get("valueRange") or {}
    t = (vr.get("type") or "").upper()
    if t == "STEP":
        s = vr.get("dataStep") or {}
        return f"{s.get('minValue')}~{s.get('maxValue')} step {s.get('step')} ({s.get('dataType')})"
    if t == "LIST":
        items = vr.get("dataList") or []
        # 过长的枚举（如目标湿度 0~90 共 91 项）只显示首尾，完整内容用 --json 查看
        if len(items) > 10:
            head = ", ".join(f"{i.get('data')}={i.get('desc')}" for i in items[:4])
            tail = ", ".join(f"{i.get('data')}={i.get('desc')}" for i in items[-2:])
            return f"{head}, ... {tail} (共 {len(items)} 项)"
        return ", ".join(f"{i.get('data')}={i.get('desc')}" for i in items)
    if t == "DATE" or t == "TIME":
        return t
    return t or "-"


def _print_table(rows: List[List[str]], headers: List[str]) -> None:
    widths = [len(h) for h in headers]
    for row in rows:
        for i, cell in enumerate(row):
            widths[i] = max(widths[i], _display_width(cell))
    def line(cells):
        return "  ".join(c + " " * (widths[i] - _display_width(c)) for i, c in enumerate(cells))
    print(line(headers))
    print(line(["-" * w for w in widths]))
    for row in rows:
        print(line(row))


def _display_width(s: str) -> int:
    import unicodedata
    return sum(2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1 for ch in s)


async def _resolve_device(client: HaierClient, key: str) -> HaierDevice:
    devices = await client.get_devices()
    for d in devices:
        if d.id == key:
            return d
    matched = [d for d in devices if key in d.name]
    if len(matched) == 1:
        return matched[0]
    if not matched:
        raise SystemExit(f"未找到设备: {key}。可用设备:\n" + "\n".join(f"  {d.id}  {d.name}" for d in devices))
    raise SystemExit(f"名称 [{key}] 匹配到多个设备，请使用设备ID:\n" + "\n".join(f"  {d.id}  {d.name}" for d in matched))


def _parse_kv(pairs: List[str]) -> Dict[str, str]:
    result = {}
    for p in pairs:
        if "=" not in p:
            raise SystemExit(f"参数格式错误，应为 key=value: {p}")
        k, v = p.split("=", 1)
        result[k.strip()] = v.strip()
    return result


async def _load_client(http: aiohttp.ClientSession, args) -> tuple[HaierSession, HaierClient]:
    session = HaierSession.load(args.session)
    client = await session.ensure_valid(http, force_check=getattr(args, "check", False))
    return session, client


# ---------------------------------------------------------------- 子命令

async def cmd_login(http, args):
    password = args.password or getpass.getpass("密码: ")
    session = await HaierSession.login(http, args.phone, password, args.session)
    print(f"登录成功: {session.user.get('mobile') or session.client_id}")
    print(f"会话已保存到 {session.path}，token 有效期至 "
          f"{time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(session.expires_at))}")


async def cmd_token(http, args):
    session = await HaierSession.from_refresh_token(
        http, args.client_id, args.refresh_token, args.app_source, args.session
    )
    print(f"登录成功: {session.user.get('mobile') or session.client_id}")
    print(f"会话已保存到 {session.path}")


async def cmd_refresh(http, args):
    session = HaierSession.load(args.session)
    await session.refresh(http)
    print(f"token 已刷新，有效期至 {time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(session.expires_at))}")


async def cmd_whoami(http, args):
    session = HaierSession.load(args.session)
    client = session.client(http)
    info = await client.get_user_info()
    if args.json:
        _print_json(info)
        return
    print(f"userId  : {info.get('userId')}")
    print(f"mobile  : {info.get('mobile')}")
    print(f"username: {info.get('username')}")
    print(f"来源    : {session.app_source}，token 剩余 {session.expires_in // 3600} 小时")


async def cmd_devices(http, args):
    _, client = await _load_client(http, args)
    devices = await client.get_devices()
    if args.json:
        _print_json([d.raw for d in devices])
        return
    if not devices:
        print("账号下没有设备")
        return
    rows = [
        [d.id, d.name, d.type or "-", d.product_name or "-", "在线" if d.online else "离线", d.wifi_type or "-"]
        for d in devices
    ]
    _print_table(rows, ["设备ID", "名称", "类型", "型号", "状态", "wifiType"])


async def cmd_attrs(http, args):
    _, client = await _load_client(http, args)
    device = await _resolve_device(client, args.device)
    attrs = await client.get_attributes(device.id)
    if args.writable:
        attrs = [a for a in attrs if a.get("writable")]
    if args.json:
        _print_json(attrs)
        return
    print(f"设备 {device.name} ({device.id}) 共 {len(attrs)} 个属性")
    rows = [
        [
            a.get("name", ""),
            a.get("desc", "") or "",
            str(a.get("value", "")),
            "RW" if a.get("writable") else ("R" if a.get("readable") else "-"),
            _fmt_range(a),
        ]
        for a in attrs
    ]
    _print_table(rows, ["属性", "说明", "当前值", "权限", "取值范围"])


async def cmd_status(http, args):
    _, client = await _load_client(http, args)
    device = await _resolve_device(client, args.device)
    snapshot = await client.get_snapshot(device.id)
    if args.json:
        _print_json({"deviceId": device.id, "name": device.name, "online": device.online, "attributes": snapshot})
        return
    print(f"设备 {device.name} ({device.id}) {'在线' if device.online else '离线'}")
    for k, v in snapshot.items():
        print(f"  {k} = {v}")


async def cmd_set(http, args):
    _, client = await _load_client(http, args)
    device = await _resolve_device(client, args.device)
    attributes = _parse_kv(args.pairs)

    if not args.no_validate:
        defs = {a["name"]: a for a in await client.get_attributes(device.id)}
        for k in attributes:
            if k not in defs:
                # 云端影子在关机等状态下会省略部分属性（如空调关机时没有 targetTemperature），
                # 所以缺失不视为错误，只提示
                print(f"提示: 当前数字模型未包含属性 [{k}]，仍尝试发送")
            elif not defs[k].get("writable"):
                raise SystemExit(f"属性 [{k}] 不可写（可加 --no-validate 强制发送）")

    before = await client.get_snapshot(device.id)
    changed: Dict[str, Any] = {}

    async def on_data(device_id: str, values: Dict[str, Any]):
        if device_id == device.id:
            changed.update(values)

    async with HaierGateway(http, client, on_data=on_data) as gw:
        await gw.subscribe([device.id])
        print(f"向 {device.name} 发送: {attributes}")
        resp = await gw.control(device.id, attributes, timeout=args.timeout)
        if resp is None:
            print("网关未在超时时间内返回命令响应（命令可能仍已执行）")
        else:
            content = resp.get("content", {})
            print(f"网关响应 topic={resp.get('topic')} "
                  f"{json.dumps(content, ensure_ascii=False)[:300]}")
        await gw.wait_data(device.id, timeout=args.wait)

    after = await client.get_snapshot(device.id)
    ok = True
    for k, v in attributes.items():
        actual = after.get(k)
        hit = str(actual) == str(v)
        ok &= hit
        print(f"  {k}: {before.get(k)} -> {actual}  {'✔' if hit else '✘ 期望 ' + str(v)}")
    if changed:
        extra = {k: v for k, v in changed.items() if k not in attributes and before.get(k) != v}
        if extra:
            print(f"  其他变化: {extra}")
    if not ok:
        raise SystemExit(2)


async def cmd_watch(http, args):
    _, client = await _load_client(http, args)
    devices = await client.get_devices()
    if args.devices:
        targets = [await _resolve_device(client, key) for key in args.devices]
    else:
        targets = devices
    names = {d.id: d.name for d in targets}
    last: Dict[str, Dict[str, Any]] = {}

    def ts():
        return time.strftime("%H:%M:%S")

    async def on_data(device_id, values):
        prev = last.get(device_id)
        if prev is None or args.full:
            print(f"[{ts()}] {names.get(device_id, device_id)}: {json.dumps(values, ensure_ascii=False)}")
        else:
            diff = {k: v for k, v in values.items() if prev.get(k) != v}
            if diff:
                print(f"[{ts()}] {names.get(device_id, device_id)}: {json.dumps(diff, ensure_ascii=False)}")
        last[device_id] = dict(values)

    async def on_online(device_id, online):
        print(f"[{ts()}] {names.get(device_id, device_id)}: {'上线' if online else '离线'}")

    async def on_message(msg):
        if args.raw:
            print(f"[{ts()}] RAW {json.dumps(msg, ensure_ascii=False)}")

    while True:
        try:
            async with HaierGateway(http, client, on_data=on_data, on_online=on_online, on_message=on_message) as gw:
                await gw.subscribe(names.keys())
                print(f"已订阅 {len(names)} 个设备，按 Ctrl+C 退出")
                # 网关只在变化时推送，先拉一次快照作为基线
                for d in targets:
                    if not d.online:
                        print(f"[{ts()}] {d.name}: 离线")
                        continue
                    try:
                        await on_data(d.id, await client.get_snapshot(d.id))
                    except HaierClientError as err:
                        print(f"[{ts()}] {d.name}: 获取快照失败 {err}")
                await gw.wait_closed()
            print(f"[{ts()}] 连接断开，30 秒后重连")
        except HaierGatewayError as err:
            print(f"[{ts()}] 网关异常: {err}，30 秒后重连")
        await asyncio.sleep(30)


# ---------------------------------------------------------------- 入口

def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="海尔智家命令行工具")
    p.add_argument("--session", default=str(DEFAULT_SESSION_FILE), help="会话文件路径")
    p.add_argument("-v", "--verbose", action="store_true", help="输出调试日志")
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("login", help="手机号+密码登录")
    s.add_argument("--phone", required=True)
    s.add_argument("--password", help="不提供则交互式输入")
    s.set_defaults(func=cmd_login)

    s = sub.add_parser("token", help="使用 refresh_token 登录（抓包获得）")
    s.add_argument("--client-id", required=True)
    s.add_argument("--refresh-token", required=True)
    s.add_argument("--app-source", default=APP_SOURCE_APP, choices=list(APP_SOURCES))
    s.set_defaults(func=cmd_token)

    s = sub.add_parser("refresh", help="强制刷新 token")
    s.set_defaults(func=cmd_refresh)

    s = sub.add_parser("whoami", help="查看当前登录用户")
    s.add_argument("--json", action="store_true")
    s.set_defaults(func=cmd_whoami)

    s = sub.add_parser("devices", help="列出设备")
    s.add_argument("--json", action="store_true")
    s.set_defaults(func=cmd_devices)

    s = sub.add_parser("attrs", help="查看设备属性定义")
    s.add_argument("device")
    s.add_argument("--writable", action="store_true", help="只显示可写属性")
    s.add_argument("--json", action="store_true")
    s.set_defaults(func=cmd_attrs)

    s = sub.add_parser("status", help="查看设备当前状态")
    s.add_argument("device")
    s.add_argument("--json", action="store_true")
    s.set_defaults(func=cmd_status)

    s = sub.add_parser("set", help="控制设备属性")
    s.add_argument("device")
    s.add_argument("pairs", nargs="+", metavar="key=value")
    s.add_argument("--timeout", type=float, default=10, help="等待网关响应秒数")
    s.add_argument("--wait", type=float, default=5, help="等待状态推送秒数")
    s.add_argument("--no-validate", action="store_true", help="跳过属性可写性校验")
    s.set_defaults(func=cmd_set)

    s = sub.add_parser("watch", help="实时监听设备状态")
    s.add_argument("devices", nargs="*")
    s.add_argument("--full", action="store_true", help="每次推送打印全部属性而非差异")
    s.add_argument("--raw", action="store_true", help="打印原始网关消息")
    s.set_defaults(func=cmd_watch)

    return p


async def _main(args) -> None:
    async with aiohttp.ClientSession() as http:
        await args.func(http, args)


def main(argv: Optional[List[str]] = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    args = build_parser().parse_args(argv)
    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )
    if not args.verbose:
        logging.getLogger("haier").setLevel(logging.WARNING)
    try:
        asyncio.run(_main(args))
    except KeyboardInterrupt:
        return 130
    except HaierAuthError as err:
        print(f"认证失败: {err}", file=sys.stderr)
        return 3
    except (HaierClientError, HaierGatewayError) as err:
        print(f"错误: {err}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
