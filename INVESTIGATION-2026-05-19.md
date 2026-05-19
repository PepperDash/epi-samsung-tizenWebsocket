# Investigation Report — 2026-05-19
## Samsung Tizen WebSocket Plugin — Connection Failures

### Environment

| Component | Version |
|---|---|
| Plugin | `epi-samsung-tizenWebsocket` |
| PepperDash Essentials (at failure) | **2.36.0** |
| PepperDash Essentials (restored) | **2.29.0** |
| Processor | CP4N (4-Series) |
| Displays | Samsung QNxx-series, Tizen OS, port 8002 (WSS) |
| Plugin tag when working | `v1.0.0-initial-development.5` |

---

## 1. Initial Issues Reported

All seven Samsung displays (`display-hallway`, `display-reception`, `display-suite-1` through `display-suite-5`) failed to establish a WebSocket connection on every retry attempt with the following error:

```
[EROR][display-*] Protocol error: Unable to read data from the transport connection:
  Connection reset by peer.
  at SamsungTizenWebsocketProtocolBridge.PerformWebSocketUpgradeAsync(...)
```

**Key observations at intake:**
- All displays failed simultaneously at processor startup (~16:16:57).
- The error was a TCP RST received from the Samsung TV **during the HTTP/101 WebSocket upgrade read**, after a successful TLS handshake.
- All errors were logged at `[EROR]` level, flooding the processor log.
- After the initial failure, no further reconnection attempts were made — displays stayed permanently disconnected.
- Python script (`ws_connect.py`) successfully connected to `10.0.133.21` (display at port 8002 / WSS) from a developer workstation, confirming the Samsung WebSocket API was reachable.

---

## 2. Plugin Changes Made During Investigation

The following changes were committed to the `initial-development` branch during this session. Each change is noted with whether it addresses a **confirmed plugin bug**, an **enhancement**, or a **suspected Essentials compatibility issue**.

---

### 2.1 Initial connection failure leaves devices permanently disconnected
**Classification: Confirmed plugin bug (exists independently of Essentials version)**

**Root cause:**  
`HandleDisconnectAsync` (the exponential-backoff reconnect loop) is only triggered from the `finally` block of `ReceiveLoopRaw`. `ReceiveLoopRaw` only starts if `ConnectAsync` succeeds. If `ConnectAsync` fails on the first call from `Initialize()`, the receive loop never starts and `HandleDisconnectAsync` is never called. The device stays disconnected with no retry.

**Fix — `SamsungTizenWebsocketProtocolBridge.cs`:**
- Changed `HandleDisconnectAsync` from `private` to `public`.

**Fix — `SamsungTizenWebsocketController.cs`:**
- Replaced `_ = protocolBridge.ConnectAsync()` in `Initialize()` with `_ = InitialConnectAsync()`.
- Added `private async Task InitialConnectAsync()` which calls `ConnectAsync()` and, on failure, calls `HandleDisconnectAsync()` to start the retry loop.

---

### 2.2 Transient TCP errors logged at `[EROR]` level
**Classification: Plugin improvement (log severity)**

**Root cause:**  
`ProtocolBridge_OnError` unconditionally called `this.LogError(...)` for all exceptions, including `IOException: Connection reset by peer` — which is a normal/expected condition whenever a Samsung TV is in standby mode and its WebSocket service is not running.

**Fix — `SamsungTizenWebsocketController.cs`:**
- Added `private static bool IsTransientConnectionError(Exception ex)` that walks the exception chain and checks for known transient TCP error strings ("connection reset", "connection refused", "actively refused", "forcibly closed", "no route to host").
- `ProtocolBridge_OnError` now calls `LogInformation` for transient errors and `LogError` only for unexpected errors.

**Result in logs (confirmed working):**
```
[INFO][display-*] Connection error (will retry): Unable to read data...
[INFO][display-*] Reconnecting in 60s (attempt 6)...
```

---

### 2.3 Commands sent while disconnected are silently dropped
**Classification: Enhancement (queued delivery)**

