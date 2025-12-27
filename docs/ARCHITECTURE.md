# WindowsSpin - Architecture Documentation

This document provides a comprehensive technical overview of the WindowsSpin architecture, design decisions, and implementation details.

## Table of Contents

- [Overview](#overview)
- [Solution Structure](#solution-structure)
- [Audio Pipeline Architecture](#audio-pipeline-architecture)
- [Clock Synchronization](#clock-synchronization)
- [Protocol Implementation](#protocol-implementation)
- [Connection Management](#connection-management)
- [Threading Model](#threading-model)
- [Sync Correction Algorithm](#sync-correction-algorithm)
- [Design Patterns](#design-patterns)
- [Key Components Reference](#key-components-reference)

---

## Overview

WindowsSpin implements the Sendspin protocol for synchronized multi-room audio playback with Music Assistant. The architecture prioritizes:

- **Microsecond-precision timing**: Kalman filter-based clock synchronization
- **Low-latency audio**: WASAPI shared mode with ~50ms buffer
- **Reliability**: Automatic reconnection, robust error handling
- **Maintainability**: Clean 3-layer architecture with separation of concerns

### High-Level Data Flow

```
                    WebSocket Connection
                           |
                           v
+------------------+    +------------------+    +------------------+
|  SendspinClient  | -> | Clock Sync       | -> | Audio Pipeline   |
|  (Orchestrator)  |    | (Kalman Filter)  |    | (Decoder+Buffer) |
+------------------+    +------------------+    +------------------+
                                                         |
                                                         v
                                              +------------------+
                                              | WASAPI Player    |
                                              | (NAudio)         |
                                              +------------------+
                                                         |
                                                         v
                                                    [Speakers]
```

---

## Solution Structure

```
SendspinClient.sln
src/
  Sendspin.SDK/              # Cross-platform protocol SDK (NuGet package)
    Audio/                   # Audio pipeline, buffer, decoders
    Client/                  # Main client orchestration
    Connection/              # WebSocket connection handling
    Discovery/               # mDNS server discovery
    Extensions/              # Task extensions (SafeFireAndForget)
    Models/                  # Data models (GroupState, TrackMetadata)
    Protocol/                # Message serialization and types
    Synchronization/         # Clock sync (Kalman filter, high-precision timer)

  SendspinClient.Services/   # Windows-specific services
    Audio/                   # WASAPI player (NAudio)
    Discord/                 # Discord Rich Presence integration
    Notifications/           # Windows toast notifications

  SendspinClient/            # WPF desktop application
    ViewModels/              # MVVM view models
    Views/                   # XAML views
    Configuration/           # App settings
    Resources/               # Icons, converters
```

### Project Dependencies

```
SendspinClient (WPF App)
    |
    +-> SendspinClient.Services (Windows Audio)
    |       |
    |       +-> Sendspin.SDK
    |
    +-> Sendspin.SDK (Protocol Implementation)
            |
            +-> Concentus (Opus decoder)
            +-> Fleck (WebSocket server)
            +-> Zeroconf (mDNS discovery)
```

### NuGet Packages

**Sendspin.SDK** (cross-platform):
- `Concentus` (2.2.2) - Opus decoder
- `Fleck` (1.2.0) - WebSocket server for host mode
- `Zeroconf` (3.7.16) - mDNS discovery
- `Makaretu.Dns.Multicast` (0.27.0) - mDNS advertisement
- `System.Text.Json` (8.0.5) - JSON serialization

**SendspinClient.Services** (Windows):
- `NAudio` (2.2.1) - WASAPI audio output
- `DiscordRichPresence` (1.3.0.28) - Discord integration
- `Microsoft.Toolkit.Uwp.Notifications` (7.1.3) - Toast notifications

---

## Audio Pipeline Architecture

The audio pipeline is the most critical subsystem, handling the flow from encoded network audio to speaker output with precise timing.

### Components

```
[Network - Binary WebSocket Messages]
    |
    v
AudioPipeline (Orchestrator)
    |
    +-> IAudioDecoder (Opus/FLAC/PCM)
    |       |
    |       v (decoded float samples)
    +-> TimedAudioBuffer (Timestamp-based circular buffer)
    |       |
    |       v (samples at correct playback time)
    +-> BufferedAudioSampleSource (Bridge to NAudio)
    |       |
    |       v
    +-> WasapiAudioPlayer (WASAPI output)
            |
            v
        [Speakers]
```

### Key Files

| Component | File | Purpose |
|-----------|------|---------|
| AudioPipeline | `Sendspin.SDK/Audio/AudioPipeline.cs` | Orchestrates decoder, buffer, player |
| TimedAudioBuffer | `Sendspin.SDK/Audio/TimedAudioBuffer.cs` | Timestamp-based circular buffer with sync correction |
| WasapiAudioPlayer | `SendspinClient.Services/Audio/WasapiAudioPlayer.cs` | Windows audio output via NAudio |
| BufferedAudioSampleSource | `SendspinClient.Services/Audio/BufferedAudioSampleSource.cs` | Bridges TimedAudioBuffer to NAudio |

### Audio Flow States

```
Idle ──StartAsync()──> Starting ──> Buffering ──buffer ready──> Playing
  ^                                     |                           |
  |                                     |                           |
  +─────────────────── StopAsync() ─────+───────────────────────────+
                                        |
                                        v
                                 Error (recoverable)
```

### TimedAudioBuffer Design

The `TimedAudioBuffer` is a thread-safe circular buffer that:

1. **Receives** decoded audio samples with server timestamps
2. **Converts** server timestamps to local playback times via clock synchronizer
3. **Releases** samples when their playback time arrives
4. **Applies** sync correction (drop/insert frames) to compensate for drift

**Key insight from CLI reference**: Track samples READ from the buffer, not samples OUTPUT to speakers:

```csharp
// When DROPPING: read 2 frames, output 1 -> samplesRead advances faster
// When INSERTING: read 0 frames, output 1 -> samplesRead stays still
_samplesReadSinceStart += actualRead;  // NOT outputCount
```

### Supported Audio Codecs

| Codec | Implementation | Notes |
|-------|----------------|-------|
| Opus | `OpusDecoder` (Concentus) | Low-latency, preferred for streaming |
| FLAC | `FlacDecoder` (SimpleFlac) | Lossless, higher bandwidth |
| PCM | `PcmDecoder` | 16/24/32-bit, no compression |

---

## Clock Synchronization

Precise clock sync is essential for multi-room audio. The implementation uses a 2D Kalman filter to track both offset and drift.

### The Clock Offset Problem

- **Server uses monotonic time**: Starts near 0, counts up in microseconds
- **Client uses Unix epoch time**: ~1.7 trillion microseconds
- **Offset can be billions of microseconds**: This is normal and expected

### NTP-Style 4-Timestamp Exchange

```
Client                              Server
   |                                   |
   |------ T1 (client transmit) ------>|
   |                                   |
   |        T2 (server receive)        |
   |        T3 (server transmit)       |
   |                                   |
   |<----- T2, T3 (server response) ---|
   |                                   |
T4 (client receive)                    |
```

**Offset calculation**: `offset = ((T2 - T1) + (T3 - T4)) / 2`
**RTT calculation**: `RTT = (T4 - T1) - (T3 - T2)`

### Kalman Filter State

```
State vector: [offset, drift]
  - offset: server_time = client_time + offset (microseconds)
  - drift: rate of change of offset (microseconds/second)

Process model:
  offset(t+dt) = offset(t) + drift(t) * dt + noise
  drift(t+dt) = drift(t) + noise
```

### Key Parameters

```csharp
// Convergence thresholds
MinMeasurementsForConvergence = 5
MaxOffsetUncertaintyForConvergence = 1000.0  // 1ms

// Drift reliability threshold
MaxDriftUncertaintyForReliable = 50.0  // 50 us/s

// Process noise (filter tuning)
processNoiseOffset = 100.0    // us^2/s
processNoiseDrift = 1.0       // us^2/s^2
measurementNoise = 10000.0    // us^2 (~3ms std dev)
```

### Burst Synchronization

Instead of single measurements, the client sends bursts of 8 time sync messages and uses only the one with the smallest RTT (best quality):

```csharp
// Send burst
for (int i = 0; i < BurstSize; i++)  // BurstSize = 8
{
    await SendTimeMessageAsync();
    await Task.Delay(BurstIntervalMs);  // 50ms
}

// Wait for responses
await Task.Delay(BurstIntervalMs * 2);

// Use only the best result (smallest RTT)
var bestResult = _burstResults.OrderBy(r => r.rtt).First();
_clockSynchronizer.ProcessMeasurement(bestResult.t1, t2, t3, t4);
```

### Adaptive Sync Intervals

Sync frequency adapts based on current quality:

| Uncertainty | Interval | Rationale |
|-------------|----------|-----------|
| Not converged | 500ms | Rapid initial sync |
| < 1ms | 10s | Well synced, detect drift over time |
| < 2ms | 5s | Good sync |
| < 5ms | 2s | Moderate sync |
| > 5ms | 1s | Poor sync, need rapid correction |

### High-Precision Timer

Windows `DateTime` has ~15ms resolution. For microsecond precision, we use `Stopwatch`:

```csharp
// HighPrecisionTimer.cs
public long GetCurrentTimeMicroseconds()
{
    var currentTicks = Stopwatch.GetTimestamp();
    var elapsedTicks = currentTicks - _startTimestampTicks;
    var elapsedMicroseconds = (long)(elapsedTicks * _ticksToMicroseconds);
    return _startTimeUnixMicroseconds + elapsedMicroseconds;
}
```

---

## Protocol Implementation

The Sendspin protocol uses WebSocket with both JSON text messages and binary audio data.

### Message Types

**Text Messages (JSON)**:

| Direction | Type | Purpose |
|-----------|------|---------|
| Client -> Server | `client/hello` | Capability negotiation |
| Server -> Client | `server/hello` | Role activation |
| Client -> Server | `client/time` | Time sync request |
| Server -> Client | `server/time` | Time sync response |
| Client -> Server | `client/state` | Player state update |
| Client -> Server | `client/command` | Playback control |
| Server -> Client | `server/command` | Volume/mute commands |
| Server -> Client | `server/state` | Full state snapshot |
| Server -> Client | `group/update` | Incremental state changes |
| Server -> Client | `stream/start` | Audio stream beginning |
| Server -> Client | `stream/clear` | Clear buffer (seek) |
| Server -> Client | `stream/end` | Stream complete |

**Binary Messages**:

```
Format: [type:1 byte][timestamp:8 bytes big-endian][payload:N bytes]

Types:
  4-7:   Player audio slots 0-3
  8-11:  Artwork slots 0-3
  16-23: Visualizer slots 0-7
```

### Message Envelope Format

All JSON messages use an envelope format:

```json
{
  "type": "client/hello",
  "payload": {
    "client_id": "abc123",
    "name": "Windows Client",
    "supported_roles": ["player@v1"],
    ...
  }
}
```

### Audio Format Negotiation

The client advertises supported formats in `client/hello`:

```json
{
  "player": {
    "supported_formats": [
      {"codec": "flac", "sample_rate": 48000, "channels": 2, "bit_depth": 16},
      {"codec": "opus", "sample_rate": 48000, "channels": 2},
      {"codec": "pcm", "sample_rate": 48000, "channels": 2, "bit_depth": 16}
    ],
    "buffer_capacity": 8000,
    "supported_commands": ["volume", "mute"]
  }
}
```

The server selects a format and includes it in `stream/start`:

```json
{
  "type": "stream/start",
  "payload": {
    "player": {
      "codec": "flac",
      "sample_rate": 48000,
      "channels": 2,
      "bit_depth": 16
    }
  }
}
```

### Connection Handshake Flow

```
1. WebSocket Connect
2. Client -> Server: client/hello (capabilities, supported formats)
3. Server -> Client: server/hello (assigned roles, server info)
4. Client -> Server: client/state (initial player state)
5. Begin time sync loop (client/time <-> server/time)
6. Server -> Client: stream/start (when playback begins)
7. Server -> Client: Binary audio chunks
```

---

## Connection Management

### Connection States

```
Disconnected ──connect()──> Connecting ──success──> Handshaking
      ^                          |                       |
      |                          |               server/hello
      |                     (failure)                    |
      |                          v                       v
      +<──────────────── Reconnecting <────────────  Connected
                              ^                          |
                              |                          |
                              +───── (connection lost) ──+
```

### Automatic Reconnection

On connection loss, the client automatically attempts reconnection with exponential backoff:

```csharp
delay = baseDelay * pow(backoffMultiplier, attempt - 1)
delay = min(delay, maxDelay)

// Defaults: base=1000ms, multiplier=1.5, max=30000ms
```

### Host Mode (Server-Initiated Connections)

The client can also operate in "host mode", advertising via mDNS and accepting incoming connections:

```
Client (Host Mode)                  Music Assistant Server
       |                                     |
       |<---- mDNS Discovery (_sendspin) ----+
       |                                     |
       |<---- WebSocket Connection ----------+
       |                                     |
       |---- server/hello ------------------>|
       |                                     |
```

This is handled by `SendspinHostService` using Fleck WebSocket server.

---

## Threading Model

### Thread Responsibilities

| Thread | Responsibility |
|--------|----------------|
| UI Thread | WPF UI, ViewModel property updates |
| WebSocket Receive | Message parsing, event dispatch |
| Time Sync Task | Periodic NTP sync bursts |
| NAudio Callback | Audio buffer reads (high-priority) |

### Thread Safety

**Lock-based synchronization**:
- `TimedAudioBuffer`: Single lock for buffer operations
- `KalmanClockSynchronizer`: Lock for state updates

**Lock-free patterns**:
- `SendspinConnection`: SemaphoreSlim for send serialization
- `ConcurrentDictionary` for server discovery

**Event dispatch**:
- Events raised on connection thread
- UI updates marshaled via `Dispatcher.Invoke()`

### High-Priority Audio Thread

The NAudio callback runs on a high-priority thread. Critical operations in this path:

1. `BufferedAudioSampleSource.Read()` - Called by NAudio
2. `TimedAudioBuffer.Read()` - Gets samples, applies sync correction
3. Sync error calculation and correction rate updates

**Critical**: Avoid allocations and blocking in this path.

---

## Sync Correction Algorithm

The sync correction algorithm matches the CLI reference implementation in `audio.py`.

### Sync Error Calculation

```csharp
// Time since playback started
elapsedTimeMicroseconds = currentLocalTime - _playbackStartLocalTime;

// How much audio we've READ (not output)
samplesReadTimeMicroseconds = _samplesReadSinceStart * _microsecondsPerSample;

// Account for WASAPI output buffer delay (~50ms)
adjustedElapsedMicroseconds = elapsedTimeMicroseconds - OutputLatencyMicroseconds;

// Sync error: positive = behind (DROP), negative = ahead (INSERT)
_currentSyncErrorMicroseconds = adjustedElapsedMicroseconds - samplesReadTimeMicroseconds;
```

### Correction Constants (matching CLI)

```csharp
CorrectionDeadbandMicroseconds = 2_000       // 2ms - no correction for small errors
ReanchorThresholdMicroseconds = 500_000      // 500ms - clear buffer if error too large
MaxSpeedCorrection = 0.04                     // 4% max speed adjustment
CorrectionTargetSeconds = 2.0                 // Aim to fix error in 2 seconds
StartupGracePeriodMicroseconds = 500_000     // 500ms - don't correct immediately
```

### Drop/Insert Mechanism

**Dropping** (when behind, positive error):
- Read TWO frames from buffer
- Output the LAST frame (skip one)
- Effect: `samplesRead` advances faster, error shrinks

**Inserting** (when ahead, negative error):
- Output last frame WITHOUT reading
- Effect: `samplesRead` stays still, error grows toward 0

### Output Latency Compensation

**Problem**: Wall clock shows 50ms elapsed, but audio in WASAPI buffer hasn't played yet.

**Solution**: Subtract output latency from elapsed time before comparing:

```csharp
// Without compensation: sync error = 50ms - 0ms = 50ms (false positive)
// With compensation:    sync error = (50ms - 50ms) - 0ms = 0ms (correct)
var adjustedElapsed = elapsedTime - OutputLatencyMicroseconds;
```

### Anchor Point

**Critical**: Use ACTUAL start time, not intended playback time.

The server sends audio ~5 seconds ahead. The first chunk's `LocalPlaybackTime` is in the FUTURE. If we use that as anchor:
- `elapsed = now - (now + 5s) = -5 seconds`
- Immediately triggers re-anchor threshold

**Correct approach**:
```csharp
_playbackStartLocalTime = currentLocalTime;  // When we actually start reading
```

---

## Design Patterns

### Dependency Injection

The solution uses Microsoft.Extensions.DependencyInjection:

```csharp
services.AddSingleton<IClockSynchronizer, KalmanClockSynchronizer>();
services.AddSingleton<IAudioPipeline, AudioPipeline>();
services.AddTransient<ISendspinConnection, SendspinConnection>();
```

**Factory patterns** for runtime creation:

```csharp
Func<AudioFormat, IClockSynchronizer, ITimedAudioBuffer> bufferFactory
Func<IAudioPlayer> playerFactory
```

### Observer Pattern

Event-based notifications throughout:

```csharp
// Connection events
connection.StateChanged += OnConnectionStateChanged;
connection.TextMessageReceived += OnTextMessageReceived;
connection.BinaryMessageReceived += OnBinaryMessageReceived;

// Client events
client.GroupStateChanged += OnGroupStateChanged;
client.ArtworkReceived += OnArtworkReceived;
client.ClockSyncConverged += OnClockSyncConverged;
```

### Strategy Pattern

Different connection strategies via common interface:

```csharp
interface ISendspinConnection
{
    Task ConnectAsync(Uri serverUri, CancellationToken ct);
    Task SendMessageAsync<T>(T message, CancellationToken ct);
    // ...
}

// Implementations:
// - SendspinConnection (client-initiated, outgoing)
// - IncomingConnection (server-initiated, incoming)
```

### MVVM Pattern

The WPF application uses CommunityToolkit.Mvvm:

```csharp
public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _currentTrackTitle = string.Empty;

    [RelayCommand]
    private async Task PlayAsync()
    {
        await _client.SendCommandAsync("play");
    }
}
```

---

## Key Components Reference

### Sendspin.SDK

| Class | File | Purpose |
|-------|------|---------|
| `SendspinClientService` | `Client/SendSpinClient.cs` | Main client orchestration, message handling |
| `SendspinConnection` | `Connection/SendSpinConnection.cs` | WebSocket connection with auto-reconnect |
| `KalmanClockSynchronizer` | `Synchronization/KalmanClockSynchronizer.cs` | 2D Kalman filter for clock sync |
| `HighPrecisionTimer` | `Synchronization/HighPrecisionTimer.cs` | Stopwatch-based microsecond timing |
| `AudioPipeline` | `Audio/AudioPipeline.cs` | Audio decoder/buffer/player orchestration |
| `TimedAudioBuffer` | `Audio/TimedAudioBuffer.cs` | Timestamp-based circular buffer with sync correction |
| `MessageSerializer` | `Protocol/MessageSerializer.cs` | JSON serialization with snake_case |
| `BinaryMessageParser` | `Protocol/BinaryMessageParser.cs` | Zero-copy binary message parsing |
| `MdnsServerDiscovery` | `Discovery/MdnsServerDiscovery.cs` | mDNS service discovery |

### SendspinClient.Services

| Class | File | Purpose |
|-------|------|---------|
| `WasapiAudioPlayer` | `Audio/WasapiAudioPlayer.cs` | WASAPI shared mode audio output |
| `BufferedAudioSampleSource` | `Audio/BufferedAudioSampleSource.cs` | NAudio ISampleProvider bridge |
| `DiscordRichPresenceService` | `Discord/DiscordRichPresenceService.cs` | Discord integration |
| `WindowsToastNotificationService` | `Notifications/WindowsToastNotificationService.cs` | Windows notifications |

### SendspinClient (WPF)

| Class | File | Purpose |
|-------|------|---------|
| `MainViewModel` | `ViewModels/MainViewModel.cs` | Main window view model |
| `StatsViewModel` | `ViewModels/StatsViewModel.cs` | Sync statistics display |
| `App.xaml.cs` | `App.xaml.cs` | DI container setup, startup |

---

## Performance Considerations

### Hot Path Optimizations

The audio callback is performance-critical:

1. **ArrayPool**: Used for WebSocket receive buffers
2. **ReadOnlySpan**: Zero-copy binary parsing
3. **Avoid allocations**: Pre-allocated decode buffers
4. **Lock-free where possible**: Interlocked for event coalescing

### Memory Efficiency

- Circular buffer with fixed allocation
- Pre-allocated frame buffers for sync correction
- Pooled byte arrays for network I/O

### Network Efficiency

- Binary protocol for audio (no JSON overhead)
- Adaptive sync intervals reduce traffic when stable
- Burst sync uses single best measurement

---

## Reference Implementation

The Python CLI reference implementation is the gold standard for sync behavior.

**Location**: `C:\Users\chris\Downloads\sendspin-cli-main\sendspin-cli-main\sendspin\audio.py`

**Key reference points**:
- **Line 1032**: Sync error calculation
- **Line 472**: DAC timing callbacks
- **clear()**: Reset behavior for track changes

**IMPORTANT**: Always consult the CLI when making changes to sync/timing logic. Don't guess - read the Python code.

---

## External References

- [Sendspin Protocol](https://github.com/music-assistant/sendspin)
- [Music Assistant](https://music-assistant.io/)
- [NTP Algorithm (RFC 5905)](https://tools.ietf.org/html/rfc5905)
- [Kalman Filter Tutorial](https://www.kalmanfilter.net/)
- [NAudio Documentation](https://github.com/naudio/NAudio)
