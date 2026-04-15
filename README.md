![PepperDash Essentials Plugin Logo](/images/essentials-plugin-blue.png)

# Samsung Tizen WebSocket Display Plugin

## License

Provided under MIT license

## Overview

PepperDash Essentials plugin for two-way control of Samsung Tizen displays over the WebSocket API (`samsung.remote.control` channel).

Tested on Samsung QN65-QN990FFXZA. Should work with other Samsung Tizen models that expose the WebSocket API on port 8001 (insecure) or 8002 (secure/TLS).

### Key Features

- Raw TCP + manual WebSocket upgrade (works around Mono `ClientWebSocket` limitations on Crestron 4-Series)
- Automatic Samsung pairing token management — tokens are persisted locally and updated on rotation
- SSL/TLS support with self-signed certificate acceptance
- Serialized writes via `SemaphoreSlim` to prevent Mono `SslStream` concurrency errors
- Exponential backoff reconnection (2–30 seconds)
- Power, volume, mute, and input routing
- EISC bridge with configurable join map

### Architecture

| Class | Role |
|---|---|
| `SamsungTizenWebsocketFactory` | Creates devices for type `samsungTizenWebsocket` |
| `SamsungTizenWebsocketController` | `TwoWayDisplayBase` controller — commands, feedback, bridge linking |
| `SamsungTizenWebsocketProtocolBridge` | Raw TCP WebSocket transport, frame encoding/decoding, Samsung event handling |
| `SamsungTizenWebsocketConfig` | Device properties configuration model |
| `SamsungTizenWebsocketBridgeJoinMap` | Digital/analog/serial bridge join definitions |

## Device Configuration

### Device Type

```
"type": "samsungTizenWebsocket"
```

### Recommended Configuration

Use `method: "https"` with port `8002` for secure WebSocket connections. This is the only mode confirmed to support Samsung pairing.

```json
{
    "key": "display-1",
    "name": "Suite 1 Display",
    "type": "samsungTizenWebsocket",
    "group": "displays",
    "properties": {
        "control": {
            "method": "https",
            "tcpSshProperties": {
                "address": "192.168.1.100",
                "port": 8002,
                "autoReconnect": true,
                "autoReconnectIntervalMs": 10000
            }
        },
        "pollIntervalMs": 30000,
        "coolingTimeMs": 8000,
        "warmingTimeMs": 10000,
        "warningTimeoutMs": 180000,
        "errorTimeoutMs": 300000,
        "friendlyNames": [
            { "inputKey": "hdmi1", "name": "HDMI 1", "hideInput": false },
            { "inputKey": "hdmi2", "name": "HDMI 2", "hideInput": false }
        ]
    }
}
```

### Properties Reference

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `control.method` | string | Recommended | `https` behavior | `http`/`ws` = insecure (port 8001), `https`/`wss` = secure (port 8002) |
| `control.tcpSshProperties.address` | string | Yes | — | IP address or hostname of the Samsung display |
| `control.tcpSshProperties.port` | int | No | `8002` | Samsung Tizen WebSocket port |
| `address` | string | Legacy fallback | — | Used only when `control.tcpSshProperties.address` is not provided |
| `port` | int | Legacy fallback | `8002` | Used only when `control.tcpSshProperties.port` is not provided |
| `pollIntervalMs` | long | No | `5000` | Status poll interval in milliseconds |
| `coolingTimeMs` | uint | No | `8000` | Local cooldown timer after power off |
| `warmingTimeMs` | uint | No | `10000` | Local warmup timer after power on |
| `warningTimeoutMs` | long | No | `60000` | Warning threshold for response delays |
| `errorTimeoutMs` | long | No | `120000` | Error threshold for response delays |
| `friendlyNames` | array | No | `[]` | Input rename/hide rules. Keys: `hdmi1`–`hdmi4`, `displayport` |

### Control Method Behavior

| Method | Port | WebSocket | SSL |
|---|---|---|---|
| `https` | 8002 | `wss://` | Yes (self-signed accepted) |
| `wss` | 8002 | `wss://` | Yes |
| `http` | 8001 | `ws://` | No |
| `ws` | 8001 | `ws://` | No |
| _(none)_ | 8002 | `wss://` | Yes (default) |

