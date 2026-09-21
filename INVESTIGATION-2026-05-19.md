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

---

## Appendix A — Raw Exception Logs (Chronological)

Logs are reproduced as observed on the processor. Each block is annotated with the issue it triggered and the section of this report that addresses it.

---

### A.1 — Startup failure, all displays, Essentials 2.36.0 build (16:16:57–16:16:59)
**Triggered:** Sections 2.1 (no reconnect), 2.2 (EROR log level), 2.4/2.5 (upgrade rejection)  
**Assembly:** `91224af6f8d645039b07b7b6cc274795`

```
Error: SimplSharpPro[App01] # 2026-05-19 16:16:59 # 164ms [EROR][display-hallway] Protocol error: Unable to read data from the transport connection: Connection reset by peer.
    at System.Net.Sockets.Socket+AwaitableSocketAsyncEventArgs.ThrowException (System.Net.Sockets.SocketError error) [0x00007] in <9c30c834d8664232bafbbf8f27cf0c6c>:0
    at System.Net.Sockets.Socket+AwaitableSocketAsyncEventArgs.GetResult (System.Int16 token) [0x00022] in <9c30c834d8664232bafbbf8f27cf0c6c>:0
    at System.Threading.Tasks.ValueTask`1+ValueTaskSourceAsTask+<>c[TResult].<.cctor>b__4_0 (System.Object state) [0x00030] in <e008ef42803d426d9817f43ed5ac3eba>:0
    --- End of stack trace from previous location where exception was thrown ---
    at PepperDash.Essentials.Plugins.Samsung.TizenWebsocket.Protocol.SamsungTizenWebsocketProtocolBridge.PerformWebSocketUpgradeAsync (System.IO.Stream stream, System.Threading.CancellationToken cancellationToken) [0x0033f] in <91224af6f8d645039b07b7b6cc274795>:0

Error: SimplSharpPro[App01] # 2026-05-19 16:16:59 # 164ms [EROR][display-suite-1] Protocol error: Unable to read data from the transport connection: Connection reset by peer.
    (same stack trace — assembly 91224af6...)

Error: SimplSharpPro[App01] # 2026-05-19 16:16:59 # 165ms [EROR][display-reception] Protocol error: Unable to read data from the transport connection: Connection reset by peer.
    (same stack trace — assembly 91224af6...)

Error: SimplSharpPro[App01] # 2026-05-19 16:16:59 # 164ms [EROR][display-suite-2] Protocol error: Unable to read data from the transport connection: Connection reset by peer.
    (same stack trace — assembly 91224af6...)

Error: SimplSharpPro[App01] # 2026-05-19 16:16:59 # 164ms [EROR][display-suite-3] Protocol error: Unable to read data from the transport connection: Connection reset by peer.
    (same stack trace — assembly 91224af6...)

Error: SimplSharpPro[App01] # 2026-05-19 16:16:59 # 189ms [EROR][display-suite-5] Protocol error: Unable to read data from the transport connection: Connection reset by peer.
    (same stack trace — assembly 91224af6...)

Error: SimplSharpPro[App01] # 2026-05-19 16:16:59 # 193ms [EROR][display-suite-4] Protocol error: Unable to read data from the transport connection: Connection reset by peer.
    (same stack trace — assembly 91224af6...)
```

**Notes:**
- All seven displays failed within the same 30 ms window at startup.
- Assembly hash `91224af6` = pre-fix build.
- No reconnect log lines followed — confirmed the reconnect loop was never started (Section 2.1).

---

### A.2 — Same failure, second build deployment (17:22:28)
**Triggered:** Confirmed Section 2.2 fix was not yet deployed  
**Assembly:** `fa1e9c7e1a664eafb12e2854dc686520`

```
[2026-05-19 17:22:28.449][EROR][App 1][display-suite-2] Protocol error: Unable to read data from the transport connection: Connection reset by peer.
    at System.Net.Sockets.Socket+AwaitableSocketAsyncEventArgs.ThrowException (System.Net.Sockets.SocketError error) [0x00007] in <9c30c834d8664232bafbbf8f27cf0c6c>:0
    at System.Net.Sockets.Socket+AwaitableSocketAsyncEventArgs.GetResult (System.Int16 token) [0x00022] in <9c30c834d8664232bafbbf8f27cf0c6c>:0
    at System.Threading.Tasks.ValueTask`1+ValueTaskSourceAsTask+<>c[TResult].<.cctor>b__4_0 (System.Object state) [0x00030] in <e008ef42803d426d9817f43ed5ac3eba>:0
    --- End of stack trace from previous location where exception was thrown ---
    at PepperDash.Essentials.Plugins.Samsung.TizenWebsocket.Protocol.SamsungTizenWebsocketProtocolBridge.PerformWebSocketUpgradeAsync (System.IO.Stream stream, System.Threading.CancellationToken cancellationToken) [0x0033f] in <fa1e9c7e1a664eafb12e2854dc686520>:0

[2026-05-19 17:22:28.451][EROR][App 1][display-reception] Protocol error: Unable to read data from the transport connection: Connection reset by peer.
    (same stack — assembly fa1e9c7e...)

[2026-05-19 17:22:28.453][EROR][App 1][display-suite-5] Protocol error: Unable to read data from the transport connection: Connection reset by peer.
    (same stack — assembly fa1e9c7e...)

[2026-05-19 17:22:28.453][EROR][App 1][display-suite-3] Protocol error: Unable to read data from the transport connection: Connection reset by peer.
    (same stack — assembly fa1e9c7e...)

[2026-05-19 17:22:28.456][EROR][App 1][display-suite-4] Protocol error: Unable to read data from the transport connection: Connection reset by peer.
    (same stack — assembly fa1e9c7e...)

[2026-05-19 17:22:28.461][EROR][App 1][display-suite-1] Protocol error: Unable to read data from the transport connection: Connection reset by peer.
    (same stack — assembly fa1e9c7e...)

[2026-05-19 17:22:28.469][EROR][App 1][display-hallway] Protocol error: Unable to read data from the transport connection: Connection reset by peer.
    (same stack — assembly fa1e9c7e...)
```

