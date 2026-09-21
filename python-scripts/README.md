# Python Test Scripts

Standalone scripts for testing Samsung Tizen display connectivity and WebSocket API behavior outside the Crestron environment.

## Requirements

```bash
pip install websocket-client
```

`check_ports.py` uses only the standard library.

## Scripts

| Script | Purpose |
|---|---|
| `check_ports.py` | TCP port scan (8001/8002) and SSL certificate check |
| `ws_connect.py` | Connect and pair — shows Allow/Deny popup on TV, prints token |
| `send_key.py` | Send one or more key codes — auto-pairs and persists tokens in `samsung-tokens.json` |
| `ws_monitor.py` | Long-running event listener with auto-reconnect — for discovering unsolicited events |

## Typical Workflow

```bash
# 1. Verify the display is reachable
python check_ports.py 10.0.133.21

# 2. Pair (first time) — accept popup on TV
python ws_connect.py 10.0.133.21

# 3. Send commands — token is auto-negotiated and saved to samsung-tokens.json
python send_key.py 10.0.133.21 KEY_POWER
python send_key.py 10.0.133.21 KEY_HDMI1
python send_key.py 10.0.133.21 KEY_VOLUP KEY_VOLUP KEY_VOLUP

# 4. Monitor events (leave running while using physical remote)
python ws_monitor.py 10.0.133.21 <token>
```
