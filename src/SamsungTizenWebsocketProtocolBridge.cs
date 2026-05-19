using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PepperDash.Essentials.Plugins.Samsung.TizenWebsocket.Protocol
{
    /// <summary>
    /// Manages WebSocket connection to Samsung display and handles protocol-level communication.
    /// Handles state machine, message serialization/deserialization, and response correlation.
    /// </summary>
    public class SamsungTizenWebsocketProtocolBridge : IDisposable
    {
        public event EventHandler<SamsungResponseMessage> OnResponseReceived;
        public event EventHandler<string> OnConnectionStateChanged;
        public event EventHandler<string> OnInfoMessage;
        public event EventHandler<string> OnVerboseMessage;
        public event EventHandler<Exception> OnError;

        private readonly string key;
        private readonly string hostAddress;
        private readonly int port;
        private readonly bool useSecureWebSocket;

        private TcpClient tcpClient;
        private Stream networkStream;
        private SslStream sslStream;
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource cancellationTokenSource;
        private Thread receiveThread;
        private bool isDisposed;
        private bool lastDisconnectWasUnauthorized;

        // State management
        private ConnectionState connectionState = ConnectionState.Disconnected;
        private int messageIdCounter = 1;
        private int reconnectAttempts = 0;
        private const int baseReconnectDelayMs = 2000;
        private const int maxReconnectDelayMs = 60000;
        private const int unauthorizedReconnectDelayMs = 15000;
        private volatile bool closeFrameReceived;
        private volatile bool reconnectEnabled = true;
        private const string clientName = "ControlSystem";
        private static readonly object tokenFileLock = new object();
        private static readonly string tokenFilePath;
        private static readonly string tokenDirectoryPath;
        private string token;

        static SamsungTizenWebsocketProtocolBridge()
        {
            var programNumber = Crestron.SimplSharp.InitialParametersClass.ApplicationNumber;
            tokenDirectoryPath = string.Format("\\user\\program{0}", programNumber);
            tokenFilePath = string.Format("{0}\\samsung-tokens.json", tokenDirectoryPath);
        }

        /// <summary>
        /// Samsung pairing token. Persisted to a shared file so config updates are not required
        /// when Samsung rotates the token.
        /// </summary>
        public string Token
        {
            get { return token; }
            set
            {
                if (string.Equals(token, value, StringComparison.Ordinal)) return;
                token = value;
                SaveTokenToFile();
            }
        }

        // Response correlation
        private readonly ConcurrentDictionary<int, TaskCompletionSource<SamsungResponseMessage>> pendingCommands
            = new ConcurrentDictionary<int, TaskCompletionSource<SamsungResponseMessage>>();

        public ConnectionState State => connectionState;
        public bool IsConnected => connectionState == ConnectionState.Connected || connectionState == ConnectionState.Ready;
        public bool IsReady => connectionState == ConnectionState.Ready;
        public string HostAddress => hostAddress;
        public int Port => port;

        public SamsungTizenWebsocketProtocolBridge(string key, string hostAddress, int port, bool useSecureWebSocket = true)
        {
            this.key = key ?? throw new ArgumentNullException(nameof(key));
            this.hostAddress = NormalizeHostAddress(hostAddress ?? throw new ArgumentNullException(nameof(hostAddress)));
            this.port = port;
            this.useSecureWebSocket = useSecureWebSocket;
        }

        /// <summary>
        /// Loads the token from the shared token file on the processor.
        /// Call once after construction, before ConnectAsync.
        /// </summary>
        public void LoadToken()
        {
            var loaded = LoadTokenFromFile();
            if (!string.IsNullOrEmpty(loaded))
            {
                token = loaded;
            }
        }

        private string LoadTokenFromFile()
        {
            try
            {
                lock (tokenFileLock)
                {
                    if (Crestron.SimplSharp.CrestronIO.File.Exists(tokenFilePath))
                    {
                        var contents = Crestron.SimplSharp.CrestronIO.File.ReadToEnd(tokenFilePath, Encoding.UTF8).Trim();
                        var jObj = JObject.Parse(contents);
                        return jObj[key]?.Value<string>();
                    }
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, new InvalidOperationException(string.Format("Failed to load token file: {0}", ex.Message), ex));
            }
            return null;
        }

        private void SaveTokenToFile()
        {
            try
            {
                if (string.IsNullOrEmpty(token)) return;

                lock (tokenFileLock)
                {
                    if (!Crestron.SimplSharp.CrestronIO.Directory.Exists(tokenDirectoryPath))
                    {
                        Crestron.SimplSharp.CrestronIO.Directory.Create(tokenDirectoryPath);
                    }

                    // Read existing tokens, merge, and write back
                    var jObj = new JObject();
                    if (Crestron.SimplSharp.CrestronIO.File.Exists(tokenFilePath))
                    {
                        var contents = Crestron.SimplSharp.CrestronIO.File.ReadToEnd(tokenFilePath, Encoding.UTF8).Trim();
                        jObj = JObject.Parse(contents);
                    }

                    jObj[key] = token;

                    using (var writer = new Crestron.SimplSharp.CrestronIO.StreamWriter(tokenFilePath, false))
                    {
                        writer.Write(jObj.ToString(Formatting.Indented));
                    }
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, new InvalidOperationException(string.Format("Failed to save token file: {0}", ex.Message), ex));
            }
        }

        /// <summary>
        /// Initiates connection to Samsung display using raw TCP + manual WebSocket upgrade.
        /// This approach bypasses ClientWebSocket compatibility issues on .NET Framework 4.7.2.
        /// </summary>
        public async Task<bool> ConnectAsync(int timeoutMs = 10000)
        {
            try
            {
                if (IsConnected)
                {
                    return false;
                }

                reconnectEnabled = true;
                SetConnectionState(ConnectionState.Connecting);

                // Use a short-lived CTS for TCP connect + upgrade only;
                // the long-lived CTS for the session is created after connect succeeds.
                using (var connectCts = new CancellationTokenSource(timeoutMs))
                {

                    // Establish TCP connection
                    tcpClient = new TcpClient();
                    await tcpClient.ConnectAsync(hostAddress, port).ConfigureAwait(false);

                    // Wrap with SSL/TLS if needed
                    networkStream = tcpClient.GetStream();
                    if (useSecureWebSocket)
                    {
                        sslStream = new SslStream(networkStream, false, (sender, cert, chain, errors) => true); // Accept self-signed
                        await sslStream.AuthenticateAsClientAsync(hostAddress, null, System.Security.Authentication.SslProtocols.Tls12, false).ConfigureAwait(false);
                        networkStream = sslStream;
                    }

                    // Perform WebSocket upgrade handshake
                    var upgradeResponse = await PerformWebSocketUpgradeAsync(networkStream, connectCts.Token).ConfigureAwait(false);
                    if (!upgradeResponse)
                    {
                        CleanupConnection();
                        SetConnectionState(ConnectionState.Disconnected);
                        return false;
                    }

                } // dispose connectCts — no longer needed

                // Create long-lived CTS for session lifetime (no timeout)
                cancellationTokenSource = new CancellationTokenSource();

                SetConnectionState(ConnectionState.Connected);
                closeFrameReceived = false;

                // Start receive thread BEFORE sending any messages
                receiveThread = new Thread(ReceiveLoopRaw) { IsBackground = true, Name = $"{key}-Receive" };
                receiveThread.Start();

                return true;
            }
            catch (IOException ioEx)
            {
                OnInfoMessage?.Invoke(this, string.Format("Connect failed (display may be off): {0}", ioEx.Message));
                CleanupConnection();
                SetConnectionState(ConnectionState.Disconnected);
                return false;
            }
            catch (SocketException sockEx)
            {
                OnInfoMessage?.Invoke(this, string.Format("Connect failed (network error {0}): {1}", sockEx.SocketErrorCode, sockEx.Message));
                CleanupConnection();
                SetConnectionState(ConnectionState.Disconnected);
                return false;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex);
                CleanupConnection();
                SetConnectionState(ConnectionState.Disconnected);
                return false;
            }
        }

        private async Task<bool> PerformWebSocketUpgradeAsync(Stream stream, CancellationToken cancellationToken)
        {
            try
            {
                // Generate WebSocket handshake key
                var guidBytes = Guid.NewGuid().ToByteArray();
                var keyBytes = new byte[16];
                Array.Copy(guidBytes, 0, keyBytes, 0, 16);
                var key = Convert.ToBase64String(keyBytes);
                var encodedClientName = Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(clientName)));

                // Build upgrade request — include token if we have one from a previous pairing
                var path = $"/api/v2/channels/samsung.remote.control?name={encodedClientName}";
                if (!string.IsNullOrEmpty(Token))
                {
                    path += $"&token={Uri.EscapeDataString(Token)}";
                }

                var upgradeRequest = $"GET {path} HTTP/1.1\r\n"
                    + $"Host: {hostAddress}:{port}\r\n"
                    + "Upgrade: websocket\r\n"
                    + "Connection: Upgrade\r\n"
                    + $"Sec-WebSocket-Key: {key}\r\n"
                    + "Sec-WebSocket-Version: 13\r\n"
                    + "User-Agent: ControlSystem\r\n"
                    + $"Origin: {(useSecureWebSocket ? "https" : "http")}://{hostAddress}\r\n"
                    + "\r\n";

                var requestBytes = Encoding.ASCII.GetBytes(upgradeRequest);
                await stream.WriteAsync(requestBytes, 0, requestBytes.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                // Read response
                var responseBuffer = new byte[4096];
                var bytesRead = await stream.ReadAsync(responseBuffer, 0, responseBuffer.Length, cancellationToken).ConfigureAwait(false);
                var responseText = Encoding.ASCII.GetString(responseBuffer, 0, bytesRead);

                // Check for 101 response
                if (responseText.Contains("101"))
                {
                    return true;
                }

                OnError?.Invoke(this, new InvalidOperationException(string.Format("WebSocket upgrade failed: {0}", responseText)));
                return false;
            }
            catch (IOException ioEx)
            {
                // Expected when the display is powered off or unreachable — log at info, not error.
                OnInfoMessage?.Invoke(this, string.Format("WebSocket upgrade failed (display may be off): {0}", ioEx.Message));
                return false;
            }
            catch (SocketException sockEx)
            {
                OnInfoMessage?.Invoke(this, string.Format("WebSocket upgrade failed (network error {0}): {1}", sockEx.SocketErrorCode, sockEx.Message));
                return false;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex);
                return false;
            }
        }

        /// <summary>
        /// Sends a command and waits for response (with timeout).
        /// </summary>
        public async Task<SamsungResponseMessage> SendCommandAsync(SamsungCommandMessage command, int timeoutMs = 5000)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Not connected to device");
            }

            try
            {
                // Register this command ID for response correlation
                var tcs = new TaskCompletionSource<SamsungResponseMessage>();
                pendingCommands.TryAdd(command.Id, tcs);

                // Send command as WebSocket frame
                var json = JsonConvert.SerializeObject(command, Formatting.None);
                await SendWebSocketFrameAsync(json, cancellationTokenSource.Token).ConfigureAwait(false);

                // Wait for response with timeout
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token))
                {
                    cts.CancelAfter(timeoutMs);
                    var response = await tcs.Task.ConfigureAwait(false);
                    return response;
                }
            }
            catch (OperationCanceledException)
            {
                pendingCommands.TryRemove(command.Id, out _);
                throw;
            }
            catch (Exception ex)
            {
                pendingCommands.TryRemove(command.Id, out _);
                OnError?.Invoke(this, ex);
                throw;
            }
        }

        /// <summary>
        /// Sends a command without waiting for response (fire-and-forget).
        /// Samsung remote control messages use method + params only (no id).
        /// </summary>
        public async Task SendCommandAsync(string method, string keyCode)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Not connected to device");
            }

            try
            {
                var payload = new JObject
                {
                    ["method"] = method,
                    ["params"] = new JObject
                    {
                        ["Cmd"] = "Click",
                        ["DataOfCmd"] = keyCode,
                        ["Option"] = "false",
                        ["TypeOfRemote"] = "SendRemoteKey"
                    }
                };

                var json = payload.ToString(Formatting.None);
                OnVerboseMessage?.Invoke(this, string.Format("TX: {0}", json));
                await SendWebSocketFrameAsync(json, cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex);
                throw;
            }
        }

        /// <summary>
        /// Sends a key-press command fire-and-forget. Safe to call from synchronous context.
        /// Returns immediately if not connected.
        /// </summary>
        public async void SendKey(string cmd)
        {
            if (!IsConnected)
            {
                OnInfoMessage?.Invoke(this, string.Format("SendKey({0}) dropped — not connected (state={1})", cmd, connectionState));
                return;
            }
            try
            {
                await SendCommandAsync(SamsungTizenCommands.RemoteControlMethod, cmd).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex);
            }
        }

        /// <summary>
        /// Closes WebSocket connection.
        /// </summary>
        public async Task DisconnectAsync()
        {
            try
            {
                reconnectEnabled = false;
                if (connectionState != ConnectionState.Disconnected)
                {
                    SetConnectionState(ConnectionState.Disconnecting);
                    CleanupConnection();
                    SetConnectionState(ConnectionState.Disconnected);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Disposes TCP/SSL/stream resources without changing connection state or reconnect flag.
        /// </summary>
        private void CleanupConnection()
        {
            try { cancellationTokenSource?.Cancel(); } catch { }
            try { cancellationTokenSource?.Dispose(); } catch { }
            cancellationTokenSource = null;

            try { sslStream?.Dispose(); } catch { }
            sslStream = null;

            // When secure, networkStream references sslStream (already disposed above)
            networkStream = null;

            try { tcpClient?.Dispose(); } catch { }
            tcpClient = null;
        }

        public void Dispose()
        {
            if (isDisposed) return;

            DisconnectAsync().GetAwaiter().GetResult();
            isDisposed = true;
            GC.SuppressFinalize(this);
        }

        // ============ Private Methods ============

        private static string NormalizeHostAddress(string address)
        {
            return address
                .Replace("https://", string.Empty)
                .Replace("http://", string.Empty)
                .Replace("wss://", string.Empty)
                .Replace("ws://", string.Empty)
                .TrimEnd('/');
        }

        private async Task SendWebSocketFrameAsync(string text, CancellationToken cancellationToken)
        {
            var data = Encoding.UTF8.GetBytes(text);
            await SendWebSocketFrameAsync(0x1, data, cancellationToken).ConfigureAwait(false);
        }

        private async Task SendWebSocketPongAsync(byte[] data, CancellationToken cancellationToken)
        {
            await SendWebSocketFrameAsync(0xA, data, cancellationToken).ConfigureAwait(false);
        }

        private async Task SendWebSocketFrameAsync(int opcode, byte[] data, CancellationToken cancellationToken)
        {
            if (networkStream == null || !networkStream.CanWrite)
                throw new InvalidOperationException("Network stream is not available");

            await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // WebSocket frame format (RFC 6455)
                // [0] FIN (1) | RSV (3) | Opcode (4)
                // [1] MASK (1) | Payload length (7) / 126 / 127
                // [2-3] or [2-7] Extended payload length (if needed)
                // [2-5] or [8-11] Masking key (4 bytes if MASK=1)
                // [rest] Payload data

                var frame = new List<byte>();

                // Byte 0: FIN=1, RSV=0, Opcode in low nibble
                frame.Add((byte)(0x80 | (opcode & 0x0F)));

                // Generate a random masking key (required for client → server)
                var maskingKey = new byte[4];
                using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
                {
                    rng.GetBytes(maskingKey);
                }

                int payloadLength = data.Length;

                // Byte 1: MASK=1, payload length encoding
                if (payloadLength < 126)
                {
                    frame.Add((byte)(0x80 | payloadLength)); // MASK=1, length
                }
                else if (payloadLength < 65536)
                {
                    frame.Add(0xFE); // MASK=1, 126 (16-bit length follows)
                    frame.Add((byte)(payloadLength >> 8));
                    frame.Add((byte)(payloadLength & 0xFF));
                }
                else
                {
                    frame.Add(0xFF); // MASK=1, 127 (64-bit length follows)
                    var lengthBytes = BitConverter.GetBytes((ulong)payloadLength);
                    if (BitConverter.IsLittleEndian)
                        Array.Reverse(lengthBytes);
                    frame.AddRange(lengthBytes);
                }

                // Add masking key
                frame.AddRange(maskingKey);

                // Mask the payload and add to frame
                var maskedData = new byte[data.Length];
                for (int i = 0; i < data.Length; i++)
                {
                    maskedData[i] = (byte)(data[i] ^ maskingKey[i % 4]);
                }
                frame.AddRange(maskedData);

                await networkStream.WriteAsync(frame.ToArray(), 0, frame.Count, cancellationToken).ConfigureAwait(false);
                await networkStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
        }

        private void ReceiveLoopRaw()
        {
            try
            {
                var buffer = new byte[4096];

                while (!cancellationTokenSource.Token.IsCancellationRequested && networkStream?.CanRead == true)
                {
                    try
                    {
                        int bytesRead = networkStream.Read(buffer, 0, buffer.Length);
                        if (bytesRead <= 0)
                        {
                            OnError?.Invoke(this, new InvalidOperationException("Receive loop: remote closed the TCP stream (0 bytes read)"));
                            break;
                        }

                        // Parse WebSocket frame
                        var frames = ParseWebSocketFrames(buffer, bytesRead);
                        foreach (var frameData in frames)
                        {
                            if (!string.IsNullOrEmpty(frameData))
                            {
                                OnVerboseMessage?.Invoke(this, string.Format("RX: {0}", frameData.Length > 512 ? frameData.Substring(0, 512) : frameData));
                                ProcessMessage(frameData);
                            }
                        }

                        // Server sent a close frame — exit receive loop without reading again
                        if (closeFrameReceived)
                            break;
                    }
                    catch (OperationCanceledException)
                    {
                        OnError?.Invoke(this, new InvalidOperationException("Receive loop: cancelled"));
                        break;
                    }
                    catch (IOException ioEx)
                    {
                        OnError?.Invoke(this, new InvalidOperationException(string.Format("Receive loop IO error: {0}", ioEx.Message), ioEx));
                        break;
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke(this, new InvalidOperationException(string.Format("Receive loop error: {0}", ex.Message), ex));
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex);
            }
            finally
            {
                _ = HandleDisconnectAsync();
            }
        }

        private List<string> ParseWebSocketFrames(byte[] buffer, int length)
        {
            var frames = new List<string>();
            int offset = 0;

            while (offset < length)
            {
                if (offset + 2 > length)
                    break;

                // Parse frame header
                byte b0 = buffer[offset];
                byte b1 = buffer[offset + 1];

                bool fin = (b0 & 0x80) != 0;
                int opcode = b0 & 0x0F;
                bool masked = (b1 & 0x80) != 0;
                int payloadLen = b1 & 0x7F;

                // Check for close frame (opcode 0x08)
                if (opcode == 0x08)
                {
                    closeFrameReceived = true;
                    // Attempt to decode close status/reason for diagnostics.
                    try
                    {
                        int frameHeaderLenForClose = 2;
                        int closePayloadLen = payloadLen;

                        if (payloadLen == 126)
                        {
                            if (offset + 4 <= length)
                            {
                                closePayloadLen = (buffer[offset + 2] << 8) | buffer[offset + 3];
                                frameHeaderLenForClose = 4;
                            }
                        }

                        var closePayloadStart = offset + frameHeaderLenForClose + (masked ? 4 : 0);
                        if (closePayloadStart + closePayloadLen <= length && closePayloadLen >= 2)
                        {
                            byte[] closePayloadBytes = new byte[closePayloadLen];
                            if (masked)
                            {
                                byte[] maskingKey = new byte[4];
                                Array.Copy(buffer, offset + frameHeaderLenForClose, maskingKey, 0, 4);
                                for (int i = 0; i < closePayloadLen; i++)
                                {
                                    closePayloadBytes[i] = (byte)(buffer[closePayloadStart + i] ^ maskingKey[i % 4]);
                                }
                            }
                            else
                            {
                                Array.Copy(buffer, closePayloadStart, closePayloadBytes, 0, closePayloadLen);
                            }

                            var closeCode = (closePayloadBytes[0] << 8) | closePayloadBytes[1];
                            var closeReason = closePayloadLen > 2 ? Encoding.UTF8.GetString(closePayloadBytes, 2, closePayloadLen - 2) : string.Empty;

                            // 'notack' = display rejected or is powered off — expected, log at info level
                            if (string.Equals(closeReason, "notack", StringComparison.OrdinalIgnoreCase))
                            {
                                OnInfoMessage?.Invoke(this, string.Format("Display closed connection (code={0}, reason='{1}')", closeCode, closeReason));
                            }
                            else
                            {
                                OnError?.Invoke(this, new InvalidOperationException(string.Format("Server closed websocket: code={0}, reason='{1}'", closeCode, closeReason)));
                            }
                        }
                        else
                        {
                            OnError?.Invoke(this, new InvalidOperationException("Server closed websocket without close details."));
                        }
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke(this, ex);
                    }

                    break;
                }

                int frameHeaderLen = 2;
                int totalFrameLen = frameHeaderLen;

                // Read extended payload length
                if (payloadLen == 126)
                {
                    if (offset + 4 > length)
                        break;

                    payloadLen = (buffer[offset + 2] << 8) | buffer[offset + 3];
                    frameHeaderLen = 4;
                }
                else if (payloadLen == 127)
                {
                    if (offset + 10 > length)
                        break;

                    ulong extendedLen =
                        ((ulong)buffer[offset + 2] << 56) |
                        ((ulong)buffer[offset + 3] << 48) |
                        ((ulong)buffer[offset + 4] << 40) |
                        ((ulong)buffer[offset + 5] << 32) |
                        ((ulong)buffer[offset + 6] << 24) |
                        ((ulong)buffer[offset + 7] << 16) |
                        ((ulong)buffer[offset + 8] << 8) |
                        buffer[offset + 9];
                    payloadLen = extendedLen > int.MaxValue ? int.MaxValue : (int)extendedLen;
                    frameHeaderLen = 10;
                }

                // Account for masking key (4 bytes if masked)
                if (masked)
                {
                    totalFrameLen = frameHeaderLen + 4 + payloadLen;
                }
                else
                {
                    totalFrameLen = frameHeaderLen + payloadLen;
                }

                if (offset + totalFrameLen > length)
                    break;

                // Extract payload
                int payloadStart = offset + frameHeaderLen;
                byte[] payloadBytes;

                if (masked)
                {
                    payloadStart += 4;
                    byte[] maskingKey = new byte[4];
                    Array.Copy(buffer, offset + frameHeaderLen, maskingKey, 0, 4);

                    payloadBytes = new byte[payloadLen];
                    for (int i = 0; i < payloadLen; i++)
                    {
                        payloadBytes[i] = (byte)(buffer[payloadStart + i] ^ maskingKey[i % 4]);
                    }
                }
                else
                {
                    payloadBytes = new byte[payloadLen];
                    Array.Copy(buffer, payloadStart, payloadBytes, 0, payloadLen);
                }

                if (opcode == 0x1) // Text frame
                {
                    frames.Add(Encoding.UTF8.GetString(payloadBytes));
                }
                else if (opcode == 0x9) // Ping frame; respond with pong and same payload
                {
                    try
                    {
                        SendWebSocketPongAsync(payloadBytes, cancellationTokenSource.Token).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke(this, ex);
                    }
                }

                offset += totalFrameLen;
            }

            return frames;
        }

        private void ProcessMessage(string json)
        {
            try
            {
                // Check for Samsung channel events first
                var jObj = JObject.Parse(json);
                var eventName = jObj["event"]?.Value<string>();

                if (eventName == "ms.channel.connect")
                {
                    // Extract and store the pairing token
                    var data = jObj["data"] as JObject;
                    var token = data?["token"]?.Value<string>();
                    if (!string.IsNullOrEmpty(token))
                    {
                        Token = token;
                        OnInfoMessage?.Invoke(this, string.Format("Samsung pairing token received: {0}", token));
                    }
                    SetConnectionState(ConnectionState.Ready);
                    lastDisconnectWasUnauthorized = false;
                    reconnectAttempts = 0;
                    OnResponseReceived?.Invoke(this, new SamsungResponseMessage { Event = eventName });
                    return;
                }

                if (eventName == "ms.channel.unauthorized")
                {
                    lastDisconnectWasUnauthorized = true;
                    OnInfoMessage?.Invoke(this, "Samsung display requires pairing approval. Check the TV screen for an Allow/Deny popup.");
                    OnResponseReceived?.Invoke(this, new SamsungResponseMessage { Event = eventName });
                    return;
                }

                var response = JsonConvert.DeserializeObject<SamsungResponseMessage>(json);

                if (response != null)
                {
                    // Route to pending command or treat as event
                    if (response.Id.HasValue && pendingCommands.TryRemove(response.Id.Value, out var tcs))
                    {
                        tcs.SetResult(response);
                    }

                    OnResponseReceived?.Invoke(this, response);
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex);
            }
        }

        private async Task HandleDisconnectAsync()
        {
            CleanupConnection();
            SetConnectionState(ConnectionState.Disconnected);

            // Reconnect with exponential backoff — never give up
            while (!isDisposed && reconnectEnabled)
            {
                reconnectAttempts++;

                int delayMs;
                if (lastDisconnectWasUnauthorized)
                {
                    delayMs = unauthorizedReconnectDelayMs;
                }
                else
                {
                    // Cap the exponent to prevent overflow: 2^6 * 2000 = 128000 → clamped to maxReconnectDelayMs
                    var exponent = Math.Min(reconnectAttempts - 1, 6);
                    delayMs = Math.Min(baseReconnectDelayMs * (int)Math.Pow(2, exponent), maxReconnectDelayMs);
                }

                OnInfoMessage?.Invoke(this, string.Format("Reconnecting in {0}s (attempt {1})...", delayMs / 1000, reconnectAttempts));
                await Task.Delay(delayMs).ConfigureAwait(false);

                if (isDisposed || !reconnectEnabled) break;

                var connected = await ConnectAsync().ConfigureAwait(false);
                if (connected)
                {
                    // New receive loop started — it will call HandleDisconnectAsync on next failure
                    return;
                }
            }
        }

        private void SetConnectionState(ConnectionState newState)
        {
            if (connectionState != newState)
            {
                connectionState = newState;
                OnConnectionStateChanged?.Invoke(this, newState.ToString());
            }
        }

        private int GetNextMessageId()
        {
            return Interlocked.Increment(ref messageIdCounter);
        }
    }

    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Authenticating,
        Ready,
        Disconnecting
    }
}