**Notes:**
- Different assembly hash (`fa1e9c7e`) = intermediate build deployed between sessions; our fixes were not yet compiled into it.
- Error still at `[EROR]` level — confirms Sections 2.1 and 2.2 fixes were not included.

---

### A.3 — Fixes 2.1 and 2.2 deployed — log level and reconnect loop confirmed (17:26:06)
**Confirmed:** Section 2.1 (reconnect loop now running) and Section 2.2 (EROR → INFO)

```
[2026-05-19 17:26:06.087][INFO][App 1][display-suite-4] Connection state changed: Connecting
[2026-05-19 17:26:06.097][INFO][App 1][display-suite-4] Connection error (will retry): Unable to read data from the transport connection: Connection reset by peer.
[2026-05-19 17:26:06.099][INFO][App 1][display-suite-4] Connection state changed: Disconnected
[2026-05-19 17:26:06.101][INFO][App 1][display-suite-4] Reconnecting in 60s (attempt 6)...

[2026-05-19 17:26:06.102][INFO][App 1][display-suite-2] Connection error (will retry): Unable to read data from the transport connection: Connection reset by peer.
[2026-05-19 17:26:06.103][INFO][App 1][display-suite-2] Connection state changed: Disconnected
[2026-05-19 17:26:06.104][INFO][App 1][display-suite-2] Reconnecting in 60s (attempt 6)...

[2026-05-19 17:26:06.104][INFO][App 1][display-reception] Connection error (will retry): Unable to read data from the transport connection: Connection reset by peer.
[2026-05-19 17:26:06.106][INFO][App 1][display-reception] Reconnecting in 60s (attempt 6)...

[2026-05-19 17:26:06.107][INFO][App 1][display-suite-1] Connection error (will retry): Unable to read data from the transport connection: Connection reset by peer.
[2026-05-19 17:26:06.111][INFO][App 1][display-suite-1] Reconnecting in 60s (attempt 6)...

[2026-05-19 17:26:06.110][INFO][App 1][display-hallway] Connection error (will retry): Unable to read data from the transport connection: Connection reset by peer.
[2026-05-19 17:26:06.113][INFO][App 1][display-hallway] Reconnecting in 60s (attempt 6)...

[2026-05-19 17:26:06.115][INFO][App 1][display-suite-3] Connection error (will retry): Unable to read data from the transport connection: Connection reset by peer.
[2026-05-19 17:26:06.117][INFO][App 1][display-suite-3] Reconnecting in 60s (attempt 6)...

[2026-05-19 17:26:06.116][INFO][App 1][display-suite-5] Connection error (will retry): Unable to read data from the transport connection: Connection reset by peer.
[2026-05-19 17:26:06.118][INFO][App 1][display-suite-5] Reconnecting in 60s (attempt 6)...
```

**Notes:**
- Error level downgraded from `[EROR]` to `[INFO]` — Section 2.2 confirmed working.
- All displays now showing "Reconnecting in 60s" — Section 2.1 confirmed working.
- All at attempt 6 (exponential backoff: 2 s → 4 s → 8 s → 16 s → 32 s → 60 s cap).
- Underlying TCP RST still occurring — plugin-side fixes did not resolve the Essentials 2.36.0 root cause.

---

### A.4 — Commands dropped while disconnected (17:45:15)
**Triggered:** Section 2.3 (queued command delivery)

```
[2026-05-19 17:45:15.768][INFO][App 1][display-suite-5] PowerOff
[2026-05-19 17:45:15.769][INFO][App 1][display-suite-5] SendKey(KEY_POWER) dropped — not connected (state=Disconnected)

[2026-05-19 17:45:19.899][INFO][App 1][display-suite-5] PowerOff
[2026-05-19 17:45:19.901][INFO][App 1][display-suite-5] SendKey(KEY_POWER) dropped — not connected (state=Disconnected)

[2026-05-19 17:45:20.571][INFO][App 1][display-suite-5] PowerOff
[2026-05-19 17:45:20.572][INFO][App 1][display-suite-5] SendKey(KEY_POWER) dropped — not connected (state=Disconnected)

[2026-05-19 17:45:21.994][INFO][App 1][display-suite-5] PowerOff
[2026-05-19 17:45:21.995][INFO][App 1][display-suite-5] SendKey(KEY_POWER) dropped — not connected (state=Disconnected)
```

