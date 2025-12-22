# Quick Start Guide

Get up and running with SendSpin Windows Client development in minutes.

## Prerequisites

- Windows 10/11
- Visual Studio 2022 or JetBrains Rider
- .NET 8.0 SDK
- Music Assistant server with SendSpin enabled

## 5-Minute Setup

### 1. Clone and Build

```bash
# Clone repository
git clone https://github.com/yourusername/windowsSpin.git
cd windowsSpin

# Restore and build
dotnet restore
dotnet build
```

### 2. Run the Application

```bash
dotnet run --project src/SendSpinClient/SendSpinClient.csproj
```

The application will start and automatically discover SendSpin servers on your network.

## Project Structure at a Glance

```
src/
├── SendSpinClient.Core/          # Protocol implementation (no UI)
│   ├── Client/                   # Main client logic
│   ├── Connection/               # WebSocket connections
│   ├── Discovery/                # mDNS discovery
│   ├── Protocol/                 # Message types and serialization
│   └── Synchronization/          # Clock sync (Kalman filter)
├── SendSpinClient.Services/      # Windows audio services
└── SendSpinClient/               # WPF application
    └── ViewModels/               # MVVM view models
```

## Common Development Tasks

### Adding a New Protocol Message

1. Create message class in `Protocol/Messages/`:

```csharp
using System.Text.Json.Serialization;

namespace SendSpinClient.Core.Protocol.Messages;

/// <summary>
/// Description of your new message.
/// </summary>
public sealed class MyNewMessage : IMessageWithPayload<MyNewPayload>
{
    [JsonPropertyName("type")]
    public string Type => "my/new-message";

    [JsonPropertyName("payload")]
    public required MyNewPayload Payload { get; init; }
}

public sealed class MyNewPayload
{
    [JsonPropertyName("field_name")]
    public string? FieldName { get; init; }
}
```

2. Add message type constant to `MessageTypes.cs`:

```csharp
public const string MyNewMessage = "my/new-message";
```

3. Handle in `SendSpinClient.cs`:

```csharp
case MessageTypes.MyNewMessage:
    HandleMyNewMessage(json);
    break;
```

### Adding a Client Command

1. Add command constant to `ClientRoles.cs`:

```csharp
public static class Commands
{
    public const string MyCommand = "my_command";
}
```

2. Send via client:

```csharp
await client.SendCommandAsync(Commands.MyCommand, new Dictionary<string, object>
{
    ["param1"] = "value1",
    ["param2"] = 123
});
```

### Adding UI Functionality

1. Create ViewModel in `ViewModels/`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace SendSpinClient.ViewModels;

public partial class MyViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _myProperty = string.Empty;

    [RelayCommand]
    private async Task DoSomethingAsync()
    {
        // Your logic here
    }
}
```

2. Use in XAML:

```xaml
<Button Content="Click Me" Command="{Binding DoSomethingCommand}" />
```

## Debugging

### Enable Detailed Logging

Set environment variables:

```bash
# PowerShell
$env:Logging__LogLevel__Default="Debug"
$env:Logging__LogLevel__SendSpinClient.Core="Trace"

# Command Prompt
set Logging__LogLevel__Default=Debug
set Logging__LogLevel__SendSpinClient.Core=Trace
```

### Useful Breakpoints

- `SendSpinClientService.OnTextMessageReceived`: All incoming messages
- `SendSpinConnection.SendMessageAsync`: All outgoing messages
- `KalmanClockSynchronizer.ProcessMeasurement`: Clock sync updates
- `MdnsServerDiscovery.ParseHost`: Server discovery

### Common Issues

**"No servers found"**
- Ensure Music Assistant has SendSpin enabled
- Check firewall allows mDNS (UDP port 5353)
- Verify client and server are on same network/subnet

**"Handshake timeout"**
- Check server logs for connection errors
- Verify WebSocket port is accessible
- Ensure correct protocol version

**"Poor clock sync"**
- Check network latency (`ping` server)
- Look for packet loss or jitter
- Network adapters with power saving can cause issues

## Testing Workflow

### Manual Testing Checklist

1. **Discovery**:
   - [ ] Client discovers servers automatically
   - [ ] Server list updates when servers appear/disappear
   - [ ] Connection info displays correctly

2. **Connection**:
   - [ ] Connects to server successfully
   - [ ] Handshake completes (Connected state)
   - [ ] Clock sync converges (<1ms uncertainty)

3. **Playback Control**:
   - [ ] Play/pause commands work
   - [ ] Volume control works
   - [ ] Track metadata displays

4. **Reconnection**:
   - [ ] Disconnect/reconnect network: auto-reconnects
   - [ ] Restart server: client reconnects
   - [ ] Server not available: appropriate error shown

### Using Wireshark

Filter for SendSpin traffic:

```
# WebSocket traffic
tcp.port == 8927

# mDNS traffic
udp.port == 5353 && dns.qry.name contains "sendspin"

# JSON messages
websocket.payload.text contains "client/hello"
```

## Code Style

### Quick Checks Before Commit

```bash
# Build in Release mode to catch all warnings
dotnet build -c Release

# Check for StyleCop/Roslynator warnings
# Fix all warnings before committing
```

### Common Patterns

**Async methods**:
```csharp
// ✓ Good
public async Task DoSomethingAsync(CancellationToken cancellationToken = default)

// ✗ Bad
public async void DoSomething()  // Never async void except event handlers
```

**Null checking**:
```csharp
// ✓ Good - use nullable reference types
public void Process(string? input)
{
    if (input is null) return;
    // input is non-null here
}
```

**Logging**:
```csharp
// ✓ Good - structured logging
_logger.LogInformation("Connected to {Server} on port {Port}", serverName, port);

// ✗ Bad - string interpolation
_logger.LogInformation($"Connected to {serverName} on port {port}");
```

**XML Documentation**:
```csharp
/// <summary>
/// Brief description of what this does.
/// </summary>
/// <param name="parameter">What this parameter is for.</param>
/// <returns>What this method returns.</returns>
public async Task<bool> MyMethodAsync(string parameter)
```

## Useful Resources

### Documentation
- [README.md](../README.md) - Project overview
- [CONTRIBUTING.md](../CONTRIBUTING.md) - Contribution guidelines
- [ARCHITECTURE.md](ARCHITECTURE.md) - Detailed architecture
- Code comments - Comprehensive XML documentation in source

### External Resources
- [SendSpin Protocol](https://github.com/music-assistant/sendspin)
- [Music Assistant Docs](https://music-assistant.io/documentation/)
- [WPF Tutorial](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/)
- [MVVM Toolkit](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/)

### Community
- [GitHub Issues](https://github.com/yourusername/windowsSpin/issues)
- [Music Assistant Discord](https://discord.gg/musicassistant)

## Next Steps

1. **Read the Architecture**: Review [ARCHITECTURE.md](ARCHITECTURE.md) for design details
2. **Browse the Code**: Start with `SendSpinClientService.cs` - the main orchestrator
3. **Pick an Issue**: Check [GitHub Issues](https://github.com/yourusername/windowsSpin/issues) for "good first issue"
4. **Join Discussion**: Ask questions in GitHub Discussions or Discord

Happy coding!
