# Spec-Compliant Connection Modes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the `Auto` connection mode so the client uses exactly one transport at a time, defaulting to server-initiated.

**Architecture:** The two transports become an if/else rather than two independent `if`s, making exclusivity structural. The mode string mapping — the part that can fail silently — moves into a pure, unit-tested class in `Sendspin.Windows.Services`. The app-side rejection of duplicate connections is deleted so the SDK's spec-compliant arbitration is left alone.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm, xUnit, Sendspin.SDK 9.3.0

**Spec:** `docs/superpowers/specs/2026-08-30-spec-compliant-connection-modes-design.md`

## Global Constraints

- Branch: `feat/spec-compliant-connection-modes`, cut from `release/2.2.4`. Forward-port to `master` after merge.
- The app MUST NEVER produce `ConnectionMode.Auto`. The SDK enum retains it (removal tracked in Sendspin/sendspin-dotnet#254); our mapping must never return it for any input.
- Config values are exactly `"AdvertiseOnly"` and `"DiscoverOnly"`.
- Display names are exactly `"Let servers connect to me"` and `"I choose a server"`.
- Default for any unrecognized, legacy, null, or empty config value is `AdvertiseOnly`.
- Migration is silent and logged at information level. No user prompt.
- Build command: `dotnet build Sendspin.Windows.sln`
- Test command: `dotnet test tests\Sendspin.Windows.Services.Tests\Sendspin.Windows.Services.Tests.csproj`
- **Pre-existing test failures:** `DynamicResamplerSampleProviderTests.RateCorrection_ConcealsShortfalls_WithoutSilenceGaps` and `MultiRoomSyncAlignmentTests.StartupPrefill_Uncompensated_DriftsOffSchedule` fail on `release/2.2.4` before any of this work. Expect them. A run showing exactly these two failures is a pass.

---

### Task 1: ConnectionModeMapping

The only unit-testable piece. `Sendspin.Windows` (the WPF app) has no test project, so everything in later tasks is verified by build plus manual checks — which is precisely why the fallible string logic is extracted here.

**Files:**
- Create: `src/Sendspin.Windows.Services/Configuration/ConnectionModeMapping.cs`
- Test: `tests/Sendspin.Windows.Services.Tests/Configuration/ConnectionModeMappingTests.cs`

**Interfaces:**
- Consumes: `Sendspin.SDK.Client.ConnectionMode` (already referenced by the Services project via `Sendspin.SDK` 9.3.0)
- Produces:
  - `const string ConnectionModeMapping.AdvertiseOnlyDisplayName = "Let servers connect to me"`
  - `const string ConnectionModeMapping.DiscoverOnlyDisplayName = "I choose a server"`
  - `static string[] ConnectionModeMapping.DisplayNames { get; }`
  - `static ConnectionMode ConnectionModeMapping.FromConfigValue(string? configValue)`
  - `static string ConnectionModeMapping.ToConfigValue(ConnectionMode mode)`
  - `static string ConnectionModeMapping.ToDisplayName(ConnectionMode mode)`
  - `static ConnectionMode ConnectionModeMapping.FromDisplayName(string? displayName)`

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.Windows.Services.Tests/Configuration/ConnectionModeMappingTests.cs`:

```csharp
using Sendspin.SDK.Client;
using Sendspin.Windows.Services.Configuration;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Configuration;

public class ConnectionModeMappingTests
{
    [Theory]
    [InlineData("AdvertiseOnly")]
    [InlineData("DiscoverOnly")]
    public void FromConfigValue_RoundTripsKnownValues(string configValue)
    {
        var mode = ConnectionModeMapping.FromConfigValue(configValue);
        Assert.Equal(configValue, ConnectionModeMapping.ToConfigValue(mode));
    }

    [Fact]
    public void FromConfigValue_DiscoverOnly_ReturnsDiscoverOnly()
    {
        Assert.Equal(ConnectionMode.DiscoverOnly, ConnectionModeMapping.FromConfigValue("DiscoverOnly"));
    }

    [Fact]
    public void FromConfigValue_AdvertiseOnly_ReturnsAdvertiseOnly()
    {
        Assert.Equal(ConnectionMode.AdvertiseOnly, ConnectionModeMapping.FromConfigValue("AdvertiseOnly"));
    }

    [Fact]
    public void FromConfigValue_LegacyAuto_MigratesToAdvertiseOnly()
    {
        Assert.Equal(ConnectionMode.AdvertiseOnly, ConnectionModeMapping.FromConfigValue("Auto"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Nonsense")]
    [InlineData("advertiseonly")]
    public void FromConfigValue_UnrecognizedInput_DefaultsToAdvertiseOnly(string? configValue)
    {
        Assert.Equal(ConnectionMode.AdvertiseOnly, ConnectionModeMapping.FromConfigValue(configValue));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Auto")]
    [InlineData("Nonsense")]
    [InlineData("AdvertiseOnly")]
    [InlineData("DiscoverOnly")]
    public void FromConfigValue_NeverReturnsAuto(string? configValue)
    {
        Assert.NotEqual(ConnectionMode.Auto, ConnectionModeMapping.FromConfigValue(configValue));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Auto")]
    [InlineData("Advertise Only")]
    [InlineData("Let servers connect to me")]
    [InlineData("I choose a server")]
    public void FromDisplayName_NeverReturnsAuto(string? displayName)
    {
        Assert.NotEqual(ConnectionMode.Auto, ConnectionModeMapping.FromDisplayName(displayName));
    }

    [Fact]
    public void FromDisplayName_DiscoverOnlyLabel_ReturnsDiscoverOnly()
    {
        Assert.Equal(
            ConnectionMode.DiscoverOnly,
            ConnectionModeMapping.FromDisplayName(ConnectionModeMapping.DiscoverOnlyDisplayName));
    }

    [Fact]
    public void FromDisplayName_UnrecognizedLabel_DefaultsToAdvertiseOnly()
    {
        Assert.Equal(ConnectionMode.AdvertiseOnly, ConnectionModeMapping.FromDisplayName("Advertise Only"));
    }

    [Theory]
    [InlineData(ConnectionMode.AdvertiseOnly)]
    [InlineData(ConnectionMode.DiscoverOnly)]
    public void ToDisplayName_RoundTrips(ConnectionMode mode)
    {
        Assert.Equal(mode, ConnectionModeMapping.FromDisplayName(ConnectionModeMapping.ToDisplayName(mode)));
    }

    [Fact]
    public void ToConfigValue_Auto_CoercedToAdvertiseOnly()
    {
        // Auto is unreachable through our own mapping, but the SDK enum still allows it.
        // Coerce rather than emit a value we would refuse to read back.
        Assert.Equal("AdvertiseOnly", ConnectionModeMapping.ToConfigValue(ConnectionMode.Auto));
    }

    [Fact]
    public void ToDisplayName_Auto_CoercedToAdvertiseOnlyLabel()
    {
        Assert.Equal(
            ConnectionModeMapping.AdvertiseOnlyDisplayName,
            ConnectionModeMapping.ToDisplayName(ConnectionMode.Auto));
    }

    [Fact]
    public void DisplayNames_ContainsExactlyTheTwoSupportedModes()
    {
        Assert.Equal(
            new[] { ConnectionModeMapping.AdvertiseOnlyDisplayName, ConnectionModeMapping.DiscoverOnlyDisplayName },
            ConnectionModeMapping.DisplayNames);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet build tests\Sendspin.Windows.Services.Tests\Sendspin.Windows.Services.Tests.csproj`

Expected: FAIL with `error CS0246: The type or namespace name 'ConnectionModeMapping' could not be found`.

- [ ] **Step 3: Create a stub so the tests fail on assertions rather than compilation**

Create `src/Sendspin.Windows.Services/Configuration/ConnectionModeMapping.cs`:

```csharp
using Sendspin.SDK.Client;

namespace Sendspin.Windows.Services.Configuration;

/// <summary>
/// Maps connection mode between its persisted config value, its settings display name, and
/// <see cref="ConnectionMode"/>.
/// </summary>
public static class ConnectionModeMapping
{
    /// <summary>Display name for server-initiated mode.</summary>
    public const string AdvertiseOnlyDisplayName = "Let servers connect to me";

    /// <summary>Display name for client-initiated mode.</summary>
    public const string DiscoverOnlyDisplayName = "I choose a server";

    /// <summary>Gets the display names offered in the settings dropdown, in order.</summary>
    public static string[] DisplayNames => Array.Empty<string>();

    /// <summary>Maps a persisted config value to a mode.</summary>
    /// <param name="configValue">The stored value, which may be legacy or absent.</param>
    /// <returns>The mode; never <see cref="ConnectionMode.Auto"/>.</returns>
    public static ConnectionMode FromConfigValue(string? configValue) => ConnectionMode.Auto;

    /// <summary>Maps a mode to its persisted config value.</summary>
    /// <param name="mode">The mode to persist.</param>
    /// <returns>The config value.</returns>
    public static string ToConfigValue(ConnectionMode mode) => string.Empty;

    /// <summary>Maps a mode to its settings display name.</summary>
    /// <param name="mode">The mode to display.</param>
    /// <returns>The display name.</returns>
    public static string ToDisplayName(ConnectionMode mode) => string.Empty;

    /// <summary>Maps a settings display name to a mode.</summary>
    /// <param name="displayName">The selected display name.</param>
    /// <returns>The mode; never <see cref="ConnectionMode.Auto"/>.</returns>
    public static ConnectionMode FromDisplayName(string? displayName) => ConnectionMode.Auto;
}
```

Run: `dotnet test tests\Sendspin.Windows.Services.Tests\Sendspin.Windows.Services.Tests.csproj --filter "FullyQualifiedName~ConnectionModeMapping"`

Expected: FAIL on assertions, not compilation. `FromConfigValue_NeverReturnsAuto` should fail with `Assert.NotEqual() Failure`.

- [ ] **Step 4: Write the implementation**

Replace everything from `public static string[] DisplayNames` to the end of the class in
`src/Sendspin.Windows.Services/Configuration/ConnectionModeMapping.cs` with the following. The two
public display-name constants at the top of the class stay exactly as written in Step 3.

```csharp
    private const string AdvertiseOnlyConfigValue = "AdvertiseOnly";
    private const string DiscoverOnlyConfigValue = "DiscoverOnly";

    /// <summary>Gets the display names offered in the settings dropdown, in order.</summary>
    public static string[] DisplayNames { get; } =
    {
        AdvertiseOnlyDisplayName,
        DiscoverOnlyDisplayName,
    };

    /// <summary>Maps a persisted config value to a mode.</summary>
    /// <param name="configValue">The stored value, which may be legacy or absent.</param>
    /// <returns>The mode; never <see cref="ConnectionMode.Auto"/>.</returns>
    /// <remarks>
    /// Anything unrecognized — including the legacy "Auto", which ran both transports in
    /// violation of the spec — resolves to <see cref="ConnectionMode.AdvertiseOnly"/>.
    /// </remarks>
    public static ConnectionMode FromConfigValue(string? configValue)
        => configValue == DiscoverOnlyConfigValue
            ? ConnectionMode.DiscoverOnly
            : ConnectionMode.AdvertiseOnly;

    /// <summary>Maps a mode to its persisted config value.</summary>
    /// <param name="mode">The mode to persist.</param>
    /// <returns>The config value.</returns>
    public static string ToConfigValue(ConnectionMode mode)
        => mode == ConnectionMode.DiscoverOnly
            ? DiscoverOnlyConfigValue
            : AdvertiseOnlyConfigValue;

    /// <summary>Maps a mode to its settings display name.</summary>
    /// <param name="mode">The mode to display.</param>
    /// <returns>The display name.</returns>
    public static string ToDisplayName(ConnectionMode mode)
        => mode == ConnectionMode.DiscoverOnly
            ? DiscoverOnlyDisplayName
            : AdvertiseOnlyDisplayName;

    /// <summary>Maps a settings display name to a mode.</summary>
    /// <param name="displayName">The selected display name.</param>
    /// <returns>The mode; never <see cref="ConnectionMode.Auto"/>.</returns>
    public static ConnectionMode FromDisplayName(string? displayName)
        => displayName == DiscoverOnlyDisplayName
            ? ConnectionMode.DiscoverOnly
            : ConnectionMode.AdvertiseOnly;
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests\Sendspin.Windows.Services.Tests\Sendspin.Windows.Services.Tests.csproj --filter "FullyQualifiedName~ConnectionModeMapping"`

Expected: PASS, all tests.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test tests\Sendspin.Windows.Services.Tests\Sendspin.Windows.Services.Tests.csproj`

Expected: exactly the two pre-existing failures listed in Global Constraints; everything else passes.

- [ ] **Step 7: Commit**

```bash
git add src/Sendspin.Windows.Services/Configuration/ConnectionModeMapping.cs tests/Sendspin.Windows.Services.Tests/Configuration/ConnectionModeMappingTests.cs
git commit -m "feat: add ConnectionModeMapping with Auto migration

Pure mapping between config value, display name, and ConnectionMode. Any
unrecognized value — including the legacy Auto, which ran both transports
against the spec's MUST — resolves to AdvertiseOnly. Tests assert Auto is
never returned for any input.

Refs #76"
```

---

### Task 2: Adopt the mapping in MainViewModel

Replaces all four sites where `Auto` currently appears. Three of them are `_ =>` fallbacks that would otherwise silently resurrect it.

**Files:**
- Modify: `src/Sendspin.Windows/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `ConnectionModeMapping` from Task 1 (all members)
- Produces: `MainViewModel.SettingsConnectionMode` now holds one of `ConnectionModeMapping.DisplayNames`; `ParseConnectionMode` no longer exists.

- [ ] **Step 1: Add the using**

At the top of `MainViewModel.cs`, alongside the other `Sendspin.Windows.Services.*` usings:

```csharp
using Sendspin.Windows.Services.Configuration;
```

- [ ] **Step 2: Change the default and the dropdown list**

Replace (around line 367):

```csharp
    [ObservableProperty]
    private string _settingsConnectionMode = "Auto";

    /// <summary>
    /// Gets the available connection mode options for the settings dropdown.
    /// </summary>
    public string[] AvailableConnectionModes { get; } = new[]
    {
        "Auto",
        "Advertise Only",
        "Discover Only"
    };
```

with:

```csharp
    [ObservableProperty]
    private string _settingsConnectionMode = ConnectionModeMapping.AdvertiseOnlyDisplayName;

    /// <summary>
    /// Gets the available connection mode options for the settings dropdown.
    /// </summary>
    public string[] AvailableConnectionModes { get; } = ConnectionModeMapping.DisplayNames;
```

- [ ] **Step 3: Delete ParseConnectionMode**

Delete this method entirely (around line 671):

```csharp
    private static ConnectionMode ParseConnectionMode(string displayName)
    {
        return displayName switch
        {
            "Advertise Only" => ConnectionMode.AdvertiseOnly,
            "Discover Only" => ConnectionMode.DiscoverOnly,
            _ => ConnectionMode.Auto
        };
    }
```

In `InitializeAsync` (around line 633), replace:

```csharp
        var mode = ParseConnectionMode(SettingsConnectionMode);
```

with:

```csharp
        var mode = ConnectionModeMapping.FromDisplayName(SettingsConnectionMode);
```

- [ ] **Step 4: Replace the save mapping**

Replace `OnSettingsConnectionModeChanged` (around line 2088):

```csharp
    partial void OnSettingsConnectionModeChanged(string value)
    {
        // Convert display name to config value
        var configValue = value switch
        {
            "Advertise Only" => "AdvertiseOnly",
            "Discover Only" => "DiscoverOnly",
            _ => "Auto"
        };
        SaveConnectionModeAsync(configValue).SafeFireAndForget(_logger);
    }
```

with:

```csharp
    partial void OnSettingsConnectionModeChanged(string value)
    {
        var configValue = ConnectionModeMapping.ToConfigValue(
            ConnectionModeMapping.FromDisplayName(value));
        SaveConnectionModeAsync(configValue).SafeFireAndForget(_logger);
    }
```

- [ ] **Step 5: Replace the load mapping and log the migration**

Replace the settings-load block (around line 2359):

```csharp
        // Load connection mode
        var modeStr = _configuration.GetValue<string>("Connection:Mode", "Auto") ?? "Auto";
        SettingsConnectionMode = modeStr switch
        {
            "AdvertiseOnly" => "Advertise Only",
            "DiscoverOnly" => "Discover Only",
            _ => "Auto"
        };
```

with:

```csharp
        // Load connection mode. Anything we do not recognize — including the legacy "Auto",
        // which ran both transports in violation of the spec — resolves to AdvertiseOnly.
        var modeStr = _configuration.GetValue<string>("Connection:Mode", string.Empty) ?? string.Empty;
        var loadedMode = ConnectionModeMapping.FromConfigValue(modeStr);
        var canonicalValue = ConnectionModeMapping.ToConfigValue(loadedMode);
        if (!string.IsNullOrEmpty(modeStr) && modeStr != canonicalValue)
        {
            _logger.LogInformation(
                "Migrating unsupported connection mode {StoredMode} to {NewMode}",
                modeStr,
                canonicalValue);
        }

        SettingsConnectionMode = ConnectionModeMapping.ToDisplayName(loadedMode);
```

- [ ] **Step 6: Build**

Run: `dotnet build Sendspin.Windows.sln`

Expected: `0 Error(s)`. If `ConnectionMode` is now an unused using in `MainViewModel.cs`, leave it — `Sendspin.SDK.Client` is used for other types.

- [ ] **Step 7: Commit**

```bash
git add src/Sendspin.Windows/ViewModels/MainViewModel.cs
git commit -m "refactor: route connection mode through ConnectionModeMapping

Replaces the four sites that referenced Auto, three of which were _ =>
fallbacks that would silently resurrect it once the mode is removed from
the UI. Logs the migration when a stored value is not one we support.

Refs #76"
```

---

### Task 3: Make the transports mutually exclusive

The core fix. Two independent `if`s become an if/else, so no mode can start both.

**Files:**
- Modify: `src/Sendspin.Windows/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `ConnectionModeMapping.FromDisplayName` (Task 1), `SettingsConnectionMode` (Task 2)
- Produces: `ApplyConnectionModeAsync(ConnectionMode mode)` — note the parameter type changes from `string` to `ConnectionMode`.

- [ ] **Step 1: Make InitializeAsync exclusive**

In `InitializeAsync` (around line 637), replace:

```csharp
            // Start host service (server-initiated mode) unless DiscoverOnly
            if (mode != ConnectionMode.DiscoverOnly)
            {
                StatusMessage = "Starting host service...";
                await _hostService.StartAsync();
                ClientId = _hostService.ClientId;
                IsHosting = true;
                _logger.LogInformation("Host service started, advertising as {ClientId}", ClientId);
            }

            // Start server discovery (client-initiated mode) unless AdvertiseOnly
            if (mode != ConnectionMode.AdvertiseOnly)
            {
                StatusMessage = "Discovering Sendspin servers...";
                await _serverDiscovery.StartAsync();
                _logger.LogInformation("Server discovery started, looking for _sendspin-server._tcp");
            }

            StatusMessage = mode switch
            {
                ConnectionMode.AdvertiseOnly => $"Advertising as player...\nClient ID: {ClientId}",
                ConnectionMode.DiscoverOnly => "Searching for servers...",
                _ => $"Searching for servers...\nClient ID: {ClientId}"
            };
```

with:

```csharp
            // Exactly one transport, per spec: a client MUST use exactly one of the two
            // connection methods at a time. An if/else keeps that structural — the previous
            // pair of independent ifs let Auto satisfy neither exclusion and start both.
            if (mode == ConnectionMode.DiscoverOnly)
            {
                StatusMessage = "Discovering Sendspin servers...";
                await _serverDiscovery.StartAsync();
                _logger.LogInformation("Server discovery started, looking for _sendspin-server._tcp");
                StatusMessage = "Searching for servers...";
            }
            else
            {
                StatusMessage = "Starting host service...";
                await _hostService.StartAsync();
                ClientId = _hostService.ClientId;
                IsHosting = true;
                _logger.LogInformation("Host service started, advertising as {ClientId}", ClientId);
                StatusMessage = $"Waiting for a server to connect...\nClient ID: {ClientId}";
            }
```

- [ ] **Step 2: Make the runtime mode switch exclusive and stop-before-start**

Replace `ApplyConnectionModeAsync` (starting around line 2116) in full:

```csharp
    private async Task ApplyConnectionModeAsync(ConnectionMode mode)
    {
        var shouldAdvertise = mode != ConnectionMode.DiscoverOnly;

        // Stop the outgoing transport BEFORE starting the incoming one, so the two are never
        // running together even momentarily.
        if (shouldAdvertise && _serverDiscovery.IsDiscovering)
        {
            try
            {
                await _serverDiscovery.StopAsync();
                _logger.LogInformation("Server discovery stopped");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop server discovery");
            }
        }
        else if (!shouldAdvertise && IsHosting)
        {
            try
            {
                await _hostService.StopAsync();
                IsHosting = false;
                _logger.LogInformation("Host service stopped");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop host service");
            }
        }

        if (shouldAdvertise && !IsHosting)
        {
            try
            {
                await _hostService.StartAsync();
                ClientId = _hostService.ClientId;
                IsHosting = true;
                _logger.LogInformation("Host service started, advertising as {ClientId}", ClientId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start host service");
            }
        }
        else if (!shouldAdvertise && !_serverDiscovery.IsDiscovering)
        {
            try
            {
                await _serverDiscovery.StartAsync();
                _logger.LogInformation("Server discovery started");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start server discovery");
            }
        }
    }
```

- [ ] **Step 3: Update the call site**

In `SaveConnectionModeAsync` (around line 2108), replace:

```csharp
            await ApplyConnectionModeAsync(mode);
```

with:

```csharp
            await ApplyConnectionModeAsync(ConnectionModeMapping.FromConfigValue(mode));
```

- [ ] **Step 4: Build**

Run: `dotnet build Sendspin.Windows.sln`

Expected: `0 Error(s)`. If the compiler reports an unused local or an unreachable branch left over from the old `shouldDiscover` variable, delete it.

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.Windows/ViewModels/MainViewModel.cs
git commit -m "fix: run exactly one connection transport at a time

The spec requires clients use exactly one of the two connection methods at
a time. Two independent ifs, each excluding one mode, let Auto satisfy
neither and start both. An if/else makes exclusivity structural.

The runtime mode switch now stops the outgoing transport before starting
the incoming one, so the two never overlap.

Refs #76"
```

---

### Task 4: Delete the app-side connection rejection

With exclusive transports this guard is unreachable, and it was the mechanism that tore down playing sessions.

**Files:**
- Modify: `src/Sendspin.Windows/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: nothing new
- Produces: `OnServerConnected` no longer calls `_hostService.DisconnectAllAsync`

- [ ] **Step 1: Delete the rejection block**

In `OnServerConnected` (around line 1258), delete:

```csharp
            // Reject server-initiated connections if we already have a client-initiated connection
            if (_manualClient?.ConnectionState == ConnectionState.Connected)
            {
                _logger.LogInformation(
                    "Rejecting server-initiated connection from {ServerName} - already connected via client-initiated mode",
                    server.ServerName);
                _hostService.DisconnectAllAsync("already_connected").SafeFireAndForget(_logger);
                return;
            }

```

Leave the rest of the method unchanged, so it begins:

```csharp
    private void OnServerConnected(object? sender, ConnectedServerInfo server)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            ConnectedServers.Add(server);
```

Add this comment immediately above `ConnectedServers.Add(server);`:

```csharp
            // No app-side arbitration. SendspinHostService already applies the spec's admission
            // rules (activity ranking, LastPlayedServerId tiebreak) and disconnects only the
            // loser. The previous guard here called DisconnectAllAsync to reject a single
            // unwanted socket, which tore down every connection and reset the shared
            // IAudioPipeline and IClockSynchronizer that the playing session was using.
```

- [ ] **Step 2: Build**

Run: `dotnet build Sendspin.Windows.sln`

Expected: `0 Error(s)`. If `_manualClient` or `ConnectionState` become unused as a result, they will not — both are used elsewhere in the file (for example `IsConnected` at line ~451).

- [ ] **Step 3: Commit**

```bash
git add src/Sendspin.Windows/ViewModels/MainViewModel.cs
git commit -m "fix: stop overriding SDK connection arbitration

OnServerConnected called DisconnectAllAsync to reject one unwanted incoming
socket, which disconnected every connection and reset the shared audio
pipeline and clock synchronizer — dropping the session that was playing.

SendspinHostService already arbitrates per spec and disconnects only the
loser. With exclusive transports the guard is unreachable anyway.

Refs #76"
```

---

### Task 5: Fix the searching indicator and gate the server picker

`IsSearchingForServers` keys off `IsHosting`, not discovery. Under `Auto` both ran, so it worked by accident; with exclusive modes `AdvertiseOnly` would show "Searching for servers…" permanently in a mode that never searches.

**Files:**
- Modify: `src/Sendspin.Windows/ViewModels/MainViewModel.cs`
- Modify: `src/Sendspin.Windows/MainWindow.xaml`

**Interfaces:**
- Consumes: `ConnectionModeMapping.FromDisplayName` (Task 1)
- Produces: `MainViewModel.IsDiscoverMode` (bool), `MainViewModel.IsSearchingForServers` (bool, semantics corrected)

- [ ] **Step 1: Add IsDiscoverMode and correct IsSearchingForServers**

Replace (around line 445):

```csharp
    public bool IsSearchingForServers => DiscoveredServers.Count == 0 && IsHosting;
```

with:

```csharp
    /// <summary>
    /// Gets whether the client picks its own server (client-initiated mode). The server list and
    /// the auto-connect preference are meaningful only in this mode; in server-initiated mode the
    /// server decides when to connect.
    /// </summary>
    public bool IsDiscoverMode
        => ConnectionModeMapping.FromDisplayName(SettingsConnectionMode) == ConnectionMode.DiscoverOnly;

    /// <summary>
    /// Gets whether a discovery scan is running and has not yet found anything.
    /// </summary>
    /// <remarks>
    /// Keyed off discovery, not hosting. It previously read <c>IsHosting</c>, which only appeared
    /// correct because Auto ran both transports; in server-initiated mode that would show a
    /// permanent "searching" state for a mode that never searches.
    /// </remarks>
    public bool IsSearchingForServers => IsDiscoverMode && DiscoveredServers.Count == 0;
```

- [ ] **Step 2: Raise change notifications when the mode changes**

In `OnSettingsConnectionModeChanged` (modified in Task 2), append the two notifications so the method reads:

```csharp
    partial void OnSettingsConnectionModeChanged(string value)
    {
        var configValue = ConnectionModeMapping.ToConfigValue(
            ConnectionModeMapping.FromDisplayName(value));
        SaveConnectionModeAsync(configValue).SafeFireAndForget(_logger);

        OnPropertyChanged(nameof(IsDiscoverMode));
        OnPropertyChanged(nameof(IsSearchingForServers));
    }
```

- [ ] **Step 3: Gate the server picker card**

In `src/Sendspin.Windows/MainWindow.xaml`, find the "Section 2: Available Servers" card (around line 241):

```xml
                    <!-- Section 2: Available Servers -->
                    <Border Style="{StaticResource WelcomeCardStyle}">
```

Replace those two lines with:

```xml
                    <!-- Section 2: Available Servers (client-initiated mode only — in
                         server-initiated mode the server chooses when to connect) -->
                    <Border Style="{StaticResource WelcomeCardStyle}"
                            Visibility="{Binding IsDiscoverMode, Converter={StaticResource BoolToVisibilityConverter}}">
```

- [ ] **Step 4: Add the waiting card for server-initiated mode**

Immediately after the closing `</Border>` of the "Available Servers" card, add:

```xml
                    <!-- Server-initiated mode: we advertise and wait. No picker — the server
                         decides when to connect. Pairing affordances are future work. -->
                    <Border Style="{StaticResource WelcomeCardStyle}">
                        <Border.Visibility>
                            <Binding Path="IsDiscoverMode" Converter="{StaticResource InverseBoolToVisibilityConverter}"/>
                        </Border.Visibility>
                        <StackPanel>
                            <TextBlock Text="Waiting for a Server" Style="{StaticResource WelcomeSectionHeader}"/>
                            <TextBlock Text="📡" Style="{StaticResource SearchingIcon}"/>
                            <TextBlock Text="Advertising on your network. Select this player in Music Assistant to connect."
                                       Style="{StaticResource CaptionText}"
                                       HorizontalAlignment="Center"
                                       TextAlignment="Center"
                                       TextWrapping="Wrap"
                                       Foreground="{StaticResource TextMutedBrush}"
                                       Margin="0,8,0,0"/>
                        </StackPanel>
                    </Border>
```

Note on the auto-connect dialog: the spec requires the "connect once / connect always" dialog be
hidden in server-initiated mode too. No separate change is needed — that dialog is raised only by
`SelectServer`, which is reachable only through `ServerCard_Click` on cards inside the
`DiscoveredServers` `ItemsControl` that Step 3 hides. Hiding the card makes the dialog unreachable.
Do not add a second visibility binding for it; `ShowAutoConnectDialog` can never become true in this
mode.

- [ ] **Step 5: Verify the inverse converter exists**

Run: `grep -n "InverseBoolToVisibilityConverter" src/Sendspin.Windows/MainWindow.xaml src/Sendspin.Windows/App.xaml src/Sendspin.Windows/Resources/Styles/*.xaml`

If it returns nothing, the converter does not exist. In that case, replace the `<Border.Visibility>` block from Step 4 with a style trigger instead, which needs no converter:

```xml
                        <Border.Style>
                            <Style TargetType="Border" BasedOn="{StaticResource WelcomeCardStyle}">
                                <Setter Property="Visibility" Value="Visible"/>
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding IsDiscoverMode}" Value="True">
                                        <Setter Property="Visibility" Value="Collapsed"/>
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Border.Style>
```

and delete the `Style="{StaticResource WelcomeCardStyle}"` attribute from that `<Border>` opening tag, since the style now comes from `BasedOn`.

- [ ] **Step 6: Build**

Run: `dotnet build Sendspin.Windows.sln`

Expected: `0 Error(s)`. XAML errors surface at build time, so a clean build confirms the markup parses and every binding target and resource key resolves.

- [ ] **Step 7: Commit**

```bash
git add src/Sendspin.Windows/ViewModels/MainViewModel.cs src/Sendspin.Windows/MainWindow.xaml
git commit -m "fix: scope the server picker and searching state to discover mode

IsSearchingForServers keyed off IsHosting rather than discovery. That only
looked right because Auto ran both transports; in server-initiated mode it
would show a permanent searching state for a mode that never searches.

The server list and auto-connect preference express a choice server-initiated
mode does not have, so they are hidden there in favour of a waiting state.

Refs #76"
```

---

### Task 6: Change the shipped default and verify end to end

**Files:**
- Modify: `src/Sendspin.Windows/appsettings.json`

**Interfaces:**
- Consumes: everything from Tasks 1–5
- Produces: shipped default `Connection:Mode = "AdvertiseOnly"`

- [ ] **Step 1: Change the shipped default**

In `src/Sendspin.Windows/appsettings.json`, replace:

```json
  "Connection": {
    "Mode": "Auto",
```

with:

```json
  "Connection": {
    "Mode": "AdvertiseOnly",
```

- [ ] **Step 2: Build and run the full suite**

Run: `dotnet build Sendspin.Windows.sln`
Expected: `0 Error(s)`

Run: `dotnet test tests\Sendspin.Windows.Services.Tests\Sendspin.Windows.Services.Tests.csproj`
Expected: exactly the two pre-existing failures from Global Constraints.

- [ ] **Step 3: Verify the migration path**

The user config lives at `%LOCALAPPDATA%\Sendspin for Windows\appsettings.json`. Back it up before editing:

```powershell
$cfg = "$env:LOCALAPPDATA\Sendspin for Windows\appsettings.json"
Copy-Item $cfg "$cfg.bak" -Force
```

Set `"Mode": "Auto"` in that file, launch the app, then check the log:

```powershell
$logs = "$env:LOCALAPPDATA\Sendspin for Windows\logs"
$f = Get-ChildItem $logs -Filter *.log | Where-Object { $_.Name -notlike "sync-health*" } |
     Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Content $f.FullName | Select-String -Pattern "Migrating unsupported connection mode|Host service started|Server discovery started"
```

Expected: a `Migrating unsupported connection mode Auto to AdvertiseOnly` line, a `Host service started` line, and **no** `Server discovery started` line. Restore the backup when finished.

- [ ] **Step 4: Verify each mode starts exactly one transport**

With `"Mode": "AdvertiseOnly"`: log shows `Host service started`, no `Server discovery started`. Welcome screen shows the "Waiting for a Server" card and no server list.

With `"Mode": "DiscoverOnly"`: log shows `Server discovery started`, no `Host service started`. Welcome screen shows "Available Servers" and no waiting card.

- [ ] **Step 5: Verify the #76 regression is fixed**

With two servers on the network and `"Mode": "AdvertiseOnly"`, let one connect and start playback, then have the second server target this player. Confirm in the log:

- an `Arbitration:` line from `SendspinHostService` deciding between them
- **no** `Rejecting server-initiated connection` line (that code is deleted)
- **no** `Clock synchronizer reset` followed by `Pipeline state: "Playing" -> "Stopping"` unless arbitration genuinely displaced the current server

- [ ] **Step 6: Verify a runtime mode switch never overlaps transports**

Launch the app, open Settings, and switch the connection mode dropdown from
`Let servers connect to me` to `I choose a server`, then back. After each switch:

```powershell
$logs = "$env:LOCALAPPDATA\Sendspin for Windows\logs"
$f = Get-ChildItem $logs -Filter *.log | Where-Object { $_.Name -notlike "sync-health*" } |
     Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Content $f.FullName | Select-String -Pattern "Host service started|Host service stopped|Server discovery started|Server discovery stopped"
```

Expected: every switch produces a *stopped* line before the matching *started* line — for example
`Host service stopped` then `Server discovery started`. A *started* line appearing before the other
transport's *stopped* line means the two overlapped and Task 3 Step 2 was not applied correctly.

- [ ] **Step 7: Commit**

```bash
git add src/Sendspin.Windows/appsettings.json
git commit -m "feat: default to server-initiated connections

Server-initiated is the spec-recommended mode: its multi-server admission
rules are standardized, whereas client-initiated multi-server handling is
explicitly implementation-defined. Existing configs carrying Auto migrate
to this value on load.

Closes #76"
```

---

## Post-implementation

1. Open a PR against `release/2.2.4`.
2. Forward-port the whole branch to `master`.
3. Port the `claude.md` connection-mode correction (PR #77, which targets `master`) back to `release/2.2.4`. Note the two branches' `claude.md` files have already diverged — `release/2.2.4` carries newer sync-correction constants that `master` lacks — so reconcile rather than overwrite.
4. `v2.2.4` is not yet tagged, so this default flip lands before release rather than changing behaviour under existing users.
