"""内置本地 MCP 服务：协议握手、工具面、越界防护，以及 Windows 的编码坑。

编码那条是踩出来的：`sys.stdout` 在 Windows 默认 GBK，中文描述被写成 GBK
字节，MCP SDK 按 UTF-8 解就抛 UnicodeDecodeError，整条连接断在握手阶段，
而报错只有一句「unhandled errors in a TaskGroup」，完全看不出跟编码有关。
"""

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from spiritscout.mcp.local_server import LocalServer, handle


def _rpc(stdin: str, root: Path) -> list[dict]:
    """起一个真进程，把 stdin 灌进去，解析 stdout 的每行 JSON。"""
    proc = subprocess.run(
        [sys.executable, "-m", "spiritscout.mcp.local_server",
         "--stdio", "--root", str(root)],
        input=stdin, capture_output=True, text=True, encoding="utf-8", timeout=90)
    out = []
    for line in proc.stdout.splitlines():
        if line.strip():
            out.append(json.loads(line))
    return out


def _line(**kw) -> str:
    return json.dumps(kw, ensure_ascii=False)


class ProtocolTests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp())
        (self.root / "hello.txt").write_text("你好 MCP", encoding="utf-8")
        (self.root / "sub").mkdir()
        (self.root / "sub" / "a.bin").write_bytes(b"\x00\x01\x02")

    def test_initialize_and_tools_list(self) -> None:
        stdin = "\n".join([
            _line(jsonrpc="2.0", id=1, method="initialize", params={}),
            _line(jsonrpc="2.0", id=2, method="tools/list"),
        ]) + "\n"
        out = _rpc(stdin, self.root)
        self.assertEqual(out[0]["result"]["serverInfo"]["name"], "spiritscout-local")
        names = [t["name"] for t in out[1]["result"]["tools"]]
        self.assertEqual(sorted(names),
                         ["file_hash", "list_dir", "read_file", "write_file"])

    def test_chinese_survives_the_pipe(self) -> None:
        """中文描述必须原样回来——GBK 编码会让整条连接断在握手。"""
        stdin = _line(jsonrpc="2.0", id=1, method="tools/list") + "\n"
        out = _rpc(stdin, self.root)
        text = json.dumps(out[0], ensure_ascii=False)
        self.assertIn("列出目录", text, "中文描述应完整保留")

    def test_read_write_and_hash(self) -> None:
        stdin = "\n".join([
            _line(jsonrpc="2.0", id=1, method="tools/call",
                  params={"name": "read_file", "arguments": {"path": "hello.txt"}}),
            _line(jsonrpc="2.0", id=2, method="tools/call",
                  params={"name": "write_file",
                          "arguments": {"path": "new/out.txt", "content": "产物"}}),
            _line(jsonrpc="2.0", id=3, method="tools/call",
                  params={"name": "file_hash", "arguments": {"path": "hello.txt"}}),
        ]) + "\n"
        out = _rpc(stdin, self.root)
        self.assertIn("你好 MCP", out[0]["result"]["content"][0]["text"])
        self.assertTrue((self.root / "new" / "out.txt").exists(), "父目录应自动创建")
        self.assertIn("sha256", out[2]["result"]["content"][0]["text"])


class GuardTests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp())
        self.server = LocalServer(self.root)

    def test_escape_is_rejected(self) -> None:
        for bad in ("../outside.txt", "sub/../../outside.txt"):
            with self.assertRaises(ValueError):
                self.server.call("read_file", {"path": bad})

    def test_absolute_path_outside_root_is_rejected(self) -> None:
        with self.assertRaises(ValueError):
            self.server.call("list_dir", {"path": "/etc"})

    def test_unknown_tool_raises(self) -> None:
        with self.assertRaises(ValueError):
            self.server.call("rm_rf", {})


class HandleTests(unittest.TestCase):
    def test_notification_gets_no_reply(self) -> None:
        server = LocalServer(Path(tempfile.mkdtemp()))
        self.assertIsNone(handle(server, {
            "jsonrpc": "2.0", "method": "notifications/initialized"}))

    def test_unknown_method_returns_error(self) -> None:
        server = LocalServer(Path(tempfile.mkdtemp()))
        reply = handle(server, {"jsonrpc": "2.0", "id": 9, "method": "no/such"})
        self.assertEqual(reply["error"]["code"], -32601)

    def test_tool_failure_is_a_result_not_a_crash(self) -> None:
        """工具内部报错要作为 isError 结果返回，不能把连接打崩。"""
        server = LocalServer(Path(tempfile.mkdtemp()))
        reply = handle(server, {"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                                "params": {"name": "read_file",
                                           "arguments": {"path": "nope.txt"}}})
        self.assertTrue(reply["result"]["isError"])
        self.assertIn("不是文件", reply["result"]["content"][0]["text"])


if __name__ == "__main__":
    unittest.main()
