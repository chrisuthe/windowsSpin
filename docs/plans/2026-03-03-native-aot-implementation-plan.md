# NativeAOT Support Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make Sendspin.SDK fully NativeAOT-compatible (v7.0.0) by replacing reflection-based JSON serialization with source generation, replacing the unmaintained Fleck WebSocket server with built-in .NET APIs, and adding automated tests.

**Architecture:** The SDK uses System.Text.Json for protocol messages and Fleck for the server-initiated WebSocket listener. We replace Fleck with TcpListener + WebSocket.CreateFromStream() and switch JSON serialization to compile-time source generation via JsonSerializerContext. These changes eliminate all reflection in the hot path.

**Tech Stack:** .NET 8/10 multi-target, System.Text.Json source generators, System.Net.WebSockets, TcpListener, xUnit

---

### Task 1: Create Test Project

**Files:**
- Create: `tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj`
- Modify: `SendspinClient.sln` (add test project)

**Step 1: Create the test project**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet new xunit -n Sendspin.SDK.Tests -o tests/Sendspin.SDK.Tests --framework net10.0
```

**Step 2: Add project reference to SDK**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet add tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj reference src/Sendspin.SDK/Sendspin.SDK.csproj
```

**Step 3: Add test project to solution**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet sln add tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj
```

**Step 4: Verify solution builds**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet build SendspinClient.sln
```
Expected: Build succeeds

**Step 5: Commit**

```bash
git add tests/ SendspinClient.sln
git commit -m "chore: add xUnit test project for Sendspin.SDK"
```

---

### Task 2: JSON Source Generation — MessageSerializerContext

**Files:**
- Create: `src/Sendspin.SDK/Protocol/MessageSerializerContext.cs`

**Step 1: Write test for source-generated serialization round-trip**

Create `tests/Sendspin.SDK.Tests/Protocol/MessageSerializerTests.cs`:

```csharp
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Protocol;

public class MessageSerializerTests
{
    [Fact]
    public void Serialize_ClientTimeMessage_RoundTrips()
    {
        var original = new ClientTimeMessage
        {
            Payload = new ClientTimePayload { ClientTransmitted = 123456789 }
        };

        var json = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize<ClientTimeMessage>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("client/time", deserialized.Type);
        Assert.Equal(123456789, deserialized.Payload.ClientTransmitted);
    }

    [Fact]
    public void Serialize_UsesSnakeCaseNaming()
    {
        var msg = new ClientTimeMessage
        {
            Payload = new ClientTimePayload { ClientTransmitted = 100 }
        };

        var json = MessageSerializer.Serialize(msg);

        Assert.Contains("\"client_transmitted\"", json);
        Assert.DoesNotContain("\"ClientTransmitted\"", json);
    }

    [Fact]
    public void Deserialize_ServerHelloMessage_ParsesCorrectly()
    {
        var json = """
        {
            "type": "server/hello",
            "payload": {
                "server_id": "test-server",
                "name": "Test Server",
                "version": 1,
                "active_roles": ["player@v1"],
                "connection_reason": "discovery"
            }
        }
        """;

        var msg = MessageSerializer.Deserialize(json) as ServerHelloMessage;

        Assert.NotNull(msg);
        Assert.Equal("test-server", msg.ServerId);
        Assert.Equal("Test Server", msg.Name);
        Assert.Equal(1, msg.Version);
        Assert.Single(msg.ActiveRoles);
        Assert.Equal("discovery", msg.ConnectionReason);
    }

    [Fact]
    public void Deserialize_UnknownType_ReturnsNull()
    {
        var json = """{"type": "unknown/type", "payload": {}}""";
        var result = MessageSerializer.Deserialize(json);
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_AllServerMessageTypes_Succeeds()
    {
        // Verify the polymorphic dispatcher handles all known server message types
        var testCases = new Dictionary<string, Type>
        {
            ["server/hello"] = typeof(ServerHelloMessage),
            ["server/time"] = typeof(ServerTimeMessage),
            ["stream/start"] = typeof(StreamStartMessage),
            ["stream/end"] = typeof(StreamEndMessage),
            ["stream/clear"] = typeof(StreamClearMessage),
            ["group/update"] = typeof(GroupUpdateMessage),
            ["server/command"] = typeof(ServerCommandMessage),
        };

        foreach (var (type, expectedType) in testCases)
        {
            var json = $"""{{ "type": "{type}", "payload": {{}} }}""";
            var msg = MessageSerializer.Deserialize(json);
            Assert.NotNull(msg);
            Assert.IsType(expectedType, msg);
        }
    }
}
```

