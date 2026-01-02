# Sendspin Windows Client

## Project Overview

A Windows desktop application for synchronized multi-room audio playback using the Sendspin protocol. The client connects to Music Assistant servers and plays audio in perfect sync with other Sendspin players across your network.

### Core Value Proposition
- **Synchronized multi-room audio**: Multiple Sendspin clients can play the same audio stream in perfect sync
- **Native Windows experience**: System tray integration, toast notifications, Discord Rich Presence
- **Low-latency audio**: WASAPI output with sub-millisecond sync accuracy via Kalman filter clock synchronization

### Design Principles
1. **Simple** - Easy to use for end users
2. **Easy to sync** - Multi-room playback is the core feature; sync accuracy is critical
3. **Maintainable** - Easy for other engineers to contribute to

---

## Architecture

### Three-Tier Project Structure

```
src/
├── Sendspin.SDK/                # Cross-platform protocol SDK (NuGet package)
│   ├── Audio/                   # Decoding, buffering, and pipeline orchestration
│   ├── Client/                  # Protocol client and host services
│   ├── Connection/              # WebSocket transport layer
│   ├── Discovery/               # mDNS service discovery and advertisement
│   ├── Protocol/                # Message serialization and types
│   └── Synchronization/         # Clock sync (Kalman filter)
│
├── SendspinClient.Services/     # Windows-specific service implementations
│   ├── Audio/                   # WASAPI player via NAudio
│   ├── Discord/                 # Discord Rich Presence integration
│   └── Notifications/           # Windows toast notifications
│
└── SendspinClient/              # WPF desktop application
    ├── Configuration/           # Settings management
    ├── ViewModels/              # MVVM view models
    └── Views/                   # XAML views
```

> **Historical Note**: Prior to v2.0.0, the core protocol implementation was in `SendspinClient.Core`. This was renamed to `Sendspin.SDK` to support NuGet packaging and cross-platform use.

### Dependency Flow
```
SendspinClient (WPF)
    └─▶ SendspinClient.Services (Windows-specific)
    └─▶ Sendspin.SDK (Cross-platform)
```

The SDK contains no Windows dependencies—it can be used to build players for other platforms.

---

## Quick Start

### Prerequisites
- .NET 10 SDK (or .NET 8 for older branches)
- Visual Studio 2022+ or VS Code with C# extension
- Windows 10/11 (for running the WPF app)

### Build Commands
```bash
# Restore packages
dotnet restore

# Build (Debug)
dotnet build

# Build (Release)
dotnet build -c Release

# Run the WPF client
dotnet run --project src/SendspinClient/SendspinClient.csproj

# Publish for distribution
dotnet publish src/SendspinClient/SendspinClient.csproj -c Release -r win-x64
```

### Solution File
```bash
# Build entire solution
dotnet build SendspinClient.sln
```

---

## Connection Modes

The client supports two connection modes:

### 1. Client-Initiated Mode (Primary)
We discover servers via mDNS and connect to them. This is the recommended mode.
- Uses `MdnsServerDiscovery` to find `_sendspin-server._tcp` services
- Client connects to server's WebSocket endpoint
- More reliable for cross-subnet scenarios

### 2. Server-Initiated Mode (Fallback)
We advertise via mDNS and servers connect to us.
- `SendspinHostService` runs a WebSocket server on a random port
- Advertises as `_sendspin._tcp.local`
- Music Assistant servers discover and connect to us

Both modes use the same protocol and can be used simultaneously.

---

## Key Technical Concepts

### Clock Synchronization

The Kalman filter synchronizes local time with server time for sample-accurate multi-room sync.

**Key file**: `src/Sendspin.SDK/Synchronization/KalmanClockSynchronizer.cs`

```
Server sends: server timestamp (monotonic, near 0)
Client has:   local Unix epoch time (microseconds)
Offset:       can be billions of microseconds - THIS IS NORMAL
```

The `IClockSynchronizer` interface provides:
- `ServerToLocalTime(serverTimestamp)` - Convert server time to local playback time
- `GetStatus()` - Current offset, drift rate, and convergence status

**Kalman Configuration** (via appsettings.json):
```json
{
  "Audio": {
    "ClockSync": {
      "ForgetFactor": 1.001,
      "AdaptiveCutoff": 0.75,
      "MinSamplesForForgetting": 100
    }
  }
}
```

