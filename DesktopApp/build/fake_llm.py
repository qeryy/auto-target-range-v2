# -*- coding: utf-8 -*-
"""给 UI 会话测试用的假 OpenAI 兼容服务。

只做一件事：任何 POST 都回一段固定的助手回复。
请求体不解析 —— 我们要验的是「界面能不能把消息发出去、回复能不能落到界面和会话文件」，
不是协议细节（协议细节 e2e_check 已经盖了）。
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer

REPLY = "收到，这是本地假模型的回复：会话链路已跑通。"


class Handler(BaseHTTPRequestHandler):
    def _respond(self):
        n = int(self.headers.get("Content-Length") or 0)
        if n:
            self.rfile.read(n)
        payload = {
            "id": "fake-1",
            "object": "chat.completion",
            "model": "local-model",
            "choices": [{
                "index": 0,
                "message": {"role": "assistant", "content": REPLY},
                "finish_reason": "stop",
            }],
            "usage": {"prompt_tokens": 100, "completion_tokens": 20, "total_tokens": 120},
        }
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        self._respond()

    def do_GET(self):
        # /models 之类也给点东西，免得界面探测时崩
        body = json.dumps({"object": "list", "data": [{"id": "local-model", "object": "model"}]}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *a):
        pass


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8000
    srv = HTTPServer(("127.0.0.1", port), Handler)
    print("fake-llm listening on %d" % port, flush=True)
    srv.serve_forever()