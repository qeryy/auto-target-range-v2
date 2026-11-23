"""stdio 传输桥：让后端在**不监听任何 TCP 端口**的前提下被桌面客户端调用。

## 为什么需要这个

原生桌面版启动后端时，如果照旧 uvicorn 监听 127.0.0.1:8010，那本质上还是
「后台跑了个 web 服务」——用户明确要求要像微信/QQ 那样的桌面软件，不要开端口。

做法：把 HTTP 报文塞进子进程的 stdin/stdout。C# 端只跟自己的子进程通信，
系统里没有任何 socket 在监听，`netstat` 看不到这个应用。

## 协议

两端都用「长度前缀 + 报文」避免粘包：

    <十进制字节数>\\n<报文字节>

请求是标准 HTTP/1.1 报文（METHOD /path HTTP/1.1 + headers + body），
响应同理。这样后端 87 个端点一行都不用改——它们仍然是 FastAPI 路由，
只是「谁把请求递进来」从 uvicorn 换成了这根管道。

## 用 h11 而不是手撕字符串

h11 是 uvicorn 自己用的 HTTP 状态机，能正确处理分块、Content-Length、
keep-alive 这类边界。手写解析器在「body 里恰好有 \\r\\n」时就会错位。
"""

from __future__ import annotations

import asyncio
import contextlib
import io
import sys
from typing import Any

import h11


class _AsgiChannel:
    """把一次 HTTP 请求喂给 ASGI app，并收回响应字节。"""

    def __init__(self, app: Any, method: str, target: str, headers: list[tuple[bytes, bytes]],
                 body: bytes) -> None:
        self.app = app
        self.method = method
        self.target = target
        self.headers = headers
        self.body = body
        self.status = 200
        self.response_headers: list[tuple[bytes, bytes]] = []
        self.response_body = bytearray()

    async def run(self) -> tuple[int, list[tuple[bytes, bytes]], bytes]:
        path, _, query = self.target.partition("?")
        scope = {
            "type": "http",
            "asgi": {"version": "3.0", "spec_version": "2.3"},
            "http_version": "1.1",
            "method": self.method,
            "scheme": "http",
            "path": path,
            "raw_path": path.encode("latin-1", "replace"),
            "query_string": query.encode("latin-1", "replace"),
            "root_path": "",
            "headers": self.headers,
            "client": ("127.0.0.1", 0),
            "server": ("pipe", 0),
        }

        sent = False

        async def receive() -> dict[str, Any]:
            nonlocal sent
            if not sent:
                sent = True
                return {"type": "http.request", "body": self.body, "more_body": False}
            # 请求体一次性给完了。**不能再挂起**——GET 这类无 body 的请求里，
            # Starlette 会先要一次 body 再继续；挂起会让整个请求永远不返回
            # （实测表现为「客户端发完请求就卡住」）。回一个空 body 即可。
            return {"type": "http.request", "body": b"", "more_body": False}

        async def send(message: dict[str, Any]) -> None:
            kind = message.get("type")
            if kind == "http.response.start":
                self.status = int(message.get("status", 200))
                self.response_headers = list(message.get("headers") or [])
            elif kind == "http.response.body":
                chunk = message.get("body") or b""
                if chunk:
                    self.response_body.extend(chunk)

        await self.app(scope, receive, send)
        return self.status, self.response_headers, bytes(self.response_body)


#: 常用状态码短语（h11 只认数字，短语由调用方给）
_REASONS = {
    200: "OK", 201: "Created", 202: "Accepted", 204: "No Content",
    301: "Moved Permanently", 302: "Found", 304: "Not Modified",
    400: "Bad Request", 401: "Unauthorized", 403: "Forbidden",
    404: "Not Found", 405: "Method Not Allowed", 409: "Conflict",
    422: "Unprocessable Entity", 429: "Too Many Requests",
    500: "Internal Server Error", 502: "Bad Gateway", 503: "Service Unavailable",
}