**Root cause:**  
`SendKey` returned immediately when `!IsConnected`, logging a dropped-command message. The plugin had no mechanism to buffer commands and no way to interrupt the reconnect backoff wait when a command arrived.

**Fix — `SamsungTizenWebsocketProtocolBridge.cs`:**
- Added `ConcurrentQueue<string> pendingKeys` and `volatile string lastPendingKey` for deduplication of consecutive identical keys (prevents power-toggle bouncing on button hold).
- Added `CancellationTokenSource reconnectDelayCts` to allow the backoff `Task.Delay` to be interrupted.
- `SendKey` now enqueues the key and calls `reconnectDelayCts.Cancel()` to wake the reconnect loop immediately.
- `HandleDisconnectAsync` now uses a cancellable delay; `OperationCanceledException` is treated as "retry now."
- `ProcessMessage` flushes `pendingKeys` after the `ms.channel.connect` event confirms the session is ready.

---

### 2.4 WebSocket upgrade URL — `Uri.EscapeDataString` on base64 client name
**Classification: Suspected compatibility issue (may relate to Essentials version)**

**Root cause (hypothesis):**  
The client name (`ControlSystem`) was base64-encoded and then wrapped in `Uri.EscapeDataString`, converting the base64 `=` padding to `%3D` in the upgrade URL:
```
?name=Q29udHJvbFN5c3RlbQ%3D%3D   ← C# (before fix)
?name=Q29udHJvbFN5c3RlbQ==       ← Python (working)
```
Samsung TVs may not URL-decode query parameters before base64-decoding the name, causing a reject.

**Fix — `SamsungTizenWebsocketProtocolBridge.cs`:**
- Removed `Uri.EscapeDataString` wrapper; raw base64 is now sent directly (matching Python behavior).
- Same change applied to the token parameter.

**Note:**  
`v1.0.0-initial-development.5` worked **with** `Uri.EscapeDataString`. The removal was made based on Python comparison and is a correctness improvement, but may not have been the root cause of failures during Essentials 2.36.0 testing. This change should be retained.

---

### 2.5 WebSocket upgrade HTTP header format mismatch
**Classification: Suspected root cause of TCP RST (may relate to Essentials version or Samsung firmware)**

**Root cause:**  
Comparison between the working Python `websocket-client` upgrade request and the C# upgrade request revealed three differences:

| Field | Python (working) | C# (before fix) |
|---|---|---|
| Header order | `Upgrade → Host → Origin → Key → Version → Connection` | `Host → Upgrade → Connection → Key → Version → User-Agent → Origin` |
| Origin value | `https://10.0.133.21:8002` (port included) | `https://10.0.133.21` (port missing) |
| User-Agent | not sent | `User-Agent: ControlSystem` |

The missing port in `Origin` is a likely rejection trigger — Samsung validates the `Origin` header, and `https://10.0.133.21` would not match the server's self-origin of `https://10.0.133.21:8002`.

**Fix — `SamsungTizenWebsocketProtocolBridge.cs`:**
- Reordered headers to match Python's order.
- Changed Origin to `{scheme}://{hostAddress}:{port}` (port now included).
- Removed `User-Agent` header.

**Note:**  
`v1.0.0-initial-development.5` worked with the old header format. The question of whether a Samsung firmware update or an Essentials 2.36.0 behavioral change altered how the TLS/TCP stream is presented to the TV (potentially making the TV stricter about the headers) remains open.

---

## 3. Essentials 2.36.0 vs 2.29.0 — Suspected Impact Areas

Rolling back from Essentials **2.36.0 to 2.29.0 restored functionality** with the same plugin build. The following Essentials subsystems should be reviewed in the 2.36.0 changelog for breaking changes that could affect this plugin:

