#!/usr/bin/env python3
"""
Send a remote-control key to a Samsung Tizen display over WebSocket.

Usage:
    python send_key.py <ip_address> <token> <KEY_CODE> [KEY_CODE ...]

Examples:
    python send_key.py 10.0.133.21 15573624 KEY_POWER
    python send_key.py 10.0.133.21 15573624 KEY_VOLUP KEY_VOLUP KEY_VOLUP
    python send_key.py 10.0.133.21 15573624 KEY_HDMI1
    python send_key.py 10.0.133.21 15573624 KEY_MUTE

Common key codes:
    KEY_POWER, KEY_POWERON, KEY_POWEROFF
    KEY_VOLUP, KEY_VOLDOWN, KEY_MUTE
    KEY_HDMI, KEY_HDMI1, KEY_HDMI2, KEY_HDMI3, KEY_HDMI4
    KEY_DISPLAYPORT, KEY_DVI
    KEY_SOURCE, KEY_MENU, KEY_HOME, KEY_RETURN, KEY_EXIT
    KEY_UP, KEY_DOWN, KEY_LEFT, KEY_RIGHT, KEY_ENTER

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


def build_key_command(key_code):
    return json.dumps({
        "method": "ms.remote.control",
        "params": {
            "Cmd": "Click",
            "DataOfCmd": key_code,
            "Option": "false",
            "TypeOfRemote": "SendRemoteKey",
        },
    })


def main():
    if len(sys.argv) < 4:
        print(__doc__.strip())
        sys.exit(1)

    host = sys.argv[1]
    token = sys.argv[2]
    keys = sys.argv[3:]
    url = build_url(host, token=token)

    ssl_opts = {"cert_reqs": ssl.CERT_NONE, "check_hostname": False}

    print(f"Connecting to {host}:8002...")
    ws = websocket.create_connection(url, sslopt=ssl_opts, timeout=10)

    # Wait for ms.channel.connect
    response = ws.recv()
    data = json.loads(response)
    event = data.get("event", "")

    if event == "ms.channel.connect":
        new_token = data.get("data", {}).get("token")
        print(f"Connected (token: {new_token})")
    elif event == "ms.channel.unauthorized":
        print("UNAUTHORIZED — approve on TV first, then retry with token")
        ws.close()
        sys.exit(1)
    else:
        print(f"Unexpected response: {json.dumps(data, indent=2)}")

    # Send each key with a small delay between
    for key in keys:
        cmd = build_key_command(key)
        print(f"Sending: {key}")
        ws.send(cmd)
        time.sleep(0.3)

    # Brief listen for any responses
    ws.settimeout(1.0)
    try:
        while True:
            msg = ws.recv()
            print(f"RX: {msg}")
    except (websocket.WebSocketTimeoutException, websocket.WebSocketConnectionClosedException):
        pass

    ws.close()
    print("Done.")


if __name__ == "__main__":
    main()
