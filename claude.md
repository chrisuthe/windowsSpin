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

### Project Structure

```
src/
├── Sendspin.Windows.Services/   # Windows-specific service implementations
│   ├── Audio/                   # WASAPI player via NAudio
│   ├── Discord/                 # Discord Rich Presence integration
│   └── Notifications/           # Windows toast notifications
│
└── Sendspin.Windows/            # WPF desktop application
    ├── Configuration/           # Settings management
    ├── ViewModels/              # MVVM view models
    └── Views/                   # XAML views
```

### External Dependencies
- **[Sendspin.SDK](https://www.nuget.org/packages/Sendspin.SDK)** (NuGet package) — Cross-platform protocol SDK providing audio pipeline, clock sync, protocol messages, mDNS discovery, and codec decoding. Source lives at [Sendspin/sendspin-dotnet](https://github.com/Sendspin/sendspin-dotnet).

### Dependency Flow
```
Sendspin.Windows (WPF)
    └─▶ Sendspin.Windows.Services (Windows-specific)
    └─▶ Sendspin.SDK (NuGet package)
```

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
dotnet run --project src/Sendspin.Windows/Sendspin.Windows.csproj

# Publish for distribution
dotnet publish src/Sendspin.Windows/Sendspin.Windows.csproj -c Release -r win-x64
```

### Solution File
```bash
# Build entire solution
dotnet build Sendspin.Windows.sln
```

---

## Connection Modes

The client supports two connection modes, and per the
[spec](https://github.com/sendspin/spec/blob/main/connection.md) it **MUST use exactly one
of them at a time**:

> Servers must support both methods described below. Clients MUST use exactly one of the two
> methods at a time, advertising or discovering accordingly.

The spec restates this as two explicit prohibitions: do not manually connect to servers while
advertising `_sendspin._tcp`, and do not advertise `_sendspin._tcp` if the client plans to
initiate the connection. Running both transports concurrently is a protocol violation, not a
convenience — see "No Auto mode" below.

The mode is chosen by `Connection.Mode` in `appsettings.json` and switched at runtime through
`MainViewModel.ApplyConnectionModeAsync`, which always stops the outgoing transport before
starting the incoming one and aborts the switch if that stop fails.

### 1. Server-Initiated Mode — `AdvertiseOnly` (Primary, default)
We advertise via mDNS and servers connect to us. **This is the default and the spec-recommended
mode.**
- `SendspinHostService` runs a WebSocket server (recommended port 8928)
- Advertises as `_sendspin._tcp.local` with a required `path` TXT record
- Music Assistant servers discover and connect to us
- Server admission (which server wins when several connect) is arbitrated inside the SDK's
  `SendspinHostService` — the app does no arbitration of its own, and must not reimplement or
  override it
- What the spec defines vs. what ships today:
  - **Spec:** admission is ranked by connection *activity* — `management` > `playback` >
    `pairing` — with a new connection held provisional until the server sends
    `server/activate`, and dropped after 30s if it never does.
  - **SDK 9.3.0 (the version this branch builds against):** arbitration is the earlier
    `connection_reason` comparison — `"playback"` beats `"discovery"` — with
    `LastPlayedServerId` breaking ordinary ties. Activity-based arbitration arrives with the
    v10 SDK; until then, do not write app code that assumes it.
- Discovery of `_sendspin-server._tcp` is NOT running in this mode

Preferred because multi-server behavior is *standardized* here. The spec defines admission
precisely: the client holds at most one admitted connection, ranked by its highest declared
activity (`management` > `playback` > `pairing`, empty lowest); an incoming connection is
compared against the current one and accepted on higher-or-equal priority, rejected on lower.
A connection stays provisional until its first `server/activate` and is dropped after 30s.
When both sides declare empty activities, the persistently stored "last-playback server"
breaks the tie. Displaced connections get `client/goodbye` reason `another_server`; rejected
incoming ones get `concurrent_attempt`.

`SendspinHostService` implements this arbitration — do not reimplement it in the app, and do
not override its decisions (for example by calling `DisconnectAllAsync` to reject a single
unwanted connection; that tears down every connection and resets the shared `IAudioPipeline`
and `IClockSynchronizer`).

### 2. Client-Initiated Mode — `DiscoverOnly` (Alternative)
We discover servers via mDNS and connect out to them. Retained as an explicit opt-in for
networks where the server cannot discover or reach us (different subnet/VLAN, client-isolating
AP, restrictive firewall, containerized servers).
- Uses `MdnsServerDiscovery` to find `_sendspin-server._tcp` services
- Client connects to the server's WebSocket endpoint
- Also covers manual connection by URL (`ConnectToServerAsync`, which refuses to run in any
  other mode)
- The host service is stopped in this mode: we neither advertise `_sendspin._tcp` nor accept
  incoming connections

The trade-off is that the spec gives no help here: "How clients handle multiple discovered
servers, server selection, and switching is implementation-defined." Every multi-server
behavior we want on this path we have to build and maintain ourselves.

### No Auto mode
There is deliberately no mode that runs both transports. A prior `Connection:Mode = "Auto"`
default did exactly that and violated the MUST above; it also left `SendspinHostService`
arbitrating without visibility of the client-initiated connection, since per spec that state
cannot occur. Removed in [#76](https://github.com/chrisuthe/windowsSpin/issues/76); existing
configs carrying `"Auto"` migrate to server-initiated.

---

## Key Technical Concepts

### Clock Synchronization

The Kalman filter synchronizes local time with server time for sample-accurate multi-room sync.

**SDK class**: `KalmanClockSynchronizer` (in Sendspin.SDK — [source](https://github.com/Sendspin/sendspin-dotnet))

```
Server sends: server timestamp (monotonic, near 0)
Client has:   local Unix epoch time (microseconds)
Offset:       can be billions of microseconds - THIS IS NORMAL
```

The `IClockSynchronizer` interface provides:
- `ServerToClientTime(serverTimestamp)` - Convert server time to local playback time (subtracts the configured static delay — audio scheduling semantics)
- `GetStatus()` - Current offset, drift rate, and convergence status

**Kalman Configuration** (via appsettings.json):
```json
{
  "Audio": {
    "ClockSync": {
      "ForgetFactor": 2.0,
      "AdaptiveCutoff": 3.0,
      "MinSamplesForForgetting": 100
    }
  }
}
```

### Audio Pipeline

The pipeline orchestrates audio from network to speakers:

**SDK class**: `AudioPipeline` (in Sendspin.SDK)

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

**SDK class**: `TimedAudioBuffer` (in Sendspin.SDK)

The buffer handles:
1. Storing PCM samples with server timestamps
2. Converting timestamps to local playback time
3. Tracking sync error (drift between expected and actual playback)
4. Applying tiered sync correction (resampling for small errors, drop/insert for larger)

**Tiered Sync Correction Strategy** (matching JS client):
| Sync Error | Correction Method | Notes |
|------------|-------------------|-------|
| < 2ms | None (deadband) | Imperceptible error, no action needed |
| 2-15ms | Playback rate adjustment (0.96x-1.04x) | Smooth, inaudible via `TargetPlaybackRate` |
| 15-500ms | Frame drop/insert | Faster correction for larger drift |
| > 500ms | Re-anchor | Clear buffer and restart sync |

**Resampling Sync Correction** (v2.2.0+):
- `ITimedAudioBuffer.TargetPlaybackRate` exposes the desired rate (1.0 = normal)
- `TargetPlaybackRateChanged` event notifies when rate changes
- Windows app uses `DynamicResamplerSampleProvider` (NAudio's WdlResampler) to apply rate
- Human pitch perception threshold is ~±3%, we use up to ±4% for inaudible corrections

**Sync Error Calculation**:
```csharp
// Track samples READ, not samples OUTPUT
// When DROPPING: read 2, output 1 → samplesRead += 2
// When INSERTING: read 0, output 1 → samplesRead += 0

syncError = elapsedTime - samplesReadTime - outputLatency
// Positive = behind (need DROP or speed up)
// Negative = ahead (need INSERT or slow down)
```

**Sync Correction Constants** (default values):
```csharp
DeadbandMicroseconds = 2_000;                 // 2ms - start correcting when error exceeds this
ResamplingThresholdMicroseconds = 15_000;     // 15ms - resampling vs drop/insert boundary
ReanchorThresholdMicroseconds = 500_000;      // 500ms - clear buffer and restart
MaxSpeedCorrection = 0.02;                    // 2% max correction rate (Windows default)
CorrectionTargetSeconds = 3.0;                // Time to correct error
```

**Configurable Sync Correction** (v3.3.0+):

These constants are now configurable via `SyncCorrectionOptions`. Pass custom options to `TimedAudioBuffer`:

```csharp
// Use CLI-compatible aggressive settings
var buffer = new TimedAudioBuffer(format, clockSync, capacityMs,
    SyncCorrectionOptions.CliDefaults, logger);

// Or customize individual parameters
var options = new SyncCorrectionOptions
{
    MaxSpeedCorrection = 0.04,        // 4% like CLI
    CorrectionTargetSeconds = 2.0,    // Faster convergence
    BypassDeadband = true,            // Continuous correction
};
var buffer = new TimedAudioBuffer(format, clockSync, capacityMs, options, logger);
```

**Static Presets**:
- `SyncCorrectionOptions.Default` - Windows defaults (conservative: 2% max, 3s target)
- `SyncCorrectionOptions.CliDefaults` - Python CLI defaults (aggressive: 4% max, 2s target)

### Clock Sync Gating

**SDK class**: `AudioPipeline` (in Sendspin.SDK)

The pipeline waits for `IClockSynchronizer.IsConverged` before starting playback. This ensures the Kalman filter has enough measurements to provide accurate timestamp conversion.

```csharp
// AudioPipeline constructor parameters
waitForConvergence: true,     // Enable/disable gating (default: true)
convergenceTimeoutMs: 5000    // Max wait time before proceeding anyway
```

Configuration via `appsettings.json`:
```json
{
  "Audio": {
    "ClockSync": {
      "WaitForConvergence": true,
      "ConvergenceTimeoutMs": 5000
    }
  }
}
```

### High-Precision Timer

**SDK class**: `HighPrecisionTimer` (in Sendspin.SDK)

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
group/update        - Group playback state and identity (playback state, group id/name)
server/state        - Delta-merged server state (metadata/progress, volume, mute)
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
2. **User AppData**: `%LOCALAPPDATA%\Sendspin for Windows\appsettings.json` (user overrides)

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
    "StaticDelayMs": 0,
    "DeviceId": null,
    "Buffer": {
      "TargetMs": 250,
      "CapacityMs": 8000
    },
    "ClockSync": {
      "ForgetFactor": 2.0,
      "AdaptiveCutoff": 3.0,
      "MinSamplesForForgetting": 100,
      "WaitForConvergence": true,
      "ConvergenceTimeoutMs": 5000
    },
    "SyncCorrection": {
      "UseResampling": true,
      "ResamplingThresholdMs": 15
    }
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
    "Mode": "AdvertiseOnly",
    "AutoConnectServerId": "",
    "LastPlayedServerId": ""
  }
}
```

### Connection Configuration
- `Mode`: `AdvertiseOnly` (server-initiated, the default) or `DiscoverOnly` (client-initiated).
  Exactly one method at a time, per spec — there is no combined mode. The legacy `Auto` value
  ran both transports; configs still carrying it migrate to `AdvertiseOnly` on load.
- `AutoConnectServerId`: **`DiscoverOnly` only.** The discovered server to auto-connect to, set
  by the "always connect" choice in the server picker. Ignored in `AdvertiseOnly`, where the
  client never initiates a connection.
- `LastPlayedServerId`: the spec's "last-playback server", used by `SendspinHostService` to break
  arbitration ties when a current and an incoming connection both declare empty activities.
  Written in both modes (see `SetLastPlayedServerId`); consumed only in `AdvertiseOnly`.

### Audio Buffer Configuration
- `Buffer.TargetMs`: Target buffer depth before starting playback (default: 250ms)
- `Buffer.CapacityMs`: Maximum buffer capacity (default: 30000ms / 30s — sized to absorb server's initial burst)

### Clock Sync Configuration
- `ClockSync.WaitForConvergence`: Wait for Kalman filter to converge before playback (default: true)
- `ClockSync.ConvergenceTimeoutMs`: Max wait time for convergence (default: 5000ms)

### Sync Correction Configuration
- `SyncCorrection.UseResampling`: Use smooth playback rate adjustment (default: true)
- `SyncCorrection.ResamplingThresholdMs`: Error threshold for resampling vs drop/insert (default: 15ms)

### Static Delay Tuning
Per the Sendspin spec (v8.0.0+), positive `StaticDelayMs` compensates for downstream
hardware delay (Bluetooth, AV receivers, external amps) by scheduling audio earlier
from the digital pipeline. The value is SUBTRACTED from converted server timestamps.
- **Positive values**: Play earlier (if this player is behind others — speaker hardware adds latency)
- **Negative values**: Play later (if this player is ahead of others)
- Typical range: -500ms to +500ms

Sign convention flipped in SDK 8.0.0. If you see a "Positive = play later" reference
anywhere in the codebase, it's stale — the v7 semantic was non-spec.

---

## MVVM Architecture

### ViewModels

**Main entry point**: `src/Sendspin.Windows/ViewModels/MainViewModel.cs`

Uses CommunityToolkit.Mvvm with source generators:
```csharp
[ObservableProperty]
private bool _isHosting;           // Generates IsHosting property + OnIsHostingChanged

[RelayCommand]
private async Task PlayPauseAsync() // Generates PlayPauseCommand
```

### Dependency Injection

Services are registered in `src/Sendspin.Windows/App.xaml.cs`:
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
**File**: `src/Sendspin.Windows.Services/Audio/WasapiAudioPlayer.cs`

Uses NAudio's `WasapiOut` for low-latency audio output. Key responsibilities:
- Initialize audio device with specific format
- Report output latency (used for sync error compensation)
- Support hot-switching between audio devices

### Toast Notifications
**File**: `src/Sendspin.Windows.Services/Notifications/WindowsToastNotificationService.cs`

Uses Microsoft.Toolkit.Uwp.Notifications for Windows toast notifications:
- Track change notifications
- Connection/disconnection alerts
- Suppressed when main window is visible and active

### Discord Rich Presence
**File**: `src/Sendspin.Windows.Services/Discord/DiscordRichPresenceService.cs`

Shows currently playing track in Discord status using discord-rpc-csharp.

---

## Reference Implementation

### Gold Standard
**Location**: `Z:\CodeProjects\sendspin-cli-main`

The Python CLI player is the reference implementation. When implementing or fixing sync, audio buffering, or timing logic, **always refer to the CLI code**. Don't guess—read the Python code.

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

### 4. Output Latency Does NOT Affect Sync Error
WASAPI has ~34-50ms output buffer latency. However, this is a **constant offset** that does NOT affect the sync correction rate. The sync error simply compares wall clock elapsed time vs samples consumed:
```csharp
// Correct: no output latency adjustment
_syncError = elapsedTime - samplesReadTime;
```

Output latency affects WHEN audio plays (all audio is delayed equally), but not WHETHER we're keeping up with the server's pace. At steady-state with rate=1.0, sync error should hover around 0.

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

### 7. JSON Field Absent vs Explicit Null (Optional<T>)
JSON has three states for a field, but C# nullable types only have two:
```
JSON                          C# Nullable      C# Optional<T>
─────────────────────────────────────────────────────────────
{"progress": {...}}           value            Present(value)
{"progress": null}            null             Present(null)   ← TRACK ENDED
{}                            null             Absent()        ← KEEP EXISTING
```

The SDK uses `Optional<T>` (in `Sendspin.SDK.Protocol`) for fields where explicit null has semantic meaning:
- `ServerMetadata.Progress`: `null` means track ended, absent means no update

```csharp
// WRONG - treats track-end (null) same as no-update (absent)
Progress = meta.Progress ?? existing.Progress;

// CORRECT - uses Optional<T> to distinguish
Progress = meta.Progress.IsPresent ? meta.Progress.Value : existing.Progress;
```

This matches the CLI's `UndefinedField` pattern in Python.

**Corollary the app depends on**: when the progress field is absent, the SDK's
carry-forward keeps the **same** `PlaybackProgress` instance (verified in 9.1.0's
`SendspinClientService.HandleServerState`), and deserializes a **new** instance exactly
when the server sent the field. `TrackProgressTracker` relies on this reference identity
to distinguish fresh progress from carried-forward stale progress.

---

## Testing & Debugging

### Logs Location
`%LOCALAPPDATA%\Sendspin for Windows\logs\windowsspin-{date}.log`

### Stats Window
The app includes a "Stats for Nerds" window showing:
- Current sync error
- Buffer depth
- Drop/insert counts
- Clock offset and drift

Access via Settings → Stats for Nerds (see `src/Sendspin.Windows/ViewModels/StatsViewModel.cs`)

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
- `Sendspin.Windows-{version}-Setup.exe` - Installer (requires .NET 10 runtime)
- `Sendspin.Windows-{version}-Setup-SelfContained.exe` - Standalone installer
- `Sendspin.Windows-{version}-portable-win-x64.zip` - Portable ZIP

---

## Sendspin.SDK (External Dependency)

The SDK is consumed as a NuGet package. Source and publishing are managed in [Sendspin/sendspin-dotnet](https://github.com/Sendspin/sendspin-dotnet).

To update the SDK version, change the `Version` in the `<PackageReference Include="Sendspin.SDK">` entries in both `Sendspin.Windows.csproj` and `Sendspin.Windows.Services.csproj`.

When bumping the SDK version, also verify that the carry-forward-by-reference behavior
still holds: `SendspinClientService.HandleServerState` must reuse the **same**
`PlaybackProgress` instance when the progress field is absent from a message —
`TrackProgressTracker`'s freshness detection depends on it (see gotcha #7). An upstream
contract test is being added to sendspin-dotnet; until it exists, this must be checked
manually against the SDK source.

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
| `src/Sendspin.Windows/App.xaml.cs` | DI setup, startup, shutdown |
| `src/Sendspin.Windows/ViewModels/MainViewModel.cs` | Primary UI state and commands |
| `src/Sendspin.Windows.Services/Audio/WasapiAudioPlayer.cs` | Windows audio output |
| `src/Sendspin.Windows.Services/Audio/DynamicResamplerSampleProvider.cs` | Playback rate resampling for sync |
| `src/Sendspin.Windows.Services/Audio/BufferedAudioSampleSource.cs` | Bridges SDK buffer to NAudio |

SDK classes (in NuGet package — source at [sendspin-dotnet](https://github.com/Sendspin/sendspin-dotnet)):
- `AudioPipeline` — Audio flow orchestration
- `TimedAudioBuffer` — Sync-aware sample buffer
- `KalmanClockSynchronizer` — Clock sync algorithm
- `HighPrecisionTimer` — Microsecond-precision timing
- `SendspinHostService` / `SendspinClientService` — Connection modes (declared in `SendSpinHostService.cs` / `SendSpinClient.cs`)
- `SyncCorrectionOptions` — Configurable sync correction parameters
- `Optional<T>` — JSON absent vs null distinction

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