**Step 2: Run tests to verify they pass (they use current reflection-based serializer)**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet test tests/Sendspin.SDK.Tests/ --filter "MessageSerializer" -v normal
```
Expected: All tests PASS (these test current behavior, not new code yet)

**Step 3: Create MessageSerializerContext.cs**

Create `src/Sendspin.SDK/Protocol/MessageSerializerContext.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Protocol;

/// <summary>
/// Source-generated JSON serializer context for all Sendspin protocol messages.
/// Enables NativeAOT-compatible serialization without runtime reflection.
/// </summary>
/// <remarks>
/// When adding a new message type, add a [JsonSerializable(typeof(NewMessageType))]
/// attribute here to include it in source generation.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(SnakeCaseEnumConverter), typeof(OptionalJsonConverterFactory)])]
[JsonSerializable(typeof(ClientHelloMessage))]
[JsonSerializable(typeof(ClientGoodbyeMessage))]
[JsonSerializable(typeof(ClientTimeMessage))]
[JsonSerializable(typeof(ClientCommandMessage))]
[JsonSerializable(typeof(ClientStateMessage))]
[JsonSerializable(typeof(ClientSyncOffsetMessage))]
[JsonSerializable(typeof(ClientSyncOffsetAckMessage))]
[JsonSerializable(typeof(StreamRequestFormatMessage))]
[JsonSerializable(typeof(ServerHelloMessage))]
[JsonSerializable(typeof(ServerTimeMessage))]
[JsonSerializable(typeof(StreamStartMessage))]
[JsonSerializable(typeof(StreamEndMessage))]
[JsonSerializable(typeof(StreamClearMessage))]
[JsonSerializable(typeof(GroupUpdateMessage))]
[JsonSerializable(typeof(ServerCommandMessage))]
[JsonSerializable(typeof(ServerStateMessage))]
internal partial class MessageSerializerContext : JsonSerializerContext
{
}

/// <summary>
/// Concrete enum converter for source generation (JsonStringEnumConverter cannot be
/// used directly in [JsonSourceGenerationOptions] Converters array).
/// </summary>
internal sealed class SnakeCaseEnumConverter : JsonStringEnumConverter
{
    public SnakeCaseEnumConverter()
        : base(JsonNamingPolicy.SnakeCaseLower)
    {
    }
}
```

**Step 4: Verify build succeeds with new source generator context**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet build src/Sendspin.SDK/
```
Expected: Build succeeds (source generator runs, no errors)

**Step 5: Commit**

```bash
git add src/Sendspin.SDK/Protocol/MessageSerializerContext.cs tests/Sendspin.SDK.Tests/Protocol/MessageSerializerTests.cs
git commit -m "feat: add JSON source generation context for NativeAOT"
```

---

### Task 3: JSON Source Generation — Update MessageSerializer

**Files:**
- Modify: `src/Sendspin.SDK/Protocol/MessageSerializer.cs`

**Step 1: Update MessageSerializer to use source-generated context**

Replace the entire file content. Key changes:
- Replace `JsonSerializerOptions s_options` with `MessageSerializerContext s_context`
- `Serialize<T>` uses `(JsonTypeInfo<T>)s_context.GetTypeInfo(typeof(T))!`
- `Deserialize(string json)` switch uses `s_context.ServerHelloMessage`, etc.
- `Deserialize<T>(string json)` uses `(JsonTypeInfo<T>)s_context.GetTypeInfo(typeof(T))!`