### Audio Pipeline

The pipeline orchestrates audio from network to speakers:

**Key file**: `src/Sendspin.SDK/Audio/AudioPipeline.cs`

```
Network → Decoder → TimedAudioBuffer → SampleSource → WASAPI
   │         │              │                │            │
   │         │              │                │            └── WasapiAudioPlayer
   │         │              │                └── BufferedAudioSampleSource
   │         │              └── Stores samples with playback timestamps
   │         └── Opus/FLAC/PCM decoding
   └── WebSocket binary messages
```

**Pipeline States**:
- `Idle` → `Starting` → `Buffering` → `Playing` → `Stopping` → `Idle`
- Can transition to `Error` from any state

### TimedAudioBuffer & Sync Correction

**Key file**: `src/Sendspin.SDK/Audio/TimedAudioBuffer.cs`

The buffer handles:
1. Storing PCM samples with server timestamps
2. Converting timestamps to local playback time
3. Tracking sync error (drift between expected and actual playback)
4. Applying sync correction (drop/insert samples)

**Sync Error Calculation**:
```csharp
// Track samples READ, not samples OUTPUT
// When DROPPING: read 2, output 1 → samplesRead += 2
// When INSERTING: read 0, output 1 → samplesRead += 0

syncError = elapsedTime - samplesReadTime - outputLatency
// Positive = behind (need DROP)
// Negative = ahead (need INSERT)
```

**Critical Constants** (matching CLI):
```csharp
CorrectionDeadbandMicroseconds = 2_000;      // 2ms - ignore smaller errors
ReanchorThresholdMicroseconds = 500_000;     // 500ms - clear buffer and restart
MaxSpeedCorrection = 0.04;                    // 4% max correction rate
CorrectionTargetSeconds = 2.0;                // Time to correct error
```

### High-Precision Timer

**Key file**: `src/Sendspin.SDK/Synchronization/HighPrecisionTimer.cs`

Windows `DateTime` only has ~15ms resolution. For microsecond-accurate sync, we use `Stopwatch.GetTimestamp()` which uses hardware performance counters (~100ns resolution).

```csharp
// Good: ~100ns resolution
var time = HighPrecisionTimer.Shared.GetCurrentTimeMicroseconds();

// Bad: ~15ms resolution
var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
```

---

## Protocol Messages

### Message Types (JSON over WebSocket text frames)
```
client/hello        - Initial handshake from client
server/hello        - Server handshake response
client/time         - Clock sync ping
server/time         - Clock sync response with 4 timestamps
stream/start        - New audio stream beginning
stream/end          - Audio stream ended
stream/clear        - Clear buffer (track change)
group/update        - Playback state change (play/pause/stop, metadata)
client/command      - Client sends command (play, pause, volume, etc.)
```

### Binary Messages (WebSocket binary frames)
First byte indicates message type:
- `4-7`: Player audio (slot 0-3)
- `8-11`: Artwork (slot 0-3)
- `16-23`: Visualizer data (slot 0-7)

Audio binary format:
```
[1 byte: type] [8 bytes: server timestamp (µs)] [encoded audio data]
```

---

## Configuration

Settings are stored in two locations:
1. **Install directory**: `appsettings.json` (defaults, read-only after install)
2. **User AppData**: `%LOCALAPPDATA%\Sendspin\appsettings.json` (user overrides)

### Key Configuration Options

```json
{
  "Logging": {
    "LogLevel": "Warning",
    "EnableFileLogging": false,
    "EnableConsoleLogging": false,
    "LogDirectory": "",
    "MaxFileSizeMB": 1,
    "RetainedFileCount": 5
  },
  "Audio": {
    "StaticDelayMs": 200,
    "DeviceId": null,
    "ClockSync": { ... }
  },
  "Player": {
    "Name": "My PC"
  },
  "Discord": {
    "Enabled": false,
    "ApplicationId": "1454545426500813014"
  },
  "Notifications": {
    "Enabled": true
  },
  "Connection": {
    "AutoConnectServerId": ""
  }
}
```

### Static Delay Tuning
If this player consistently plays behind/ahead of others:
- **Positive values**: Play later (if this player is ahead of others)
- **Negative values**: Play earlier (if this player is behind others)
- Typical range: -500ms to +500ms

