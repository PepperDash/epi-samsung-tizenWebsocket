# Python Test Scripts

Standalone scripts for testing Samsung Tizen display connectivity, [WebSocket API](https://developer.samsung.com/smarttv/develop/extension-libraries/smart-view-sdk/receiver-apps/tv-websocket-api.html) behavior, and [UPnP SOAP RenderingControl](http://upnp.org/specs/av/UPnP-av-RenderingControl-v1-Service.pdf) outside the Crestron environment.

## Requirements

```bash
pip install websocket-client
```

`check_ports.py` and `upnp_volume.py` use only the Python standard library — no additional dependencies.

## Scripts

| Script | Purpose | Dependencies |
|---|---|---|
| `check_ports.py` | TCP port scan (8001/8002) and SSL certificate check | stdlib |
| `ws_connect.py` | Connect and pair — shows Allow/Deny popup on TV, prints token | `websocket-client` |
| `send_key.py` | Send one or more [key codes](https://github.com/xchwarze/samsung-tv-ws-api/blob/main/COMMANDS.md) — auto-pairs and persists tokens in `samsung-tokens.json` | `websocket-client` |
| `ws_monitor.py` | Long-running event listener with auto-reconnect — for discovering unsolicited events | `websocket-client` |
| `upnp_volume.py` | UPnP SOAP volume/mute control — `get`, `set`, `up`, `down`, `mute`, `unmute`, `status` | stdlib |

## Typical Workflow

```bash
# 1. Verify the display is reachable
python check_ports.py 10.0.133.21

# 2. Test UPnP volume/mute (no pairing required)
python upnp_volume.py 10.0.133.21 status          # read current volume + mute
python upnp_volume.py 10.0.133.21 set 25           # set volume to 25
python upnp_volume.py 10.0.133.21 up 5             # raise volume by 5
python upnp_volume.py 10.0.133.21 mute             # mute audio
python upnp_volume.py 10.0.133.21 unmute           # unmute audio

# 3. Pair via WebSocket (first time) — accept popup on TV
python ws_connect.py 10.0.133.21

# 4. Send key commands — token is auto-negotiated and saved to samsung-tokens.json
python send_key.py 10.0.133.21 KEY_POWER
python send_key.py 10.0.133.21 KEY_HDMI1
python send_key.py 10.0.133.21 KEY_VOLUP KEY_VOLUP KEY_VOLUP

# 5. Monitor events (leave running while using physical remote)
python ws_monitor.py 10.0.133.21 <token>
```

## UPnP SOAP Details

The `upnp_volume.py` script sends SOAP requests to the Samsung display's [UPnP RenderingControl:1](http://upnp.org/specs/av/UPnP-av-RenderingControl-v1-Service.pdf) service, which is exposed on port **9197** by default.

### Supported Actions

| Action | SOAP Method | Arguments |
|---|---|---|
| Get volume | `GetVolume` | `InstanceID=0`, `Channel=Master` → `CurrentVolume` (0–100) |
| Set volume | `SetVolume` | `InstanceID=0`, `Channel=Master`, `DesiredVolume` (0–100) |
| Get mute | `GetMute` | `InstanceID=0`, `Channel=Master` → `CurrentMute` (0 or 1) |
| Set mute | `SetMute` | `InstanceID=0`, `Channel=Master`, `DesiredMute` (0 or 1) |

### Notes

- UPnP SOAP does **not** require WebSocket pairing — it works independently
- The display must be powered on for UPnP to respond
- If port 9197 doesn't respond, try port 7676 — some firmware versions use a different port (check via SSDP)
- See the [ha-samsungtv-tizen UPnP implementation](https://github.com/jaruba/ha-samsungtv-tizen/blob/main/custom_components/samsungtv_tizen/upnp.py) for a reference Python client