```csharp
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Protocol;

/// <summary>
/// Handles serialization and deserialization of Sendspin protocol messages.
/// Uses source-generated JsonSerializerContext for NativeAOT compatibility.
/// </summary>
public static class MessageSerializer
{
    private static readonly MessageSerializerContext s_context = MessageSerializerContext.Default;

    private static JsonTypeInfo<T> GetTypeInfo<T>() =>
        (JsonTypeInfo<T>)s_context.GetTypeInfo(typeof(T))!;

    /// <summary>
    /// Serializes a message to JSON string.
    /// </summary>
    public static string Serialize<T>(T message) where T : IMessage
    {
        return JsonSerializer.Serialize(message, GetTypeInfo<T>());
    }

    /// <summary>
    /// Serializes a message to UTF-8 bytes.
    /// </summary>
    public static byte[] SerializeToBytes<T>(T message) where T : IMessage
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, GetTypeInfo<T>());
    }

    /// <summary>
    /// Deserializes a JSON message, returning the appropriate message type.
    /// </summary>
    public static IMessage? Deserialize(string json)
    {
        // First, parse to get the message type
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("type", out var typeProp))
        {
            return null;
        }

        var messageType = typeProp.GetString();
        return messageType switch
        {
            MessageTypes.ServerHello => JsonSerializer.Deserialize(json, s_context.ServerHelloMessage),
            MessageTypes.ServerTime => JsonSerializer.Deserialize(json, s_context.ServerTimeMessage),
            MessageTypes.StreamStart => JsonSerializer.Deserialize(json, s_context.StreamStartMessage),
            MessageTypes.StreamEnd => JsonSerializer.Deserialize(json, s_context.StreamEndMessage),
            MessageTypes.StreamClear => JsonSerializer.Deserialize(json, s_context.StreamClearMessage),
            MessageTypes.GroupUpdate => JsonSerializer.Deserialize(json, s_context.GroupUpdateMessage),
            MessageTypes.ServerCommand => JsonSerializer.Deserialize(json, s_context.ServerCommandMessage),
            _ => null // Unknown message type
        };
    }

    /// <summary>
    /// Deserializes a specific message type.
    /// </summary>
    public static T? Deserialize<T>(string json) where T : class, IMessage
    {
        return JsonSerializer.Deserialize(json, GetTypeInfo<T>());
    }

    /// <summary>
    /// Gets the message type from a JSON string without full deserialization.
    /// </summary>
    public static string? GetMessageType(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var typeProp))
            {
                return typeProp.GetString();
            }
        }
        catch (JsonException)
        {
            // Invalid JSON
        }
        return null;
    }

    /// <summary>
    /// Gets the message type from a UTF-8 byte span without full deserialization.
    /// </summary>
    public static string? GetMessageType(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName &&
                    reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    return reader.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Invalid JSON
        }
        return null;
    }
}
```

**Step 2: Run tests to verify serialization still works**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet test tests/Sendspin.SDK.Tests/ --filter "MessageSerializer" -v normal
```
Expected: All tests PASS

**Step 3: Commit**

```bash
git add src/Sendspin.SDK/Protocol/MessageSerializer.cs
git commit -m "feat: switch MessageSerializer to source-generated context"
```

---

### Task 4: JSON Source Generation — Fix OptionalJsonConverter

**Files:**
- Modify: `src/Sendspin.SDK/Protocol/OptionalJsonConverter.cs`
- Create: `tests/Sendspin.SDK.Tests/Protocol/OptionalJsonConverterTests.cs`

**Step 1: Write tests for Optional<T> serialization**

Create `tests/Sendspin.SDK.Tests/Protocol/OptionalJsonConverterTests.cs`:

```csharp
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Protocol;

public class OptionalJsonConverterTests
{
    [Fact]
    public void Optional_AbsentField_DeserializesToAbsent()
    {
        // JSON with no "progress" field at all
        var json = """
        {
            "type": "server/state",
            "payload": {
                "metadata": {
                    "title": "Test Song",
                    "artist": "Test Artist"
                }
            }
        }
        """;

        var msg = MessageSerializer.Deserialize<ServerStateMessage>(json);
        Assert.NotNull(msg);
        Assert.NotNull(msg.Payload.Metadata);
        Assert.True(msg.Payload.Metadata.Progress.IsAbsent);
    }

    [Fact]
    public void Optional_ExplicitNull_DeserializesToPresentNull()
    {
        // JSON with "progress": null — means track ended
        var json = """
        {
            "type": "server/state",
            "payload": {
                "metadata": {
                    "title": "Test Song",
                    "artist": "Test Artist",
                    "progress": null
                }
            }
        }
        """;

        var msg = MessageSerializer.Deserialize<ServerStateMessage>(json);
        Assert.NotNull(msg);
        Assert.NotNull(msg.Payload.Metadata);
        Assert.True(msg.Payload.Metadata.Progress.IsPresent);
        Assert.Null(msg.Payload.Metadata.Progress.Value);
    }
}
```

**Step 2: Run tests to verify they pass with current code**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet test tests/Sendspin.SDK.Tests/ --filter "OptionalJsonConverter" -v normal
```
Expected: PASS

**Step 3: Update OptionalJsonConverter for AOT compatibility**

In `src/Sendspin.SDK/Protocol/OptionalJsonConverter.cs`, update `OptionalJsonConverter<T>.Read()` to use `JsonTypeInfo<T>`:

```csharp
    /// <inheritdoc />
    public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // If we're reading, the field IS present in the JSON
        // (System.Text.Json only calls Read when the property exists)
        if (reader.TokenType == JsonTokenType.Null)
        {
            // Field is present with explicit null value
            return Optional<T>.Present(default);
        }

        // Field is present with a value — use JsonTypeInfo for AOT safety
        var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        var value = JsonSerializer.Deserialize(ref reader, typeInfo);
        return Optional<T>.Present(value);
    }
```