def build_http_response(status: int, headers: list[tuple[bytes, bytes]], body: bytes) -> bytes:
    """把 ASGI 的响应拼回 HTTP/1.1 报文。"""
    conn = h11.Connection(h11.SERVER)
    out = io.BytesIO()

    def emit(event: Any) -> None:
        data = conn.send(event)
        if data:
            out.write(data)

    # h11 不自带短语表（0.16 也没有 _util），查一张小表即可；
    # 没收录的一律空串，HTTP/1.1 允许省略 reason-phrase。
    emit(h11.Response(status_code=status, headers=list(headers),
                      reason=_REASONS.get(status, "")))
    if body:
        emit(h11.Data(data=body))
    emit(h11.EndOfMessage())
    return out.getvalue()


def parse_http_request(raw: bytes) -> tuple[str, str, list[tuple[bytes, bytes]], bytes]:
    """把一根 HTTP/1.1 请求报文解析成 (method, target, headers, body)。"""
    # 我们是「服务端」：receive_data 在这边期望的是**请求**，不是响应。
    # 用 CLIENT 会报 "illegal status line"——它以为自己在收响应行。
    conn = h11.Connection(h11.SERVER)
    conn.receive_data(raw)
    event = conn.next_event()
    if not isinstance(event, h11.Request):
        raise ValueError("不是合法的 HTTP 请求报文")
    body = bytearray()
    while True:
        ev = conn.next_event()
        if isinstance(ev, h11.Data):
            body.extend(ev.data)
        elif isinstance(ev, h11.EndOfMessage):
            break
        elif ev is h11.NEED_DATA:
            break
    return (event.method.decode("ascii"), event.target.decode("latin-1"),
            list(event.headers), bytes(body))


# ---------- 长度前缀帧 ----------

def _read_exact(stream: Any, n: int) -> bytes:
    buf = bytearray()
    while len(buf) < n:
        chunk = stream.read(n - len(buf))
        if not chunk:
            raise EOFError("管道已关闭")
        buf.extend(chunk)
    return bytes(buf)


def read_frame(stdin: Any) -> bytes:
    """读一帧：十进制长度 + \\n + 报文。"""
    line = bytearray()
    while True:
        ch = stdin.read(1)
        if not ch:
            raise EOFError("管道已关闭")
        if ch == b"\n":
            break
        line.extend(ch)
    if not line.strip():
        raise ValueError("空的长度前缀")
    size = int(line.decode("ascii").strip())
    if size < 0 or size > 256 * 1024 * 1024:
        raise ValueError(f"帧长度不合理：{size}")
    return _read_exact(stdin, size)


def write_frame(stdout: Any, payload: bytes) -> None:
    stdout.write(str(len(payload)).encode("ascii") + b"\n")
    stdout.write(payload)
    stdout.flush()


async def serve(app: Any) -> int:
    """主循环：从 stdin 读请求、调 ASGI、把响应写回 stdout。"""
    # 二进制管道：不走文本编码，避免中文 body 被转码两次。
    # 注意 `__main__` 在 --stdio 时已把 sys.stdout 指向 stderr，所以这里必须
    # 从**原始**文件描述符取 stdout，那才是连着客户端的那根管子。
    stdin = sys.stdin.buffer
    # `__main__` 在 --stdio 时把真 stdout 存在这里（sys.stdout 已指向 stderr）
    stdout = getattr(sys, "_spiritscout_stdio_out", None)
    if stdout is None:
        stdout = sys.stdout.buffer
    sys.stdout = sys.stderr

    loop = asyncio.get_running_loop()
    print("[stdio] 后端已就绪，等待客户端请求", file=sys.stderr, flush=True)

    while True:
        try:
            raw = await loop.run_in_executor(None, read_frame, stdin)
        except EOFError:
            print("[stdio] 客户端已断开，退出", file=sys.stderr, flush=True)
            return 0
        except Exception as exc:  # noqa: BLE001 —— 坏帧不该让整个后端退出
            print(f"[stdio] 读帧失败：{exc}", file=sys.stderr, flush=True)
            continue

        try:
            method, target, headers, body = parse_http_request(raw)
            channel = _AsgiChannel(app, method, target, headers, body)
            status, resp_headers, resp_body = await channel.run()
            payload = build_http_response(status, resp_headers, resp_body)
        except Exception as exc:  # noqa: BLE001
            payload = build_http_response(
                500, [(b"content-type", b"text/plain; charset=utf-8")],
                f"后端处理失败：{type(exc).__name__}: {exc}".encode("utf-8"))

        with contextlib.suppress(Exception):
            await loop.run_in_executor(None, write_frame, stdout, payload)
    return 0