---

## MVVM Architecture

### ViewModels

**Main entry point**: `src/SendspinClient/ViewModels/MainViewModel.cs`

Uses CommunityToolkit.Mvvm with source generators:
```csharp
[ObservableProperty]
private bool _isHosting;           // Generates IsHosting property + OnIsHostingChanged

[RelayCommand]
private async Task PlayPauseAsync() // Generates PlayPauseCommand
```

### Dependency Injection

Services are registered in `src/SendspinClient/App.xaml.cs`:
```csharp
services.AddSingleton<IClockSynchronizer, KalmanClockSynchronizer>();
services.AddSingleton<IAudioPipeline, AudioPipeline>();
services.AddTransient<IAudioPlayer, WasapiAudioPlayer>();
```

Key pattern: Factories are used for components needing runtime parameters:
```csharp
services.AddSingleton<IAudioPipeline>(sp => new AudioPipeline(
    logger,
    decoderFactory,
    clockSync,
    bufferFactory: (format, sync) => new TimedAudioBuffer(...),
    playerFactory: () => sp.GetRequiredService<IAudioPlayer>(),
    ...));
```

---

## Windows-Specific Services

### WASAPI Audio Player
**File**: `src/SendspinClient.Services/Audio/WasapiAudioPlayer.cs`

Uses NAudio's `WasapiOut` for low-latency audio output. Key responsibilities:
- Initialize audio device with specific format
- Report output latency (used for sync error compensation)
- Support hot-switching between audio devices

### Toast Notifications
**File**: `src/SendspinClient.Services/Notifications/WindowsToastNotificationService.cs`

Uses Microsoft.Toolkit.Uwp.Notifications for Windows toast notifications:
- Track change notifications
- Connection/disconnection alerts
- Suppressed when main window is visible and active

### Discord Rich Presence
**File**: `src/SendspinClient.Services/Discord/DiscordRichPresenceService.cs`

Shows currently playing track in Discord status using discord-rpc-csharp.

---

## Reference Implementation

### Gold Standard
**Location**: `C:\Users\chris\Downloads\sendspin-cli-main`

The Python CLI player is the reference implementation. When implementing or fixing sync, audio buffering, or timing logic, **always refer to the CLI code**. Don't guess—read the Python code.

**Reference file**: `C:\Users\chris\Downloads\sendspin-cli-main\sendspin-cli-main\sendspin\audio.py`

### Key CLI Reference Points

**Sync error calculation** (audio.py:1032):
```python
sync_error_us = self._last_known_playback_position_us - self._server_ts_cursor_us
```
Both values are in **server timestamp space**.

**Clear/Reset behavior** - When `clear()` is called, the CLI resets **EVERYTHING**:
```python
self._playback_state = PlaybackState.INITIALIZING
self._scheduled_start_loop_time_us = None
self._server_ts_cursor_us = 0
self._sync_error_filter.reset()
self._insert_every_n_frames = 0
self._drop_every_n_frames = 0
# ... all timing state reset
```

**CLI Constants**:
```python
_CORRECTION_DEADBAND_US = 2_000      # 2ms
_REANCHOR_THRESHOLD_US = 500_000     # 500ms
_MAX_SPEED_CORRECTION = 0.04         # 4%
_CORRECTION_TARGET_SECONDS = 2.0     # Fix error in 2 seconds
```

**CLI's DAC Timing** (audio.py:472):
```python
dac_time_us = int(time.outputBufferDacTime * 1_000_000)
self._dac_loop_calibrations.append((dac_time_us, loop_time_us))
estimated_position = self._compute_server_time(loop_at_dac_us)
self._last_known_playback_position_us = estimated_position
```

---

## Common Gotchas

### 1. Clock Offset Can Be Billions
Server uses monotonic time (starts near 0). Client uses Unix epoch. The offset is huge—this is correct behavior.

### 2. Use ACTUAL Start Time for Anchor
```csharp
// WRONG - firstSegment.LocalPlaybackTime is 5 seconds in the future!
_playbackStartLocalTime = firstSegment.LocalPlaybackTime;

// CORRECT - use when playback actually starts
_playbackStartLocalTime = currentLocalTime;
```

