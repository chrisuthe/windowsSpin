# Sendspin SDK

A cross-platform .NET SDK for the Sendspin synchronized multi-room audio protocol. Build players that sync perfectly with Music Assistant and other Sendspin-compatible players.

[![NuGet](https://img.shields.io/nuget/v/Sendspin.SDK.svg)](https://www.nuget.org/packages/Sendspin.SDK/)
[![GitHub](https://img.shields.io/github/license/chrisuthe/windowsSpin)](https://github.com/chrisuthe/windowsSpin/blob/master/LICENSE)

## Features

- **Multi-room Audio Sync**: Microsecond-precision clock synchronization using Kalman filtering
- **Tiered Sync Correction**: Smooth playback rate adjustment for small drift, frame drop/insert for larger errors
- **Fast Startup**: Audio plays within ~300ms of connection (vs 5+ seconds in earlier versions)
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
│                     Your Application                             │
├─────────────────────────────────────────────────────────────────┤
│  SendspinClientService    │  AudioPipeline    │  IAudioPlayer   │
│  (protocol handling)      │  (orchestration)  │  (your impl)    │
├─────────────────────────────────────────────────────────────────┤
│  SendspinConnection  │  KalmanClockSync  │  TimedAudioBuffer    │
│  (WebSocket)         │  (timing)         │  (sync correction)   │
├─────────────────────────────────────────────────────────────────┤
│  OpusDecoder  │  FlacDecoder  │  PcmDecoder                     │
└─────────────────────────────────────────────────────────────────┘
```

**Namespaces:**
- `Sendspin.SDK.Client` - Client services and capabilities
- `Sendspin.SDK.Connection` - WebSocket connection management
- `Sendspin.SDK.Protocol` - Message types and serialization
- `Sendspin.SDK.Synchronization` - Clock sync (Kalman filter)
- `Sendspin.SDK.Audio` - Pipeline, buffer, and decoders
- `Sendspin.SDK.Discovery` - mDNS server discovery
- `Sendspin.SDK.Models` - Data models (GroupState, TrackMetadata)

## Sync Correction System

The SDK uses a tiered sync correction strategy for imperceptible multi-room synchronization:

| Sync Error | Correction Method | Description |
|------------|-------------------|-------------|
| < 0.5ms | None (exit) | Error too small to matter |
| 0.5-2ms | Hysteresis zone | Maintains current correction state |
| 2-15ms | Playback rate adjustment | Smooth 0.96x-1.04x resampling |
| 15-500ms | Frame drop/insert | Faster correction for larger drift |
| > 500ms | Re-anchor | Clear buffer and restart sync |

The hysteresis prevents oscillation between correcting and not correcting.

## Platform-Specific Audio

The SDK handles decoding, buffering, and sync correction. You implement `IAudioPlayer` for audio output:

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
        // Called by audio thread - read from TimedAudioBuffer
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

## Device Info (v3.0.1+)

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

### Upgrading to v3.0.0

**Breaking change**: `IClockSynchronizer` now requires `HasMinimalSync` property.

```csharp
// Add to custom IClockSynchronizer implementations:
public bool HasMinimalSync => MeasurementCount >= 2;
```

If you only use `KalmanClockSynchronizer` (most users), no changes needed.

### Upgrading to v2.0.0

1. **`HardwareLatencyMs` removed** - No action needed, latency handled automatically
2. **`IAudioPipeline.SwitchDeviceAsync()` required** - Implement for device switching support
3. **`IAudioPlayer.SwitchDeviceAsync()` required** - Implement in your audio player

## Example Projects

See the [Windows client](https://github.com/chrisuthe/windowsSpin/tree/master/src/SendspinClient) for a complete WPF implementation using NAudio/WASAPI.

## License

MIT License - see [LICENSE](https://github.com/chrisuthe/windowsSpin/blob/master/LICENSE) for details.
