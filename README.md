![PepperDash Essentials Pluign Logo](/images/essentials-plugin-blue.png)

# Essentials Plugin Template (c) 2025

## License

Provided under MIT license

## Overview

Fork this repo when creating a new plugin for Essentials. For more information about plugins, refer to the Essentials Wiki [Plugins](https://pepperdash.github.io/Essentials/docs/Plugins.html) article.

This plugin provides two-way control of Samsung displays over the Samsung Tizen WebSocket API.

Status behavior note:
* The Samsung `samsung.remote.control` WebSocket is treated as a command/session transport, not a `GenericCommunicationMonitor` transport.
* Online state is controller-driven from WebSocket connection state.
* Power, mute, volume, and source feedback are a mix of optimistic local state and any unsolicited Samsung events actually observed at runtime.
* The plugin does not assume consumer Samsung TVs support explicit WebSocket getter commands for power, volume, mute, or source.

Core runtime classes:
* `SamsungTizenWebsocketDeviceFactory`: creates Samsung devices for `type: "samsungTizenWebsocket"`
* `SamsungTizenWebsocketController`: command/feedback controller and bridge link implementation
* `SamsungTizenWebsocketConfig`: device properties configuration model
* `SamsungTizenWebsocketBridgeJoinMap`: digital/analog/serial bridge join definitions

## Device Configuration

Factory expectations:
* Device type name must be `samsungTizenWebsocket`
* Preferred config uses `properties.control`
* `properties.control.tcpSshProperties.address` is required when `control` is used
* `properties.control.tcpSshProperties.port` defaults to `8002` when omitted
* Legacy `properties.address` / `properties.port` are still accepted as fallback

Example Essentials device config:

```json
{
	"key": "display-lobby-samsung",
	"name": "Lobby Samsung Display",
	"type": "samsungTizenWebsocket",
	"group": "displays",
	"properties": {
		"control": {
			"method": "https",
			"tcpSshProperties": {
				"address": "192.168.1.100",
				"port": 8002,
				"username": "optional",
				"password": "optional",
				"autoReconnect": true,
				"autoReconnectIntervalMs": 10000
			}
		},
		"pollIntervalMs": 5000,
		"coolingTimeMs": 8000,
		"warmingTimeMs": 10000,
		"warningTimeoutMs": 60000,
		"errorTimeoutMs": 120000,
		"friendlyNames": [
			{ "inputKey": "hdmi1", "name": "Teams Room PC" },
			{ "inputKey": "displayport", "name": "Wall Plate", "hideInput": false }
		]
	}
}
```

Property reference:

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `control` | object | Preferred | n/a | Essentials control object. Use `method` plus `tcpSshProperties` for address, port, and credentials |
| `control.method` | string | No | `https` behavior | Supported values: `http`, `https`, `ws`, `wss`. `http`/`ws` use insecure WebSocket, `https`/`wss` use secure WebSocket |
| `control.tcpSshProperties.address` | string | Yes with `control` | n/a | IP address or hostname of the Samsung display |
| `control.tcpSshProperties.port` | int | No | `8002` | Samsung Tizen WebSocket port |
| `control.tcpSshProperties.username` | string | No | empty | Optional credential stored with the Essentials control config |
| `control.tcpSshProperties.password` | string | No | empty | Optional credential stored with the Essentials control config |
| `address` | string | Legacy fallback | n/a | Legacy top-level address. Used only when `control.tcpSshProperties.address` is not provided |
| `port` | int | Legacy fallback | `8002` | Legacy top-level port. Used only when `control.tcpSshProperties.port` is not provided |
| `pollIntervalMs` | long | No | `5000` | Poll cycle interval |
| `coolingTimeMs` | uint | No | `8000` | Local cooldown timer used after power off |
| `warmingTimeMs` | uint | No | `10000` | Local warmup timer used after power on |
| `warningTimeoutMs` | long | No | `60000` | Warning threshold for delayed responses |
| `errorTimeoutMs` | long | No | `120000` | Error threshold for delayed responses |
| `friendlyNames` | array | No | empty | Optional input rename and hide rules keyed by source id |

Connection notes:
* The plugin still talks to Samsung over the Tizen WebSocket API.
* `control.method` is used to determine whether the underlying WebSocket URI is built as `ws://` or `wss://`.
* If no `control.method` is supplied, the plugin defaults to secure WebSocket behavior to preserve the previous implementation.

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
| `VolumeLevel` | 1 | ToFromSIMPL | Volume level (0-100) |

### Serial Joins

| Join Name | Offset | Capability | Description |
|---|---:|---|---|
| `DeviceName` | 1 | ToSIMPL | Device name |
| `CurrentSource` | 2 | ToSIMPL | Current source feedback |

Supported current source values depend on Samsung API payloads, typically values such as `hdmi1`, `hdmi2`, `hdmi3`, `hdmi4`, and `displayport`.

Input-name customization:
* `friendlyNames[].inputKey` supports `hdmi1`, `hdmi2`, `hdmi3`, `hdmi4`, and `displayport`
* `friendlyNames[].name` overrides the bridge-reported input label
* `friendlyNames[].hideInput` removes the input from the selectable input collection

## V1 Implementation Status & Known Risks

**Current Target:** Samsung QNxx-QN990FFXZA displays via Samsung Tizen WebSocket API  
**V1 Features:** Power on/off · Input/source select · Volume/mute · Status polling · LAN discovery  
**Timeline:** 6–7 weeks (phases 1–5)

### Known Risks & Mitigations for Operators

| Risk | Impact | Mitigation |
|------|--------|-----------|
| **Firmware incompatibility** | Commands may fail or produce unexpected responses if display firmware differs from tested version | Check Samsung firmware version before deployment; consult `.github/context/development-links.md` for supported versions. Report firmware mismatches in GitHub Issues. |
| **WebSocket connection drops** | Device becomes unresponsive during use | Plugin implements exponential backoff reconnection (2–30s). Monitor "latency" telemetry in logs for persistent issues. |
| **Auth token expiry** | Commands fail mid-session if display requires token re-authentication | Plugin handles token refresh automatically. If persistent auth failures occur, restart the Essentials service or manually reset display credentials. |
| **Polling latency exceeds expectations** | Status updates (power, volume, input) may lag by 1–3 seconds | Polling interval is configurable (default 5s). Adjust `PollIntervalMs` in device config if tighter feedback required. |
| **Consumer WebSocket status limits** | Real-time source, volume, mute, or power feedback may not reflect the panel unless Samsung emits usable unsolicited events | Treat WebSocket feedback as best-effort. For guaranteed telemetry, validate model-specific REST or UPnP paths before relying on two-way joins in production. |
| **Port 8002 blocked by firewall** | Device cannot connect to display | Verify port 8002 is open between control system and display. Check network firewall rules and display settings for WebSocket service enablement. |
| **Samsung API changes in new firmware** | Plugin may break on display firmware updates | Before updating display firmware, verify compatibility in GitHub Issues or contact support. We maintain a firmware compatibility matrix. |

## Cloning Instructions

After forking this repository into your own GitHub space, you can create a new repository using this one as the template.  Then you must install the necessary dependencies as indicated below.

## Dependencies

The [Essentials](https://github.com/PepperDash/Essentials) libraries are required. They referenced via nuget. You must have nuget.exe installed and in the `PATH` environment variable to use the following command. Nuget.exe is available at [nuget.org](https://dist.nuget.org/win-x86-commandline/latest/nuget.exe).

### Installing Dependencies

Dependencies will be automatically installed when

### Instructions for Renaming Solution and Files

See the Task List in Visual Studio for a guide on how to start using the template.  There is extensive inline documentation and examples as well.

For renaming instructions in particular, see the XML `remarks` tags on class definitions

## Build Instructions (PepperDash Internal) 

## Generating Nuget Package

A nuget package is automatically generated when the plugin is build. To modify the name and other details of the package, edit the following properties in the .csproj file:

1. `PackageId` - This is the name that will be used to pull the package from Nuget once it's published
2. `PackgeProjectUrl` - This should match the URL for the plugin repo
3. `AssemblyTitle` - This is the dll file name that is will show on a processor when the plugin is loaded


## Essentials User Commands

Program slot for this test session: `9`

```
APIMETHODS:9                  Operator            (*) 
APPDEBUGMESSAGE:9             Operator            (*) appdebug:P [0-10]: Sets the application's console debug message level
APPDEBUGFILTER:9              Operator            (*) appdebug:P [0-10]: Sets the application's console debug message level
APPDEBUGCLEAR:9               Operator            (*) appdebug:P [0-10]: Sets the application's console debug message level
APPDEBUGLOG:9                 Operator            (*) appdebug:P [0-10]: Sets the application's console debug message level
APPDEBUG:9                    Operator            (*) appdebug:P [0-10]: Sets the application's console debug message level
APPDEBUGCLEAR:9               Operator            (*) appdebugclear:P Clears the current custom log
APPDEBUGFILTER:9              Operator            (*) appdebugfilter [params]
APPDEBUGLOG:9                 Operator            (*) appdebuglog:P [all] Use "all" for full log.
APPDEBUGMESSAGE:9             Operator            (*) Writes message to log
DELETESECRET:9                Administrator       (*) Deletes secret from secrest provider
DEVCOMMSTATUS:9               Operator            (*) Lists the communication status of all devices
DEVFB:9                       Operator            (*) Lists current feedbacks
DEVJSON:9                     Operator            (*) 
DEVLIST:9                     Operator            (*) Lists current managed devices
DEVMETHODS:9                  Operator            (*) 
DEVPROPS:9                    Operator            (*) 
DEVSIMRECEIVE:9               Operator            (*) Simulates incoming data on a com device
DISABLEALLSTREAMDEBUG:9          Operator            (*) disables stream debugging on all devices
DONOTLOADONNEXTBOOT:9          Operator            (*) donotloadonnextboot:P [true/false]: Should the application load on next boot
GETJOINMAPMARKDOWN:9          Operator            (*) map(s) for bridge or device on bridge [brKey [devKey]]
GETJOINMAP:9                  Operator            (*) map(s) for bridge or device on bridge [brKey [devKey]]
GETJOINMAPMARKDOWN:9          Operator            (*) generate markdown of map(s) for bridge or device on bridge [brKey [devKey]]
GETROUTINGPORTS:9             Operator            (*) Reports all routing ports, if any.  Requires a device key
GETTYPES:9                    Operator            (*) Gets the device types that can be built. Accepts a filter string.
LISTTIELINES:9                Operator            (*) Prints out all tie lines
PORTALINFO:9                  Operator            (*) Shows portal URLS from configuration
REPORTVERSIONS:9              Operator            (*) Reports the versions of the loaded assemblies
SECRETPROVIDERINFO:9          Administrator       (*) Return data about secrets provider
SECRETPROVIDERLIST:9          Administrator       (*) Return list of all valid secrets providers
SETDEVICESTREAMDEBUG:9          Operator            (*) set comm debug [deviceKey] [off/rx/tx/both] ([minutes])
SETSECRET:9                   Operator            (*) Adds secret to secrets provider
SHOWCONFIG:9                  Operator            (*) Shows the current running merged config
UPDATESECRET:9                Administrator       (*) Updates secret in secrets provider
```

### DEVJSON Commands

```
DEVCOMMSTATUS:9 display-1
DEVJSON:9 {"deviceKey":"display-1","methodName":"SendKey","params":["KEY_HDMI1"]}
DEVJSON:9 {"deviceKey":"display-1","methodName":"SendKey","params":["KEY_HDMI2"]}
DEVJSON:9 {"deviceKey":"display-1","methodName":"SendKey","params":["KEY_HDMI"]}
DEVJSON:9 {"deviceKey":"display-1","methodName":"SendKey","params":["KEY_SOURCE"]}
DEVJSON:9 {"deviceKey":"display-1","methodName":"SendKey","params":["KEY_DTV"]}
DEVFB:9 display-1
```