> **Note:** Samsung pairing has only been confirmed working on the secure port (8002). Insecure connections (port 8001) may connect but Samsung may not present the pairing prompt.

## Samsung Pairing & Token Management

### First-Time Pairing

On the first connection, the Samsung display must approve the client:

1. The plugin connects via WebSocket and Samsung sends `ms.channel.unauthorized`
2. The display shows an **Allow / Deny** popup on screen
3. Once approved, Samsung sends `ms.channel.connect` with a pairing token
4. The plugin extracts and persists the token automatically

### Token File

Tokens are stored in a shared JSON file on the processor filesystem:

```
\user\program{X}\samsung-tokens.json
```

Where `{X}` is the Essentials program slot number (e.g., `\user\program9\samsung-tokens.json` for slot 9).

File format:

```json
{
    "display-1": "40902035",
    "display-2": "87654321"
}
```

- Each key is the Essentials device key
- Each value is the Samsung pairing token for that device
- Multiple plugin instances share the same file with thread-safe read/write
- Tokens are updated automatically when Samsung rotates them
- No config file changes are required after initial pairing

### Samsung Display Setup

Before first connection, verify the following on the Samsung display:

1. **Settings → General → Network → Device Connect Manager** → set to **Always notify**
2. **Check the blocked device list** — if `ControlSystem` appears, remove it
3. **The TV must be on the home screen** (not in Settings or an app) for the pairing popup to appear

### Troubleshooting Pairing

| Symptom | Cause | Fix |
|---|---|---|
| `ms.channel.unauthorized` then disconnect | No pairing popup shown | Ensure TV is on home screen, Device Connect Manager = "Always notify", check blocked list |
| Token works once then fails | Samsung rotated the token | Token auto-updates — check `samsung-tokens.json` for the latest value |
| `No Authorized` error on commands | Stale or missing token | Delete `samsung-tokens.json` and restart Essentials to re-pair |

## Console Commands

### Standard Essentials Commands

```
DEVCOMMSTATUS:9 display-1          -- Connection status
DEVFB:9 display-1                  -- Current feedback values
DEVMETHODS:9 display-1             -- Available methods
DEVPROPS:9 display-1               -- Device properties
DEVLIST:9                          -- All managed devices
```

### Device Commands (DEVJSON)

Replace `9` with your program slot and `display-1` with your device key.

#### Power

```
DEVJSON:9 {"deviceKey":"display-1","methodName":"PowerOn","params":[]}
DEVJSON:9 {"deviceKey":"display-1","methodName":"PowerOff","params":[]}
DEVJSON:9 {"deviceKey":"display-1","methodName":"PowerToggle","params":[]}
```

> **Note:** Power commands send `KEY_POWER` (toggle). `KEY_POWERON` and `KEY_POWEROFF` are not supported on all Samsung models.

#### Input Selection

```
DEVJSON:9 {"deviceKey":"display-1","methodName":"InputHdmi1","params":[]}
DEVJSON:9 {"deviceKey":"display-1","methodName":"InputHdmi2","params":[]}
DEVJSON:9 {"deviceKey":"display-1","methodName":"InputHdmi3","params":[]}
DEVJSON:9 {"deviceKey":"display-1","methodName":"InputHdmi4","params":[]}
DEVJSON:9 {"deviceKey":"display-1","methodName":"InputDisplayPort","params":[]}
```

#### Volume & Mute

```
DEVJSON:9 {"deviceKey":"display-1","methodName":"VolumeUp","params":[false]}
DEVJSON:9 {"deviceKey":"display-1","methodName":"VolumeDown","params":[false]}
DEVJSON:9 {"deviceKey":"display-1","methodName":"MuteToggle","params":[]}
```

#### Send Arbitrary Key (Testing)

Use `SendKey` to send any Samsung remote control key code for testing:

```
DEVJSON:9 {"deviceKey":"display-1","methodName":"SendKey","params":["KEY_POWER"]}
DEVJSON:9 {"deviceKey":"display-1","methodName":"SendKey","params":["KEY_HDMI1"]}
DEVJSON:9 {"deviceKey":"display-1","methodName":"SendKey","params":["KEY_SOURCE"]}
DEVJSON:9 {"deviceKey":"display-1","methodName":"SendKey","params":["KEY_VOLUP"]}
DEVJSON:9 {"deviceKey":"display-1","methodName":"SendKey","params":["KEY_MUTE"]}
```

Common Samsung key codes: `KEY_POWER`, `KEY_POWERON`, `KEY_POWEROFF`, `KEY_VOLUP`, `KEY_VOLDOWN`, `KEY_MUTE`, `KEY_HDMI`, `KEY_HDMI1`–`KEY_HDMI4`, `KEY_DISPLAYPORT`, `KEY_DVI`, `KEY_SOURCE`, `KEY_DTV`

### Verbose Logging

Enable verbose logging to see TX/RX WebSocket frames:

```
APPDEBUG:9 2
```

Log output includes:
- `[VERB] TX: {...}` — JSON sent to the display
- `[VERB] RX: {...}` — JSON received from the display
- `[INFO] SendKey: KEY_xxx` — key code dispatched
- `[INFO] Samsung pairing token received: xxxxx` — token updates
- `[INFO] Connection state changed: Ready` — connection lifecycle

## Bridge Configuration

The controller links to EISC using `SamsungTizenWebsocketBridgeJoinMap` with a configurable `joinStart` offset. All joins below are relative to `joinStart`.

When a custom bridge map is supplied, the plugin applies it via `joinMapKey` through `JoinMapHelper.TryGetJoinMapAdvancedForDevice(...)`.

## Join Map

### Digital Joins

| Join Name | Offset | Capability | Description |
|---|---:|---|---|
| `PowerOff` | 1 | FromSIMPL | Power Off command |
| `PowerOn` | 2 | ToFromSIMPL | Power On command / Power Is On feedback |
| `MuteToggle` | 3 | ToFromSIMPL | Mute Toggle command / Is Muted feedback |
| `VolumeUp` | 5 | FromSIMPL | Volume Up command |
| `VolumeDown` | 6 | FromSIMPL | Volume Down command |
| `InputHdmi1` | 11 | FromSIMPL | Select HDMI 1 |
| `InputHdmi2` | 12 | FromSIMPL | Select HDMI 2 |
| `InputHdmi3` | 13 | FromSIMPL | Select HDMI 3 |
| `InputHdmi4` | 14 | FromSIMPL | Select HDMI 4 |
| `InputDisplayPort` | 15 | FromSIMPL | Select DisplayPort |
| `IsOnline` | 50 | ToSIMPL | Device online feedback |

### Analog Joins

| Join Name | Offset | Capability | Description |
|---|---:|---|---|
| `VolumeLevel` | 1 | ToFromSIMPL | Volume level (0–100) |

### Serial Joins

| Join Name | Offset | Capability | Description |
|---|---:|---|---|
| `DeviceName` | 1 | ToSIMPL | Device name |
| `CurrentSource` | 2 | ToSIMPL | Current source feedback |

## Known Limitations

- **Power commands use `KEY_POWER` (toggle)** — `KEY_POWERON` / `KEY_POWEROFF` are not supported on all Samsung models. The plugin needs power state feedback to avoid toggling in the wrong direction.
- **Input selection via key codes** — some Samsung models may not respond to `KEY_HDMI1` etc. Use `SendKey` to test which codes your model supports.
- **Feedback is optimistic** — power, volume, mute, and source feedback are set locally on command send. Samsung consumer displays do not reliably emit status events over WebSocket.
- **Token rotation** — Samsung rotates pairing tokens on each connection. The plugin handles this automatically but the display must remain accessible on the network.

## Dependencies

- [PepperDash Essentials](https://github.com/PepperDash/Essentials) (referenced via NuGet)
- Crestron 4-Series processor (.NET Framework 4.7.2)

## Build

```
dotnet build epi-samsung-tizenWebsocket.4Series.sln
```

Output: `output/epi-samsung-tizenWebsocket.4Series.1.0.0-local.cplz`