# Development Links

This file tracks links referenced for Samsung QNxx-QN990FFXZA Essentials plugin planning and development.

## Core References (User Requested)
1. PepperDash Essentials repository
- https://github.com/PepperDash/Essentials
2. PepperDash LG display plugin (reference implementation style)
- https://github.com/PepperDash/epi-lg-display

## Existing Repository References (Discovered)
1. Essentials Plugins wiki page (architecture and plugin guidance)
- https://pepperdash.github.io/Essentials/docs/Plugins.html
2. NuGet CLI download (dependency/install tooling)
- https://dist.nuget.org/win-x86-commandline/latest/nuget.exe

## Samsung API References
1. Samsung Smart TV remote-control guide
- https://developer.samsung.com/smarttv/develop/guides/user-interaction/remote-control.html
2. Community Samsung TV WebSocket API reference
- https://github.com/xchwarze/samsung-tv-ws-api
3. Home Assistant Samsung TV integration
- https://github.com/home-assistant/core/tree/dev/homeassistant/components/samsungtv

## Implementation Notes
- Consumer `samsung.remote.control` appears reliable for key injection, app list requests, app launch, text/IME, and session events.
- Do not assume support for explicit getters like `getPower`, `getVolume`, or `getSource` on this channel.
- Prefer observed unsolicited events when available. If hard two-way telemetry is required, validate REST `/api/v2/` power reporting and UPnP RenderingControl volume/mute support on the target firmware.

## Deferred Work (Requires Physical QN990 Display)

### #2 — Live WebSocket Traffic Capture
Capture and log raw WebSocket frames from a real QN990 during:
- Power on/off cycle
- Volume up/down
- Mute toggle
- Source switch (HDMI 1 → HDMI 2)

Goal: Discover any model-specific unsolicited events emitted by the display so that optimistic feedback fields can be promoted to event-driven if the display does emit them. Update `TryApplyPower`, `TryApplyMute`, `TryApplyVolume`, and `TryApplySource` with confirmed event names and payload field paths.

### #3 — Optional `useOptimisticFeedback` Config Flag
If live testing shows the display reliably emits source/mute/volume events, consider adding a `UseOptimisticFeedback` config bool to `SamsungTizenWebsocketPropertiesConfig` that gates whether `SendInputCommand`, `MuteToggle`, and `VolumeUp/Down` set local state immediately or wait for a confirmed event. Default should remain `true` (optimistic on) for usability when events are absent.