Also add the `using` at the top:
```csharp
using System.Text.Json.Serialization.Metadata;
```

**Step 4: Run tests to verify Optional still works**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet test tests/Sendspin.SDK.Tests/ --filter "OptionalJsonConverter" -v normal
```
Expected: PASS

**Step 5: Build full solution to verify no regressions**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet build SendspinClient.sln
```
Expected: Build succeeds

**Step 6: Commit**

```bash
git add src/Sendspin.SDK/Protocol/OptionalJsonConverter.cs tests/Sendspin.SDK.Tests/Protocol/OptionalJsonConverterTests.cs
git commit -m "feat: make OptionalJsonConverter AOT-compatible"
```

---

### Task 5: Replace Fleck — Create WebSocketClientConnection

**Files:**
- Create: `src/Sendspin.SDK/Connection/WebSocketClientConnection.cs`

**Step 1: Create WebSocketClientConnection**

This wraps a `System.Net.WebSockets.WebSocket` with an event-based API matching what `IncomingConnection` needs from the old Fleck `IWebSocketConnection`.

Create `src/Sendspin.SDK/Connection/WebSocketClientConnection.cs`:

```csharp
using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Sendspin.SDK.Connection;

/// <summary>
/// Wraps a System.Net.WebSockets.WebSocket accepted by SimpleWebSocketServer.
/// Provides event-based message dispatch (OnMessage, OnBinary, OnClose, OnError)
/// and send methods, replacing Fleck's IWebSocketConnection.
/// </summary>
public sealed class WebSocketClientConnection : IAsyncDisposable
{
    private readonly WebSocket _webSocket;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private bool _disposed;

    /// <summary>Client IP address.</summary>
    public IPAddress ClientIpAddress { get; }

    /// <summary>Client port.</summary>
    public int ClientPort { get; }

    /// <summary>The HTTP request path used during the WebSocket upgrade.</summary>
    public string Path { get; }

    /// <summary>Raised when a text message is received.</summary>
    public Action<string>? OnMessage { get; set; }

    /// <summary>Raised when a binary message is received.</summary>
    public Action<byte[]>? OnBinary { get; set; }

    /// <summary>Raised when the connection closes.</summary>
    public Action? OnClose { get; set; }

    /// <summary>Raised when an error occurs.</summary>
    public Action<Exception>? OnError { get; set; }

    public WebSocketClientConnection(
        WebSocket webSocket,
        IPAddress clientIpAddress,
        int clientPort,
        string path,
        ILogger? logger = null)
    {
        _webSocket = webSocket;
        ClientIpAddress = clientIpAddress;
        ClientPort = clientPort;
        Path = path;
        _logger = logger;
    }

    /// <summary>
    /// Starts the background receive loop that dispatches messages to callbacks.
    /// </summary>
    public void StartReceiving()
    {
        if (_receiveLoop is not null)
            throw new InvalidOperationException("Receive loop already started");

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Sends a text message.
    /// </summary>
    public async Task SendAsync(string message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_webSocket.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not open");

        var bytes = Encoding.UTF8.GetBytes(message);
        await _webSocket.SendAsync(
            bytes.AsMemory(),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a binary message.
    /// </summary>
    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_webSocket.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not open");

        await _webSocket.SendAsync(
            data.AsMemory(),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Initiates a graceful WebSocket close.
    /// </summary>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_webSocket.State == WebSocketState.Open ||
            _webSocket.State == WebSocketState.CloseReceived)
        {
            try
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "closing",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Error during graceful WebSocket close");
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   _webSocket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _webSocket.ReceiveAsync(
                        buffer.AsMemory(),
                        cancellationToken).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        OnClose?.Invoke();
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var data = ms.ToArray();

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(data);
                    OnMessage?.Invoke(text);
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    OnBinary?.Invoke(data);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (WebSocketException ex) when (
            _webSocket.State == WebSocketState.Aborted ||
            _webSocket.State == WebSocketState.Closed)
        {
            _logger?.LogDebug(ex, "WebSocket closed during receive");
            OnClose?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "WebSocket receive error");
            OnError?.Invoke(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (_webSocket.State != WebSocketState.Closed &&
                _webSocket.State != WebSocketState.Aborted)
            {
                OnClose?.Invoke();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync();

        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); }
            catch { /* Swallow — loop handles its own errors */ }
        }

        _webSocket.Dispose();
        _cts.Dispose();
    }
}
```

