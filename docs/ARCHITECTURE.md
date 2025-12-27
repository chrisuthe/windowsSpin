# Sendspin Windows Client - Architecture Documentation

This document provides a technical overview of the Sendspin Windows Client architecture and design decisions.

## Table of Contents
- [Project Overview](#project-overview)
- [Solution Structure](#solution-structure)
- [Core Components](#core-components)
- [Protocol Implementation](#protocol-implementation)
- [Clock Synchronization](#clock-synchronization)
- [Threading Model](#threading-model)
- [Dependency Injection](#dependency-injection)
- [Design Patterns](#design-patterns)

## Project Overview

The Sendspin Windows Client is a .NET 8.0 WPF application that implements the Sendspin protocol for synchronized multi-room audio playback with Music Assistant. The implementation focuses on:

- **High precision timing**: Kalman filter-based clock synchronization for sub-millisecond accuracy
- **Reliability**: Automatic reconnection, robust error handling
- **Flexibility**: Support for both client-initiated and server-initiated connection modes
- **Maintainability**: Clean architecture with separation of concerns

## Solution Structure

```
SendspinClient.sln
├── SendspinClient.Core         # Platform-agnostic protocol implementation
├── SendspinClient.Services     # Windows-specific audio services
└── SendspinClient              # WPF desktop application
```

### SendspinClient.Core

**Target**: .NET 8.0 (platform-agnostic)
**Dependencies**: Fleck, NAudio, Concentus, Zeroconf, Makaretu.Dns

The core library implements the Sendspin protocol without any UI or platform-specific code.

**Key Namespaces**:
- `Client/`: Client orchestration and service management
- `Connection/`: WebSocket connection handling (both outgoing and incoming)
- `Discovery/`: mDNS server discovery and service advertisement
- `Protocol/`: Message serialization, parsing, and protocol types
- `Models/`: Data models for audio formats, playback state, metadata
- `Synchronization/`: Clock synchronization algorithms

### SendspinClient.Services

**Target**: .NET 8.0
**Dependencies**: NAudio, Concentus

Platform-specific audio services for Windows (planned):
- NAudio-based audio output
- Codec support (PCM, FLAC, Opus)
- Audio buffer management
- Device enumeration and selection

### SendspinClient

**Target**: .NET 8.0 Windows (10.0.17763.0)
**Dependencies**: CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection

WPF desktop application with:
- MVVM architecture using CommunityToolkit.Mvvm
- System tray integration
- Dependency injection for loose coupling
- Reactive UI updates via property change notifications

## Core Components

### Client Services

#### ISendspinClient / SendspinClientService
The main client interface that orchestrates:
- Connection lifecycle management
- Handshake negotiation
- Message routing
- Clock synchronization loop
- State management

**Operation Modes**:
1. **Client-Initiated**: Client discovers servers via mDNS and initiates connection
2. **Server-Initiated**: Client advertises via mDNS and accepts incoming connections

#### SendspinHostService
Hosts a WebSocket server for server-initiated connections:
- Runs a Fleck WebSocket server
- Advertises via mDNS
- Manages multiple concurrent server connections
- Aggregates events from all connections

### Connection Management

#### ISendspinConnection
Abstract interface for WebSocket connections with two implementations:

**SendspinConnection** (outgoing):
- Uses `ClientWebSocket` from System.Net.WebSockets
- Automatic reconnection with exponential backoff
- Connection state machine: Disconnected → Connecting → Handshaking → Connected
- Graceful disconnection with goodbye message

**IncomingConnection** (incoming):
- Wraps Fleck's `IWebSocketConnection`
- Server-initiated connection handling
- Event-based message processing

#### Connection State Machine

```
Disconnected ──connect()──> Connecting ──success──> Handshaking ──hello──> Connected
     ↑                           │                                             │
     │                           │                                             │
     └──────────────── disconnect() / error ────────────────────────────────────┘
                               │
                               ↓
                         Reconnecting (if auto-reconnect enabled)
```

### Discovery

#### mDNS Service Discovery
Uses Zeroconf library to:
- Discover Sendspin servers via `_sendspin-server._tcp.local.`
- Parse TXT records for server metadata
- Track server availability (add/update/remove events)
- Periodic scanning with stale server cleanup

#### mDNS Service Advertisement
Uses Makaretu.Dns to:
- Advertise this client as `_sendspin._tcp.local.`
- Publish WebSocket port and path in TXT records
- Support multi-homed hosts (multiple network interfaces)

### Protocol Implementation

#### Message Types

**Text Messages (JSON)**:
```
Handshake:    client/hello ↔ server/hello
Time Sync:    client/time ↔ server/time
State:        client/state, server/state, group/update
Commands:     client/command
Stream:       stream/start, stream/end, stream/clear
```

**Binary Messages**:
- Format: `[type:1 byte][timestamp:8 bytes][payload:N bytes]`
- Types: Player audio (4-7), Artwork (8-11), Visualizer (16-23)

#### MessageSerializer
JSON serialization with:
- Snake_case property naming
- Enum to string conversion
- Null value omission
- Type detection without full deserialization

#### BinaryMessageParser
Zero-copy parsing using `ReadOnlySpan<byte>`:
- Extract type, timestamp, and payload
- Parse specific message types (audio, artwork)
- Category detection

## Clock Synchronization

### Kalman Filter Implementation

The `KalmanClockSynchronizer` uses a 2D Kalman filter to estimate:
- **Clock offset**: Difference between server and client clocks (microseconds)
- **Clock drift**: Rate of change of offset (microseconds/second)

**State Vector**: `[offset, drift]`

**Process Model**:
```
offset(t+dt) = offset(t) + drift(t) * dt + noise
drift(t+dt) = drift(t) + noise
```

**Measurement**:
Uses NTP-style 4-timestamp exchange:
- T1: Client transmit time
- T2: Server receive time
- T3: Server transmit time
- T4: Client receive time

Offset calculation: `offset = ((T2 - T1) + (T3 - T4)) / 2`

### Adaptive Time Sync Intervals

Synchronization frequency adapts based on quality:
- **Initial/Poor sync** (>5ms uncertainty): 200ms intervals
- **Moderate sync** (2-5ms): 500ms
- **Good sync** (1-2ms): 1000ms
- **Excellent sync** (<1ms): 3000ms

This balances network traffic with synchronization quality.

### Convergence Detection

Synchronizer is considered converged when:
- At least 5 measurements processed
- Offset uncertainty < 1ms (1000 microseconds)

## Threading Model

### Async/Await Pattern

The codebase uses modern async/await throughout:
- No blocking I/O operations
- CancellationToken support for cooperative cancellation
- ValueTask for hot paths (planned optimizations)

### Thread Safety

**Immutable Messages**: Protocol messages are immutable records/classes

**Lock-Based Synchronization**:
- `KalmanClockSynchronizer`: Uses lock for state updates
- `SendspinConnection`: SemaphoreSlim for send serialization
- `MdnsServerDiscovery`: ConcurrentDictionary for server list

**Event Dispatch**:
- Events raised on connection thread
- UI updates marshaled to UI thread via dispatcher

### Background Tasks

Long-running operations:
- WebSocket receive loop: Dedicated task per connection
- Time sync loop: Periodic async task with adaptive delays
- mDNS discovery: Periodic scanning task

## Dependency Injection

Uses Microsoft.Extensions.DependencyInjection:

```csharp
services.AddLogging();
services.AddSingleton<ILoggerFactory, LoggerFactory>();
services.AddSingleton<ClientCapabilities>();
services.AddTransient<ISendspinConnection, SendspinConnection>();
services.AddTransient<ISendspinClient, SendspinClientService>();
services.AddSingleton<IServerDiscovery, MdnsServerDiscovery>();
```

**Lifetime Scopes**:
- **Singleton**: Logger factories, discovery services, configuration
- **Transient**: Connections, clients (can have multiple instances)
- **Scoped**: Not used (WPF doesn't have request scopes)

## Design Patterns

### Interface Segregation

Small, focused interfaces:
- `ISendspinConnection`: Connection lifecycle
- `ISendspinClient`: Client operations
- `IServerDiscovery`: Server discovery
- `IClockSynchronizer`: Time synchronization

### Repository Pattern

Server discovery acts as a repository:
- `IServerDiscovery.Servers`: Current server collection
- Events for add/update/remove

### Observer Pattern

Event-based notifications throughout:
- Connection state changes
- Message reception
- Server discovery
- Playback state updates

### Factory Pattern

Static factory methods for message creation:
```csharp
ClientHelloMessage.Create(clientId, name, roles, ...);
ClientTimeMessage.CreateNow();
ClientStateMessage.CreateSynchronized(volume, muted);
```

### Strategy Pattern

Different connection strategies:
- `SendspinConnection`: Client-initiated
- `IncomingConnection`: Server-initiated
- Both implement `ISendspinConnection`

## Error Handling

### Connection Errors

**Automatic Recovery**:
- Connection lost: Auto-reconnect with exponential backoff
- Handshake timeout: Disconnect and retry
- WebSocket errors: Log and attempt reconnection

**Error States**:
- ConnectionState.Disconnected: Clean state, no connection
- ConnectionState.Reconnecting: Attempting to restore connection
- PlaybackState.Error: Audio playback error

### Logging

Structured logging with Microsoft.Extensions.Logging:
- **Trace**: Time sync details, binary message reception
- **Debug**: Message send/receive, state transitions
- **Information**: Connection lifecycle, discovery events
- **Warning**: Recoverable errors, unexpected conditions
- **Error**: Unrecoverable errors, exceptions

Log categories:
- `SendspinClient.Core.Client.SendspinClientService`
- `SendspinClient.Core.Connection.SendspinConnection`
- `SendspinClient.Core.Discovery.MdnsServerDiscovery`
- `SendspinClient.Core.Synchronization.KalmanClockSynchronizer`

## Performance Considerations

### Memory Efficiency

- **ArrayPool**: Used for WebSocket receive buffers
- **ReadOnlySpan**: Zero-copy binary message parsing
- **String pooling**: Constant message type strings

### Network Efficiency

- **Binary protocol**: Compact binary format for audio
- **Adaptive sync**: Reduces time sync frequency when stable
- **WebSocket compression**: Optional (server dependent)

### Audio Pipeline (Planned)

- **Lock-free ring buffer**: For audio sample queue
- **Jitter buffer**: Smooth out network variance
- **Sample interpolation**: Handle clock drift in playback

## Future Enhancements

### Short Term
- Audio playback implementation (NAudio integration)
- Codec support (Opus via Concentus, FLAC, PCM)
- UI improvements (track progress, visualizations)

### Long Term
- Multiple audio output device support
- Latency compensation configuration
- Audio passthrough to WASAPI
- Plugin architecture for custom codecs

## Testing Strategy

### Unit Tests (Planned)
- Message serialization/deserialization
- Clock synchronizer correctness
- Binary message parsing
- State machine transitions

### Integration Tests (Planned)
- End-to-end handshake
- Time synchronization convergence
- Reconnection scenarios
- mDNS discovery

### Manual Testing
- Connect to real Music Assistant server
- Monitor clock sync convergence
- Test network disruption recovery
- Multi-room playback verification

## References

- [Sendspin Protocol Specification](https://github.com/music-assistant/sendspin)
- [Music Assistant Documentation](https://music-assistant.io/)
- [NTP Algorithm (RFC 5905)](https://tools.ietf.org/html/rfc5905)
- [Kalman Filter Tutorial](https://www.kalmanfilter.net/)