The server sends audio ~5 seconds ahead. The first chunk's `LocalPlaybackTime` is in the FUTURE. If you use that as anchor, you get `elapsed = now - (now + 5s) = -5 seconds`—a massive negative error that triggers re-anchor threshold (500ms) and creates an infinite loop.

### 3. Track Samples READ, Not OUTPUT
```csharp
// When dropping: read 2 frames, output 1
_samplesReadSinceStart += actualRead;  // += 2, not += 1

// When inserting: read 0 frames, output 1
_samplesReadSinceStart += actualRead;  // += 0
```

### 4. Output Latency Compensation
WASAPI has ~34-50ms output buffer latency. Sync error must account for this:
```csharp
var adjustedElapsed = elapsedTime - OutputLatencyMicroseconds;
_syncError = adjustedElapsed - samplesReadTime;
```

Without this compensation, you'll see a constant offset equal to the output buffer latency.

### 5. DateTime Resolution
Use `HighPrecisionTimer` for any timing-critical code:
```csharp
// Bad: ~15ms resolution
DateTime.UtcNow

// Good: ~100ns resolution
HighPrecisionTimer.Shared.GetCurrentTimeMicroseconds()
```

### 6. Track Change / Stream Restart
When tracks change, the sync error calculation can get into a stuck "dropping" state. The buffer's `Clear()` method must reset **all** timing state, matching CLI's behavior.

---

## Testing & Debugging

### Logs Location
`%LOCALAPPDATA%\Sendspin\logs\windowsspin-{date}.log`

### Stats Window
The app includes a "Stats for Nerds" window showing:
- Current sync error
- Buffer depth
- Drop/insert counts
- Clock offset and drift

Access via Settings → Stats for Nerds (see `src/SendspinClient/ViewModels/StatsViewModel.cs`)

### Debug Logging
Enable verbose logging in appsettings.json:
```json
{
  "Logging": {
    "LogLevel": "Debug",
    "EnableFileLogging": true
  }
}
```

**Warning**: Verbose logging impacts performance and creates large log files.

---

## CI/CD Pipeline

### GitHub Actions Workflows

**CI** (`.github/workflows/ci.yml`):
- Runs on push/PR to master
- Builds Release configuration
- Creates signed dev prereleases for every push

**Release** (`.github/workflows/release.yml`):
- Triggered by version tags (v*)
- Builds installers (framework-dependent and self-contained)
- Signs executables using Azure Trusted Signing
- Creates GitHub release with artifacts

### Build Artifacts
- `WindowsSpin-{version}-Setup.exe` - Installer (requires .NET 10 runtime)
- `WindowsSpin-{version}-Setup-SelfContained.exe` - Standalone installer
- `WindowsSpin-{version}-portable-win-x64.zip` - Portable ZIP

---

## NuGet Package: Sendspin.SDK

