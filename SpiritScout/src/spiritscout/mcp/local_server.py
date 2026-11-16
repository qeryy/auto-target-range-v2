"""内置本地 MCP 服务：零依赖、单文件，跑在本机。

设计取舍（为什么自己写而不引第三方）：

* 官方 `modelcontextprotocol/servers` 是 Node/TypeScript，要先装 npm 依赖，
  一个 filesystem server 拖进来几十 MB 的 node_modules——和「绿色、体积小」
  正好相反。
* MCP 的 stdio 传输就是「stdin/stdout 上跑 JSON-RPC 2.0」，标准库足够。
  这样它跟着灵枢一起发，用户点一下就能用，不需要 node、不需要联网。

工具面只给「干活必需、风险可控」的四个：看目录、读文件、写文件、算哈希。
刻意**不做**任意命令执行——那应该走灵枢自己的 sandbox 通道，那里有审批与
审计；MCP 这一层保持只读为主，范围由调用方的 `root` 参数限定。

运行方式（灵枢自动拉起，也可手工试）：

    python -m spiritscout.mcp.local_server --stdio --root D:\\some\\dir
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
from pathlib import Path
from typing import Any

PROTOCOL_VERSION = "2024-11-05"
SERVER_NAME = "spiritscout-local"
SERVER_VERSION = "0.60.0"

#: 单次读写上限：MCP 走 stdio，超大内容会把管道堵死
_MAX_READ_BYTES = 2 * 1024 * 1024
_MAX_WRITE_BYTES = 2 * 1024 * 1024
_MAX_LIST_ENTRIES = 500


def _tools() -> list[dict[str, Any]]:
    return [
        {
            "name": "list_dir",
            "description": "列出目录下的文件与子目录（名称、大小、修改时间）。",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {"type": "string",
                             "description": "相对 root 的路径，空字符串表示 root 本身"},
                },
            },
        },
        {
            "name": "read_file",
            "description": "读取文本文件内容；二进制文件会返回 base64 摘要提示。",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {"type": "string", "description": "相对 root 的文件路径"},
                    "max_bytes": {"type": "integer",
                                  "description": "最多读多少字节，默认 262144"},
                },
                "required": ["path"],
            },
        },
        {
            "name": "write_file",
            "description": "写入文本文件（覆盖）。父目录不存在会自动创建。",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {"type": "string", "description": "相对 root 的文件路径"},
                    "content": {"type": "string", "description": "文件内容"},
                },
                "required": ["path", "content"],
            },
        },
        {
            "name": "file_hash",
            "description": "算文件的 sha256 与大小，用于校验样本或对比产物。",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {"type": "string", "description": "相对 root 的文件路径"},
                },
                "required": ["path"],
            },
        },
    ]


class LocalServer:
    """把 root 之后的所有路径操作收在一处，越界一律拒绝。"""

    def __init__(self, root: Path) -> None:
        self.root = root.resolve()
        self.root.mkdir(parents=True, exist_ok=True)

    def _resolve(self, rel: str) -> Path:
        """相对路径 → 绝对路径；跳出 root 直接报错（不做静默钳制）。"""
        target = (self.root / str(rel or "")).resolve()
        if target != self.root and self.root not in target.parents:
            raise ValueError(f"路径越界：{rel!r} 不在 {self.root} 之内")
        return target

    def call(self, name: str, args: dict[str, Any]) -> dict[str, Any]:
        if name == "list_dir":
            target = self._resolve(str(args.get("path") or ""))
            if not target.is_dir():
                raise ValueError(f"不是目录：{args.get('path')!r}")
            entries = []
            for i, item in enumerate(sorted(target.iterdir(),
                                            key=lambda p: (p.is_file(), p.name))):
                if i >= _MAX_LIST_ENTRIES:
                    break
                try:
                    stat = item.stat()
                except OSError:
                    continue
                entries.append({
                    "name": item.name,
                    "dir": item.is_dir(),
                    "size": 0 if item.is_dir() else stat.st_size,
                    "mtime": int(stat.st_mtime),
                })
            return {"path": str(args.get("path") or ""), "entries": entries,
                    "truncated": len(entries) >= _MAX_LIST_ENTRIES}

        if name == "read_file":
            target = self._resolve(str(args.get("path") or ""))
            if not target.is_file():
                raise ValueError(f"不是文件：{args.get('path')!r}")
            limit = min(int(args.get("max_bytes") or 262144), _MAX_READ_BYTES)
            raw = target.read_bytes()[:limit]
            if b"\x00" in raw[:2048]:
                digest = hashlib.sha256(target.read_bytes()).hexdigest()
                return {"path": str(args.get("path")), "binary": True,
                        "size": target.stat().st_size, "sha256": digest}
            return {"path": str(args.get("path")), "binary": False,
                    "content": raw.decode("utf-8", "replace"),
                    "truncated": target.stat().st_size > limit}

        if name == "write_file":
            content = str(args.get("content") or "")
            if len(content.encode("utf-8")) > _MAX_WRITE_BYTES:
                raise ValueError(f"内容超过 {_MAX_WRITE_BYTES} 字节")
            target = self._resolve(str(args.get("path") or ""))
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(content, encoding="utf-8")
            return {"path": str(args.get("path")), "bytes": target.stat().st_size}

        if name == "file_hash":
            target = self._resolve(str(args.get("path") or ""))
            if not target.is_file():
                raise ValueError(f"不是文件：{args.get('path')!r}")
            digest = hashlib.sha256()
            with target.open("rb") as fh:
                for chunk in iter(lambda: fh.read(1 << 20), b""):
                    digest.update(chunk)
            return {"path": str(args.get("path")), "sha256": digest.hexdigest(),
                    "size": target.stat().st_size}

        raise ValueError(f"未知工具：{name}")


def _reply(msg_id: Any, result: Any) -> dict[str, Any]:
    return {"jsonrpc": "2.0", "id": msg_id, "result": result}


def _error(msg_id: Any, code: int, message: str) -> dict[str, Any]:
    return {"jsonrpc": "2.0", "id": msg_id,
            "error": {"code": code, "message": message}}


def handle(server: LocalServer, msg: dict[str, Any]) -> dict[str, Any] | None:
    """处理一条 JSON-RPC 消息；通知（无 id）不回复。"""
    method = str(msg.get("method") or "")
    msg_id = msg.get("id")
    params = msg.get("params") or {}

    if method == "initialize":
        return _reply(msg_id, {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {"tools": {}},
            "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
        })
    if method in ("notifications/initialized", "initialized"):
        return None                      # 通知，不回复
    if method == "ping":
        return _reply(msg_id, {})
    if method == "tools/list":
        return _reply(msg_id, {"tools": _tools()})
    if method == "tools/call":
        name = str(params.get("name") or "")
        args = params.get("arguments") or {}
        try:
            payload = server.call(name, args if isinstance(args, dict) else {})
        except Exception as exc:  # noqa: BLE001 —— 工具失败作为结果返回，不崩连接
            return _reply(msg_id, {
                "isError": True,
                "content": [{"type": "text", "text": f"{type(exc).__name__}: {exc}"}],
            })
        return _reply(msg_id, {
            "content": [{"type": "text",
                         "text": json.dumps(payload, ensure_ascii=False)}],
        })
    if msg_id is None:
        return None
    return _error(msg_id, -32601, f"未实现的方法：{method}")


def serve_stdio(root: Path) -> int:
    """stdio 传输：按行读 JSON-RPC，按行写回。"""
    server = LocalServer(root)
    out = sys.stdout
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except ValueError:
            out.write(json.dumps(_error(None, -32700, "JSON 解析失败")) + "\n")
            out.flush()
            continue
        if not isinstance(msg, dict):
            continue
        reply = handle(server, msg)
        if reply is not None:
            out.write(json.dumps(reply, ensure_ascii=False) + "\n")
            out.flush()
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="灵枢内置本地 MCP 服务")
    parser.add_argument("--stdio", action="store_true",
                        help="按 stdio 传输运行（灵枢默认这样拉起）")
    parser.add_argument("--root", default=os.getcwd(),
                        help="允许访问的根目录，默认当前工作目录")
    args = parser.parse_args(argv)
    return serve_stdio(Path(args.root))


if __name__ == "__main__":
    raise SystemExit(main())