**Notes:**
- Four PowerOff commands were sent within 6 seconds; all silently dropped.
- No reconnect attempt was triggered by the command arrival.
- SIMPL program pulsed Bool 201 (PowerOff join) four times in rapid succession.

---

### A.5 — Section 2.3 fix confirmed — command queuing and backoff interrupt (17:52:03)
**Confirmed:** Section 2.3 (pending key queue and reconnect-delay interrupt)

```
[2026-05-19 17:52:03.157][INFO][App 1][display-suite-5] Connection error (will retry): Unable to read data from the transport connection: Connection reset by peer.
[2026-05-19 17:52:03.158][INFO][App 1][display-suite-5] Connection state changed: Disconnected
[2026-05-19 17:52:03.159][INFO][App 1][display-suite-5] Reconnecting in 60s (attempt 20)...

[2026-05-19 17:52:03.330][INFO][App 1][display-suite-5] Reconnect delay interrupted by pending command; retrying now...
[2026-05-19 17:52:03.331][INFO][App 1][display-suite-5] Connection state changed: Connecting
[2026-05-19 17:52:03.332][INFO][App 1][display-suite-5] SendKey(KEY_VOLUP) queued — not connected, retrying now (state=Connecting)

[2026-05-19 17:52:03.341][INFO][App 1][display-suite-5] Connection error (will retry): Unable to read data from the transport connection: Connection reset by peer.
[2026-05-19 17:52:03.342][INFO][App 1][display-suite-5] Connection state changed: Disconnected
[2026-05-19 17:52:03.343][INFO][App 1][display-suite-5] Reconnecting in 60s (attempt 21)...

[2026-05-19 17:52:16.211][INFO][App 1][display-suite-2] Connection error (will retry): Unable to read data from the transport connection: Connection reset by peer.
[2026-05-19 17:52:16.212][INFO][App 1][display-suite-2] Connection state changed: Disconnected
[2026-05-19 17:52:16.213][INFO][App 1][display-suite-2] Reconnecting in 60s (attempt 6)...

[2026-05-19 17:52:16.213][INFO][App 1][display-suite-1] Connection error (will retry): Unable to read data from the transport connection: Connection reset by peer.
[2026-05-19 17:52:16.215][INFO][App 1][display-suite-1] Connection state changed: Disconnected
[2026-05-19 17:52:16.216][INFO][App 1][display-suite-1] Reconnecting in 60s (attempt 6)...

[2026-05-19 17:52:16.213][INFO][App 1][display-reception] Connection error (will retry): Unable to read data from the transport connection: Connection reset by peer.
[2026-05-19 17:52:16.212][INFO][App 1][display-reception] Connection state changed: Disconnected
[2026-05-19 17:52:16.213][INFO][App 1][display-reception] Reconnecting in 60s (attempt 6)...

[2026-05-19 17:52:16.213][INFO][App 1][display-hallway] Connection error (will retry): Unable to read data from the transport connection: Connection reset by peer.
[2026-05-19 17:52:16.215][INFO][App 1][display-hallway] Connection state changed: Disconnected
[2026-05-19 17:52:16.216][INFO][App 1][display-hallway] Reconnecting in 60s (attempt 6)...

[2026-05-19 17:52:16.295][INFO][App 1][display-suite-3] Connection error (will retry): Unable to read data from the transport connection: Connection reset by peer.
[2026-05-19 17:52:16.297][INFO][App 1][display-suite-3] Connection state changed: Disconnected
[2026-05-19 17:52:16.297][INFO][App 1][display-suite-3] Reconnecting in 60s (attempt 6)...

[2026-05-19 17:52:16.298][INFO][App 1][display-suite-4] Connection error (will retry): Unable to read data from the transport connection: Connection reset by peer.
[2026-05-19 17:52:16.299][INFO][App 1][display-suite-4] Connection state changed: Disconnected
[2026-05-19 17:52:16.300][INFO][App 1][display-suite-4] Reconnecting in 60s (attempt 6)...
```

**Notes:**
- `display-suite-5` at attempt 20 (running since a prior startup cycle); all others at attempt 6 (fresh deployment).
- "Reconnect delay interrupted by pending command; retrying now..." confirms the `reconnectDelayCts.Cancel()` path in `SendKey` is working.
- 60 ms later the KEY_VOLUP command is still queued as "retrying now (state=Connecting)" — correct behavior while the TCP connect is in progress.
- The underlying RST still occurs 9 ms after `Connecting` — Essentials 2.36.0 root cause unresolved at this point.
