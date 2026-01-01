# WindowsSpin User Guide

Welcome to WindowsSpin, a Windows desktop application that brings synchronized multi-room audio to your PC. This guide will help you get started and make the most of the application.

## What is WindowsSpin?

WindowsSpin turns your Windows PC into a synchronized audio player that works with [Music Assistant](https://music-assistant.io/). Imagine playing music throughout your home - in your living room, kitchen, office, and bedroom - all perfectly in sync. No more audio delays or echoes as you walk between rooms. That is what multi-room audio provides, and WindowsSpin brings this capability to any Windows computer.

### Key Benefits

- **Perfect Audio Sync**: Play the same music on multiple devices throughout your home, all synchronized within milliseconds
- **Use Any Windows PC**: Turn an old laptop, a home theater PC, or your desktop into a multi-room audio endpoint
- **Background Operation**: Runs quietly in your system tray - start it once and forget about it
- **Full Playback Control**: Control your music directly from Windows without needing your phone
- **Discord Integration**: Show what you are listening to in your Discord status
- **Toast Notifications**: Get notified when tracks change without interrupting what you are doing

## How It Works

WindowsSpin connects to a Music Assistant server running on your home network. Music Assistant is a free, open-source music streaming platform that can:

- Play music from your personal library
- Stream from services like Spotify, YouTube Music, Tidal, and more
- Manage multiple audio players throughout your home
- Keep all players perfectly synchronized

When you run WindowsSpin, it automatically discovers Music Assistant servers on your network and connects to them. Once connected, your Windows PC becomes another audio zone that Music Assistant can send music to.

## System Requirements

- **Operating System**: Windows 10 version 1809 (October 2018 Update) or later, or Windows 11
- **Runtime**: .NET 10.0 Runtime (the installer will prompt you if needed, or use the Self-Contained version)
- **Network**: Your PC must be on the same network as your Music Assistant server
- **Audio**: Any working audio output device (speakers, headphones, HDMI audio, etc.)

## Installation

### Option 1: Download the Installer (Recommended)

1. Go to the [Releases page](https://github.com/chrisuthe/windowsSpin/releases)
2. Download the latest `.exe` installer
3. Run the installer and follow the prompts
4. Launch WindowsSpin from the Start menu

### Option 2: Download Portable Version

1. Go to the [Releases page](https://github.com/chrisuthe/windowsSpin/releases)
2. Download the portable `.zip` file
3. Extract to a folder of your choice
4. Run `SendspinClient.exe`

## Getting Started

### First Launch

When you first start WindowsSpin:

1. The application window opens showing "Searching for servers..."
2. If Music Assistant is running on your network with Sendspin enabled, WindowsSpin will automatically discover and connect to it
3. Once connected, the window shows the server name and "Connected" status

### Automatic Server Discovery

WindowsSpin uses a technology called mDNS (similar to how Apple AirPlay finds speakers) to automatically find Music Assistant servers. This works seamlessly when:

- Your PC and Music Assistant server are on the same network subnet
- Your router allows mDNS traffic (most home routers do)
- Music Assistant has the Sendspin player provider enabled

### Manual Connection

If automatic discovery does not find your server (common in complex network setups), you can connect manually:

1. In the WindowsSpin window, look for the "Manual Connection" section at the bottom
2. Enter the server address in this format: `10.0.2.8:8927` (replace with your server's IP address)
3. Click "Connect"

To find your Music Assistant server's IP address:
- Check your router's connected devices list
- Look in Music Assistant's settings
- Check the device running Music Assistant (e.g., Home Assistant dashboard)

## Using WindowsSpin

### Main Window

The main window shows:

- **Header**: Server name (when connected) or "WindowsSpin" (when disconnected)
- **Album Art**: Large artwork for the currently playing track
- **Track Info**: Song title and artist name
- **Playback Controls**: Previous, Play/Pause, Next buttons
- **Volume Slider**: Adjust volume with mute button

### Settings

Click the gear icon in the top-right corner to access settings:

#### General Settings

- **Show Notifications**: Enable/disable Windows toast notifications for track changes
- **Discord Rich Presence**: Show your currently playing track in your Discord status
- **Player Name**: The name shown in Music Assistant for this player (defaults to your computer name)

#### Audio Settings

- **Playback Device**: Select which audio output to use (speakers, headphones, etc.)
- **Static Delay**: Fine-tune audio sync timing. Use positive values if this player is ahead of others, negative if behind

#### Logging Settings

- **Log Level**: Controls how much detail is written to logs
- **File Logging**: Save logs to disk for troubleshooting
- **Console Logging**: Output logs to a console window (for debugging)

### System Tray

WindowsSpin runs in your system tray (the area near your clock):

- **Left-click** the tray icon to show/hide the main window
- **Right-click** for a quick menu with:
  - Play/Pause
  - Next/Previous track
  - Mute toggle
  - Volume control
  - Show Window
  - Exit

You can close the main window and the app continues running in the tray. This is useful if you want to set it up once and control it from Music Assistant.

### Discord Integration

When enabled in settings, WindowsSpin shows what you are listening to in your Discord profile:

- Your friends see the song title and artist
- Status updates automatically as tracks change
- Presence clears when music is paused or stopped

To enable:
1. Open Settings (gear icon)
2. Toggle on "Discord Rich Presence"
3. Click Save

Note: Discord must be running on the same computer.

## Controlling Playback

You can control music in several ways:

### From WindowsSpin
- Use the playback buttons in the main window
- Use the system tray menu
- Adjust volume with the slider

### From Music Assistant
- Use the Music Assistant web interface
- Use the Home Assistant dashboard
- Use the Music Assistant mobile app

### From Other Devices
- Any device connected to Music Assistant can control playback
- Changes sync instantly across all players

## Troubleshooting

### WindowsSpin cannot find my server

**Check network connectivity:**
- Ensure your PC and Music Assistant server are on the same network
- Try pinging the server: Open Command Prompt and type `ping [server-ip]`

**Check Music Assistant configuration:**
- In Music Assistant, go to Settings > Player Providers
- Ensure "Sendspin" is enabled
- Restart Music Assistant after enabling

**Try manual connection:**
- Use the Manual Connection feature with the server's IP address and port (default is 8927)

**Check firewall:**
- Windows Firewall may be blocking the connection
- Allow "WindowsSpin" through the firewall when prompted
- Required ports: UDP 5353 (mDNS discovery), TCP 8927 (default WebSocket port)

### Audio is not playing

**Check audio device:**
- Ensure your speakers/headphones are connected and working
- Try playing audio from another application
- In Settings, try selecting a different Playback Device

**Check Music Assistant:**
- Verify the WindowsSpin player appears in Music Assistant
- Try playing to a different player first to confirm Music Assistant is working

### Audio is out of sync with other players

**Adjust Static Delay:**
1. Open Settings (gear icon)
2. Find the "Static Delay" slider under Audio Sync
3. Adjust while music is playing:
   - If WindowsSpin plays **ahead** of other players: increase the delay (positive values)
   - If WindowsSpin plays **behind** other players: decrease the delay (negative values)
4. Changes apply immediately - no need to save first

**Typical values:**
- Most setups work well with 0ms (default)
- Hardware differences may require adjustments of -100ms to +100ms
- Network issues may require larger adjustments

### Connection drops frequently

**Check network stability:**
- Ensure your PC has a stable network connection
- Wired Ethernet connections are more reliable than Wi-Fi
- Check for network congestion or interference

**Check server stability:**
- Verify Music Assistant is running consistently
- Check Music Assistant logs for errors

### Getting more help

**Enable detailed logging:**
1. Open Settings
2. Set Log Level to "Debug" or "Verbose"
3. Enable File Logging
4. Reproduce the issue
5. Click "Open" to access log files

**Report issues:**
- Open an issue on [GitHub](https://github.com/chrisuthe/windowsSpin/issues)
- Include log files and a description of the problem
- Mention your Windows version and Music Assistant version

## Tips and Best Practices

### For Best Audio Sync

1. Use wired Ethernet connections when possible
2. Ensure all devices have accurate system time (use Windows automatic time sync)
3. Keep Music Assistant and WindowsSpin updated to latest versions

### For Convenience

1. Set WindowsSpin to start with Windows (add to Startup folder)
2. Use the system tray for quick access to controls
3. Configure your preferred audio device once and forget about it

### For Multiple PCs

You can run WindowsSpin on multiple computers to create additional audio zones:
1. Install WindowsSpin on each PC
2. Give each a unique Player Name in Settings
3. Each PC appears as a separate player in Music Assistant
4. Group them together in Music Assistant for synchronized playback

## Privacy and Security

WindowsSpin is designed for use on trusted home networks:

- **Local Network Only**: All communication stays on your local network
- **No Cloud Services**: Your music and listening data are not sent to external servers
- **No Account Required**: No sign-up, no tracking, no advertisements
- **Open Source**: The full source code is available for inspection

For detailed security information, see the Security Considerations section in the [README](../README.md).

## Glossary

- **Music Assistant**: An open-source music streaming platform that manages your music and audio players
- **Multi-room Audio**: Playing synchronized audio across multiple speakers/devices in different rooms
- **mDNS**: A technology for discovering devices on a local network without manual configuration
- **Sendspin**: The protocol used for synchronized audio streaming between Music Assistant and players
- **System Tray**: The area on your Windows taskbar (usually bottom-right) that shows background application icons

## Getting More Information

- **Music Assistant Documentation**: [music-assistant.io](https://music-assistant.io/)
- **WindowsSpin GitHub**: [github.com/chrisuthe/windowsSpin](https://github.com/chrisuthe/windowsSpin)
- **Music Assistant Discord**: [discord.gg/musicassistant](https://discord.gg/musicassistant) (for Music Assistant questions)

---

*WindowsSpin is an independent, unofficial project and is not affiliated with or endorsed by the Music Assistant project.*
