# Sendspin SDK

A cross-platform .NET SDK for the Sendspin synchronized multi-room audio protocol.

## Features

- **Multi-room Audio Sync**: Microsecond-precision clock synchronization using Kalman filtering
- **Protocol Support**: Full Sendspin WebSocket protocol implementation
- **Server Discovery**: mDNS-based automatic server discovery
- **Audio Decoding**: Built-in PCM, FLAC, and Opus codec support
- **Cross-Platform**: Works on Windows, Linux, and macOS

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
