#!/usr/bin/env python3
"""
Send a remote-control key to a Samsung Tizen display over WebSocket.

Tokens are stored in samsung-tokens.json (same directory as this script).
If no token exists for the target IP, the script will attempt to pair
automatically — accept the Allow/Deny popup on the TV.

Usage:
    python send_key.py <ip_address> <KEY_CODE> [KEY_CODE ...]

Examples:
    python send_key.py 10.0.133.21 KEY_POWER
    python send_key.py 10.0.133.21 KEY_VOLUP KEY_VOLUP KEY_VOLUP
    python send_key.py 10.0.133.21 KEY_HDMI1
    python send_key.py 10.0.133.21 KEY_MUTE

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
import os
import ssl
import sys
import time
from base64 import b64encode
from pathlib import Path

try:
    import websocket
except ImportError:
    print("Install websocket-client:  pip install websocket-client")
    sys.exit(1)


CLIENT_NAME = "PythonTest"
TOKEN_FILE = Path(__file__).parent / "samsung-tokens.json"
SSL_OPTS = {"cert_reqs": ssl.CERT_NONE, "check_hostname": False}


# ---------- token persistence ----------

def load_tokens():
    """Load the token store from disk. Returns a dict keyed by IP."""
    if TOKEN_FILE.exists():
        try:
            return json.loads(TOKEN_FILE.read_text())
        except (json.JSONDecodeError, OSError):
            pass
    return {}


def save_tokens(tokens):
    """Write the token store to disk."""
    TOKEN_FILE.write_text(json.dumps(tokens, indent=2) + "\n")


def get_token(host):
    """Return saved token for *host*, or None."""
    return load_tokens().get(host)


def set_token(host, token):
    """Persist *token* for *host*."""
    tokens = load_tokens()
    tokens[host] = token
    save_tokens(tokens)


# ---------- WebSocket helpers ----------

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


def connect_and_handshake(host, token=None):
    """
    Connect to the display. Returns (ws, token) on success.
    If unauthorized, waits up to 30 s for the user to accept on the TV,
    then retries once.
    """
    url = build_url(host, token=token)
    print(f"Connecting to {host}:8002...")
    ws = websocket.create_connection(url, sslopt=SSL_OPTS, timeout=10)

    response = ws.recv()
    data = json.loads(response)
    event = data.get("event", "")

    if event == "ms.channel.connect":
        new_token = data.get("data", {}).get("token")
        if new_token:
            set_token(host, new_token)
        print(f"Connected (token: {new_token})")
        return ws, new_token

    if event == "ms.channel.unauthorized":
        ws.close()
        if token:
            # Saved token was rejected — try without it (fresh pairing)
            print("Saved token rejected — initiating fresh pairing...")
            return pair(host)
        return pair(host)

    print(f"Unexpected response: {json.dumps(data, indent=2)}")
    ws.close()
    sys.exit(1)


def pair(host):
    """
    Initiate a pairing flow (no token). Waits up to 30 s for
    the user to accept on the TV.
    """
    url = build_url(host)
    print("Pairing — accept the Allow/Deny popup on the TV...")
    ws = websocket.create_connection(url, sslopt=SSL_OPTS, timeout=30)

    response = ws.recv()
    data = json.loads(response)
    event = data.get("event", "")

    if event == "ms.channel.connect":
        new_token = data.get("data", {}).get("token")
        if new_token:
            set_token(host, new_token)
        print(f"Paired (token: {new_token})")
        return ws, new_token

    if event == "ms.channel.unauthorized":
        # Still unauthorized after waiting — TV popup was likely denied or timed out.
        # Wait for a second message in case the approval comes in.
        print("Waiting for TV approval (up to 30 s)...")
        ws.settimeout(30)
        try:
            response = ws.recv()
            data = json.loads(response)
            if data.get("event") == "ms.channel.connect":
                new_token = data.get("data", {}).get("token")
                if new_token:
                    set_token(host, new_token)
                print(f"Paired (token: {new_token})")
                return ws, new_token
        except (websocket.WebSocketTimeoutException, websocket.WebSocketConnectionClosedException):
            pass

        print("Pairing failed — TV did not approve the connection.")
        ws.close()
        sys.exit(1)

    print(f"Unexpected response: {json.dumps(data, indent=2)}")
    ws.close()
    sys.exit(1)


# ---------- main ----------

def main():
    if len(sys.argv) < 3:
        print(__doc__.strip())
        sys.exit(1)

    host = sys.argv[1]
    keys = sys.argv[2:]

    token = get_token(host)
    if token:
        print(f"Using saved token for {host}")

    ws, token = connect_and_handshake(host, token=token)

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