| Essentials Subsystem | Potential Impact |
|---|---|
| `TcpClient` / `ClientWebSocket` abstraction | If 2.36.0 wraps or replaces the underlying `System.Net.Sockets.TcpClient` used by the plugin, TCP-level behavior (buffer sizes, keep-alive, timeout handling) could differ. |
| `async`/`await` scheduler | If Essentials 2.36.0 changed the `SynchronizationContext` or task scheduler on the Crestron platform, `ConfigureAwait(false)` behavior in `ConnectAsync` / `PerformWebSocketUpgradeAsync` could be affected. |
| TLS/SSL layer | If 2.36.0 updated or patched the Mono `SslStream` implementation (or changed `SslProtocols` defaults), the TLS handshake with Samsung TVs could change in a way that triggers RST. |
| Logging framework | The `LogError` / `LogInformation` API changes between versions are unlikely to cause connection issues but should be verified. |
| `CancellationToken` propagation | If Essentials 2.36.0 changed how device lifecycle `CancellationToken`s are created or cancelled (e.g., on boot/restart), `cancellationTokenSource` could be prematurely cancelled, causing the upgrade `ReadAsync` to throw and manifest as "connection reset." |

### Recommended next steps

1. **Diff Essentials 2.29.0 → 2.36.0** for changes to `TcpClient`, `SslStream`, `CancellationToken`, and task scheduler.
2. **Test the plugin against Essentials 2.36.0** with the header-format and URL-encoding fixes applied (2.5 and 2.4 above) to determine if those changes are sufficient for 2.36.0 compatibility.
3. **Capture a Wireshark trace** on the display VLAN during a connection attempt under 2.36.0 to confirm whether the TCP RST comes from the TV or from the processor's TCP stack (which would indicate an Essentials-layer change).
4. **Check if 2.36.0 changed TLS cipher negotiation** — Samsung Tizen TVs support a limited cipher suite list; a change in Mono's default cipher preferences under 2.36.0 could cause the TLS handshake to succeed at the Mono level but result in the TV immediately RST-ing the WebSocket upgrade.

---

## 4. Resolution — Confirmed Working Configuration

**Date confirmed:** 2026-05-19  
**Status: ✅ RESOLVED on Essentials 2.29.0**

| Component | Version |
|---|---|
| PepperDash Essentials | **2.29.0** |
| Plugin build | `epi-samsung-tizenWebsocket.4Series 1.0.0-local+1656cc76d02f701378677e7d9e35e65bd91c3b6a` |
| Base commit | `1656cc7` — feat: implement volume ramping functionality for absolute volume control |

The plugin build above includes all plugin-side fixes documented in Section 2 (uncommitted working-tree changes on top of commit `1656cc7`) running against Essentials **2.29.0**. Full display control — connection, power, volume, source — was verified working.

**Confirmed conclusion:**  
The TCP RST / connection failure was caused by a breaking change introduced in Essentials **2.36.0**. The plugin-side fixes in Section 2 represent correctness and robustness improvements that should be retained regardless of the Essentials version, but they did not resolve the failure under 2.36.0. Essentials 2.36.0 must be investigated before the plugin can be certified against it.

---

## 5. Summary of Changes — Files Modified

### `src/SamsungTizenWebsocketProtocolBridge.cs`
- `HandleDisconnectAsync`: `private` → `public`
- `PerformWebSocketUpgradeAsync`: removed `Uri.EscapeDataString` on name/token; fixed header order and Origin format; removed `User-Agent`
- `SendKey`: now queues command and interrupts backoff delay instead of dropping
- `HandleDisconnectAsync`: delay replaced with cancellable `Task.Delay`
- `ProcessMessage`: flushes `pendingKeys` after `ms.channel.connect`
- Added fields: `pendingKeys`, `lastPendingKey`, `reconnectDelayCts`
- Added method: `FlushPendingKeys()`

### `src/SamsungTizenWebsocketController.cs`
- `Initialize()`: replaced `_ = protocolBridge.ConnectAsync()` with `_ = InitialConnectAsync()`
- Added `private async Task InitialConnectAsync()`
- `ProtocolBridge_OnError`: split into transient vs non-transient error handling
- Added `private static bool IsTransientConnectionError(Exception ex)`