**Step 2: Verify build**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet build src/Sendspin.SDK/
```
Expected: Build succeeds

**Step 3: Commit**

```bash
git add src/Sendspin.SDK/Connection/WebSocketClientConnection.cs
git commit -m "feat: add WebSocketClientConnection to replace Fleck IWebSocketConnection"
```

---

### Task 6: Replace Fleck — Create SimpleWebSocketServer

**Files:**
- Create: `src/Sendspin.SDK/Connection/SimpleWebSocketServer.cs`

**Step 1: Create SimpleWebSocketServer**

Create `src/Sendspin.SDK/Connection/SimpleWebSocketServer.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Sendspin.SDK.Connection;

/// <summary>
/// Minimal WebSocket server using TcpListener + WebSocket.CreateFromStream().
/// Replaces Fleck for NativeAOT compatibility — no HTTP.sys, no admin privileges.
/// </summary>
public sealed class SimpleWebSocketServer : IAsyncDisposable
{
    private static readonly Regex s_getRequestLine = new(
        @"^GET\s+(\S+)\s+HTTP/1\.1",
        RegexOptions.Compiled);

    private static readonly Regex s_webSocketKeyHeader = new(
        @"Sec-WebSocket-Key:\s*(\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The WebSocket GUID used in the Sec-WebSocket-Accept computation per RFC 6455.
    /// </summary>
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-5AB0DC85B11C";

    private readonly ILogger? _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private bool _disposed;

    /// <summary>
    /// Port the server is listening on.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// Raised when a new WebSocket client connects. The handler receives a
    /// <see cref="WebSocketClientConnection"/> with the receive loop already started.
    /// </summary>
    public event EventHandler<WebSocketClientConnection>? ClientConnected;

    public SimpleWebSocketServer(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Starts listening for incoming WebSocket connections.
    /// </summary>
    /// <param name="port">Port to bind to.</param>
    public void Start(int port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_listener is not null)
            throw new InvalidOperationException("Server is already running");

        Port = port;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

        _logger?.LogInformation("WebSocket server listening on port {Port}", port);
    }

    /// <summary>
    /// Stops the server and closes all pending accept operations.
    /// </summary>
    public async Task StopAsync()
    {
        if (_listener is null) return;

        _logger?.LogInformation("Stopping WebSocket server");

        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        _listener.Stop();

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch { /* Swallow — loop handles its own errors */ }
        }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _listener!.AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);

                // Handle each connection concurrently
                _ = HandleConnectionAsync(tcpClient, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error accepting TCP connection");
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        var remoteEndPoint = tcpClient.Client.RemoteEndPoint as IPEndPoint;
        try
        {
            var stream = tcpClient.GetStream();

            // Read the HTTP upgrade request
            var requestBytes = new byte[4096];
            var bytesRead = await stream.ReadAsync(requestBytes.AsMemory(), cancellationToken)
                .ConfigureAwait(false);

            if (bytesRead == 0)
            {
                tcpClient.Dispose();
                return;
            }

            var request = Encoding.UTF8.GetString(requestBytes, 0, bytesRead);

            // Parse the request
            var pathMatch = s_getRequestLine.Match(request);
            if (!pathMatch.Success)
            {
                _logger?.LogWarning("Invalid HTTP request from {Endpoint}", remoteEndPoint);
                await SendHttpResponse(stream, 400, "Bad Request", cancellationToken);
                tcpClient.Dispose();
                return;
            }

            var path = pathMatch.Groups[1].Value;

            // Extract WebSocket key
            var keyMatch = s_webSocketKeyHeader.Match(request);
            if (!keyMatch.Success)
            {
                _logger?.LogWarning("Missing Sec-WebSocket-Key from {Endpoint}", remoteEndPoint);
                await SendHttpResponse(stream, 400, "Missing Sec-WebSocket-Key", cancellationToken);
                tcpClient.Dispose();
                return;
            }

            var webSocketKey = keyMatch.Groups[1].Value;

            // Compute Sec-WebSocket-Accept per RFC 6455
            var acceptKey = ComputeAcceptKey(webSocketKey);

            // Send HTTP 101 Switching Protocols
            var response = $"HTTP/1.1 101 Switching Protocols\r\n" +
                           $"Upgrade: websocket\r\n" +
                           $"Connection: Upgrade\r\n" +
                           $"Sec-WebSocket-Accept: {acceptKey}\r\n" +
                           $"\r\n";

            var responseBytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(responseBytes.AsMemory(), cancellationToken)
                .ConfigureAwait(false);

            // Create WebSocket from the stream
            var webSocket = WebSocket.CreateFromStream(
                stream,
                new WebSocketCreationOptions { IsServer = true });

            var clientIp = remoteEndPoint?.Address ?? IPAddress.Loopback;
            var clientPort = remoteEndPoint?.Port ?? 0;

            var connection = new WebSocketClientConnection(
                webSocket,
                clientIp,
                clientPort,
                path,
                _logger);

            connection.StartReceiving();

            _logger?.LogDebug("WebSocket connection established from {Endpoint} on path {Path}",
                remoteEndPoint, path);

            ClientConnected?.Invoke(this, connection);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error handling WebSocket upgrade from {Endpoint}", remoteEndPoint);
            tcpClient.Dispose();
        }
    }