> **Important**: The `src/Sendspin.SDK/` project in this repository is the source for the [Sendspin.SDK NuGet package](https://www.nuget.org/packages/Sendspin.SDK). Changes to this project affect external consumers who depend on the NuGet package.

### When to Publish to NuGet

Publish a new version when SDK changes include:
- **Bug fixes** that affect SDK consumers (bump patch: 2.1.0 → 2.1.1)
- **New features** like new protocol messages, events, or public APIs (bump minor: 2.1.0 → 2.2.0)
- **Breaking changes** to interfaces or behavior (bump major: 2.x → 3.0.0)

Do NOT publish for:
- Changes only to `SendspinClient` or `SendspinClient.Services` (Windows app only)
- Internal refactoring that doesn't change public API
- Documentation-only changes

### Publishing Checklist

1. **Update version** in `src/Sendspin.SDK/Sendspin.SDK.csproj`:
   ```xml
   <Version>X.Y.Z</Version>
   ```

2. **Update release notes** in the same file:
   ```xml
   <PackageReleaseNotes>
   vX.Y.Z:
   - Description of changes
   ...
   </PackageReleaseNotes>
   ```

3. **Build and pack**:
   ```bash
   dotnet pack src/Sendspin.SDK/Sendspin.SDK.csproj -c Release
   ```

4. **Publish to NuGet**:
   ```bash
   dotnet nuget push src/Sendspin.SDK/bin/Release/Sendspin.SDK.X.Y.Z.nupkg --api-key YOUR_KEY --source https://api.nuget.org/v3/index.json
   ```

### Semantic Versioning

Follow [SemVer](https://semver.org/):
- **MAJOR** (3.0.0): Breaking changes to public interfaces
- **MINOR** (2.1.0): New features, backward compatible
- **PATCH** (2.0.1): Bug fixes, backward compatible

---

## Code Conventions

### Naming
- Private fields: `_camelCase`
- Properties: `PascalCase`
- Constants: `PascalCase`
- Interfaces: `IInterfaceName`

### Async Patterns
- Suffix async methods with `Async`
- Use `CancellationToken` for cancelable operations
- Fire-and-forget uses `.SafeFireAndForget(logger)` extension

### Logging
- Use structured logging: `_logger.LogInformation("Connected to {ServerName}", name)`
- Use appropriate levels: Error (failures), Warning (recoverable), Information (state changes), Debug (details)

### Null Safety
- Enable nullable reference types
- Use `required` modifier for required init properties
- Validate inputs at public API boundaries

### Documentation
- XML docs on all public APIs
- Use `<remarks>` for implementation details
- Include `<example>` where helpful

---

## Key Interfaces

| Interface | Purpose | Implementation |
|-----------|---------|----------------|
| `IClockSynchronizer` | Server↔local time conversion | `KalmanClockSynchronizer` |
| `IAudioPipeline` | Audio flow orchestration | `AudioPipeline` |
| `IAudioPlayer` | Platform audio output | `WasapiAudioPlayer` |
| `ITimedAudioBuffer` | Timestamped sample storage | `TimedAudioBuffer` |
| `IAudioDecoder` | Codec decoding | `OpusDecoder`, `FlacDecoder` |
| `ISendspinConnection` | WebSocket transport | `SendspinConnection`, `IncomingConnection` |
| `IServerDiscovery` | mDNS server discovery | `MdnsServerDiscovery` |
| `INotificationService` | Toast notifications | `WindowsToastNotificationService` |
| `IDiscordRichPresenceService` | Discord integration | `DiscordRichPresenceService` |

---

## File Quick Reference

| File | Purpose |
|------|---------|
| `src/SendspinClient/App.xaml.cs` | DI setup, startup, shutdown |
| `src/SendspinClient/ViewModels/MainViewModel.cs` | Primary UI state and commands |
| `src/Sendspin.SDK/Audio/AudioPipeline.cs` | Audio flow orchestration |
| `src/Sendspin.SDK/Audio/TimedAudioBuffer.cs` | Sync-aware sample buffer |
| `src/Sendspin.SDK/Synchronization/KalmanClockSynchronizer.cs` | Clock sync algorithm |
| `src/Sendspin.SDK/Synchronization/HighPrecisionTimer.cs` | Microsecond-precision timing |
| `src/Sendspin.SDK/Client/SendSpinHostService.cs` | Server-initiated connection mode |
| `src/Sendspin.SDK/Client/SendSpinClient.cs` | Client-initiated connection mode |
| `src/SendspinClient.Services/Audio/WasapiAudioPlayer.cs` | Windows audio output |
| `src/Sendspin.SDK/Protocol/Messages/MessageTypes.cs` | Protocol message definitions |

---

## Sync Error Deep Dive

### How Sync Error Works

```
Initial: error = 0ms (just started)
After 1 second: wall clock = 1000ms, samplesReadTime = 1000ms → error = 0
If reading slow: wall clock = 1000ms, samplesReadTime = 990ms → error = +10ms → DROP
After dropping: samplesReadTime advances faster → error shrinks
```

### Output Latency Problem

The CLI uses PyAudio's `outputBufferDacTime` to know exactly when audio reaches the speaker. WASAPI doesn't expose this. When comparing wall clock vs samples read:
- Wall clock: "50ms has passed"
- Samples read: 50ms worth
- Samples at speaker: ~0ms (still in WASAPI's 50ms output buffer!)

**Solution**: Subtract output latency from elapsed time:
```csharp
var adjustedElapsedMicroseconds = elapsedTimeMicroseconds - OutputLatencyMicroseconds;
_currentSyncErrorMicroseconds = adjustedElapsedMicroseconds - samplesReadTimeMicroseconds;
```

This asks "how much audio has actually played through the speaker?" instead of "how much wall clock time has passed?"
