#!/usr/bin/env python3
"""
mock_server.py — 临时 mock API 服务器，用于调试 bg_tool 上报数据

启动后监听 5000 端口，接收 /api/plugin/* 请求，打印完整请求数据并返回 200。
不需要 MongoDB，不需要任何依赖（只用标准库）。

用法：
  python mock_server.py              # 默认 0.0.0.0:5000
  python mock_server.py 8080         # 指定端口

bg_tool 的 config.json 中 apiBaseUrl 设为 http://localhost:5000
"""

import json
import sys
from datetime import datetime, timezone
from http.server import HTTPServer, BaseHTTPRequestHandler


class MockHandler(BaseHTTPRequestHandler):
    """处理所有请求，打印请求数据"""

    def do_POST(self):
        content_len = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(content_len) if content_len > 0 else b""

        # 解析 JSON
        try:
            data = json.loads(body) if body else {}
        except json.JSONDecodeError:
            data = {"_raw": body.decode("utf-8", errors="replace")}

        # 打印到控制台
        ts = datetime.now().strftime("%H:%M:%S")
        print(f"\n{'='*60}")
        print(f"[{ts}] POST {self.path}")
        print(f"{'─'*60}")
        print(f"Headers:")
        for k, v in self.headers.items():
            print(f"  {k}: {v}")
        print(f"Body ({content_len} bytes):")
        print(json.dumps(data, indent=2, ensure_ascii=False))
        print(f"{'='*60}")

        # 保存到日志文件
        with open("mock_requests.log", "a", encoding="utf-8") as f:
            f.write(f"\n[{ts}] POST {self.path}\n")
            f.write(f"Headers: {dict(self.headers)}\n")
            f.write(f"Body: {json.dumps(data, ensure_ascii=False)}\n")

        # 根据路径返回不同的模拟响应
        if "/check-league" in self.path:
            resp = {"isLeague": True, "verificationCode": "MOCK01"}
        elif "/update-placement" in self.path:
            resp = {"ok": True, "finalized": False}
        else:
            resp = {"ok": True}

        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(json.dumps(resp, ensure_ascii=False).encode("utf-8"))

    def do_GET(self):
        ts = datetime.now().strftime("%H:%M:%S")
        print(f"[{ts}] GET {self.path}")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(b'{"ok": true}')

    def log_message(self, format, *args):
        """抑制默认的 access log，只保留我们自己的输出"""
        pass


def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 5000
    server = HTTPServer(("0.0.0.0", port), MockHandler)
    print(f"🎯 Mock API 服务器启动: http://0.0.0.0:{port}")
    print(f"   接收 /api/plugin/check-league 和 /api/plugin/update-placement")
    print(f"   请求记录保存到 mock_requests.log")
    print(f"   Ctrl+C 停止\n")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\n⏹ 已停止")


if __name__ == "__main__":
    main()