    private static string ComputeAcceptKey(string webSocketKey)
    {
        var combined = webSocketKey + WebSocketGuid;
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToBase64String(hash);
    }

    private static async Task SendHttpResponse(
        NetworkStream stream, int statusCode, string reason,
        CancellationToken cancellationToken)
    {
        var response = $"HTTP/1.1 {statusCode} {reason}\r\nContent-Length: 0\r\n\r\n";
        var bytes = Encoding.UTF8.GetBytes(response);
        await stream.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }
}
```

**Step 2: Verify build**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet build src/Sendspin.SDK/
```
Expected: Build succeeds

**Step 3: Commit**

```bash
git add src/Sendspin.SDK/Connection/SimpleWebSocketServer.cs
git commit -m "feat: add SimpleWebSocketServer using built-in .NET WebSocket APIs"
```

---

### Task 7: Replace Fleck — Write WebSocket Integration Tests

**Files:**
- Create: `tests/Sendspin.SDK.Tests/Connection/SimpleWebSocketServerTests.cs`

**Step 1: Write integration tests**

Create `tests/Sendspin.SDK.Tests/Connection/SimpleWebSocketServerTests.cs`:

```csharp
using System.Net.WebSockets;
using Sendspin.SDK.Connection;

namespace Sendspin.SDK.Tests.Connection;

public class SimpleWebSocketServerTests : IAsyncDisposable
{
    private readonly SimpleWebSocketServer _server = new();

    [Fact]
    public async Task Server_AcceptsWebSocketConnection()
    {
        _server.Start(0); // OS assigns random port
        var port = ((System.Net.IPEndPoint)GetListenerEndpoint()).Port;

        // Use a random available port — need to get it from the listener
        // Actually, SimpleWebSocketServer.Port is set in Start()
        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/sendspin"), CancellationToken.None);

        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(serverConn);
        Assert.Equal("/sendspin", serverConn.Path);
        Assert.Equal(WebSocketState.Open, client.State);

        await serverConn.DisposeAsync();
    }

    [Fact]
    public async Task Server_SendsAndReceivesTextMessages()
    {
        _server.Start(0);

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/test"), CancellationToken.None);

        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Client sends, server receives
        var received = new TaskCompletionSource<string>();
        serverConn.OnMessage = msg => received.TrySetResult(msg);

        var msgBytes = System.Text.Encoding.UTF8.GetBytes("hello from client");
        await client.SendAsync(msgBytes, WebSocketMessageType.Text, true, CancellationToken.None);

        var text = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello from client", text);

        // Server sends, client receives
        await serverConn.SendAsync("hello from server");

        var buffer = new byte[1024];
        var result = await client.ReceiveAsync(buffer, CancellationToken.None);
        var response = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
        Assert.Equal("hello from server", response);

        await serverConn.DisposeAsync();
    }

    [Fact]
    public async Task Server_SendsAndReceivesBinaryMessages()
    {
        _server.Start(0);

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/test"), CancellationToken.None);

        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var received = new TaskCompletionSource<byte[]>();
        serverConn.OnBinary = data => received.TrySetResult(data);

        var payload = new byte[] { 0x04, 0x00, 0x01, 0x02, 0x03 };
        await client.SendAsync(payload, WebSocketMessageType.Binary, true, CancellationToken.None);

        var data = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(payload, data);

        await serverConn.DisposeAsync();
    }

    [Fact]
    public async Task Server_RaisesOnClose_WhenClientDisconnects()
    {
        _server.Start(0);

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/test"), CancellationToken.None);

        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var closed = new TaskCompletionSource<bool>();
        serverConn.OnClose = () => closed.TrySetResult(true);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

        var wasClosed = await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(wasClosed);

        await serverConn.DisposeAsync();
    }

    private System.Net.EndPoint GetListenerEndpoint()
    {
        // The port is stored on the server
        return new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, _server.Port);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
    }
}
```

