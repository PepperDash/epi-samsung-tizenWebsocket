#!/usr/bin/env python3
"""
Long-running monitor that stays connected to a Samsung display and logs
all unsolicited events. Useful for discovering what events the TV emits
when power, volume, source, etc. change via the physical remote or other means.

Auto-reconnects on disconnect.

Usage:
    python ws_monitor.py <ip_address> <token>

Example:
    python ws_monitor.py 10.0.133.21 15573624

Requires: pip install websocket-client
"""

import json
import ssl
import sys
import time
from base64 import b64encode
from datetime import datetime

try:
    import websocket
except ImportError:
    print("Install websocket-client:  pip install websocket-client")
    sys.exit(1)


CLIENT_NAME = "PythonMonitor"
RECONNECT_DELAY = 5


def build_url(host, port=8002, token=None):
    encoded_name = b64encode(CLIENT_NAME.encode()).decode()
    url = f"wss://{host}:{port}/api/v2/channels/samsung.remote.control?name={encoded_name}"
    if token:
        url += f"&token={token}"
    return url


def ts():
    return datetime.now().strftime("%H:%M:%S.%f")[:-3]


def on_message(ws, message):
    try:
        data = json.loads(message)
        event = data.get("event", "unknown")

        # Update token if rotated
        if event == "ms.channel.connect":
            new_token = data.get("data", {}).get("token")
            if new_token:
                ws.token = new_token
                print(f"[{ts()}] CONNECT — token updated: {new_token}")
            return

        print(f"[{ts()}] EVENT: {event}")
        print(f"         {json.dumps(data, indent=2)}")
    except json.JSONDecodeError:
        print(f"[{ts()}] RAW: {message}")


def on_error(ws, error):
    print(f"[{ts()}] ERROR: {error}")


def on_close(ws, close_status_code, close_msg):
    print(f"[{ts()}] CLOSED: code={close_status_code}, reason={close_msg}")


def on_open(ws):
    print(f"[{ts()}] WebSocket opened — monitoring events...\n")


def main():
    if len(sys.argv) < 3:
        print(__doc__.strip())
        sys.exit(1)

    host = sys.argv[1]
    token = sys.argv[2]

    print(f"Samsung Event Monitor — {host}:8002")
    print(f"Press Ctrl+C to stop\n")

    while True:
        url = build_url(host, token=token)

        ws = websocket.WebSocketApp(
            url,
            on_open=on_open,
            on_message=on_message,
            on_error=on_error,
            on_close=on_close,
        )
        ws.token = token

        try:
            ws.run_forever(
                sslopt={"cert_reqs": ssl.CERT_NONE, "check_hostname": False},
                ping_interval=30,
                ping_timeout=10,
            )
        except KeyboardInterrupt:
            print(f"\n[{ts()}] Stopped by user.")
            break

        # Use updated token for reconnect
        if hasattr(ws, "token") and ws.token != token:
            token = ws.token

        print(f"[{ts()}] Reconnecting in {RECONNECT_DELAY}s...")
        time.sleep(RECONNECT_DELAY)


if __name__ == "__main__":
    main()
