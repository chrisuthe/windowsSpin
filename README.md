# WindowsSpin - Multi-Room Audio for Windows

<p align="center">
  <img src="icon.png" alt="WindowsSpin" width="128" height="128">
</p>

**Turn any Windows PC into a synchronized multi-room audio player.**

WindowsSpin connects your Windows computer to [Music Assistant](https://music-assistant.io/), allowing you to play music throughout your home in perfect sync. Whether you have an old laptop, a home theater PC, or a desktop workstation, WindowsSpin transforms it into part of your whole-home audio system.

> **Disclaimer:** This is an **independent, unofficial project** and is **not affiliated with, endorsed by, or associated with** the Music Assistant project or the official Sendspin protocol maintainers. This is a third-party implementation created for personal use and community benefit.

---

## Why WindowsSpin?

**The Problem:** You want to play music on multiple devices throughout your home, but audio from different speakers reaches your ears at different times - creating an annoying echo effect as you move between rooms.

**The Solution:** WindowsSpin uses precision clock synchronization to ensure audio plays at exactly the same moment on all connected players. Walk from your living room to your kitchen and the music follows you seamlessly - no delays, no echoes, just continuous audio.

### Perfect For:

- **Home Theater PCs** - Add your media center to your whole-home audio system
- **Office Desktops** - Keep your work music in sync with the rest of the house
- **Repurposed Laptops** - Give old hardware a new life as a dedicated audio endpoint
- **Gaming PCs** - Background music that stays in sync while you play

---

## Quick Start

### 1. Install

Download from the [Releases page](https://github.com/chrisuthe/windowsSpin/releases) and run the installer.

### 2. Connect

WindowsSpin automatically discovers Music Assistant servers on your network. If it does not find your server, enter the address manually (e.g., `10.0.2.8:8927`).

### 3. Play

That's it! Your Windows PC now appears as a player in Music Assistant. Add it to a group with other players for synchronized multi-room playback.

**For detailed instructions, see the [User Guide](docs/USER_GUIDE.md).**

---

## Overview

WindowsSpin is a native Windows application that implements the [Sendspin protocol](https://www.sendspin-audio.com/) for synchronized audio streaming. It connects to Music Assistant servers using WebSocket communication for control messages and binary audio streaming, combined with NTP-style clock synchronization to achieve sub-millisecond audio sync across devices.

The application operates in two modes:
- **Client Mode**: Actively discovers and connects to Sendspin servers (primary)
- **Host Mode**: Advertises itself via mDNS and accepts incoming server connections (fallback)

## Features

- **Multi-Room Audio Synchronization**: Sub-millisecond audio synchronization using Kalman filter-based clock sync
- **Automatic Server Discovery**: mDNS/DNS-SD discovery of Sendspin servers on the local network
- **Multiple Audio Formats**: Support for PCM, FLAC, and Opus audio codecs
- **Real-time Metadata**: Display of currently playing track information and album artwork
- **Playback Control**: Full playback control (play, pause, volume, next/previous)
- **Dual Operation Modes**:
  - Client-initiated connections (discover and connect to servers)
  - Server-initiated connections (advertise and accept connections)
- **System Tray Integration**: Runs in the background with system tray icon
- **Modern WPF UI**: Clean, responsive user interface using MVVM pattern

## Screenshots

### Main Window
The main interface displays album artwork, track information, and playback controls. Shows connection status to your Music Assistant server.

<p align="center">
  <img src="docs/ScreenShots/mainwindow.png" alt="Main Window" width="400">
</p>

### System Tray
Quick access to playback controls without opening the main window. The app runs in the background and can be controlled entirely from the system tray.

<p align="center">
  <img src="docs/ScreenShots/TrayMenu.png" alt="System Tray Menu" width="350">
</p>

### Manual Connection
For cross-subnet scenarios where mDNS discovery doesn't work, you can manually enter the server's WebSocket URL.

<p align="center">
  <img src="docs/ScreenShots/manualconnect.png" alt="Manual Connection" width="400">
</p>

## Prerequisites

### Runtime Requirements
- Windows 10 version 1809 (build 17763) or later
- .NET 8.0 Runtime or .NET 9.0 Runtime
- Network connectivity to Music Assistant server

### Development Requirements
- Visual Studio 2022 (17.8 or later) or JetBrains Rider
- .NET 8.0 SDK or .NET 9.0 SDK
- Windows 10 SDK (10.0.17763.0)

## Installation

### Option 1: Download Release Binary (Coming Soon)
1. Download the latest release from the [Releases](https://github.com/chrisuthe/windowsSpin/releases) page
2. Extract the archive to your preferred location
3. Run `SendspinClient.exe`

### Option 2: Build from Source
```bash
# Clone the repository
git clone https://github.com/chrisuthe/windowsSpin.git
cd windowsSpin

# Build the solution
dotnet build SendspinClient.sln --configuration Release

# Run the application
dotnet run --project src/SendspinClient/SendspinClient.csproj
```

## Building and Running

### Using Visual Studio
1. Open `SendspinClient.sln` in Visual Studio 2022
2. Select the desired configuration (Debug or Release)
3. Build the solution (F7 or Build > Build Solution)
4. Run the application (F5 for debugging or Ctrl+F5 without debugging)

### Using Command Line
```bash
# Build Debug configuration
dotnet build

# Build Release configuration
dotnet build --configuration Release

# Run the application
dotnet run --project src/SendspinClient/SendspinClient.csproj

# Publish for deployment
dotnet publish src/SendspinClient/SendspinClient.csproj -c Release -r win-x64 --self-contained false
```

### Using Rider
1. Open `SendspinClient.sln` in JetBrains Rider
2. Select run configuration
3. Click Run or Debug

## For Developers

Interested in how WindowsSpin works under the hood or want to contribute?

- **[Architecture Documentation](docs/ARCHITECTURE.md)** - Technical deep-dive into the codebase, audio pipeline, clock synchronization, and protocol implementation
- **[Contributing Guide](CONTRIBUTING.md)** - Development setup, code style, and pull request process
- **[Quick Start for Development](docs/QUICKSTART.md)** - Get up and running quickly

## Contributing

We welcome contributions! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for:
- Development setup instructions
- Code style guidelines
- Pull request process
- Code review expectations

## Troubleshooting

### Common Issues

**Server not discovered:**
- Ensure client and server are on the same network
- Check firewall settings (allow mDNS port 5353, WebSocket ports)
- Verify Music Assistant has Sendspin enabled

**Connection fails:**
- Check server logs for connection errors
- Verify WebSocket endpoint is accessible
- Ensure correct port is configured

**Audio not playing:**
- Verify audio device is configured and working
- Check audio format compatibility
- Review client logs for decoder errors

**Poor synchronization:**
- Check network latency and jitter
- Ensure stable network connection
- Review clock sync status in logs

### Logging

Enable detailed logging by setting environment variables:
```bash
# Set log level to Debug
set Logging__LogLevel__Default=Debug

# Set specific namespace to Trace
set Logging__LogLevel__SendspinClient.Core=Trace
```

Logs include:
- Connection state changes
- Message send/receive (with full JSON)
- Clock synchronization statistics
- Audio buffer status

## Security Considerations

This application is designed for **trusted local networks** (home/office LANs).

### Network Security Model

- **Unencrypted Communication**: WebSocket connections use `ws://` (not `wss://`). Audio data and control messages are transmitted in plain text on your local network.
- **Network Binding**: The host service binds to all network interfaces (`0.0.0.0`) to allow connections from other devices on your network.
- **mDNS Discovery**: Server discovery broadcasts are visible to all devices on the local network segment.

### When to Use This App

| Environment | Recommendation |
|-------------|----------------|
| Home network | ✅ **Safe** - Trusted environment |
| Office LAN | ✅ **Safe** - Controlled network |
| Shared housing (dorms) | ⚠️ **Use caution** - Others can see your client |
| Public WiFi | ❌ **Not recommended** - Untrusted network |
| VPN/tunneled connection | ⚠️ **Consider alternatives** - Latency may affect sync |

### Firewall Configuration

If using Windows Firewall, allow the following:
- **mDNS Discovery**: UDP port 5353 (for automatic server discovery)
- **WebSocket**: TCP port 8927 (default, or the port shown in your Music Assistant settings)

The installer will prompt to add firewall rules automatically.

### Privacy Note

The client broadcasts its name via mDNS, which is visible to other devices on your network. By default, this uses your Windows machine name. A future version will allow configuring a custom display name.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- [Music Assistant](https://music-assistant.io/) - The music streaming and multi-room platform
- [Sendspin Protocol](https://github.com/music-assistant/sendspin) - The synchronization protocol specification
- [NAudio](https://github.com/naudio/NAudio) - Audio library for Windows (WASAPI playback)
- [Concentus](https://github.com/lostromb/concentus) - Pure C# Opus decoder
- [SimpleFlac](https://github.com/jdpurcell/SimpleFlac) - Pure C# FLAC decoder (vendored)
- [Zeroconf](https://github.com/novotnyllc/Zeroconf) - mDNS/DNS-SD library

## Related Projects

- [Music Assistant](https://github.com/music-assistant/server) - Server implementation
- [Sendspin CLI](https://github.com/music-assistant/sendspin-cli) - Official CLI reference client
- [aiosendspin](https://github.com/music-assistant/aiosendspin) - Python client library

## Support

For issues, questions, or feature requests **specific to this Windows client**:
- Open an issue on [GitHub Issues](https://github.com/chrisuthe/windowsSpin/issues)

For general Music Assistant questions (not related to this client):
- Join the [Music Assistant Discord](https://discord.gg/musicassistant)
- Check the [Music Assistant documentation](https://music-assistant.io/documentation/)

*Note: Please do not contact the Music Assistant team for support with this unofficial client.*