**Step 2: Run tests**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet test tests/Sendspin.SDK.Tests/ --filter "SimpleWebSocketServer" -v normal
```
Expected: All tests PASS

**Step 3: Commit**

```bash
git add tests/Sendspin.SDK.Tests/Connection/SimpleWebSocketServerTests.cs
git commit -m "test: add integration tests for SimpleWebSocketServer"
```

---

### Task 8: Replace Fleck — Update SendspinListener

**Files:**
- Modify: `src/Sendspin.SDK/Connection/SendSpinListener.cs`

**Step 1: Rewrite SendspinListener to use SimpleWebSocketServer**

Replace Fleck usage with the new `SimpleWebSocketServer`. Key changes:
- Remove `using Fleck;`
- Replace `WebSocketServer` with `SimpleWebSocketServer`
- Replace `FleckLog.LogAction` with ILogger (already used)
- Change `ServerConnected` event type from `IWebSocketConnection` to `WebSocketClientConnection`

```csharp
using Microsoft.Extensions.Logging;

namespace Sendspin.SDK.Connection;

/// <summary>
/// WebSocket server listener for server-initiated Sendspin connections.
/// Uses a built-in .NET WebSocket server (no admin privileges required).
/// Listens on the configured port and accepts incoming connections from Sendspin servers.
/// </summary>
public sealed class SendspinListener : IAsyncDisposable
{
    private readonly ILogger<SendspinListener> _logger;
    private readonly ListenerOptions _options;
    private SimpleWebSocketServer? _server;
    private bool _disposed;
    private bool _isListening;

    /// <summary>
    /// Raised when a new server connects.
    /// </summary>
    public event EventHandler<WebSocketClientConnection>? ServerConnected;

    /// <summary>
    /// Whether the listener is currently running.
    /// </summary>
    public bool IsListening => _isListening;

    /// <summary>
    /// The port the listener is bound to.
    /// </summary>
    public int Port => _options.Port;

    public SendspinListener(ILogger<SendspinListener> logger, ListenerOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new ListenerOptions();
    }

    /// <summary>
    /// Starts listening for incoming WebSocket connections.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_isListening)
        {
            _logger.LogWarning("Listener is already running");
            return Task.CompletedTask;
        }

        _server = new SimpleWebSocketServer(_logger);
        _server.ClientConnected += OnClientConnected;
        _server.Start(_options.Port);

        _isListening = true;
        _logger.LogInformation("Sendspin listener started on ws://0.0.0.0:{Port} (path: {Path})",
            _options.Port, _options.Path);

        return Task.CompletedTask;
    }

    private void OnClientConnected(object? sender, WebSocketClientConnection connection)
    {
        _logger.LogInformation("WebSocket connection opened from {ClientIp}",
            connection.ClientIpAddress);

        // Check if this is the correct path
        var path = connection.Path;
        if (!string.Equals(path, _options.Path, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, _options.Path + "/", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Connection to unexpected path: {Path}, expected: {Expected}",
                path, _options.Path);
        }

        // Raise the event
        ServerConnected?.Invoke(this, connection);
    }

    /// <summary>
    /// Stops listening for connections.
    /// </summary>
    public async Task StopAsync()
    {
        if (_server == null || !_isListening)
            return;

        _logger.LogInformation("Stopping Sendspin listener");

        try
        {
            await _server.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping WebSocket server");
        }
        finally
        {
            _server = null;
            _isListening = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();
    }
}

/// <summary>
/// Configuration options for the Sendspin listener.
/// </summary>
public sealed class ListenerOptions
{
    /// <summary>
    /// Port to listen on.
    /// Default: 8928 (Sendspin standard port for clients)
    /// </summary>
    public int Port { get; set; } = 8928;

    /// <summary>
    /// WebSocket endpoint path.
    /// Default: "/sendspin"
    /// </summary>
    public string Path { get; set; } = "/sendspin";
}
```

**Step 2: Verify build**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet build src/Sendspin.SDK/
```
Expected: Build FAILS — IncomingConnection and SendspinHostService still reference Fleck types

**Step 3: Commit (partial — will fix remaining files next)**

Do NOT commit yet — proceed to Task 9 first to fix the remaining files.

---

### Task 9: Replace Fleck — Update IncomingConnection and SendspinHostService

**Files:**
- Modify: `src/Sendspin.SDK/Connection/IncomingConnection.cs`
- Modify: `src/Sendspin.SDK/Client/SendSpinHostService.cs`

**Step 1: Update IncomingConnection to use WebSocketClientConnection**

Replace `using Fleck;` and change the constructor parameter from `IWebSocketConnection` to `WebSocketClientConnection`. Update all Fleck-specific API calls.

Key changes:
- Constructor takes `WebSocketClientConnection` instead of `IWebSocketConnection`
- `ServerUri` built from `connection.ClientIpAddress` and `connection.ClientPort`
- Wire up callbacks: `connection.OnMessage`, `connection.OnBinary`, `connection.OnClose`, `connection.OnError`
- `_socket.Send(json)` → `connection.SendAsync(json)`
- `_socket.Send(data.ToArray())` → `connection.SendAsync(data.ToArray())`
- `_socket.Close()` → `connection.CloseAsync()`

