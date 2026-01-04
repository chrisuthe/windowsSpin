# Sendspin SDK

A cross-platform .NET SDK for the Sendspin synchronized multi-room audio protocol.

[![NuGet](https://img.shields.io/nuget/v/Sendspin.SDK.svg)](https://www.nuget.org/packages/Sendspin.SDK/)

## Features

- **Multi-room Audio Sync**: Microsecond-precision clock synchronization using Kalman filtering
- **Protocol Support**: Full Sendspin WebSocket protocol implementation
- **Server Discovery**: mDNS-based automatic server discovery
- **Audio Decoding**: Built-in PCM, FLAC, and Opus codec support
- **Cross-Platform**: Works on Windows, Linux, and macOS
- **Audio Device Switching**: Hot-switch audio output devices without interrupting playback

## Installation

```bash
dotnet add package Sendspin.SDK
```

**Supported Frameworks**: .NET 8.0, .NET 10.0

---

## ⚠️ Breaking Changes in v2.0.0

If you're upgrading from v1.x, please review the following breaking changes:

### 1. `IClockSynchronizer.HardwareLatencyMs` Removed

**What changed**: The `HardwareLatencyMs` property has been removed from the `IClockSynchronizer` interface.

**Why**: Hardware latency is a constant offset that doesn't affect the sync correction rate. The `TimedAudioBuffer.OutputLatencyMicroseconds` property stores the output latency for diagnostic purposes but it is NOT used in the sync error calculation.

**Migration**:
```csharp
// Before (v1.x)
clockSync.HardwareLatencyMs = player.OutputLatencyMs;

// After (v2.0) - No action needed!
// The AudioPipeline stores buffer.OutputLatencyMicroseconds for diagnostics
```

### 2. `IAudioPipeline.SwitchDeviceAsync()` Required

**What changed**: The `IAudioPipeline` interface now requires a `SwitchDeviceAsync()` method.

**Why**: Enables hot-switching audio devices without stopping playback - essential for multi-room setups.

**Migration**: Implement the new method in your `IAudioPipeline` implementation:
```csharp
public async Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
{
    // Switch your audio player to the new device
    await _player.SwitchDeviceAsync(deviceId, cancellationToken);

    // Reset sync tracking to prevent timing discontinuities
    if (_buffer is TimedAudioBuffer timedBuffer)
    {
        timedBuffer.ResetSyncTracking();
    }
}
```

### 3. `IAudioPlayer.SwitchDeviceAsync()` Required

**What changed**: The `IAudioPlayer` interface now requires a `SwitchDeviceAsync()` method.

**Why**: Platform-specific audio players need to support device switching.

**Migration**: Implement the new method in your `IAudioPlayer` implementation:
```csharp
public async Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
{
    // Stop current playback
    Stop();

    // Reinitialize with new device
    _deviceId = deviceId;
    await InitializeAsync(_currentFormat, cancellationToken);

    // Resume if we were playing
    Play();
}
```

---

## Quick Start

```csharp
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Synchronization;

// Create dependencies
var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var connection = new SendspinConnection(loggerFactory.CreateLogger<SendspinConnection>());
var clockSync = new KalmanClockSynchronizer(loggerFactory.CreateLogger<KalmanClockSynchronizer>());

// Create client
var client = new SendspinClientService(
    loggerFactory.CreateLogger<SendspinClientService>(),
    connection,
    clockSync,
    new ClientCapabilities { ClientName = "My App" }
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

## New in v2.0.0

### Audio Device Hot-Switching

Switch audio output devices without interrupting the stream:

```csharp
// Switch to a specific device
await audioPipeline.SwitchDeviceAsync("device-id-here");

// Switch to system default
await audioPipeline.SwitchDeviceAsync(null);
```

### mDNS Advertising Control

Control when your client is discoverable by servers:

```csharp
var hostService = new SendspinHostService(...);
await hostService.StartAsync();

// When manually connecting to a server, stop advertising
await hostService.StopAdvertisingAsync();

// When disconnecting, resume advertising
await hostService.StartAdvertisingAsync();

// Check current state
if (hostService.IsAdvertising) { ... }
```

### Improved Sync Tracking

Reset sync timing after device switches or other timing discontinuities:

```csharp
// Soft reset - keeps buffered audio, resets timing anchors
timedBuffer.ResetSyncTracking();
```

## Architecture

The SDK is organized into these namespaces:

- `Sendspin.SDK.Client` - Main client interface and implementation
- `Sendspin.SDK.Connection` - WebSocket connection management
- `Sendspin.SDK.Protocol` - Message serialization and protocol types
- `Sendspin.SDK.Synchronization` - Clock synchronization (Kalman filter)
- `Sendspin.SDK.Audio` - Audio pipeline interfaces and decoders
- `Sendspin.SDK.Discovery` - mDNS server discovery
- `Sendspin.SDK.Models` - Data models (GroupState, TrackMetadata, etc.)

## Platform-Specific Audio

The SDK provides audio decoding and buffering, but audio output is platform-specific.
Implement `IAudioPlayer` for your target platform:

- **Windows**: Use NAudio with WASAPI
- **Linux**: Use OpenAL or PulseAudio
- **macOS**: Use AudioToolbox
- **Cross-platform**: Use SDL2 or similar

## License

MIT License - see LICENSE file for details.
