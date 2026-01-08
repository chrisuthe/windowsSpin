# Sendspin SDK

A cross-platform .NET SDK for the Sendspin synchronized multi-room audio protocol. Build players that sync perfectly with Music Assistant and other Sendspin-compatible players.

[![NuGet](https://img.shields.io/nuget/v/Sendspin.SDK.svg)](https://www.nuget.org/packages/Sendspin.SDK/)
[![GitHub](https://img.shields.io/github/license/chrisuthe/windowsSpin)](https://github.com/chrisuthe/windowsSpin/blob/master/LICENSE)

## Features

- **Multi-room Audio Sync**: Microsecond-precision clock synchronization using Kalman filtering
- **External Sync Correction** (v5.0+): SDK reports sync error, your app applies correction
- **Platform Flexibility**: Use playback rate, drop/insert, or hardware rate adjustment
- **Fast Startup**: Audio plays within ~300ms of connection
- **Protocol Support**: Full Sendspin WebSocket protocol implementation
- **Server Discovery**: mDNS-based automatic server discovery
- **Audio Decoding**: Built-in PCM, FLAC, and Opus codec support
- **Cross-Platform**: Works on Windows, Linux, and macOS (.NET 8.0 / .NET 10.0)
- **Audio Device Switching**: Hot-switch audio output devices without interrupting playback

## Installation

```bash
dotnet add package Sendspin.SDK
```

## Quick Start

```csharp
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Synchronization;

// Create dependencies
var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var connection = new SendspinConnection(loggerFactory.CreateLogger<SendspinConnection>());
var clockSync = new KalmanClockSynchronizer(loggerFactory.CreateLogger<KalmanClockSynchronizer>());

// Create client with device info
var capabilities = new ClientCapabilities
{
    ClientName = "My Player",
    ProductName = "My Awesome Player",
    Manufacturer = "My Company",
    SoftwareVersion = "1.0.0"
};

var client = new SendspinClientService(
    loggerFactory.CreateLogger<SendspinClientService>(),
    connection,
    clockSync,
    capabilities
);

// Connect to server
await client.ConnectAsync(new Uri("ws://192.168.1.100:8927/sendspin"));

// Handle events
client.GroupStateChanged += (sender, group) =>
{
    Console.WriteLine($"Now playing: {group.Metadata?.Title}");
};

// Send commands
await client.SendCommandAsync("play");
await client.SetVolumeAsync(75);
```

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     Your Application                            │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  SyncCorrectionCalculator  │  Your Resampler/Drop Logic │   │
│  │  (correction decisions)    │  (applies correction)      │   │
│  └─────────────────────────────────────────────────────────┘   │
├─────────────────────────────────────────────────────────────────┤
│  SendspinClientService    │  AudioPipeline    │  IAudioPlayer   │
│  (protocol handling)      │  (orchestration)  │  (your impl)    │
├─────────────────────────────────────────────────────────────────┤
│  SendspinConnection  │  KalmanClockSync  │  TimedAudioBuffer    │
│  (WebSocket)         │  (timing)         │  (reports error)     │
├─────────────────────────────────────────────────────────────────┤
│  OpusDecoder  │  FlacDecoder  │  PcmDecoder                     │
└─────────────────────────────────────────────────────────────────┘
```

**Namespaces:**
- `Sendspin.SDK.Client` - Client services and capabilities
- `Sendspin.SDK.Connection` - WebSocket connection management
- `Sendspin.SDK.Protocol` - Message types and serialization
- `Sendspin.SDK.Synchronization` - Clock sync (Kalman filter)
- `Sendspin.SDK.Audio` - Pipeline, buffer, decoders, and sync correction
- `Sendspin.SDK.Discovery` - mDNS server discovery
- `Sendspin.SDK.Models` - Data models (GroupState, TrackMetadata)

## Sync Correction System (v5.0+)

Starting with v5.0.0, sync correction is **external** - the SDK reports sync error and your application decides how to correct it. This enables platform-specific correction strategies:

- **Windows**: WDL resampler, SoundTouch, or drop/insert
- **Browser**: Native `playbackRate` (WSOLA time-stretching)
- **Linux**: ALSA hardware rate adjustment, PipeWire rate
- **Embedded**: Platform-specific DSP

### How It Works

```
SDK (reports error only)              App (applies correction)
────────────────────────────────────────────────────────────────
TimedAudioBuffer                      SyncCorrectionCalculator
├─ ReadRaw() - no correction          ├─ UpdateFromSyncError()
├─ SyncErrorMicroseconds              ├─ DropEveryNFrames
├─ SmoothedSyncErrorMicroseconds      ├─ InsertEveryNFrames
└─ NotifyExternalCorrection()         └─ TargetPlaybackRate
```

### Tiered Correction Strategy

The `SyncCorrectionCalculator` implements the same tiered strategy as the reference CLI:

| Sync Error | Correction Method | Description |
|------------|-------------------|-------------|
| < 1ms | None (deadband) | Error too small to matter |
| 1-15ms | Playback rate adjustment | Smooth resampling (imperceptible) |
| 15-500ms | Frame drop/insert | Faster correction for larger drift |
| > 500ms | Re-anchor | Clear buffer and restart sync |

### Usage Example

```csharp
using Sendspin.SDK.Audio;

// Create the correction calculator
var correctionProvider = new SyncCorrectionCalculator(
    SyncCorrectionOptions.Default,  // or SyncCorrectionOptions.CliDefaults
    sampleRate: 48000,
    channels: 2
);

// Subscribe to correction changes
correctionProvider.CorrectionChanged += provider =>
{
    // Update your resampler rate
    myResampler.Rate = provider.TargetPlaybackRate;

    // Or handle drop/insert
    if (provider.CurrentMode == SyncCorrectionMode.Dropping)
    {
        dropEveryN = provider.DropEveryNFrames;
    }
};

// In your audio callback:
public int Read(float[] buffer, int offset, int count)
{
    // Read raw samples (no internal correction)
    int read = timedAudioBuffer.ReadRaw(buffer, offset, count, currentTimeMicroseconds);

    // Update correction provider with current error
    correctionProvider.UpdateFromSyncError(
        timedAudioBuffer.SyncErrorMicroseconds,
        timedAudioBuffer.SmoothedSyncErrorMicroseconds
    );

    // Apply your correction strategy...
    // If dropping/inserting, notify the buffer:
    timedAudioBuffer.NotifyExternalCorrection(samplesDropped, samplesInserted);

    return outputCount;
}
```

### Configuring Sync Behavior

```csharp
// Use default settings (conservative: 2% max, 3s target)
var options = SyncCorrectionOptions.Default;

// Use CLI-compatible settings (aggressive: 4% max, 2s target)
var options = SyncCorrectionOptions.CliDefaults;

// Custom options
var options = new SyncCorrectionOptions
{
    MaxSpeedCorrection = 0.04,                    // 4% max rate adjustment
    CorrectionTargetSeconds = 2.0,                // Time to eliminate drift
    ResamplingThresholdMicroseconds = 15_000,     // Resampling vs drop/insert
    ReanchorThresholdMicroseconds = 500_000,      // Clear buffer threshold
    StartupGracePeriodMicroseconds = 500_000,     // No correction during startup
};

var calculator = new SyncCorrectionCalculator(options, sampleRate, channels);
```

## Platform-Specific Audio

The SDK handles decoding, buffering, and sync error reporting. You implement `IAudioPlayer` for audio output:

```csharp
public class MyAudioPlayer : IAudioPlayer
{
    public long OutputLatencyMicroseconds { get; private set; }

    public Task InitializeAsync(AudioFormat format, CancellationToken ct)
    {
        // Initialize your audio backend (WASAPI, PulseAudio, CoreAudio, etc.)
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // Called by audio thread - read from TimedAudioBuffer.ReadRaw()
        // Apply sync correction externally
    }

    // ... other methods
}
```

**Platform suggestions:**
- **Windows**: NAudio with WASAPI (`WasapiOut`)
- **Linux**: OpenAL, PulseAudio, or PipeWire
- **macOS**: AudioToolbox or AVAudioEngine
- **Cross-platform**: SDL2

## Server Discovery

Automatically discover Sendspin servers on your network:

```csharp
var discovery = new MdnsServerDiscovery(logger);
discovery.ServerDiscovered += (sender, server) =>
{
    Console.WriteLine($"Found: {server.Name} at {server.Uri}");
};
await discovery.StartAsync();
```

## Device Info

Identify your player to servers:

```csharp
var capabilities = new ClientCapabilities
{
    ClientName = "Living Room",           // Display name
    ProductName = "MySpeaker Pro",        // Product identifier
    Manufacturer = "Acme Audio",          // Your company
    SoftwareVersion = "2.1.0"             // App version
};
```

All fields are optional and omitted from the protocol if null.

## Migration Guide

### Upgrading to v5.0.0

**Breaking change**: Sync correction is now external. The SDK reports error; you apply correction.

**Before (v4.x and earlier):**
```csharp
// SDK applied correction internally
var read = buffer.Read(samples, currentTime);
buffer.TargetPlaybackRateChanged += rate => resampler.Rate = rate;
```

**After (v5.0+):**
```csharp
// Create correction provider
var correctionProvider = new SyncCorrectionCalculator(
    SyncCorrectionOptions.Default, sampleRate, channels);

// Read raw samples (no internal correction)
var read = buffer.ReadRaw(samples, offset, count, currentTime);

// Update and apply correction externally
correctionProvider.UpdateFromSyncError(
    buffer.SyncErrorMicroseconds,
    buffer.SmoothedSyncErrorMicroseconds);

// Subscribe to rate changes
correctionProvider.CorrectionChanged += p => resampler.Rate = p.TargetPlaybackRate;

// Notify buffer of any drops/inserts for accurate tracking
buffer.NotifyExternalCorrection(droppedCount, insertedCount);
```

**Benefits:**
- Browser apps can use native `playbackRate` (WSOLA)
- Windows apps can choose WDL resampler, SoundTouch, or drop/insert
- Linux apps can use ALSA hardware rate adjustment
- Testability: correction logic is isolated

### Upgrading to v3.0.0

**Breaking change**: `IClockSynchronizer` requires `HasMinimalSync` property.

```csharp
// Add to custom IClockSynchronizer implementations:
public bool HasMinimalSync => MeasurementCount >= 2;
```

### Upgrading to v2.0.0

1. **`HardwareLatencyMs` removed** - No action needed, latency handled automatically
2. **`IAudioPipeline.SwitchDeviceAsync()` required** - Implement for device switching
3. **`IAudioPlayer.SwitchDeviceAsync()` required** - Implement in your audio player

## Example Projects

See the [Windows client](https://github.com/chrisuthe/windowsSpin/tree/master/src/SendspinClient) for a complete WPF implementation using NAudio/WASAPI with external sync correction.

## License

MIT License - see [LICENSE](https://github.com/chrisuthe/windowsSpin/blob/master/LICENSE) for details.