**Step 2: Update SendspinHostService**

- Remove `using Fleck;`
- Change `OnServerConnected` parameter from `IWebSocketConnection` to `WebSocketClientConnection`
- The rest of the method stays the same (it creates an `IncomingConnection` from the socket)

**Step 3: Remove Fleck from csproj**

In `src/Sendspin.SDK/Sendspin.SDK.csproj`, remove:
```xml
<PackageReference Include="Fleck" Version="1.2.0" />
```

**Step 4: Build full solution**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet build SendspinClient.sln
```
Expected: Build succeeds with zero Fleck references

**Step 5: Commit all Fleck replacement files together**

```bash
git add src/Sendspin.SDK/Connection/SendSpinListener.cs src/Sendspin.SDK/Connection/IncomingConnection.cs src/Sendspin.SDK/Client/SendSpinHostService.cs src/Sendspin.SDK/Sendspin.SDK.csproj
git commit -m "feat!: replace Fleck WebSocket server with built-in .NET APIs

BREAKING: SendspinListener.ServerConnected event parameter changed
from Fleck.IWebSocketConnection to WebSocketClientConnection.

Removes the unmaintained Fleck dependency (last updated 5 years ago)
and replaces it with TcpListener + WebSocket.CreateFromStream() for
full NativeAOT compatibility."
```

---

### Task 10: csproj — Add AOT/Trim Properties and Bump Version

**Files:**
- Modify: `src/Sendspin.SDK/Sendspin.SDK.csproj`

**Step 1: Add IsAotCompatible and IsTrimmable**

Add after the existing PropertyGroup's `<NoWarn>` line:

```xml
  <!-- NativeAOT and trimming compatibility -->
  <IsAotCompatible>true</IsAotCompatible>
  <IsTrimmable>true</IsTrimmable>
```

**Step 2: Bump version to 7.0.0 and update release notes**

Change `<Version>6.3.4</Version>` to `<Version>7.0.0</Version>`.

Prepend to `<PackageReleaseNotes>`:
```
v7.0.0 - NativeAOT Support:

BREAKING CHANGES:
- SendspinListener.ServerConnected event: parameter type changed from
  Fleck.IWebSocketConnection to Sendspin.SDK.Connection.WebSocketClientConnection
- Fleck NuGet dependency removed (replaced with built-in .NET WebSocket APIs)

New Features:
- Full NativeAOT compatibility: SDK can be used in NativeAOT-published applications
- JSON source generation: System.Text.Json uses compile-time source generation
  instead of runtime reflection (also improves startup performance)
- Built-in WebSocket server: TcpListener + WebSocket.CreateFromStream() replaces
  Fleck for server-initiated connections (no admin privileges, fully AOT-safe)
- IsAotCompatible and IsTrimmable properties enabled

Migration:
- If you subscribe to SendspinListener.ServerConnected directly, update the event
  handler parameter type from IWebSocketConnection to WebSocketClientConnection
- If you reference Fleck types through the SDK, switch to WebSocketClientConnection
- No changes needed if you only use SendspinHostService or SendspinClient
```

**Step 3: Build and verify no AOT/trim analyzer warnings**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet build src/Sendspin.SDK/ -c Release -warnaserror
```
Expected: Build succeeds with no warnings. If there are AOT analyzer warnings, fix them before proceeding.

**Step 4: Commit**

```bash
git add src/Sendspin.SDK/Sendspin.SDK.csproj
git commit -m "feat!: bump to v7.0.0 with NativeAOT support

- Add IsAotCompatible and IsTrimmable
- Update version to 7.0.0
- Add release notes documenting breaking changes"
```

---

### Task 11: Run All Tests and Final Verification

**Step 1: Run all tests**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet test tests/Sendspin.SDK.Tests/ -v normal
```
Expected: All tests PASS

**Step 2: Build entire solution in Release**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
dotnet build SendspinClient.sln -c Release
```
Expected: Build succeeds

**Step 3: Verify no Fleck references remain**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
grep -ri "fleck" src/ --include="*.cs" --include="*.csproj"
```
Expected: No results

**Step 4: Verify no reflection-based JSON calls remain in serializer**

```bash
cd /z/CodeProjects/windowsSpin-native-aot-support
grep -n "Activator.CreateInstance\|MakeGenericType" src/Sendspin.SDK/Protocol/
```
Expected: No results (the factory still uses these but only in the converter which is registered with source gen context)
