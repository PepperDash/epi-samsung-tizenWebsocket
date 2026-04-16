#!/usr/bin/env python3
"""
Connect to a Samsung Tizen display over WebSocket (wss, port 8002).
Handles pairing flow and prints all received messages.

If the display is unpaired, it will show an Allow/Deny popup on screen.
Accept on the TV to receive a pairing token.

Usage:
    python ws_connect.py <ip_address> [token]

Examples:
    python ws_connect.py 10.0.133.21               # first-time pairing
    python ws_connect.py 10.0.133.21 15573624       # reconnect with saved token

Requires: pip install websocket-client
"""

import json
import ssl
import sys
import time
from base64 import b64encode

try:
    import websocket
except ImportError:
    print("Install websocket-client:  pip install websocket-client")
    sys.exit(1)


CLIENT_NAME = "PythonTest"


def build_url(host, port=8002, token=None):
    encoded_name = b64encode(CLIENT_NAME.encode()).decode()
    url = f"wss://{host}:{port}/api/v2/channels/samsung.remote.control?name={encoded_name}"
    if token:
        url += f"&token={token}"
    return url


def on_message(ws, message):
    try:
        data = json.loads(message)
        event = data.get("event", "")
        pretty = json.dumps(data, indent=2)

        if event == "ms.channel.connect":
            token = data.get("data", {}).get("token")
            print(f"\n*** CONNECTED — token: {token} ***\n")
            print(pretty)
        elif event == "ms.channel.unauthorized":
            print("\n*** UNAUTHORIZED — check TV for Allow/Deny popup ***\n")
            print(pretty)
        else:
            print(f"RX: {pretty}")
    except json.JSONDecodeError:
        print(f"RX (raw): {message}")


def on_error(ws, error):
    print(f"ERROR: {error}")


def on_close(ws, close_status_code, close_msg):
    print(f"CLOSED: code={close_status_code}, reason={close_msg}")


def on_open(ws):
    print("WebSocket opened — waiting for Samsung handshake...\n")


def main():
    if len(sys.argv) < 2:
        print(__doc__.strip())
        sys.exit(1)

    host = sys.argv[1]
    token = sys.argv[2] if len(sys.argv) > 2 else None
    url = build_url(host, token=token)

    print(f"Connecting to {url}\n")

    ws = websocket.WebSocketApp(
        url,
        on_open=on_open,
        on_message=on_message,
        on_error=on_error,
        on_close=on_close,
    )

    ws.run_forever(
        sslopt={"cert_reqs": ssl.CERT_NONE, "check_hostname": False},
        ping_interval=30,
        ping_timeout=10,
    )


if __name__ == "__main__":
    main()
