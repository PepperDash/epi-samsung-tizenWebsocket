---
description: "PepperDash Essentials plugin conventions for Samsung Tizen WebSocket: naming, folder structure, testing patterns, and configuration hygiene."
---

# Samsung Tizen WebSocket Plugin Conventions

This guide applies to all Samsung QNxx-QN990FFXZA plugin work in this repository.

## Naming Conventions

- **Namespace**: `PepperDash.Essentials.Plugins.Samsung.TizenWebsocket`
- **Class names**: Use PascalCase with domain clarity
  - Device class: `SamsungTizenWebsocketDevice`
  - Factory: `SamsungTizenWebsocketFactory`
  - Config: `SamsungTizenWebsocketPropertiesConfig`
  - Join map: `SamsungTizenWebsocketBridgeJoinMap`
  - Protocol handler: `SamsungTizenWebsocketProtocolBridge`
- **Methods**: Use imperative verbs for commands
  - `SendPowerOn()`, `SendSourceSelect(sourceId)`, `QueryDeviceStatus()`
  - Feedback setters: `SetPowerStatus(isOn)`, `SetVolumeLevel(level)`
- **File structure**: One class per file; match class name
- **Plugin package ID**: `PepperDash.Essentials.Plugins.Samsung.TizenWebsocket`

## File Layout

Keep the main plugin files flat under `src/`, matching the existing Samsung template and the LG display reference style. Add subfolders only when a distinct variant or isolated feature genuinely needs one.

```
src/
├── SamsungTizenWebsocketController.cs
├── SamsungTizenWebsocketFactory.cs
├── SamsungTizenWebsocketPropertiesConfig.cs
├── SamsungTizenWebsocketBridgeJoinMap.cs
├── SamsungTizenWebsocketProtocolBridge.cs
├── SamsungTizenWebsocketProtocolMessages.cs
└── SamsungTizenWebsocketAuthentication.cs
```

## Configuration Patterns

Config JSON structure (appsettings-style):

```json
{
  "device": {
    "key": "samsung-qn990-display-01",
    "name": "Lobby Display",
    "type": "SamsungTizenWebsocket",
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
      "discoveryMode": "static|mdns|upnp",
      "firmwareVersion": "optional-constraint",
      "pollIntervalMs": 5000,
      "reconnectDelayMs": 2000
    }
  }
}
```

## Testing Patterns

- **Unit tests** target protocol message parsing and command serialization
- **Integration tests** mock WebSocket connections with canned responses
- **Smoke tests** verify device lifecycle: connect, send command, receive feedback, disconnect
- Test naming: `Test_MethodUnderTest_InputCondition_ExpectedOutcome`
- Add tests in a separate test project when test dependencies are introduced; do not place xUnit tests inside the main plugin library by default

## Build & Package

- NuGet package auto-generates on `dotnet build`
- Update `PackageId`, `PackageProjectUrl`, and `AssemblyTitle` in `.csproj` for each release
- Semantic versioning: `MAJOR.MINOR.PATCH-LABEL` (e.g., `1.0.0-beta1`, `1.2.3`)
