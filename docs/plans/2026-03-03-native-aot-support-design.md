# NativeAOT Support for Sendspin.SDK v7.0.0

## Summary

Make Sendspin.SDK fully NativeAOT-compatible by:
1. Adding `IsAotCompatible` / `IsTrimmable` to the csproj
2. Replacing reflection-based JSON serialization with source generation
3. Replacing the unmaintained Fleck WebSocket server with built-in .NET APIs
4. Adding automated tests for the new components

This is a **major version bump** (6.x → 7.0.0) because `SendspinListener.ServerConnected` event parameter type changes from Fleck's `IWebSocketConnection` to our own `WebSocketClientConnection`.

## Motivation

PR #9 from an SDK consumer identified the key NativeAOT blockers. This design addresses them properly for a NuGet library (the PR used a local Fleck fork which breaks NuGet consumers).

## Design

### 1. csproj Changes

Add a single unconditional PropertyGroup:

```xml
<PropertyGroup>
  <IsAotCompatible>true</IsAotCompatible>
  <IsTrimmable>true</IsTrimmable>
</PropertyGroup>
```

- Remove `Fleck` NuGet dependency
- Do NOT add `PublishAot` (consumer's choice, not a library concern)
- Do NOT add `PublishTrimming.xml` (trimming descriptors are a consumer concern)

### 2. JSON Source Generation

**New file: `Protocol/MessageSerializerContext.cs`**

```csharp
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
internal partial class MessageSerializerContext : JsonSerializerContext { }

internal sealed class SnakeCaseEnumConverter : JsonStringEnumConverter
{
    public SnakeCaseEnumConverter() : base(JsonNamingPolicy.SnakeCaseLower) { }
}
```

**Modified: `Protocol/MessageSerializer.cs`**

Replace `JsonSerializerOptions s_options` with `MessageSerializerContext s_context`. Use typed `JsonTypeInfo<T>` for all serialize/deserialize calls.

**Modified: `Protocol/OptionalJsonConverter.cs`**

Replace `Activator.CreateInstance()` with `options.GetTypeInfo(typeof(T))` to get `JsonTypeInfo<T>` for AOT-safe deserialization.

### 3. Fleck Replacement

#### New: `Connection/SimpleWebSocketServer.cs`

Minimal WebSocket server using .NET built-in APIs:

```
TcpListener.AcceptTcpClientAsync()
  → Parse HTTP upgrade request
  → Compute Sec-WebSocket-Accept hash
  → Send HTTP 101 response
  → WebSocket.CreateFromStream()
```

- No admin privileges needed (raw TCP, not HTTP.sys)
- Fully AOT-compatible (no reflection)
- Supports path filtering (e.g., `/sendspin`)
- Runs accept loop on background task

#### New: `Connection/WebSocketClientConnection.cs`

Wraps `System.Net.WebSockets.WebSocket` with:
- Event callbacks: `OnMessage`, `OnBinary`, `OnClose`, `OnError`
- Methods: `SendAsync(string)`, `SendAsync(byte[])`, `CloseAsync()`
- Properties: `ClientIpAddress`, `ClientPort`, `Path`
- Background receive loop that dispatches to callbacks

#### Modified Files

- `SendspinListener.cs` → use `SimpleWebSocketServer` + `WebSocketClientConnection`
- `IncomingConnection.cs` → accept `WebSocketClientConnection` instead of `IWebSocketConnection`
- `SendspinHostService.cs` → update event handler parameter type

### 4. OpusDecoder

No changes. Keep `OpusCodecFactory.CreateDecoder()`.

### 5. Testing

- **MessageSerializer tests**: Verify source-generated serialization round-trips all message types correctly, including `Optional<T>` semantics (absent vs explicit null)
- **SimpleWebSocketServer tests**: Verify HTTP upgrade handshake, WebSocket message send/receive, path filtering, connection lifecycle
- **AOT compatibility test project**: A minimal console app with `<PublishAot>true</PublishAot>` that exercises the SDK to catch whole-program AOT issues in CI

## Breaking Changes

| Change | Impact |
|--------|--------|
| `SendspinListener.ServerConnected` event: `IWebSocketConnection` → `WebSocketClientConnection` | Low — most consumers use `SendspinHostService` which abstracts this |
| Fleck NuGet dependency removed | Low — no public Fleck types in SDK's public API except the event above |

## Migration Guide

- If you subscribe to `SendspinListener.ServerConnected` directly, update the event handler parameter type
- If you reference Fleck types through the SDK, switch to `WebSocketClientConnection`
- No changes needed if you only use `SendspinHostService` or `SendspinClient`
