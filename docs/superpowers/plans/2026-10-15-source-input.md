# Source Input ("Send") Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user press Send to stream desktop audio or a recording device to Music Assistant through the Sendspin `source@v1` role, with no captured audio leaving the PC until they do.

**Architecture:** SDK 10.1.0 gains a host-level line-sense API and source-role state, because the host keeps its per-connection clients private. In the app, one long-lived `WindowsAudioCaptureDevice` is handed to the SDK. It runs generated silence until Send opens its gate, then a WASAPI process-loopback source (desktop, excluding Sendspin) or an endpoint source (recording device), always at 48 kHz/16-bit/stereo. A `SourceSendController` owns Send, reports line-sense, and computes why Send is unavailable. The view model and XAML surface it as a volume-bar button, a red pulsing badge, a tray item and a Settings picker.

**Tech Stack:** .NET 10 / WPF / CommunityToolkit.Mvvm, NAudio 2.2.1 (WASAPI), COM interop (process loopback), Sendspin.SDK 10.0.0 → 10.1.0, xUnit 2.9.

**Spec:** `docs/superpowers/specs/2026-10-15-source-input-design.md` (read it first; this plan argues from it).

## Global Constraints

- Target framework stays `net10.0-windows10.0.17763.0`; process loopback is runtime-gated with `OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)`.
- No new NuGet packages. NAudio stays 2.2.1.
- Wire format for every source: codec `pcm`, 48000 Hz, 2 channels, 16-bit (`CaptureFormat`).
- `source@v1` is always advertised, with `SourceRoleSupport { LineSense = true }`.
- No captured audio leaves the PC while Send is off, and no recording device or loopback is opened while Send is off.
- Send is off at launch, and `absent` is reported before the first `present`, because MA records a client's first report silently.
- Setting key `Audio:SourceInput`: value `"desktop"` or a WASAPI endpoint ID.
- UI strings, verbatim: badge `SENDING · <input name>`; tooltips `Send <input name>` / `Stop sending`; desktop picker label `Desktop audio (all apps except Sendspin)`; settings caption `INPUT`, label `Send Input`, description `What the Send button streams to Music Assistant`; hint `If Music Assistant plays this input back on this PC, use headphones or choose another target.`
- Unavailable reasons, verbatim: `Not connected to Music Assistant`, `Pair this player with Music Assistant to send audio`, `This server doesn't accept audio inputs`, `No audio inputs available`, `<name> isn't connected`.
- Commits and PRs never mention Claude, AI, or carry Co-Authored-By lines. Push before asking the user to test.
- Never run a built installer on the dev machine. The Task 5 probe plays audible tones, so tell the user before running it.
- Build: `dotnet build Sendspin.Windows.sln`. App tests: `dotnet test tests/Sendspin.Windows.Services.Tests/Sendspin.Windows.Services.Tests.csproj`. SDK tests: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj` (in the SDK worktree).

## Review Focus

These are the inputs the spec implies but doesn't test directly, most likely first. Each one has a test in the task that owns the code.

1. **Double-clicking Send, or toggling during a switch:** two capture sources must never run at once, and the microphone must not be left open. Pinned by Task 6's concurrent-toggles test and Task 8's repeated-call test.
2. **The SDK disposing the shared device when a dial client is cleaned up or the host stops:** the next connection's `start` must still work. Pinned by Task 6's `DisposeIsNotTerminal` test.
3. **Sleep/resume while MA is streaming generated silence:** the silence source must not burst minutes of zeros on wake. Pinned by Task 3's stall test.
4. **Saved recording device unplugged before launch:** the choice must not be silently replaced by another input. Pinned by Task 8's saved-missing-input test.
5. **Device unplugged mid-send, then plugged back in:** Send must be possible again without restarting. Pinned by Task 6's retry test and Task 8's fault-then-resend test.

---

## Part A — Sendspin SDK 10.1.0 (repo `Sendspin/sendspin-dotnet`)

Work in a new worktree: `git -C C:\CodeProjects\SendspinSDK fetch origin && git -C C:\CodeProjects\SendspinSDK worktree add C:\CodeProjects\SendspinSDK-wt-host-source -b feat/host-source-signal origin/main`. All Part A paths are relative to `C:\CodeProjects\SendspinSDK-wt-host-source`.

### Task 1: Host-level source signal

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinHostService.cs` (fields near line 33; new methods after `SetLastPlayedServerId`; seeding after `TrackOpenClient(client);` in `HandleServerConnectedAsync`)
- Modify: `tests/Sendspin.SDK.Tests/Client/FakeServer.cs`
- Create: `tests/Sendspin.SDK.Tests/Client/SendspinHostServiceSourceTests.cs`

**Interfaces:**
- Produces: `public Task SendspinHostService.SetSourceSignalAsync(bool present)`. Test support: `FakeServer(..., IReadOnlyList<string>? activeRoles = null)` and `Task<bool> FakeServer.WaitForSourceSignalAsync(string signal, TimeSpan timeout)`.

- [ ] **Step 1: Teach `FakeServer` to activate roles and capture the source signal**

In `FakeServer.cs`, add the field and channel next to `_pairAborts`:

```csharp
    private readonly IReadOnlyList<string> _activeRoles;

    // Every source signal the host reports in client/state, in arrival order.
    private readonly Channel<string> _sourceSignals = Channel.CreateUnbounded<string>();
```

Extend the constructor (keep existing parameters and order; add the last one):

```csharp
    /// <param name="activeRoles">server/activate active_roles; empty when omitted.</param>
    internal FakeServer(
        byte[] psk,
        IReadOnlyList<string> activities,
        KeyPair? keys = null,
        string? pskCategory = null,
        IReadOnlyList<string>? activeRoles = null)
    {
        _psk = psk;
        _pskCategory = pskCategory;
        _activities = activities;
        _activeRoles = activeRoles ?? [];
        _keys = keys ?? KeyPair.Generate();
```

(Leave the rest of the constructor body unchanged.)

Add after `WaitForPairAbortAsync`:

```csharp
    /// <summary>
    /// Whether a <c>client/state</c> reporting source <c>signal</c> <paramref name="signal"/>
    /// arrives before the timeout. Earlier reports are consumed and skipped.
    /// </summary>
    internal async Task<bool> WaitForSourceSignalAsync(string signal, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (await _sourceSignals.Reader.WaitToReadAsync(cts.Token))
            {
                while (_sourceSignals.Reader.TryRead(out var received))
                {
                    if (received == signal)
                    {
                        return true;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out: report the absence, matching WaitForPairAbortAsync.
        }

        return false;
    }
```

In `HandleEncryptedFrame`'s `switch`, add a case:

```csharp
            case MessageTypes.ClientState:
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.GetProperty("payload").TryGetProperty("source", out var source)
                    && source.TryGetProperty("signal", out var signal))
                {
                    _sourceSignals.Writer.TryWrite(signal.GetString() ?? string.Empty);
                }

                break;
            }
```

In `BuildActivateJson`, replace `["active_roles"] = Array.Empty<string>(),` with `["active_roles"] = _activeRoles,`.

- [ ] **Step 2: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Client/SendspinHostServiceSourceTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The host's source-role surface: the line-sense signal it reports on every connection, and
/// the source-role state it raises for the app. The host keeps each connection's client
/// private, so without these a server-initiated app could neither drive line-sense nor tell
/// whether its source role is active. Loopback end-to-end, like
/// <see cref="SendspinHostServiceLastPlaybackServerTests"/>.
/// </summary>
[Collection("RealSockets")]
public class SendspinHostServiceSourceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly byte[] TestPsk = Enumerable.Repeat((byte)0x42, 32).ToArray();

    private static readonly string[] SourceRole = ["source@v1"];

    private static async Task<SendspinHostService> StartHostAsync()
    {
        var records = new InMemoryPairingRecordStore();

        // Unbound LongTerm record: the session is paired, so the source role may activate.
        records.Upsert(new PairingRecord(TestPsk, PskCategory.LongTerm));

        var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions
            {
                Identity = SendspinIdentity.Generate(),
                PairingRecordStore = records,
                Capabilities = new ClientCapabilities
                {
                    Roles = { "source@v1" },
                    SourceRoleSupport = new SourceRoleSupport { LineSense = true },
                },
                CaptureDevice = new FakeCaptureDevice(),

                // Converged, so the initial client/state — which carries the signal — is sent.
                ClockSynchronizer = new ConvergedClockSynchronizer(),
            },
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        await host.StartAsync();
        return host;
    }

    private static async Task WaitForServerConnectedAsync(SendspinHostService host, string serverId)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? s, ConnectedServerInfo info)
        {
            if (info.ServerId == serverId)
            {
                tcs.TrySetResult();
            }
        }

        host.ServerConnected += Handler;
        try
        {
            if (host.ConnectedServers.Any(c => c.ServerId == serverId))
            {
                tcs.TrySetResult();
            }

            await tcs.Task.WaitAsync(Timeout);
        }
        finally
        {
            host.ServerConnected -= Handler;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {because}");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task ASignalSetBeforeAConnection_RidesItsInitialClientState()
    {
        await using var host = await StartHostAsync();
        await host.SetSourceSignalAsync(false);

        await using var server = new FakeServer(TestPsk, ["playback"], activeRoles: SourceRole);
        await server.ConnectAsync(host.ListeningPort);

        Assert.True(await server.WaitForSourceSignalAsync("absent", Timeout));
    }

    [Fact]
    public async Task ASignalSetAfterAdmission_ReachesTheAdmittedServer()
    {
        await using var host = await StartHostAsync();
        await using var server = new FakeServer(TestPsk, ["playback"], activeRoles: SourceRole);
        await server.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, server.ServerId);

        await host.SetSourceSignalAsync(true);

        Assert.True(await server.WaitForSourceSignalAsync("present", Timeout));
    }

    [Fact]
    public async Task TheSignal_IsRememberedForTheNextConnection()
    {
        await using var host = await StartHostAsync();
        await using (var first = new FakeServer(TestPsk, ["playback"], activeRoles: SourceRole))
        {
            await first.ConnectAsync(host.ListeningPort);
            await WaitForServerConnectedAsync(host, first.ServerId);
            await host.SetSourceSignalAsync(true);
        }

        await WaitUntilAsync(() => host.ConnectedServers.Count == 0, "the first server to leave");

        await using var second = new FakeServer(TestPsk, ["playback"], activeRoles: SourceRole);
        await second.ConnectAsync(host.ListeningPort);

        Assert.True(await second.WaitForSourceSignalAsync("present", Timeout));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~SendspinHostServiceSourceTests"`
Expected: build FAILS with `'SendspinHostService' does not contain a definition for 'SetSourceSignalAsync'`.

- [ ] **Step 4: Implement the host signal**

In `SendSpinHostService.cs`, add the fields after `_openClients`:

```csharp
    // The line-sense signal the app last reported (SetSourceSignalAsync), applied to every
    // connection the host opens after it. Null until the app reports one. Guarded by
    // _connectionsLock.
    private bool? _sourceSignal;

    // Serializes SetSourceSignalAsync with the seeding of a new connection, so a connection
    // opened during a change ends with the newest signal rather than whichever write landed last.
    private readonly SemaphoreSlim _sourceSignalGate = new(1, 1);
```

Add the methods after `SetLastPlayedServerId`:

```csharp
    /// <summary>
    /// Reports whether this client's source input has signal (<c>line_sense</c>) on every open
    /// connection, and on each connection the host accepts afterwards.
    /// </summary>
    /// <remarks>
    /// The host owns its per-connection clients, so this is the server-initiated counterpart of
    /// <see cref="ISendspinClient.SetSourceSignalAsync"/>. The value is remembered: a connection
    /// opened later carries it in its initial <c>client/state</c>. A server may record a client's
    /// first report without acting on it, so an app that drives autostart with the signal should
    /// report <c>false</c> before its first <c>true</c>. Requires
    /// <see cref="SourceRoleSupport.LineSense"/>; without it each client ignores the call.
    /// </remarks>
    /// <param name="present">True when the input has signal.</param>
    public async Task SetSourceSignalAsync(bool present)
    {
        await _sourceSignalGate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<SendspinClientService> clients;
            lock (_connectionsLock)
            {
                _sourceSignal = present;
                clients = _openClients.ToList();
            }

            foreach (var client in clients)
            {
                await ReportSourceSignalAsync(client, present).ConfigureAwait(false);
            }
        }
        finally
        {
            _sourceSignalGate.Release();
        }
    }

    private async Task SeedSourceSignalAsync(SendspinClientService client)
    {
        await _sourceSignalGate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool? signal;
            lock (_connectionsLock)
            {
                signal = _sourceSignal;
            }

            if (signal is { } present)
            {
                await ReportSourceSignalAsync(client, present).ConfigureAwait(false);
            }
        }
        finally
        {
            _sourceSignalGate.Release();
        }
    }

    private async Task ReportSourceSignalAsync(SendspinClientService client, bool present)
    {
        try
        {
            await client.SetSourceSignalAsync(present).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A connection closing under us. Its server is going away, and the next one is
            // seeded when it opens.
            _logger.LogDebug(ex, "Could not report the source signal to a connection");
        }
    }
```

In `HandleServerConnectedAsync`, directly after `TrackOpenClient(client);`, add:

```csharp
            // Before the connection starts: the client records the signal and its initial
            // client/state reads it live, so the server's first report is the current one.
            await SeedSourceSignalAsync(client);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~SendspinHostServiceSourceTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Run the whole SDK suite**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj`
Expected: PASS, no regressions. The `FakeServer` changes are additive.

- [ ] **Step 7: Commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinHostService.cs tests/Sendspin.SDK.Tests/Client/FakeServer.cs tests/Sendspin.SDK.Tests/Client/SendspinHostServiceSourceTests.cs
git commit -m "Report the source line-sense signal through the host"
```

### Task 2: Host source-role state, version 10.1.0, and the PR

**Files:**
- Create: `src/Sendspin.SDK/Client/SourceRoleState.cs`
- Modify: `src/Sendspin.SDK/Client/SendSpinHostService.cs` (event near `PairingGestureRequested`; subscription after the `RecordLastPlaybackServer` subscription; raise after `ServerConnected?.Invoke`)
- Modify: `src/Sendspin.SDK/Sendspin.SDK.csproj:11`
- Test: `tests/Sendspin.SDK.Tests/Client/SendspinHostServiceSourceTests.cs`

**Interfaces:**
- Consumes: `FakeServer(..., activeRoles)` from Task 1.
- Produces: `public sealed record SourceRoleState(string ServerId, bool IsActive, SendspinTrustLevel TrustLevel)` and `public event EventHandler<SourceRoleState>? SendspinHostService.SourceRoleStateChanged`. Package version `10.1.0`.

- [ ] **Step 1: Write the failing tests**

Append to `SendspinHostServiceSourceTests`:

```csharp
    private static Task<SourceRoleState> NextSourceRoleStateAsync(
        SendspinHostService host, Func<SourceRoleState, bool> match)
    {
        var tcs = new TaskCompletionSource<SourceRoleState>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SourceRoleStateChanged += (s, state) =>
        {
            if (match(state))
            {
                tcs.TrySetResult(state);
            }
        };
        return tcs.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Admission_ReportsAnActiveSourceRole_AndTheTrust()
    {
        await using var host = await StartHostAsync();
        var reported = NextSourceRoleStateAsync(host, _ => true);

        await using var server = new FakeServer(TestPsk, ["playback"], activeRoles: SourceRole);
        await server.ConnectAsync(host.ListeningPort);

        var state = await reported;
        Assert.Equal(server.ServerId, state.ServerId);
        Assert.True(state.IsActive);
        Assert.Equal(SendspinTrustLevel.Paired, state.TrustLevel);
    }

    [Fact]
    public async Task Admission_WithoutTheSourceRole_ReportsItInactive()
    {
        await using var host = await StartHostAsync();
        var reported = NextSourceRoleStateAsync(host, _ => true);

        await using var server = new FakeServer(TestPsk, ["playback"]);
        await server.ConnectAsync(host.ListeningPort);

        Assert.False((await reported).IsActive);
    }

    [Fact]
    public async Task ALaterActivate_ThatGrantsTheSourceRole_IsReported()
    {
        await using var host = await StartHostAsync();
        await using var server = new FakeServer(TestPsk, ["playback"]);
        await server.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, server.ServerId);
        var granted = NextSourceRoleStateAsync(host, s => s.IsActive);

        await server.SendJsonAsync(
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["source@v1"]}}""");

        Assert.Equal(server.ServerId, (await granted).ServerId);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~SendspinHostServiceSourceTests"`
Expected: build FAILS with `'SendspinHostService' does not contain a definition for 'SourceRoleStateChanged'`.

- [ ] **Step 3: Implement**

Create `src/Sendspin.SDK/Client/SourceRoleState.cs`:

```csharp
namespace Sendspin.SDK.Client;

/// <summary>
/// Whether the admitted server has activated this client's <c>source</c> role, and how far the
/// session is trusted. Lets an app say why it cannot send: an unpaired session never gets the
/// role, and a paired one only gets it from a server that accepts inputs.
/// </summary>
/// <param name="ServerId">The admitted server.</param>
/// <param name="IsActive">True when a <c>source@</c> role is in the latest <c>active_roles</c>.</param>
/// <param name="TrustLevel">The session's trust level when the state was read.</param>
public sealed record SourceRoleState(string ServerId, bool IsActive, SendspinTrustLevel TrustLevel);
```

In `SendSpinHostService.cs`, add the event after `PairingGestureRequested`:

```csharp
    /// <summary>
    /// Raised when a connection is admitted, and on each later <c>server/activate</c> it
    /// receives, with whether its <c>source</c> role is active and the session's trust.
    /// Raised on the connection's receive loop or during admission: handlers must not block,
    /// and must marshal to a UI thread themselves.
    /// </summary>
    public event EventHandler<SourceRoleState>? SourceRoleStateChanged;
```

In `HandleServerConnectedAsync`, after the block that subscribes `RecordLastPlaybackServer` (and its `if (client.LastServerActivate is { } admittedActivate)` follow-up), add:

```csharp
                // Each later activate can grant or withdraw the source role — pairing's
                // re-handshake, for one, is followed by a fresh activate at the new trust level.
                client.ServerActivateReceived += (s, _) => RaiseSourceRoleState(serverId, client);
```

Directly after the `ServerConnected?.Invoke(this, new ConnectedServerInfo { ... });` statement, add:

```csharp
                RaiseSourceRoleState(serverId, client);
```

Add the helper next to `RecordLastPlaybackServer`:

```csharp
    private void RaiseSourceRoleState(string serverId, SendspinClientService client)
    {
        bool isActive = client.LastServerHello?.ActiveRoles
            .Any(r => r.StartsWith("source@", StringComparison.Ordinal)) ?? false;
        SourceRoleStateChanged?.Invoke(this, new SourceRoleState(serverId, isActive, client.TrustLevel));
    }
```

In `src/Sendspin.SDK/Sendspin.SDK.csproj`, change `<Version>10.0.0</Version>` to `<Version>10.1.0</Version>`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj`
Expected: PASS, including the 6 `SendspinHostServiceSourceTests`.

- [ ] **Step 5: Commit, push and open the PR**

```bash
git add src/Sendspin.SDK/Client/SourceRoleState.cs src/Sendspin.SDK/Client/SendSpinHostService.cs src/Sendspin.SDK/Sendspin.SDK.csproj tests/Sendspin.SDK.Tests/Client/SendspinHostServiceSourceTests.cs
git commit -m "Raise source-role state from the host; version 10.1.0"
git push -u origin feat/host-source-signal
gh pr create --repo Sendspin/sendspin-dotnet --base main --title "Host-level source signal and source-role state" --body "SendspinHostService keeps each connection's client private, so a server-initiated app could neither report line-sense nor see whether its source role was active.

- SetSourceSignalAsync(bool): applied to every open connection and remembered, so each new connection carries it in its initial client/state.
- SourceRoleStateChanged (SourceRoleState: ServerId, IsActive, TrustLevel): raised on admission and on every later server/activate.

Version 10.1.0. Tests: loopback FakeServer end to end (SendspinHostServiceSourceTests)."
```

- [ ] **Step 6: GATE — stop and hand off to the user**

Report the PR URL. Merging, tagging and publishing `Sendspin.SDK 10.1.0` are the user's to do. Part B Tasks 3–7 don't need 10.1.0 and can proceed now. Task 8 onwards needs either the published package or the local source build described in Task 8 Step 1.

---

## Part B — Sendspin for Windows (worktree `C:\CodeProjects\windowsSpin.worktrees\source-input`, branch `feat/source-input`)

All Part B paths are relative to that worktree. Capture types live in namespace `Sendspin.Windows.Services.Audio.Capture`, and tests in `Sendspin.Windows.Services.Tests.Audio.Capture`. Types are `public` because the Services project has no `InternalsVisibleTo`, matching `ArtworkDownloader`.

### Task 3: Capture core types and the silence source

**Files:**
- Create: `src/Sendspin.Windows.Services/Audio/Capture/CaptureFormat.cs`
- Create: `src/Sendspin.Windows.Services/Audio/Capture/ICaptureSource.cs`
- Create: `src/Sendspin.Windows.Services/Audio/Capture/SilenceSource.cs`
- Create: `tests/Sendspin.Windows.Services.Tests/Audio/Capture/FakeTimer.cs`
- Test: `tests/Sendspin.Windows.Services.Tests/Audio/Capture/SilenceSourceTests.cs`

**Interfaces:**
- Produces:
  - `CaptureFormat` (`SampleRate`, `Channels`, `BitsPerSample`, `BytesPerFrame`, `AudioFormat Create()`, `long FramesToMicroseconds(long)`).
  - `ICaptureSource` (`event EventHandler<CapturedAudio>? FramesCaptured`, `event EventHandler<Exception>? Faulted`, `Task StartAsync(CancellationToken = default)`, `Task StopAsync()`, `IAsyncDisposable`).
  - `SilenceSource(IHighPrecisionTimer timer, bool runPacer = true)` with a public `void Pump()`.
  - Test fake: `FakeTimer : IHighPrecisionTimer` with a settable `Now`.

- [ ] **Step 1: Write the failing tests**

`tests/Sendspin.Windows.Services.Tests/Audio/Capture/FakeTimer.cs`:

```csharp
// <copyright file="FakeTimer.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Synchronization;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class FakeTimer : IHighPrecisionTimer
{
    public long Now { get; set; }

    public long GetCurrentTimeMicroseconds() => Now;

    public long GetElapsedMicroseconds(long fromTimeMicroseconds) => Now - fromTimeMicroseconds;
}
```

`tests/Sendspin.Windows.Services.Tests/Audio/Capture/SilenceSourceTests.cs`:

```csharp
// <copyright file="SilenceSourceTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Audio.Source;
using Sendspin.Windows.Services.Audio.Capture;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

public class SilenceSourceTests
{
    private const long Chunk = 20_000;

    private static (SilenceSource Source, FakeTimer Timer, List<CapturedAudio> Frames) Create(long start = 1_000_000)
    {
        var timer = new FakeTimer { Now = start };
        var source = new SilenceSource(timer, runPacer: false);
        var frames = new List<CapturedAudio>();
        source.FramesCaptured += (_, f) => frames.Add(f);
        return (source, timer, frames);
    }

    [Fact]
    public async Task EmitsContiguousTwentyMillisecondChunksOfZeros()
    {
        var (source, timer, frames) = Create();
        await source.StartAsync();

        timer.Now += (3 * Chunk) + 5_000;
        source.Pump();

        Assert.Equal(new[] { 1_000_000L, 1_020_000, 1_040_000 }, frames.Select(f => f.CaptureTimeMicroseconds));
        Assert.All(frames, f =>
        {
            Assert.Equal(960 * CaptureFormat.BytesPerFrame, f.Pcm.Length);
            Assert.True(f.Pcm.Span.IndexOfAnyExcept((byte)0) < 0);
        });
    }

    [Fact]
    public async Task DoesNotDrift_OverThousandsOfUnevenTicks()
    {
        var (source, timer, frames) = Create(start: 0);
        await source.StartAsync();

        for (int i = 0; i < 20_000; i++)
        {
            timer.Now += 7_000;
            source.Pump();
        }

        // 140 s of clock is exactly 7000 chunks, each starting where the last one ended.
        Assert.Equal(7_000, frames.Count);
        Assert.Equal(6_999 * Chunk, frames[^1].CaptureTimeMicroseconds);
    }

    [Fact]
    public async Task AfterAStall_ResumesFromNow_InsteadOfBursting()
    {
        // Sleep/resume: minutes pass between pumps. Bursting them as silence would flood the stream.
        var (source, timer, frames) = Create();
        await source.StartAsync();

        timer.Now += 300_000_000;
        source.Pump();

        var only = Assert.Single(frames);
        Assert.Equal(timer.Now - Chunk, only.CaptureTimeMicroseconds);
    }

    [Fact]
    public async Task EmitsNothing_BeforeStartOrAfterStop()
    {
        var (source, timer, frames) = Create();

        timer.Now += 5 * Chunk;
        source.Pump();
        Assert.Empty(frames);

        await source.StartAsync();
        await source.StopAsync();
        timer.Now += 5 * Chunk;
        source.Pump();
        Assert.Empty(frames);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Sendspin.Windows.Services.Tests/Sendspin.Windows.Services.Tests.csproj --filter "FullyQualifiedName~Audio.Capture"`
Expected: build FAILS with `The type or namespace name 'Capture' does not exist`.

- [ ] **Step 3: Implement**

`src/Sendspin.Windows.Services/Audio/Capture/CaptureFormat.cs`:

```csharp
using Sendspin.SDK.Models;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>
/// The one format every capture source delivers: 48 kHz, 16-bit, stereo PCM.
/// </summary>
/// <remarks>
/// Fixed, so the device's format never changes: switching input — or between generated silence
/// and real capture — then needs no new <c>client_stream/start</c>. Windows converts each input
/// to it (<c>AUTOCONVERTPCM</c>), and it is what Music Assistant's source bridge converts to
/// anyway.
/// </remarks>
public static class CaptureFormat
{
    /// <summary>Sample rate in Hz.</summary>
    public const int SampleRate = 48000;

    /// <summary>Interleaved channel count.</summary>
    public const int Channels = 2;

    /// <summary>Bits per sample (signed little-endian integer).</summary>
    public const int BitsPerSample = 16;

    /// <summary>Bytes in one frame (one sample per channel).</summary>
    public const int BytesPerFrame = Channels * BitsPerSample / 8;

    /// <summary>Creates the SDK description of this format.</summary>
    /// <returns>A new <see cref="AudioFormat"/> for PCM 48000/2/16.</returns>
    public static AudioFormat Create() => new()
    {
        Codec = AudioCodecs.Pcm,
        SampleRate = SampleRate,
        Channels = Channels,
        BitDepth = BitsPerSample,
    };

    /// <summary>Duration of <paramref name="frames"/> frames, rounded down to whole microseconds.</summary>
    /// <param name="frames">Frame count.</param>
    /// <returns>Microseconds.</returns>
    public static long FramesToMicroseconds(long frames) => frames * 1_000_000 / SampleRate;
}
```

`src/Sendspin.Windows.Services/Audio/Capture/ICaptureSource.cs`:

```csharp
using Sendspin.SDK.Audio.Source;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>
/// One thing <c>WindowsAudioCaptureDevice</c> can capture from: generated silence, desktop
/// loopback, or a recording endpoint.
/// </summary>
public interface ICaptureSource : IAsyncDisposable
{
    /// <summary>
    /// Raised for each captured buffer, in <see cref="CaptureFormat"/>, with the capture time of
    /// its first frame in the <c>HighPrecisionTimer</c> microsecond domain.
    /// </summary>
    event EventHandler<CapturedAudio>? FramesCaptured;

    /// <summary>Raised once if capture fails after it started; no frames follow.</summary>
    event EventHandler<Exception>? Faulted;

    /// <summary>Starts capturing. Throws if the source cannot be opened.</summary>
    /// <param name="cancellationToken">Unused by current sources; part of the start contract.</param>
    /// <returns>A task that completes once capture is running.</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops capturing. Idempotent.</summary>
    /// <returns>A task that completes once no more frames will be raised.</returns>
    Task StopAsync();
}
```

`src/Sendspin.Windows.Services/Audio/Capture/SilenceSource.cs`:

```csharp
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Synchronization;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>
/// Generates silence in <see cref="CaptureFormat"/>, paced by the clock, without opening any
/// device.
/// </summary>
/// <remarks>
/// This is what Music Assistant receives while Send is off. Accepting its <c>start</c> and
/// streaming silence is what a real line-in with nothing plugged in does; refusing would look
/// broken to MA, which drops a source that sends nothing within five seconds. Because nothing
/// is opened, the Windows microphone indicator stays dark until the user presses Send.
/// </remarks>
public sealed class SilenceSource : ICaptureSource
{
    /// <summary>Length of each emitted chunk.</summary>
    public const int ChunkMilliseconds = 20;

    private const int FramesPerChunk = CaptureFormat.SampleRate * ChunkMilliseconds / 1000;
    private const int PaceMilliseconds = 10;

    // Further behind than this (sleep/resume, a stalled thread) and the timeline restarts from
    // now: minutes of silence in one burst would only flood the stream with stale audio.
    private const long MaxCatchUpMicroseconds = 200_000;

    private static readonly long ChunkMicroseconds = CaptureFormat.FramesToMicroseconds(FramesPerChunk);

    private readonly IHighPrecisionTimer _timer;
    private readonly bool _runPacer;
    private readonly byte[] _silence = new byte[FramesPerChunk * CaptureFormat.BytesPerFrame];
    private readonly object _lock = new();
    private bool _started;
    private long _nextChunkStart;
    private CancellationTokenSource? _pacerCts;
    private Task? _pacer;

    /// <summary>Initializes a new instance of the <see cref="SilenceSource"/> class.</summary>
    /// <param name="timer">The clock capture times are read from.</param>
    /// <param name="runPacer">False in tests, which call <see cref="Pump"/> themselves.</param>
    public SilenceSource(IHighPrecisionTimer timer, bool runPacer = true)
    {
        _timer = timer;
        _runPacer = runPacer;
    }

    /// <inheritdoc/>
    public event EventHandler<CapturedAudio>? FramesCaptured;

    /// <inheritdoc/>
    /// <remarks>Generated silence cannot fail, so this is never raised.</remarks>
    public event EventHandler<Exception>? Faulted
    {
        add { }
        remove { }
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }

            _started = true;
            _nextChunkStart = _timer.GetCurrentTimeMicroseconds();
        }

        if (_runPacer)
        {
            _pacerCts = new CancellationTokenSource();
            var token = _pacerCts.Token;
            _pacer = Task.Run(() => PaceAsync(token));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        lock (_lock)
        {
            _started = false;
        }

        if (_pacerCts is { } cts)
        {
            cts.Cancel();
            if (_pacer is { } pacer)
            {
                await pacer.ConfigureAwait(false);
            }

            cts.Dispose();
            _pacerCts = null;
            _pacer = null;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// Emits every chunk that is due by now. Called by the pacer; public so tests can drive the
    /// source without waiting on real time.
    /// </summary>
    public void Pump()
    {
        lock (_lock)
        {
            if (!_started)
            {
                return;
            }

            long now = _timer.GetCurrentTimeMicroseconds();
            if (now - _nextChunkStart > MaxCatchUpMicroseconds)
            {
                _nextChunkStart = now - ChunkMicroseconds;
            }

            while (_nextChunkStart + ChunkMicroseconds <= now)
            {
                // The buffer is shared: it is zeros and is never written after construction.
                FramesCaptured?.Invoke(this, new CapturedAudio(_silence, _nextChunkStart));
                _nextChunkStart += ChunkMicroseconds;
            }
        }
    }

    private async Task PaceAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                Pump();
                await Task.Delay(PaceMilliseconds, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Sendspin.Windows.Services.Tests/Sendspin.Windows.Services.Tests.csproj --filter "FullyQualifiedName~Audio.Capture"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.Windows.Services/Audio/Capture tests/Sendspin.Windows.Services.Tests/Audio/Capture
git commit -m "Add the capture format, capture source contract and generated silence"
```

### Task 4: WASAPI capture loop, capture time and error messages

**Files:**
- Create: `src/Sendspin.Windows.Services/Audio/Capture/ICapturePacketReader.cs`
- Create: `src/Sendspin.Windows.Services/Audio/Capture/WasapiCaptureSource.cs`
- Create: `src/Sendspin.Windows.Services/Audio/Capture/CaptureTime.cs`
- Create: `src/Sendspin.Windows.Services/Audio/Capture/CaptureErrorMessages.cs`
- Test: `tests/Sendspin.Windows.Services.Tests/Audio/Capture/WasapiCaptureSourceTests.cs`
- Test: `tests/Sendspin.Windows.Services.Tests/Audio/Capture/CaptureTimeTests.cs`
- Test: `tests/Sendspin.Windows.Services.Tests/Audio/Capture/CaptureErrorMessagesTests.cs`

**Interfaces:**
- Consumes: `ICaptureSource`, `CaptureFormat` (Task 3).
- Produces:
  - `ICapturePacketReader` (`int GetNextPacketSize()`, `IntPtr GetBuffer(out int frames, out bool silent, out long qpcPosition)`, `void ReleaseBuffer(int frames)`).
  - `abstract class WasapiCaptureSource : ICaptureSource`:
    - ctor `(IHighPrecisionTimer timer, ILogger logger)`;
    - `protected abstract ICapturePacketReader Open(EventWaitHandle packetReady)`;
    - `protected abstract void Close()`;
    - `protected const long BufferDuration = 2_000_000`.
  - `CaptureTime.ToTimerMicroseconds(long qpcPosition100ns, long nowTimerMicroseconds, long nowStopwatchTicks, long stopwatchFrequency)`.
  - `CaptureErrorMessages.Describe(string inputName, Exception exception)` and the HRESULT constants `AccessDenied`, `DeviceInUse`, `DeviceInvalidated`.

- [ ] **Step 1: Write the failing tests**

`tests/Sendspin.Windows.Services.Tests/Audio/Capture/CaptureTimeTests.cs`:

```csharp
// <copyright file="CaptureTimeTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.Windows.Services.Audio.Capture;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

public class CaptureTimeTests
{
    [Theory]
    // Stopwatch at 10 MHz: 5 s now, packet captured at 4.99 s → 10 ms old.
    [InlineData(10_000_000L, 50_000_000L, 49_900_000L, 10_000L)]
    // Stopwatch at 3 MHz: 5 s now, packet at 4.9 s → 100 ms old.
    [InlineData(3_000_000L, 15_000_000L, 49_000_000L, 100_000L)]
    public void APacketsTimestamp_IsNowMinusItsAge(long frequency, long nowTicks, long qpc100ns, long expectedAgeUs)
    {
        const long nowUs = 1_700_000_000_000_000;

        Assert.Equal(nowUs - expectedAgeUs, CaptureTime.ToTimerMicroseconds(qpc100ns, nowUs, nowTicks, frequency));
    }
}
```

`tests/Sendspin.Windows.Services.Tests/Audio/Capture/CaptureErrorMessagesTests.cs`:

```csharp
// <copyright file="CaptureErrorMessagesTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Runtime.InteropServices;
using Sendspin.Windows.Services.Audio.Capture;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

public class CaptureErrorMessagesTests
{
    [Fact]
    public void PrivacyDenial_PointsAtTheWindowsSetting()
    {
        var message = CaptureErrorMessages.Describe("Line In", new UnauthorizedAccessException());

        Assert.Contains("Privacy & security › Microphone", message);
    }

    [Theory]
    [InlineData(CaptureErrorMessages.DeviceInUse, "Line In is in use by another app.")]
    [InlineData(CaptureErrorMessages.DeviceInvalidated, "Line In was disconnected.")]
    [InlineData(unchecked((int)0x80004005), "Line In stopped unexpectedly.")]
    public void DeviceErrors_NameTheInput(int hresult, string expected)
    {
        Assert.Equal(expected, CaptureErrorMessages.Describe("Line In", new COMException("x", hresult)));
    }
}
```

`tests/Sendspin.Windows.Services.Tests/Audio/Capture/WasapiCaptureSourceTests.cs`:

```csharp
// <copyright file="WasapiCaptureSourceTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio.Source;
using Sendspin.Windows.Services.Audio.Capture;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

public class WasapiCaptureSourceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Packets_AreCopiedOut_InOrder()
    {
        await using var source = new TestSource();
        var frames = Collect(source, count: 2);
        await source.StartAsync();

        source.Reader.Enqueue([1, 1, 1, 1, 2, 2, 2, 2], silent: false, qpc: 0);
        source.Reader.Enqueue([3, 3, 3, 3], silent: false, qpc: 0);
        source.Signal();

        var got = await frames.Task.WaitAsync(Timeout);
        Assert.Equal(new byte[] { 1, 1, 1, 1, 2, 2, 2, 2 }, got[0].Pcm.ToArray());
        Assert.Equal(new byte[] { 3, 3, 3, 3 }, got[1].Pcm.ToArray());
    }

    [Fact]
    public async Task ASilentPacket_IsDeliveredAsZeros()
    {
        await using var source = new TestSource();
        var frames = Collect(source, count: 1);
        await source.StartAsync();

        source.Reader.Enqueue([9, 9, 9, 9], silent: true, qpc: 0);
        source.Signal();

        Assert.Equal(new byte[4], (await frames.Task.WaitAsync(Timeout))[0].Pcm.ToArray());
    }

    [Fact]
    public async Task WithoutAQpcPosition_TheTimestampIsNowMinusTheDuration()
    {
        await using var source = new TestSource(new FakeTimer { Now = 10_000_000 });
        var frames = Collect(source, count: 1);
        await source.StartAsync();

        source.Reader.Enqueue(new byte[960 * CaptureFormat.BytesPerFrame], silent: false, qpc: 0);
        source.Signal();

        Assert.Equal(10_000_000 - 20_000, (await frames.Task.WaitAsync(Timeout))[0].CaptureTimeMicroseconds);
    }

    [Fact]
    public async Task AReadFailure_RaisesFaultedOnce()
    {
        await using var source = new TestSource();
        var faults = new ConcurrentQueue<Exception>();
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Faulted += (_, ex) =>
        {
            faults.Enqueue(ex);
            faulted.TrySetResult();
        };
        await source.StartAsync();

        source.Reader.FailWith = new COMException("gone", CaptureErrorMessages.DeviceInvalidated);
        source.Reader.Enqueue([1, 1, 1, 1], silent: false, qpc: 0);
        source.Signal();

        await faulted.Task.WaitAsync(Timeout);
        await Task.Delay(200);
        Assert.Single(faults);
    }

    [Fact]
    public async Task AnOpenFailure_FailsTheStart_AndCloses()
    {
        await using var source = new TestSource { OpenFailure = new UnauthorizedAccessException() };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => source.StartAsync());
        Assert.True(source.Closed);
    }

    [Fact]
    public async Task Stop_Closes_AndNoFramesFollow()
    {
        await using var source = new TestSource();
        int frames = 0;
        source.FramesCaptured += (_, _) => Interlocked.Increment(ref frames);
        await source.StartAsync();

        await source.StopAsync();
        source.Reader.Enqueue([1, 1, 1, 1], silent: false, qpc: 0);

        Assert.True(source.Closed);
        Assert.Equal(0, frames);
    }

    private static TaskCompletionSource<List<CapturedAudio>> Collect(ICaptureSource source, int count)
    {
        var frames = new List<CapturedAudio>();
        var done = new TaskCompletionSource<List<CapturedAudio>>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.FramesCaptured += (_, f) =>
        {
            frames.Add(f);
            if (frames.Count == count)
            {
                done.TrySetResult(frames);
            }
        };
        return done;
    }

    private sealed class TestSource(FakeTimer? timer = null)
        : WasapiCaptureSource(timer ?? new FakeTimer(), NullLogger.Instance)
    {
        private EventWaitHandle? _packetReady;

        public FakeReader Reader { get; } = new();

        public Exception? OpenFailure { get; init; }

        public bool Closed { get; private set; }

        public void Signal() => _packetReady!.Set();

        protected override ICapturePacketReader Open(EventWaitHandle packetReady)
        {
            _packetReady = packetReady;
            if (OpenFailure is not null)
            {
                throw OpenFailure;
            }

            return Reader;
        }

        protected override void Close() => Closed = true;
    }

    private sealed class FakeReader : ICapturePacketReader
    {
        private readonly ConcurrentQueue<(byte[] Data, bool Silent, long Qpc)> _packets = new();
        private IntPtr _locked;

        public Exception? FailWith { get; set; }

        public void Enqueue(byte[] data, bool silent, long qpc) => _packets.Enqueue((data, silent, qpc));

        public int GetNextPacketSize() =>
            _packets.TryPeek(out var p) ? p.Data.Length / CaptureFormat.BytesPerFrame : 0;

        public IntPtr GetBuffer(out int frames, out bool silent, out long qpcPosition)
        {
            if (FailWith is not null)
            {
                throw FailWith;
            }

            _packets.TryDequeue(out var p);
            frames = p.Data.Length / CaptureFormat.BytesPerFrame;
            silent = p.Silent;
            qpcPosition = p.Qpc;
            _locked = Marshal.AllocHGlobal(p.Data.Length);
            Marshal.Copy(p.Data, 0, _locked, p.Data.Length);
            return _locked;
        }

        public void ReleaseBuffer(int frames) => Marshal.FreeHGlobal(_locked);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Sendspin.Windows.Services.Tests/Sendspin.Windows.Services.Tests.csproj --filter "FullyQualifiedName~Audio.Capture"`
Expected: build FAILS with `The type or namespace name 'WasapiCaptureSource' could not be found`.

- [ ] **Step 3: Implement**

`src/Sendspin.Windows.Services/Audio/Capture/ICapturePacketReader.cs`:

```csharp
namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>
/// The three <c>IAudioCaptureClient</c> calls the capture loop makes, so NAudio's wrapper and
/// the process-loopback interop can share one loop.
/// </summary>
public interface ICapturePacketReader
{
    /// <summary>Frames in the next packet, or 0 when none is waiting.</summary>
    /// <returns>The frame count.</returns>
    int GetNextPacketSize();

    /// <summary>Locks the next packet. Must be followed by <see cref="ReleaseBuffer"/>.</summary>
    /// <param name="frames">Frames in the packet.</param>
    /// <param name="silent">True when Windows marked the packet silent; its contents are then undefined.</param>
    /// <param name="qpcPosition">Capture time of the first frame in 100-ns QPC units, or 0 when unknown.</param>
    /// <returns>The packet's data.</returns>
    IntPtr GetBuffer(out int frames, out bool silent, out long qpcPosition);

    /// <summary>Releases the packet returned by <see cref="GetBuffer"/>.</summary>
    /// <param name="frames">The frame count <see cref="GetBuffer"/> reported.</param>
    void ReleaseBuffer(int frames);
}
```

`src/Sendspin.Windows.Services/Audio/Capture/CaptureTime.cs`:

```csharp
namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>Converts WASAPI packet times into the clock the SDK synchronizes.</summary>
public static class CaptureTime
{
    /// <summary>
    /// Converts a packet's QPC position into the <c>HighPrecisionTimer</c> microsecond domain.
    /// </summary>
    /// <remarks>
    /// <c>HighPrecisionTimer</c> is itself built on the Stopwatch (QPC), so the packet's age —
    /// now minus its QPC position — is exact in both domains. Converting by age avoids depending
    /// on the timer's private epoch.
    /// </remarks>
    /// <param name="qpcPosition100ns">The packet's QPC position from <c>GetBuffer</c>, in 100-ns units.</param>
    /// <param name="nowTimerMicroseconds">The timer's current reading.</param>
    /// <param name="nowStopwatchTicks"><c>Stopwatch.GetTimestamp()</c>, read alongside it.</param>
    /// <param name="stopwatchFrequency"><c>Stopwatch.Frequency</c>.</param>
    /// <returns>The packet's capture time in timer microseconds.</returns>
    public static long ToTimerMicroseconds(
        long qpcPosition100ns, long nowTimerMicroseconds, long nowStopwatchTicks, long stopwatchFrequency)
    {
        long now100ns = (long)((Int128)nowStopwatchTicks * 10_000_000 / stopwatchFrequency);
        long age100ns = now100ns - qpcPosition100ns;
        return nowTimerMicroseconds - (age100ns / 10);
    }
}
```

`src/Sendspin.Windows.Services/Audio/Capture/CaptureErrorMessages.cs`:

```csharp
namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>Turns a capture failure into a sentence the user can act on.</summary>
public static class CaptureErrorMessages
{
    /// <summary><c>E_ACCESSDENIED</c>: Windows privacy settings block recording.</summary>
    public const int AccessDenied = unchecked((int)0x80070005);

    /// <summary><c>AUDCLNT_E_DEVICE_IN_USE</c>: another app holds the endpoint exclusively.</summary>
    public const int DeviceInUse = unchecked((int)0x8889000A);

    /// <summary><c>AUDCLNT_E_DEVICE_INVALIDATED</c>: the endpoint was removed.</summary>
    public const int DeviceInvalidated = unchecked((int)0x88890004);

    /// <summary>Describes why capture from <paramref name="inputName"/> stopped.</summary>
    /// <param name="inputName">The input's display name.</param>
    /// <param name="exception">The failure.</param>
    /// <returns>A one-sentence message.</returns>
    public static string Describe(string inputName, Exception exception) => exception.HResult switch
    {
        AccessDenied =>
            "Windows privacy settings are blocking recording. Turn on \"Let desktop apps access your microphone\" in Settings › Privacy & security › Microphone.",
        DeviceInUse => $"{inputName} is in use by another app.",
        DeviceInvalidated => $"{inputName} was disconnected.",
        _ => $"{inputName} stopped unexpectedly.",
    };
}
```

(`UnauthorizedAccessException` carries HResult `0x80070005`, so it maps to `AccessDenied`. `Marshal.ThrowExceptionForHR(E_ACCESSDENIED)` produces exactly that.)

`src/Sendspin.Windows.Services/Audio/Capture/WasapiCaptureSource.cs`:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Synchronization;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>
/// Event-driven WASAPI capture on a dedicated thread, shared by the endpoint and process-loopback
/// sources.
/// </summary>
/// <remarks>
/// The client is opened, read and closed on the capture thread, an MTA thread, so no COM call
/// crosses apartments. That matters most for process loopback, whose client comes from an
/// asynchronous activation completed on a worker thread.
/// </remarks>
public abstract class WasapiCaptureSource : ICaptureSource
{
    /// <summary>Shared-mode buffer: 200 ms, in 100-ns units.</summary>
    protected const long BufferDuration = 2_000_000;

    private readonly IHighPrecisionTimer _timer;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private Thread? _thread;
    private AutoResetEvent? _packetReady;
    private volatile bool _stopping;

    /// <summary>Initializes a new instance of the <see cref="WasapiCaptureSource"/> class.</summary>
    /// <param name="timer">The clock capture times are expressed in.</param>
    /// <param name="logger">Logger for capture failures.</param>
    protected WasapiCaptureSource(IHighPrecisionTimer timer, ILogger logger)
    {
        _timer = timer;
        _logger = logger;
    }

    /// <inheritdoc/>
    public event EventHandler<CapturedAudio>? FramesCaptured;

    /// <inheritdoc/>
    public event EventHandler<Exception>? Faulted;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_thread is not null)
            {
                return;
            }

            _stopping = false;
            _packetReady = new AutoResetEvent(false);
            _thread = new Thread(() => Run(opened))
            {
                IsBackground = true,
                Name = "Sendspin capture",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        try
        {
            await opened.Task.ConfigureAwait(false);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public Task StopAsync()
    {
        Thread? thread;
        AutoResetEvent? packetReady;
        lock (_lock)
        {
            thread = _thread;
            packetReady = _packetReady;
            _thread = null;
            _packetReady = null;
        }

        if (thread is null)
        {
            return Task.CompletedTask;
        }

        _stopping = true;
        packetReady!.Set();

        // Joined off the caller's thread: a Faulted handler may be the one stopping us, and it
        // runs on the capture thread itself.
        return Task.Run(() =>
        {
            thread.Join();
            packetReady.Dispose();
        });
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// Opens and starts the client, arranging for <paramref name="packetReady"/> to be signalled
    /// as packets arrive. Runs on the capture thread.
    /// </summary>
    /// <param name="packetReady">The event WASAPI signals.</param>
    /// <returns>The reader for the started client.</returns>
    protected abstract ICapturePacketReader Open(EventWaitHandle packetReady);

    /// <summary>Stops and releases what <see cref="Open"/> acquired. Runs on the capture thread.</summary>
    protected abstract void Close();

    private void Run(TaskCompletionSource opened)
    {
        var packetReady = _packetReady!;
        ICapturePacketReader reader;
        try
        {
            reader = Open(packetReady);
        }
        catch (Exception ex)
        {
            SafeClose();
            opened.TrySetException(ex);
            return;
        }

        opened.TrySetResult();
        try
        {
            while (!_stopping)
            {
                packetReady.WaitOne(100);
                Drain(reader);
            }
        }
        catch (Exception ex) when (!_stopping)
        {
            _logger.LogWarning(ex, "Audio capture failed");
            Faulted?.Invoke(this, ex);
        }
        finally
        {
            SafeClose();
        }
    }

    private void Drain(ICapturePacketReader reader)
    {
        while (!_stopping && reader.GetNextPacketSize() > 0)
        {
            IntPtr data = reader.GetBuffer(out int frames, out bool silent, out long qpcPosition);
            try
            {
                var pcm = new byte[frames * CaptureFormat.BytesPerFrame];
                if (!silent)
                {
                    Marshal.Copy(data, pcm, 0, pcm.Length);
                }

                FramesCaptured?.Invoke(this, new CapturedAudio(pcm, CaptureTimeOf(qpcPosition, frames)));
            }
            finally
            {
                reader.ReleaseBuffer(frames);
            }
        }
    }

    private long CaptureTimeOf(long qpcPosition, int frames)
    {
        long now = _timer.GetCurrentTimeMicroseconds();
        return qpcPosition > 0
            ? CaptureTime.ToTimerMicroseconds(qpcPosition, now, Stopwatch.GetTimestamp(), Stopwatch.Frequency)
            : now - CaptureFormat.FramesToMicroseconds(frames);
    }

    private void SafeClose()
    {
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Closing audio capture failed");
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Sendspin.Windows.Services.Tests/Sendspin.Windows.Services.Tests.csproj --filter "FullyQualifiedName~Audio.Capture"`
Expected: PASS (all capture tests so far).

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.Windows.Services/Audio/Capture tests/Sendspin.Windows.Services.Tests/Audio/Capture
git commit -m "Add the WASAPI capture loop, capture-time conversion and capture error messages"
```

### Task 5: Process-loopback interop, the two capture sources, the input catalog, and the probe

**Files:**
- Create: `src/Sendspin.Windows.Services/Audio/Capture/Interop/AudioClientInterop.cs`
- Create: `src/Sendspin.Windows.Services/Audio/Capture/Interop/ProcessLoopbackActivator.cs`
- Create: `src/Sendspin.Windows.Services/Audio/Capture/ProcessLoopbackCaptureSource.cs`
- Create: `src/Sendspin.Windows.Services/Audio/Capture/EndpointCaptureSource.cs`
- Create: `src/Sendspin.Windows.Services/Audio/Capture/CaptureInputCatalog.cs`
- Scratch (throwaway, not committed): `C:\Users\chris\AppData\Local\Temp\claude\c--CodeProjects-windowsSpin\f3333ade-9a3b-489c-b02b-6b790d230928\scratchpad\capture-probe\`

**Interfaces:**
- Consumes: `WasapiCaptureSource`, `ICapturePacketReader`, `CaptureFormat` (Task 4).
- Produces:
  - `ProcessLoopbackCaptureSource(IHighPrecisionTimer timer, ILogger logger)`.
  - `EndpointCaptureSource(string deviceId, IHighPrecisionTimer timer, ILogger logger)`.
  - `public sealed record CaptureInput(string Id, string Name, bool IsAvailable = true)` with `string DisplayName`.
  - `public interface ICaptureInputCatalog { IReadOnlyList<CaptureInput> GetInputs(); CaptureInput? Describe(string inputId); }`.
  - `public sealed class CaptureInputCatalog : ICaptureInputCatalog` with `public const string DesktopId = "desktop"`.

This task's code talks to real audio hardware, which CI runners don't have. It is verified by the probe in Step 3, not by unit tests.

- [ ] **Step 1: Write the interop**

`src/Sendspin.Windows.Services/Audio/Capture/Interop/AudioClientInterop.cs`:

```csharp
using System.Runtime.InteropServices;

namespace Sendspin.Windows.Services.Audio.Capture.Interop;

// Minimal WASAPI declarations for process-loopback capture. NAudio 2.2.1 covers endpoint capture,
// but its AudioClient wrapper cannot be built around a client obtained from
// ActivateAudioInterfaceAsync (the constructor is internal), so this path declares its own.
// Vtable order matters: every IAudioClient method up to GetService is declared, in order.

/// <summary>Constants and the activation entry point.</summary>
internal static class AudioClientInterop
{
    public const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";

    public const int ShareModeShared = 0;
    public const uint StreamFlagsLoopback = 0x00020000;
    public const uint StreamFlagsEventCallback = 0x00040000;
    public const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    public const uint StreamFlagsAutoConvertPcm = 0x80000000;
    public const uint BufferFlagsSilent = 0x2;

    public const int ActivationTypeProcessLoopback = 1;
    public const int ProcessLoopbackModeExcludeTargetProcessTree = 1;
    public const ushort VariantTypeBlob = 65;

    public static readonly Guid IAudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IAudioCaptureClientId = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
}

/// <summary><c>AUDIOCLIENT_ACTIVATION_PARAMS</c> for process loopback.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public int ActivationType;
    public uint TargetProcessId;
    public int ProcessLoopbackMode;
}

/// <summary>A <c>PROPVARIANT</c> holding a <c>VT_BLOB</c>; sequential layout aligns the pointer on x86 and x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariantBlob
{
    public ushort VariantType;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public uint BlobSize;
    public IntPtr BlobData;
}

/// <summary><c>WAVEFORMATEX</c>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort Size;
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, [In] ref WaveFormatEx format, IntPtr audioSessionGuid);

    void GetBufferSize(out uint bufferFrames);

    void GetStreamLatency(out long latency);

    void GetCurrentPadding(out uint padding);

    [PreserveSig]
    int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

    void GetMixFormat(out IntPtr format);

    void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

    void Start();

    void Stop();

    void Reset();

    void SetEventHandle(IntPtr eventHandle);

    [return: MarshalAs(UnmanagedType.IUnknown)]
    object GetService([In, MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId);
}

[ComImport]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    // PreserveSig throughout: AUDCLNT_S_BUFFER_EMPTY is a success code worth seeing.
    [PreserveSig]
    int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint numFramesRead);

    [PreserveSig]
    int GetNextPacketSize(out uint numFramesInNextPacket);
}

[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

/// <summary>Marker: the completion handler may be called on any thread.</summary>
[ComImport]
[Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}
```

`src/Sendspin.Windows.Services/Audio/Capture/Interop/ProcessLoopbackActivator.cs`:

```csharp
using System.Runtime.InteropServices;

namespace Sendspin.Windows.Services.Audio.Capture.Interop;

/// <summary>
/// Activates an <see cref="IAudioClient"/> that captures everything Windows plays except one
/// process tree.
/// </summary>
internal static class ProcessLoopbackActivator
{
    /// <summary>Process loopback exists from Windows 10 version 2004 (build 19041).</summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    /// <summary>Activates a loopback client excluding <paramref name="processId"/> and its children.</summary>
    /// <param name="processId">The process whose output to leave out — ours.</param>
    /// <returns>An uninitialized client.</returns>
    public static async Task<IAudioClient> ActivateExcludingProcessTreeAsync(int processId)
    {
        int paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(paramsSize);
        IntPtr variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(
                new AudioClientActivationParams
                {
                    ActivationType = AudioClientInterop.ActivationTypeProcessLoopback,
                    TargetProcessId = (uint)processId,
                    ProcessLoopbackMode = AudioClientInterop.ProcessLoopbackModeExcludeTargetProcessTree,
                },
                paramsPtr,
                fDeleteOld: false);
            Marshal.StructureToPtr(
                new PropVariantBlob
                {
                    VariantType = AudioClientInterop.VariantTypeBlob,
                    BlobSize = (uint)paramsSize,
                    BlobData = paramsPtr,
                },
                variantPtr,
                fDeleteOld: false);

            var handler = new CompletionHandler();
            AudioClientInterop.ActivateAudioInterfaceAsync(
                AudioClientInterop.VirtualAudioDeviceProcessLoopback,
                AudioClientInterop.IAudioClientId,
                variantPtr,
                handler,
                out _);

            // Awaited inside the try so the parameter memory outlives the activation.
            return await handler.Task.ConfigureAwait(false);
        }
        finally
        {
            Marshal.FreeHGlobal(variantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Task => _completion.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out int hr, out object activated);
                if (hr < 0)
                {
                    _completion.TrySetException(Marshal.GetExceptionForHR(hr)!);
                }
                else
                {
                    _completion.TrySetResult((IAudioClient)activated);
                }
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }
    }
}
```

- [ ] **Step 2: Write the sources and the catalog**

`src/Sendspin.Windows.Services/Audio/Capture/ProcessLoopbackCaptureSource.cs`:

```csharp
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Synchronization;
using Sendspin.Windows.Services.Audio.Capture.Interop;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>
/// Desktop audio: everything Windows plays except Sendspin's own process tree.
/// </summary>
/// <remarks>
/// Excluding our own tree is what keeps this feedback-free when Music Assistant plays the input
/// back on this PC — the default for a device that is both player and source. Requires Windows 10
/// 2004 (<see cref="CaptureInputCatalog"/> hides the input before that).
/// </remarks>
public sealed class ProcessLoopbackCaptureSource : WasapiCaptureSource
{
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;

    /// <summary>Initializes a new instance of the <see cref="ProcessLoopbackCaptureSource"/> class.</summary>
    /// <param name="timer">The clock capture times are expressed in.</param>
    /// <param name="logger">Logger for capture failures.</param>
    public ProcessLoopbackCaptureSource(IHighPrecisionTimer timer, ILogger logger)
        : base(timer, logger)
    {
    }

    /// <inheritdoc/>
    protected override ICapturePacketReader Open(EventWaitHandle packetReady)
    {
        _client = ProcessLoopbackActivator.ActivateExcludingProcessTreeAsync(Environment.ProcessId)
            .GetAwaiter().GetResult();

        var format = new WaveFormatEx
        {
            FormatTag = 1, // WAVE_FORMAT_PCM
            Channels = CaptureFormat.Channels,
            SamplesPerSec = CaptureFormat.SampleRate,
            AvgBytesPerSec = CaptureFormat.SampleRate * CaptureFormat.BytesPerFrame,
            BlockAlign = CaptureFormat.BytesPerFrame,
            BitsPerSample = CaptureFormat.BitsPerSample,
            Size = 0,
        };
        _client.Initialize(
            AudioClientInterop.ShareModeShared,
            AudioClientInterop.StreamFlagsLoopback
                | AudioClientInterop.StreamFlagsEventCallback
                | AudioClientInterop.StreamFlagsAutoConvertPcm
                | AudioClientInterop.StreamFlagsSrcDefaultQuality,
            BufferDuration,
            0,
            ref format,
            IntPtr.Zero);
        _client.SetEventHandle(packetReady.SafeWaitHandle.DangerousGetHandle());
        _capture = (IAudioCaptureClient)_client.GetService(AudioClientInterop.IAudioCaptureClientId);
        _client.Start();
        return new InteropPacketReader(_capture);
    }

    /// <inheritdoc/>
    protected override void Close()
    {
        if (_client is not null)
        {
            try
            {
                _client.Stop();
            }
            catch (COMException)
            {
                // Not started, or the session is already gone.
            }
        }

        if (_capture is not null)
        {
            Marshal.ReleaseComObject(_capture);
            _capture = null;
        }

        if (_client is not null)
        {
            Marshal.ReleaseComObject(_client);
            _client = null;
        }
    }

    private sealed class InteropPacketReader(IAudioCaptureClient capture) : ICapturePacketReader
    {
        public int GetNextPacketSize()
        {
            Check(capture.GetNextPacketSize(out uint frames));
            return (int)frames;
        }

        public IntPtr GetBuffer(out int frames, out bool silent, out long qpcPosition)
        {
            Check(capture.GetBuffer(out IntPtr data, out uint count, out uint flags, out _, out ulong qpc));
            frames = (int)count;
            silent = (flags & AudioClientInterop.BufferFlagsSilent) != 0;
            qpcPosition = (long)qpc;
            return data;
        }

        public void ReleaseBuffer(int frames) => Check(capture.ReleaseBuffer((uint)frames));

        private static void Check(int hr)
        {
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }
    }
}
```

`src/Sendspin.Windows.Services/Audio/Capture/EndpointCaptureSource.cs`:

```csharp
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sendspin.SDK.Synchronization;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>
/// One recording endpoint — line in, microphone, USB interface — in WASAPI shared mode, converted
/// by Windows to <see cref="CaptureFormat"/>.
/// </summary>
public sealed class EndpointCaptureSource : WasapiCaptureSource
{
    private readonly string _deviceId;
    private MMDevice? _device;
    private AudioClient? _client;

    /// <summary>Initializes a new instance of the <see cref="EndpointCaptureSource"/> class.</summary>
    /// <param name="deviceId">The WASAPI endpoint ID.</param>
    /// <param name="timer">The clock capture times are expressed in.</param>
    /// <param name="logger">Logger for capture failures.</param>
    public EndpointCaptureSource(string deviceId, IHighPrecisionTimer timer, ILogger logger)
        : base(timer, logger)
    {
        _deviceId = deviceId;
    }

    /// <inheritdoc/>
    protected override ICapturePacketReader Open(EventWaitHandle packetReady)
    {
        using (var enumerator = new MMDeviceEnumerator())
        {
            _device = enumerator.GetDevice(_deviceId);
        }

        _client = _device.AudioClient;
        _client.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            BufferDuration,
            0,
            new WaveFormat(CaptureFormat.SampleRate, CaptureFormat.BitsPerSample, CaptureFormat.Channels),
            Guid.Empty);
        _client.SetEventHandle(packetReady.SafeWaitHandle.DangerousGetHandle());
        var reader = new NAudioPacketReader(_client.AudioCaptureClient);
        _client.Start();
        return reader;
    }

    /// <inheritdoc/>
    protected override void Close()
    {
        try
        {
            _client?.Stop();
        }
        finally
        {
            _client?.Dispose();
            _client = null;
            _device?.Dispose();
            _device = null;
        }
    }

    private sealed class NAudioPacketReader(AudioCaptureClient capture) : ICapturePacketReader
    {
        public int GetNextPacketSize() => capture.GetNextPacketSize();

        public IntPtr GetBuffer(out int frames, out bool silent, out long qpcPosition)
        {
            IntPtr data = capture.GetBuffer(out frames, out AudioClientBufferFlags flags, out _, out qpcPosition);
            silent = (flags & AudioClientBufferFlags.Silent) != 0;
            return data;
        }

        public void ReleaseBuffer(int frames) => capture.ReleaseBuffer(frames);
    }
}
```

`src/Sendspin.Windows.Services/Audio/Capture/CaptureInputCatalog.cs`:

```csharp
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Sendspin.Windows.Services.Audio.Capture.Interop;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>Something Send can capture from.</summary>
/// <param name="Id"><see cref="CaptureInputCatalog.DesktopId"/>, or a WASAPI endpoint ID.</param>
/// <param name="Name">Short name, used in the badge and tooltips.</param>
/// <param name="IsAvailable">False for a saved endpoint that is not plugged in.</param>
public sealed record CaptureInput(string Id, string Name, bool IsAvailable = true)
{
    /// <summary>Gets the label shown in the Settings picker.</summary>
    public string DisplayName =>
        !IsAvailable ? $"{Name} — not connected"
        : Id == CaptureInputCatalog.DesktopId ? $"{Name} (all apps except Sendspin)"
        : Name;
}

/// <summary>Lists what Send can capture from.</summary>
public interface ICaptureInputCatalog
{
    /// <summary>Lists the inputs available now: desktop audio first when supported, then active recording endpoints.</summary>
    /// <returns>The inputs.</returns>
    IReadOnlyList<CaptureInput> GetInputs();

    /// <summary>Describes an input by ID even when it is not available, so a saved choice can be shown by name.</summary>
    /// <param name="inputId">The input ID.</param>
    /// <returns>The input, or null if Windows does not know it at all.</returns>
    CaptureInput? Describe(string inputId);
}

/// <summary>Windows implementation of <see cref="ICaptureInputCatalog"/>.</summary>
public sealed class CaptureInputCatalog : ICaptureInputCatalog
{
    /// <summary>The ID of the desktop-audio input.</summary>
    public const string DesktopId = "desktop";

    private static readonly CaptureInput Desktop = new(DesktopId, "Desktop audio");

    /// <inheritdoc/>
    public IReadOnlyList<CaptureInput> GetInputs()
    {
        var inputs = new List<CaptureInput>();
        if (ProcessLoopbackActivator.IsSupported)
        {
            inputs.Add(Desktop);
        }

        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                inputs.Add(new CaptureInput(device.ID, device.FriendlyName));
            }
        }

        return inputs;
    }

    /// <inheritdoc/>
    public CaptureInput? Describe(string inputId)
    {
        if (inputId == DesktopId)
        {
            return ProcessLoopbackActivator.IsSupported ? Desktop : null;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(inputId);
            return new CaptureInput(device.ID, device.FriendlyName, device.State == DeviceState.Active);
        }
        catch (COMException)
        {
            return null;
        }
    }
}
```

Run: `dotnet build Sendspin.Windows.sln`
Expected: build succeeds. If `MMDevice` doesn't implement `IDisposable` in NAudio 2.2.1, remove the two `using (device)`/`using var device` and the `_device?.Dispose()` call. That is the only acceptable deviation.

- [ ] **Step 3: Run the throwaway probe (manual, audible: tell the user first)**

Create `C:\Users\chris\AppData\Local\Temp\claude\c--CodeProjects-windowsSpin\f3333ade-9a3b-489c-b02b-6b790d230928\scratchpad\capture-probe\capture-probe.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="C:\CodeProjects\windowsSpin.worktrees\source-input\src\Sendspin.Windows.Services\Sendspin.Windows.Services.csproj" />
  </ItemGroup>
</Project>
```

`Program.cs` in the same folder:

```csharp
// Throwaway probe: checks the design's load-bearing claims on real hardware. Not committed.
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Sendspin.SDK.Synchronization;
using Sendspin.Windows.Services.Audio.Capture;

static async Task<(double Rms, long Bytes)> CaptureAsync(ICaptureSource source, TimeSpan duration)
{
    long sumSquares = 0, samples = 0, bytes = 0;
    source.FramesCaptured += (_, f) =>
    {
        var span = f.Pcm.Span;
        bytes += span.Length;
        for (int i = 0; i + 1 < span.Length; i += 2)
        {
            short s = (short)(span[i] | (span[i + 1] << 8));
            sumSquares += (long)s * s;
            samples++;
        }
    };
    await source.StartAsync();
    await Task.Delay(duration);
    await source.StopAsync();
    return (samples == 0 ? 0 : Math.Sqrt((double)sumSquares / samples), bytes);
}

var timer = HighPrecisionTimer.Shared;

// 1. Our own tone must NOT appear in desktop capture.
using (var tone = new WasapiOut())
{
    tone.Init(new SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.2 });
    tone.Play();
    var (rms, bytes) = await CaptureAsync(new ProcessLoopbackCaptureSource(timer, NullLogger.Instance), TimeSpan.FromSeconds(3));
    Console.WriteLine($"[1] own tone, desktop capture: RMS {rms:F0}, {bytes} bytes (expect RMS < 50, bytes ~576000)");
}

// 2. Another process's sound MUST appear.
using (var other = Process.Start(new ProcessStartInfo("powershell",
    "-NoProfile -Command \"$p = New-Object Media.SoundPlayer 'C:\\Windows\\Media\\Alarm01.wav'; $p.PlayLooping(); Start-Sleep 6\"")
    { CreateNoWindow = true })!)
{
    await Task.Delay(1000);
    var (rms, _) = await CaptureAsync(new ProcessLoopbackCaptureSource(timer, NullLogger.Instance), TimeSpan.FromSeconds(3));
    Console.WriteLine($"[2] other process, desktop capture: RMS {rms:F0} (expect > 200)");
    other.Kill();
}

// 3. The default recording device delivers 48k/16/2 through AUTOCONVERTPCM.
using var enumerator = new MMDeviceEnumerator();
var mic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
var (_, micBytes) = await CaptureAsync(new EndpointCaptureSource(mic.ID, timer, NullLogger.Instance), TimeSpan.FromSeconds(2));
Console.WriteLine($"[3] {mic.FriendlyName}: {micBytes} bytes in 2 s (expect ~384000 = 48000 x 4 x 2)");
```

Tell the user: "Running the capture probe: it plays a 440 Hz tone and the Windows alarm sound for about 10 seconds, and opens your default microphone for 2 seconds." Then run `dotnet run --project <that folder>`.

Expected:
- [1] RMS < 50: our own output is excluded.
- [2] RMS > 200: other apps are captured.
- [3] about 384,000 bytes: the format is 48k/16/2.

If [1] fails, **stop**. The feedback-free claim is false, so go back to the spec with the result. If [3] is far off, AUTOCONVERTPCM didn't convert; stop and report. Don't commit the probe.

- [ ] **Step 4: Commit**

```bash
git add src/Sendspin.Windows.Services/Audio/Capture
git commit -m "Capture desktop audio without Sendspin, and recording endpoints, at 48k/16/2"
```

### Task 6: The capture device the SDK sees

**Files:**
- Create: `src/Sendspin.Windows.Services/Audio/Capture/CaptureFaultedEventArgs.cs`
- Create: `src/Sendspin.Windows.Services/Audio/Capture/WindowsAudioCaptureDevice.cs`
- Create: `tests/Sendspin.Windows.Services.Tests/Audio/Capture/FakeCaptureSource.cs`
- Test: `tests/Sendspin.Windows.Services.Tests/Audio/Capture/WindowsAudioCaptureDeviceTests.cs`

**Interfaces:**
- Consumes: `ICaptureSource`, `CaptureFormat`, `CaptureErrorMessages` (Tasks 3–4).
- Produces:
  - `CaptureFaultedEventArgs(string inputId, Exception exception)`.
  - `WindowsAudioCaptureDevice : IAudioCaptureDevice`:
    - ctor `(Func<ICaptureSource> createSilence, Func<string, ICaptureSource> createInput, ILogger<WindowsAudioCaptureDevice> logger)`;
    - `Task SetInputAsync(string? inputId)`;
    - `Task SetSendEnabledAsync(bool enabled)`;
    - `event EventHandler<CaptureFaultedEventArgs>? Faulted`;
    - `DisposeAsync` is non-terminal.
  - Test fake: `FakeCaptureSource`.

- [ ] **Step 1: Write the failing tests**

`tests/Sendspin.Windows.Services.Tests/Audio/Capture/FakeCaptureSource.cs`:

```csharp
// <copyright file="FakeCaptureSource.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Runtime.InteropServices;
using Sendspin.SDK.Audio.Source;
using Sendspin.Windows.Services.Audio.Capture;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

/// <summary>A capture source the test drives: emits frames and failures on demand.</summary>
internal sealed class FakeCaptureSource(string name) : ICaptureSource
{
    public event EventHandler<CapturedAudio>? FramesCaptured;

    public event EventHandler<Exception>? Faulted;

    public string Name { get; } = name;

    public bool ThrowOnStart { get; init; }

    public bool Running { get; private set; }

    public bool Disposed { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (ThrowOnStart)
        {
            throw new COMException("in use", CaptureErrorMessages.DeviceInUse);
        }

        Running = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Running = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public void Emit(long captureTime, int frames, byte fill = 1) =>
        FramesCaptured?.Invoke(
            this,
            new CapturedAudio(Enumerable.Repeat(fill, frames * CaptureFormat.BytesPerFrame).ToArray(), captureTime));

    public void Fail(Exception exception) => Faulted?.Invoke(this, exception);
}
```

`tests/Sendspin.Windows.Services.Tests/Audio/Capture/WindowsAudioCaptureDeviceTests.cs`:

```csharp
// <copyright file="WindowsAudioCaptureDeviceTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio.Source;
using Sendspin.Windows.Services.Audio.Capture;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

public class WindowsAudioCaptureDeviceTests
{
    [Fact]
    public async Task WhileSendIsOff_AStartRunsSilence_AndNeverConstructsTheInput()
    {
        // The privacy guarantee: an MA start with Send off opens no device at all.
        var h = new Harness();
        await h.Device.SetInputAsync("mic");

        await h.Device.StartAsync();

        Assert.True(Assert.Single(h.Silences).Running);
        Assert.Empty(h.Inputs);
    }

    [Fact]
    public async Task SendOn_SwitchesToTheInput_AndOff_BackToSilence()
    {
        var h = new Harness();
        await h.Device.SetInputAsync("mic");
        await h.Device.StartAsync();

        await h.Device.SetSendEnabledAsync(true);
        Assert.True(Assert.Single(h.Inputs).Running);
        Assert.True(h.Silences[0].Disposed);
        Assert.Equal(1, h.Running);

        await h.Device.SetSendEnabledAsync(false);
        Assert.True(h.Inputs[0].Disposed);
        Assert.True(h.Silences[^1].Running);
        Assert.Equal(1, h.Running);
    }

    [Fact]
    public async Task BeforeTheSdkStarts_SendAndInputChanges_OpenNothing()
    {
        var h = new Harness();

        await h.Device.SetInputAsync("mic");
        await h.Device.SetSendEnabledAsync(true);

        Assert.Empty(h.Silences);
        Assert.Empty(h.Inputs);
    }

    [Fact]
    public async Task ChangingInputWhileSending_SwitchesTheSource()
    {
        var h = new Harness();
        await h.Device.SetInputAsync("a");
        await h.Device.StartAsync();
        await h.Device.SetSendEnabledAsync(true);

        await h.Device.SetInputAsync("b");

        Assert.Equal(new[] { "a", "b" }, h.Inputs.Select(i => i.Name));
        Assert.True(h.Inputs[0].Disposed);
        Assert.True(h.Inputs[1].Running);
    }

    [Fact]
    public async Task AnInputThatFailsToStart_FallsBackToSilence_AndCanBeRetried()
    {
        var h = new Harness();
        h.FailingInputs.Add("mic");
        await h.Device.SetInputAsync("mic");
        await h.Device.StartAsync();

        await h.Device.SetSendEnabledAsync(true);

        var fault = Assert.Single(h.Faults);
        Assert.Equal("mic", fault.InputId);
        Assert.True(h.Silences[^1].Running);

        // Plugged back in: the next Send opens a fresh source.
        h.FailingInputs.Clear();
        await h.Device.SetSendEnabledAsync(true);
        Assert.True(h.Inputs[^1].Running);
        Assert.Equal(2, h.Inputs.Count);
    }

    [Fact]
    public async Task ARunningInputThatFails_FallsBackToSilence()
    {
        var h = new Harness();
        await h.Device.SetInputAsync("mic");
        await h.Device.StartAsync();
        await h.Device.SetSendEnabledAsync(true);

        h.Inputs[0].Fail(new COMException("gone", CaptureErrorMessages.DeviceInvalidated));

        await WaitUntilAsync(() => h.Faults.Count == 1);
        await WaitUntilAsync(() => h.Silences[^1].Running && h.Running == 1);
        Assert.Equal("mic", h.Faults.Single().InputId);
    }

    [Fact]
    public async Task FramesFromAStoppedSource_AreDropped()
    {
        var h = new Harness();
        await h.Device.SetInputAsync("mic");
        await h.Device.StartAsync();
        var silence = h.Silences[0];
        await h.Device.SetSendEnabledAsync(true);

        silence.Emit(captureTime: 0, frames: 960);

        Assert.Empty(h.Delivered);
    }

    [Fact]
    public async Task AnOverlapAcrossASwitch_IsTrimmed()
    {
        var h = new Harness();
        await h.Device.SetInputAsync("mic");
        await h.Device.StartAsync();
        h.Silences[0].Emit(captureTime: 0, frames: 960, fill: 0);
        await h.Device.SetSendEnabledAsync(true);

        // The input's first packet starts 10 ms before the silence ended.
        h.Inputs[0].Emit(captureTime: 10_000, frames: 960, fill: 7);

        Assert.Equal(2, h.Delivered.Count);
        Assert.Equal(20_000, h.Delivered[1].CaptureTimeMicroseconds);
        Assert.Equal(480 * CaptureFormat.BytesPerFrame, h.Delivered[1].Pcm.Length);
    }

    [Fact]
    public async Task AStop_ResetsTheTimeline()
    {
        // MA may stop and later restart the stream; the next stream's frames are not "overlaps".
        var h = new Harness();
        await h.Device.StartAsync();
        h.Silences[0].Emit(captureTime: 1_000_000, frames: 960);
        await h.Device.StopAsync();
        await h.Device.StartAsync();

        h.Silences[^1].Emit(captureTime: 0, frames: 960);

        Assert.Equal(0, h.Delivered[^1].CaptureTimeMicroseconds);
        Assert.Equal(960 * CaptureFormat.BytesPerFrame, h.Delivered[^1].Pcm.Length);
    }

    [Fact]
    public async Task TheFormat_IsFixedPcm48k16Stereo()
    {
        var h = new Harness();
        var before = h.Device.Format;
        await h.Device.StartAsync();
        await h.Device.SetSendEnabledAsync(true);

        Assert.Same(before, h.Device.Format);
        Assert.Equal(("pcm", 48000, 2, (int?)16), (before.Codec, before.SampleRate, before.Channels, before.BitDepth));
    }

    [Fact]
    public async Task DisposeIsNotTerminal()
    {
        // The SDK disposes the shared device whenever it disposes an owning client — the dial
        // cleanup, the host's stop. The next connection's start must still work.
        var h = new Harness();
        await h.Device.StartAsync();

        await h.Device.DisposeAsync();
        Assert.Equal(0, h.Running);

        await h.Device.StartAsync();
        Assert.True(h.Silences[^1].Running);
    }

    [Fact]
    public async Task ConcurrentToggles_LeaveExactlyOneSourceRunning()
    {
        var h = new Harness();
        await h.Device.SetInputAsync("mic");
        await h.Device.StartAsync();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => h.Device.SetSendEnabledAsync(i % 2 == 0)));

        Assert.Equal(1, h.Running);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(10);
        }
    }

    private sealed class Harness
    {
        public Harness()
        {
            Device = new WindowsAudioCaptureDevice(
                () => Add(Silences, new FakeCaptureSource("silence")),
                id => Add(Inputs, new FakeCaptureSource(id) { ThrowOnStart = FailingInputs.Contains(id) }),
                NullLogger<WindowsAudioCaptureDevice>.Instance);
            Device.AudioCaptured += (_, a) => Delivered.Add(a);
            Device.Faulted += (_, f) => Faults.Enqueue(f);
        }

        public WindowsAudioCaptureDevice Device { get; }

        public List<FakeCaptureSource> Silences { get; } = new();

        public List<FakeCaptureSource> Inputs { get; } = new();

        public HashSet<string> FailingInputs { get; } = new();

        public List<CapturedAudio> Delivered { get; } = new();

        public ConcurrentQueue<CaptureFaultedEventArgs> Faults { get; } = new();

        public int Running => Silences.Concat(Inputs).Count(s => s.Running);

        private static FakeCaptureSource Add(List<FakeCaptureSource> list, FakeCaptureSource source)
        {
            lock (list)
            {
                list.Add(source);
            }

            return source;
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Sendspin.Windows.Services.Tests/Sendspin.Windows.Services.Tests.csproj --filter "FullyQualifiedName~WindowsAudioCaptureDeviceTests"`
Expected: build FAILS with `The type or namespace name 'WindowsAudioCaptureDevice' could not be found`.

- [ ] **Step 3: Implement**

`src/Sendspin.Windows.Services/Audio/Capture/CaptureFaultedEventArgs.cs`:

```csharp
namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>Capture from an input failed; the device has already fallen back to silence.</summary>
/// <param name="inputId">The input that failed.</param>
/// <param name="exception">Why.</param>
public sealed class CaptureFaultedEventArgs(string inputId, Exception exception) : EventArgs
{
    /// <summary>Gets the input that failed.</summary>
    public string InputId { get; } = inputId;

    /// <summary>Gets the failure.</summary>
    public Exception Exception { get; } = exception;
}
```

`src/Sendspin.Windows.Services/Audio/Capture/WindowsAudioCaptureDevice.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Models;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>
/// The single capture device handed to the SDK for the <c>source@v1</c> role.
/// </summary>
/// <remarks>
/// <para>
/// Two independent states meet here. Music Assistant owns whether a stream is open: it calls
/// <see cref="StartAsync"/> and <see cref="StopAsync"/> through the SDK. The user owns what fills
/// it: <see cref="SetSendEnabledAsync"/>. While Send is off a started device runs generated
/// silence and opens nothing; with Send on it runs the selected input. The stream stays open
/// across every switch and <see cref="Format"/> never changes, so the SDK sees one continuous
/// stream.
/// </para>
/// <para>
/// Emitted capture times are monotonic and never overlap: a frame that starts before the
/// previous one ended is trimmed. Gaps are allowed; MA's source bridge absorbs them.
/// </para>
/// <para>
/// <see cref="DisposeAsync"/> only stops. The SDK disposes this device whenever it disposes a
/// client that owns its pipelines — every dial cleanup and host stop — and the next connection
/// must still be able to start it.
/// </para>
/// </remarks>
public sealed class WindowsAudioCaptureDevice : IAudioCaptureDevice
{
    private const long NoFrames = long.MinValue;

    private readonly Func<ICaptureSource> _createSilence;
    private readonly Func<string, ICaptureSource> _createInput;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly object _frameLock = new();

    // Guarded by _switchGate.
    private string? _inputId;
    private bool _sendEnabled;
    private bool _started;
    private string? _activeInputId;

    // Written under _switchGate, read on capture threads.
    private ICaptureSource? _active;

    // Guarded by _frameLock.
    private long _lastFrameEnd = NoFrames;

    /// <summary>Initializes a new instance of the <see cref="WindowsAudioCaptureDevice"/> class.</summary>
    /// <param name="createSilence">Creates the generated-silence source.</param>
    /// <param name="createInput">Creates the source for an input ID.</param>
    /// <param name="logger">Logger.</param>
    public WindowsAudioCaptureDevice(
        Func<ICaptureSource> createSilence,
        Func<string, ICaptureSource> createInput,
        ILogger<WindowsAudioCaptureDevice> logger)
    {
        _createSilence = createSilence;
        _createInput = createInput;
        _logger = logger;
    }

    /// <inheritdoc/>
    public AudioFormat Format { get; } = CaptureFormat.Create();

    /// <inheritdoc/>
    public event EventHandler<CapturedAudio>? AudioCaptured;

    /// <summary>
    /// Raised when the selected input fails to start or stops working. Send is already off inside
    /// the device and silence is flowing; the handler only has to tell the user.
    /// </summary>
    public event EventHandler<CaptureFaultedEventArgs>? Faulted;

    /// <summary>Selects what Send captures. Applies immediately if sending.</summary>
    /// <param name="inputId">An input ID, or null for none.</param>
    /// <returns>A task that completes once the change is applied.</returns>
    public Task SetInputAsync(string? inputId) => SwitchAsync(() => _inputId = inputId);

    /// <summary>Opens or closes the Send gate.</summary>
    /// <param name="enabled">True to capture the selected input; false for silence.</param>
    /// <returns>A task that completes once the change is applied.</returns>
    public Task SetSendEnabledAsync(bool enabled) => SwitchAsync(() => _sendEnabled = enabled);

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken = default) => SwitchAsync(() => _started = true);

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken = default) => SwitchAsync(() => _started = false);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task SwitchAsync(Action change)
    {
        CaptureFaultedEventArgs? fault;
        await _switchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            change();
            fault = await ApplyAsync().ConfigureAwait(false);
        }
        finally
        {
            _switchGate.Release();
        }

        // Outside the gate, so a handler may call back in.
        if (fault is not null)
        {
            Faulted?.Invoke(this, fault);
        }
    }

    private async Task<CaptureFaultedEventArgs?> ApplyAsync()
    {
        bool wantsInput = _started && _sendEnabled && _inputId is not null;
        string? wantedInput = wantsInput ? _inputId : null;
        bool running = _active is not null;

        if (!_started && !running)
        {
            return null;
        }

        if (_started && running && _activeInputId == wantedInput)
        {
            return null;
        }

        await StopActiveAsync().ConfigureAwait(false);
        if (!_started)
        {
            lock (_frameLock)
            {
                _lastFrameEnd = NoFrames;
            }

            return null;
        }

        if (!wantsInput)
        {
            await StartSourceAsync(_createSilence(), null).ConfigureAwait(false);
            return null;
        }

        try
        {
            await StartSourceAsync(_createInput(wantedInput!), wantedInput).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start capture from {InputId}; sending silence", wantedInput);
            _sendEnabled = false;
            await StartSourceAsync(_createSilence(), null).ConfigureAwait(false);
            return new CaptureFaultedEventArgs(wantedInput!, ex);
        }
    }

    private async Task StartSourceAsync(ICaptureSource source, string? inputId)
    {
        source.FramesCaptured += OnFramesCaptured;
        source.Faulted += OnSourceFaulted;
        Volatile.Write(ref _active, source);
        _activeInputId = inputId;
        try
        {
            await source.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            await StopActiveAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StopActiveAsync()
    {
        var source = _active;
        if (source is null)
        {
            return;
        }

        Volatile.Write(ref _active, null);
        _activeInputId = null;
        source.FramesCaptured -= OnFramesCaptured;
        source.Faulted -= OnSourceFaulted;
        try
        {
            await source.StopAsync().ConfigureAwait(false);
            await source.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopping a capture source failed");
        }
    }

    private void OnFramesCaptured(object? sender, CapturedAudio audio)
    {
        lock (_frameLock)
        {
            // A source being stopped may still deliver a packet it had in hand.
            if (!ReferenceEquals(sender, Volatile.Read(ref _active)))
            {
                return;
            }

            long start = audio.CaptureTimeMicroseconds;
            var pcm = audio.Pcm;
            int frames = pcm.Length / CaptureFormat.BytesPerFrame;

            if (_lastFrameEnd != NoFrames && start < _lastFrameEnd)
            {
                long overlap = _lastFrameEnd - start;
                long drop = ((overlap * CaptureFormat.SampleRate) + 999_999) / 1_000_000;
                if (drop >= frames)
                {
                    return;
                }

                pcm = pcm.Slice((int)drop * CaptureFormat.BytesPerFrame);
                start += CaptureFormat.FramesToMicroseconds(drop);
                frames -= (int)drop;
            }

            _lastFrameEnd = start + CaptureFormat.FramesToMicroseconds(frames);

            // Inside the lock so frames reach the SDK in timeline order across a switch.
            AudioCaptured?.Invoke(this, new CapturedAudio(pcm, start));
        }
    }

    private void OnSourceFaulted(object? sender, Exception exception) =>
        _ = HandleSourceFaultAsync(sender as ICaptureSource, exception);

    private async Task HandleSourceFaultAsync(ICaptureSource? source, Exception exception)
    {
        try
        {
            string? failedInput = null;
            await SwitchAsync(() =>
            {
                if (source is not null && ReferenceEquals(source, _active) && _activeInputId is not null)
                {
                    failedInput = _activeInputId;
                    _sendEnabled = false;
                }
            }).ConfigureAwait(false);

            if (failedInput is not null)
            {
                _logger.LogWarning(exception, "Capture from {InputId} failed; sending silence", failedInput);
                Faulted?.Invoke(this, new CaptureFaultedEventArgs(failedInput, exception));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovering from a capture failure failed");
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Sendspin.Windows.Services.Tests/Sendspin.Windows.Services.Tests.csproj --filter "FullyQualifiedName~Audio.Capture"`
Expected: PASS (12 device tests plus the earlier capture tests).

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.Windows.Services/Audio/Capture tests/Sendspin.Windows.Services.Tests/Audio/Capture
git commit -m "Add the capture device: silence until Send, then the selected input, one stream throughout"
```

### Task 7: Contract test against the real SDK pipeline

**Files:**
- Test: `tests/Sendspin.Windows.Services.Tests/Audio/Capture/SourcePipelineContractTests.cs`

**Interfaces:**
- Consumes: `WindowsAudioCaptureDevice`, `FakeCaptureSource` (Task 6); SDK `SourceStreamPipeline(IAudioCaptureDevice, IClockSynchronizer, Func<IMessage, Task>, Func<byte[], Task>, ILogger, Func<bool>, ...)`.
- Produces: nothing new. This pins the integration to the SDK version we build against.

- [ ] **Step 1: Write the test**

```csharp
// <copyright file="SourcePipelineContractTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;
using Sendspin.Windows.Services.Audio.Capture;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

/// <summary>
/// The device driven by the SDK's own source pipeline: what Music Assistant actually receives.
/// Re-check when bumping the SDK.
/// </summary>
public class SourcePipelineContractTests
{
    private const int ChunkHeaderBytes = 9;

    [Fact]
    public async Task SilenceUntilSend_ThenTheInput_InOneStream()
    {
        var input = new FakeCaptureSource("mic");
        var silences = new List<FakeCaptureSource>();
        var device = new WindowsAudioCaptureDevice(
            () =>
            {
                var s = new FakeCaptureSource("silence");
                silences.Add(s);
                return s;
            },
            _ => input,
            NullLogger<WindowsAudioCaptureDevice>.Instance);
        await device.SetInputAsync("mic");

        var messages = new ConcurrentQueue<IMessage>();
        var chunks = new ConcurrentQueue<byte[]>();
        await using var pipeline = new SourceStreamPipeline(
            device,
            new KalmanClockSynchronizer(),
            m =>
            {
                messages.Enqueue(m);
                return Task.CompletedTask;
            },
            b =>
            {
                chunks.Enqueue(b);
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            canStream: () => true);

        await pipeline.HandleCommandAsync("start");

        var start = Assert.Single(messages.OfType<ClientStreamStartMessage>()).Payload.Source;
        Assert.Equal(("pcm", 48000, 2, 16), (start.Codec, start.SampleRate, start.Channels, start.BitDepth));

        silences[0].Emit(captureTime: 1_000_000, frames: 960, fill: 0);
        await WaitUntilAsync(() => !chunks.IsEmpty);
        Assert.All(chunks, c => Assert.True(c.AsSpan(ChunkHeaderBytes).IndexOfAnyExcept((byte)0) < 0));

        await device.SetSendEnabledAsync(true);
        input.Emit(captureTime: 1_020_000, frames: 960, fill: 7);
        await WaitUntilAsync(() => chunks.Any(c => c.Length > ChunkHeaderBytes && c[ChunkHeaderBytes] == 7));

        await device.SetSendEnabledAsync(false);
        Assert.Single(messages.OfType<ClientStreamStartMessage>());
        Assert.DoesNotContain(messages, m => m is ClientStreamEndMessage);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(10);
        }
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test tests/Sendspin.Windows.Services.Tests/Sendspin.Windows.Services.Tests.csproj --filter "FullyQualifiedName~SourcePipelineContractTests"`
Expected: PASS. Tasks 3–6 already did the implementation work; this test pins the SDK behavior. If it fails, the device and the SDK disagree. Read `SourceStreamPipeline` in the SDK source and fix the device; don't change the test's expectations.

- [ ] **Step 3: Commit**

```bash
git add tests/Sendspin.Windows.Services.Tests/Audio/Capture/SourcePipelineContractTests.cs
git commit -m "Pin the capture device's contract with the SDK source pipeline"
```

### Task 8: SDK 10.1.0 and the Send controller

**Files:**
- Modify: `src/Sendspin.Windows/Sendspin.Windows.csproj:68` and `src/Sendspin.Windows.Services/Sendspin.Windows.Services.csproj:35` (`Version="10.0.0"` → `Version="10.1.0"`)
- Create: `src/Sendspin.Windows.Services/Audio/Capture/SourceSendController.cs`
- Test: `tests/Sendspin.Windows.Services.Tests/Audio/Capture/SourceSendControllerTests.cs`

**Interfaces:**
- Consumes: `WindowsAudioCaptureDevice`, `CaptureFaultedEventArgs` (Task 6), `ICaptureInputCatalog`, `CaptureInput` (Task 5), `CaptureErrorMessages` (Task 4), SDK `SourceRoleState` and `SendspinTrustLevel` (Task 2).
- Produces:
  - `enum SendAvailability { Available, Disconnected, NotPaired, ServerDoesNotAcceptInputs, NoInputs, InputMissing }`.
  - `SourceSendController`:
    - ctor `(WindowsAudioCaptureDevice, ICaptureInputCatalog, ILogger<SourceSendController>)`;
    - properties `Func<bool, Task> ReportSignalAsync`, `bool IsSending`, `string? InputId`, `IReadOnlyList<CaptureInput> Inputs`, `CaptureInput? SelectedInput`, `SendAvailability Availability`, `string? UnavailableReason`;
    - methods `Task InitializeAsync(string? savedInputId)`, `void RefreshInputs()`, `CaptureInput? DescribeInput(string)`, `Task SelectInputAsync(string)`, `void UpdateServer(SourceRoleState?)`, `Task SetSendingAsync(bool)`, `Task StopAsync()`;
    - events `EventHandler? StateChanged`, `EventHandler<string>? SendingStopped`.

- [ ] **Step 1: Move to SDK 10.1.0**

Check that it's published: `curl -s https://api.nuget.org/v3-flatcontainer/sendspin.sdk/index.json`. Expected: `"10.1.0"` is in the list.

Change both `PackageReference Include="Sendspin.SDK" Version="10.0.0"` entries to `10.1.0`.

If 10.1.0 isn't published yet, change the versions anyway. Then build and test **locally only** against the SDK branch by adding `-p:UseSdkSource=true -p:SdkSourcePath=C:\CodeProjects\SendspinSDK-wt-host-source` to every `dotnet build`/`dotnet test` in Tasks 8–11. Don't push until 10.1.0 is published, because CI restores from NuGet.

Run: `dotnet build Sendspin.Windows.sln` (plus the flags if needed)
Expected: succeeds.

Then check the SDK-bump rule in CLAUDE.md, the progress carry-forward contract:

Run: `git -C C:\CodeProjects\SendspinSDK-wt-host-source diff origin/main -- src/Sendspin.SDK/Client/SendSpinClient.cs`
Expected: empty output, so `HandleServerState` is unchanged and the reference-identity contract still holds.

- [ ] **Step 2: Write the failing tests**

```csharp
// <copyright file="SourceSendControllerTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.Windows.Services.Audio.Capture;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

public class SourceSendControllerTests
{
    private static readonly CaptureInput Desktop = new(CaptureInputCatalog.DesktopId, "Desktop audio");
    private static readonly CaptureInput LineIn = new("line-in", "Line In");
    private static readonly SourceRoleState Active = new("ma", IsActive: true, SendspinTrustLevel.Paired);

    [Fact]
    public async Task Initialize_ReportsAbsent_AndPicksTheFirstInput()
    {
        // MA records a client's first signal silently: absent must come before the first present.
        var h = new Harness();

        await h.Controller.InitializeAsync(savedInputId: null);

        Assert.Equal(new[] { false }, h.Signals);
        Assert.Equal(CaptureInputCatalog.DesktopId, h.Controller.InputId);
    }

    [Fact]
    public async Task ASavedInputThatIsUnplugged_IsKept_AndExplained()
    {
        var h = new Harness();
        h.Catalog.Known["usb-1"] = new CaptureInput("usb-1", "USB Interface", IsAvailable: false);

        await h.Controller.InitializeAsync("usb-1");
        h.Controller.UpdateServer(Active);

        Assert.Equal("usb-1", h.Controller.InputId);
        Assert.Equal(SendAvailability.InputMissing, h.Controller.Availability);
        Assert.Equal("USB Interface isn't connected", h.Controller.UnavailableReason);
    }

    [Fact]
    public async Task SendOn_ReportsPresent_AndOff_ReportsAbsent()
    {
        var h = new Harness();
        await h.Controller.InitializeAsync(null);
        h.Controller.UpdateServer(Active);

        await h.Controller.SetSendingAsync(true);
        Assert.True(h.Controller.IsSending);

        await h.Controller.SetSendingAsync(false);
        Assert.False(h.Controller.IsSending);
        Assert.Equal(new[] { false, true, false }, h.Signals);
    }

    [Fact]
    public async Task RepeatedCalls_AreIdempotent()
    {
        var h = new Harness();
        await h.Controller.InitializeAsync(null);
        h.Controller.UpdateServer(Active);

        await h.Controller.SetSendingAsync(true);
        await h.Controller.SetSendingAsync(true);

        Assert.Equal(new[] { false, true }, h.Signals);
    }

    public static TheoryData<SourceRoleState?, bool, SendAvailability, string> Unavailable => new()
    {
        { null, true, SendAvailability.Disconnected, "Not connected to Music Assistant" },
        { new SourceRoleState("ma", false, SendspinTrustLevel.Unpaired), true, SendAvailability.NotPaired, "Pair this player with Music Assistant to send audio" },
        { new SourceRoleState("ma", false, SendspinTrustLevel.Paired), true, SendAvailability.ServerDoesNotAcceptInputs, "This server doesn't accept audio inputs" },
        { new SourceRoleState("ma", true, SendspinTrustLevel.Paired), false, SendAvailability.NoInputs, "No audio inputs available" },
    };

    [Theory]
    [MemberData(nameof(Unavailable))]
    public async Task SendIsRefused_WhenUnavailable_WithTheReason(
        SourceRoleState? server, bool haveInputs, SendAvailability expected, string reason)
    {
        var h = new Harness();
        if (!haveInputs)
        {
            h.Catalog.Inputs.Clear();
        }

        await h.Controller.InitializeAsync(null);
        h.Controller.UpdateServer(server);

        await h.Controller.SetSendingAsync(true);

        Assert.False(h.Controller.IsSending);
        Assert.Equal(expected, h.Controller.Availability);
        Assert.Equal(reason, h.Controller.UnavailableReason);
        Assert.Equal(new[] { false }, h.Signals);
    }

    [Fact]
    public async Task AFailingInput_TurnsSendOff_ReportsAbsent_AndExplains()
    {
        var h = new Harness();
        h.FailingInputs.Add(CaptureInputCatalog.DesktopId);
        await h.Controller.InitializeAsync(null);
        h.Controller.UpdateServer(Active);
        await h.Device.StartAsync(); // MA is already streaming

        await h.Controller.SetSendingAsync(true);

        Assert.False(h.Controller.IsSending);
        Assert.DoesNotContain(true, h.Signals);
        Assert.Equal("Desktop audio is in use by another app.", Assert.Single(h.StopMessages));
    }

    [Fact]
    public async Task AfterAFault_SendWorksAgain()
    {
        var h = new Harness();
        h.FailingInputs.Add(CaptureInputCatalog.DesktopId);
        await h.Controller.InitializeAsync(null);
        h.Controller.UpdateServer(Active);
        await h.Device.StartAsync();
        await h.Controller.SetSendingAsync(true);

        h.FailingInputs.Clear();
        await h.Controller.SetSendingAsync(true);

        Assert.True(h.Controller.IsSending);
        Assert.Equal(true, h.Signals[^1]);
    }

    [Fact]
    public async Task SendStaysOn_AcrossADisconnect_AndCanStillBeTurnedOff()
    {
        var h = new Harness();
        await h.Controller.InitializeAsync(null);
        h.Controller.UpdateServer(Active);
        await h.Controller.SetSendingAsync(true);

        h.Controller.UpdateServer(null);
        Assert.True(h.Controller.IsSending);

        await h.Controller.SetSendingAsync(false);
        Assert.False(h.Controller.IsSending);
        Assert.False(h.Signals[^1]);
    }

    private sealed class Harness
    {
        public Harness()
        {
            Device = new WindowsAudioCaptureDevice(
                () => new FakeCaptureSource("silence"),
                id => new FakeCaptureSource(id) { ThrowOnStart = FailingInputs.Contains(id) },
                NullLogger<WindowsAudioCaptureDevice>.Instance);
            Controller = new SourceSendController(Device, Catalog, NullLogger<SourceSendController>.Instance)
            {
                ReportSignalAsync = present =>
                {
                    Signals.Add(present);
                    return Task.CompletedTask;
                },
            };
            Controller.SendingStopped += (_, message) => StopMessages.Add(message);
        }

        public FakeCatalog Catalog { get; } = new();

        public HashSet<string> FailingInputs { get; } = new();

        public WindowsAudioCaptureDevice Device { get; }

        public SourceSendController Controller { get; }

        public List<bool> Signals { get; } = new();

        public List<string> StopMessages { get; } = new();
    }

    private sealed class FakeCatalog : ICaptureInputCatalog
    {
        public List<CaptureInput> Inputs { get; } = [Desktop, LineIn];

        public Dictionary<string, CaptureInput> Known { get; } = new();

        public IReadOnlyList<CaptureInput> GetInputs() => Inputs.ToList();

        public CaptureInput? Describe(string inputId) =>
            Inputs.FirstOrDefault(i => i.Id == inputId) ?? Known.GetValueOrDefault(inputId);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Sendspin.Windows.Services.Tests/Sendspin.Windows.Services.Tests.csproj --filter "FullyQualifiedName~SourceSendControllerTests"`
Expected: build FAILS with `The type or namespace name 'SourceSendController' could not be found`.

- [ ] **Step 4: Implement**

`src/Sendspin.Windows.Services/Audio/Capture/SourceSendController.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Client;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>Whether Send can be turned on, and if not, why.</summary>
public enum SendAvailability
{
    /// <summary>Send can be turned on.</summary>
    Available,

    /// <summary>No server is connected.</summary>
    Disconnected,

    /// <summary>The session is not paired, so the server will not activate the source role.</summary>
    NotPaired,

    /// <summary>Paired, but the server did not activate the source role.</summary>
    ServerDoesNotAcceptInputs,

    /// <summary>This PC has nothing to capture from.</summary>
    NoInputs,

    /// <summary>The selected input is not available.</summary>
    InputMissing,
}

/// <summary>
/// Owns Send: opens the capture gate, reports line-sense, and says why Send is unavailable.
/// </summary>
/// <remarks>
/// Music Assistant records a client's first signal report without acting on it, so
/// <see cref="InitializeAsync"/> reports <c>absent</c> before anything else; the first Send
/// press is then a transition MA turns into autostart. Not thread-safe beyond the fault path:
/// call it from the UI thread.
/// </remarks>
public sealed class SourceSendController
{
    private readonly WindowsAudioCaptureDevice _device;
    private readonly ICaptureInputCatalog _catalog;
    private readonly ILogger _logger;
    private IReadOnlyList<CaptureInput> _inputs = [];
    private SourceRoleState? _server;

    /// <summary>Initializes a new instance of the <see cref="SourceSendController"/> class.</summary>
    /// <param name="device">The capture device handed to the SDK.</param>
    /// <param name="catalog">What can be captured.</param>
    /// <param name="logger">Logger.</param>
    public SourceSendController(
        WindowsAudioCaptureDevice device,
        ICaptureInputCatalog catalog,
        ILogger<SourceSendController> logger)
    {
        _device = device;
        _catalog = catalog;
        _logger = logger;
        _device.Faulted += OnDeviceFaulted;
    }

    /// <summary>Raised when any state the UI shows changes. May be raised off the UI thread.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised with a user-facing message when Send turned itself off after a failure.</summary>
    public event EventHandler<string>? SendingStopped;

    /// <summary>
    /// Gets or sets the delivery of the line-sense signal to whichever connection is current. Set by
    /// the view model, which knows the connection mode.
    /// </summary>
    public Func<bool, Task> ReportSignalAsync { get; set; } = _ => Task.CompletedTask;

    /// <summary>Gets a value indicating whether Send is on.</summary>
    public bool IsSending { get; private set; }

    /// <summary>Gets the selected input's ID.</summary>
    public string? InputId { get; private set; }

    /// <summary>Gets the inputs available at the last refresh.</summary>
    public IReadOnlyList<CaptureInput> Inputs => _inputs;

    /// <summary>Gets the selected input, or null when it is not available.</summary>
    public CaptureInput? SelectedInput => _inputs.FirstOrDefault(i => i.Id == InputId);

    /// <summary>Gets whether Send can be turned on.</summary>
    public SendAvailability Availability =>
        _server is null ? SendAvailability.Disconnected
        : !_server.IsActive
            ? (_server.TrustLevel == SendspinTrustLevel.Paired
                ? SendAvailability.ServerDoesNotAcceptInputs
                : SendAvailability.NotPaired)
        : _inputs.Count == 0 ? SendAvailability.NoInputs
        : SelectedInput is null ? SendAvailability.InputMissing
        : SendAvailability.Available;

    /// <summary>Gets why Send is unavailable, or null when it is available.</summary>
    public string? UnavailableReason => Availability switch
    {
        SendAvailability.Disconnected => "Not connected to Music Assistant",
        SendAvailability.NotPaired => "Pair this player with Music Assistant to send audio",
        SendAvailability.ServerDoesNotAcceptInputs => "This server doesn't accept audio inputs",
        SendAvailability.NoInputs => "No audio inputs available",
        SendAvailability.InputMissing =>
            $"{(InputId is { } id ? _catalog.Describe(id)?.Name : null) ?? "The selected input"} isn't connected",
        _ => null,
    };

    /// <summary>
    /// Selects the saved input (or the first available) and reports <c>absent</c>. Call once at
    /// startup, after <see cref="ReportSignalAsync"/> is set.
    /// </summary>
    /// <param name="savedInputId">The persisted input ID, if any.</param>
    /// <returns>A task that completes once applied.</returns>
    public async Task InitializeAsync(string? savedInputId)
    {
        _inputs = _catalog.GetInputs();
        InputId = !string.IsNullOrEmpty(savedInputId) ? savedInputId : _inputs.FirstOrDefault()?.Id;
        await _device.SetInputAsync(InputId).ConfigureAwait(false);
        await ReportAsync(false).ConfigureAwait(false);
        RaiseStateChanged();
    }

    /// <summary>Re-reads the available inputs.</summary>
    public void RefreshInputs()
    {
        _inputs = _catalog.GetInputs();
        RaiseStateChanged();
    }

    /// <summary>Describes an input by ID, even when it is not plugged in.</summary>
    /// <param name="inputId">The input ID.</param>
    /// <returns>The input, or null if Windows does not know it.</returns>
    public CaptureInput? DescribeInput(string inputId) => _catalog.Describe(inputId);

    /// <summary>Selects what Send captures; applies immediately, including while sending.</summary>
    /// <param name="inputId">The input ID.</param>
    /// <returns>A task that completes once applied.</returns>
    public async Task SelectInputAsync(string inputId)
    {
        InputId = inputId;
        await _device.SetInputAsync(inputId).ConfigureAwait(false);
        RaiseStateChanged();
    }

    /// <summary>Records the connected server's source-role state, or null when disconnected.</summary>
    /// <param name="state">The state, or null.</param>
    public void UpdateServer(SourceRoleState? state)
    {
        _server = state;
        RaiseStateChanged();
    }

    /// <summary>Turns Send on or off. Turning on is ignored while <see cref="Availability"/> says no.</summary>
    /// <param name="sending">True to send.</param>
    /// <returns>A task that completes once applied.</returns>
    public async Task SetSendingAsync(bool sending)
    {
        if (sending == IsSending)
        {
            return;
        }

        if (sending)
        {
            _inputs = _catalog.GetInputs();
            if (Availability != SendAvailability.Available)
            {
                RaiseStateChanged();
                return;
            }
        }

        IsSending = sending;
        RaiseStateChanged();

        if (sending)
        {
            await _device.SetSendEnabledAsync(true).ConfigureAwait(false);

            // A failed start has already turned Send off (OnDeviceFaulted); don't claim signal.
            if (IsSending)
            {
                await ReportAsync(true).ConfigureAwait(false);
            }
        }
        else
        {
            await ReportAsync(false).ConfigureAwait(false);
            await _device.SetSendEnabledAsync(false).ConfigureAwait(false);
        }
    }

    /// <summary>Stops capture at shutdown.</summary>
    /// <returns>A task that completes once stopped.</returns>
    public Task StopAsync() => _device.StopAsync();

    private async Task ReportAsync(bool present)
    {
        try
        {
            await ReportSignalAsync(present).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not report source signal {Signal}", present ? "present" : "absent");
        }
    }

    private void OnDeviceFaulted(object? sender, CaptureFaultedEventArgs e)
    {
        string name = _inputs.FirstOrDefault(i => i.Id == e.InputId)?.Name
            ?? _catalog.Describe(e.InputId)?.Name
            ?? "The input";
        bool wasSending = IsSending;
        IsSending = false;
        _inputs = _catalog.GetInputs();
        RaiseStateChanged();

        if (wasSending)
        {
            _ = ReportAsync(false);
        }

        SendingStopped?.Invoke(this, CaptureErrorMessages.Describe(name, e.Exception));
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Sendspin.Windows.Services.Tests/Sendspin.Windows.Services.Tests.csproj`
Expected: PASS, the whole suite.

- [ ] **Step 6: Commit**

```bash
git add src/Sendspin.Windows/Sendspin.Windows.csproj src/Sendspin.Windows.Services/Sendspin.Windows.Services.csproj src/Sendspin.Windows.Services/Audio/Capture/SourceSendController.cs tests/Sendspin.Windows.Services.Tests/Audio/Capture/SourceSendControllerTests.cs
git commit -m "Move to SDK 10.1.0 and add the Send controller"
```

### Task 9: Wire Send into the app

**Files:**
- Modify: `src/Sendspin.Windows/App.xaml.cs` (capabilities initializer near line 294, role add near line 337, DI before `services.AddSingleton(sp => new SendspinClientOptions`, options initializer)
- Modify: `src/Sendspin.Windows/ViewModels/MainViewModel.cs` (fields, constructor, `InitializeAsync`, `CreateAndConfigureManualClient`, `CleanupManualClientAsync`, `OnServerDisconnected`, `OnManualClientConnectionStateChanged`, `ToggleSettings`, shutdown)

**Interfaces:**
- Consumes: everything from Tasks 5, 6 and 8; SDK `SendspinHostService.SetSourceSignalAsync`, `SourceRoleStateChanged`.
- Produces (bound by Task 10's XAML):
  - `MainViewModel` properties: `IsSending`, `ShowSendingBadge`, `SendInputName`, `SendMenuHeader`, `SendBadgeText`, `SendButtonTooltip`, `AvailableSendInputs`, `SettingsSelectedSendInput`, `ShowSendInputFeedbackHint`;
  - `ToggleSendCommand`.

This task has no automated test: `MainViewModel` has no test harness. The behavior it routes is covered by Tasks 6–8. Verification is the build plus the manual run in Task 11.

- [ ] **Step 1: App.xaml.cs**

Add usings if missing: `using Sendspin.SDK.Synchronization;` and `using Sendspin.Windows.Services.Audio.Capture;`.

In the `new ClientCapabilities { ... }` initializer, after `VisualizerRoleSupport = ...,`, add:

```csharp
            // Line-sense lets the Send button drive Music Assistant's autostart.
            SourceRoleSupport = new SourceRoleSupport { LineSense = true },
```

After the `visualizer@v1` block, add:

```csharp
        // source@v1 is always advertised. Whether real audio leaves the PC is decided by Send in
        // the app — the capture device streams generated silence until then — because roles are
        // negotiated per connection and cannot change once it is open.
        if (!clientCapabilities.Roles.Contains("source@v1"))
        {
            clientCapabilities.Roles.Add("source@v1");
        }
```

Immediately before `services.AddSingleton(sp => new SendspinClientOptions`, add:

```csharp
        // Source input (Send). One device for every connection: silence until Send, then the
        // selected input.
        services.AddSingleton<ICaptureInputCatalog, CaptureInputCatalog>();
        services.AddSingleton(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return new WindowsAudioCaptureDevice(
                createSilence: () => new SilenceSource(HighPrecisionTimer.Shared),
                createInput: id => id == CaptureInputCatalog.DesktopId
                    ? new ProcessLoopbackCaptureSource(HighPrecisionTimer.Shared, loggerFactory.CreateLogger<ProcessLoopbackCaptureSource>())
                    : new EndpointCaptureSource(id, HighPrecisionTimer.Shared, loggerFactory.CreateLogger<EndpointCaptureSource>()),
                loggerFactory.CreateLogger<WindowsAudioCaptureDevice>());
        });
        services.AddSingleton<SourceSendController>();
```

In the `SendspinClientOptions` initializer, after `PairingWindow = ...,`, add:

```csharp
            CaptureDevice = sp.GetRequiredService<WindowsAudioCaptureDevice>(),
```

- [ ] **Step 2: MainViewModel — fields, constructor, events**

Add usings: `using Sendspin.Windows.Services.Audio.Capture;` (and `using Sendspin.SDK.Protocol.Messages;` if `ServerActivatePayload` isn't already resolved).

Add the field next to `_settingsService`:

```csharp
    private readonly SourceSendController _sourceSend;
    private bool _loadingSendInputs;
```

Add the constructor parameter `SourceSendController sourceSend` after `OutputDelayStore outputDelayStore`. In the body, after `_mediaControlsService = mediaControlsService;`, add:

```csharp
        _sourceSend = sourceSend;
        _sourceSend.ReportSignalAsync = ReportSourceSignalAsync;
        _sourceSend.StateChanged += (_, _) => RunOnUi(RefreshSendState);
        _sourceSend.SendingStopped += (_, message) => RunOnUi(() => _notificationService.ShowError("Sending stopped", message));
```

Next to `_hostService.PairingGestureRequested += OnPairingGestureRequested;` (line ~680), add:

```csharp
        _hostService.SourceRoleStateChanged += OnSourceRoleStateChanged;
```

Next to `_hostService.PairingGestureRequested -= OnPairingGestureRequested;` in the shutdown unsubscribe block (line ~3404), add:

```csharp
        _hostService.SourceRoleStateChanged -= OnSourceRoleStateChanged;
```

- [ ] **Step 3: MainViewModel — startup, dial client, disconnects, shutdown**

In `InitializeAsync`, before `await _connectionModeLock.WaitAsync();`, add:

```csharp
        // Report 'absent' before any connection reports anything: MA ignores a client's first
        // signal report, so the first Send press must be a transition, not that first report.
        await _sourceSend.InitializeAsync(_configuration.GetValue<string>("Audio:SourceInput"));
        LoadSendInputs();
```

At the end of `CreateAndConfigureManualClient`, add:

```csharp
        _manualClient.ServerActivateReceived += OnManualClientServerActivate;

        // Seeded before connecting: the dial client's initial client/state reads it live.
        _manualClient.SetSourceSignalAsync(_sourceSend.IsSending).SafeFireAndForget(_logger);
```

In `CleanupManualClientAsync`, next to `client.PairingGestureRequested -= OnPairingGestureRequested;`, add:

```csharp
            client.ServerActivateReceived -= OnManualClientServerActivate;
```

In `OnServerDisconnected`, inside `if (ConnectedServers.Count == 0) { ... }`, add as the first statement:

```csharp
                _sourceSend.UpdateServer(null);
```

In `OnManualClientConnectionStateChanged`, inside the `else if (e.NewState == ConnectionState.Disconnected)` branch, add as the first statement:

```csharp
                _sourceSend.UpdateServer(null);
```

In the shutdown sequence, after `await StopStepAsync(() => _hostService.StopAsync(), "host service").ConfigureAwait(false);`, add:

```csharp
            await StopStepAsync(() => _sourceSend.StopAsync(), "source input").ConfigureAwait(false);
```

Change `ToggleSettings` to:

```csharp
    private void ToggleSettings()
    {
        IsSettingsOpen = !IsSettingsOpen;
        if (IsSettingsOpen)
        {
            // Inputs come and go (USB interfaces); re-read them whenever Settings opens.
            LoadSendInputs();
        }
    }
```

- [ ] **Step 4: MainViewModel — the Send members**

Add a `#region`-free block near the other output-related members (for example after `SendPlayerStateToActiveClientAsync`):

```csharp
    /// <summary>Gets a value indicating whether Send is on.</summary>
    public bool IsSending => _sourceSend.IsSending;

    /// <summary>Gets a value indicating whether the red SENDING badge shows: sending, and connected.</summary>
    public bool ShowSendingBadge => _sourceSend.IsSending && _sourceSend.Availability != SendAvailability.Disconnected;

    /// <summary>Gets the selected input's short name.</summary>
    public string SendInputName => _sourceSend.SelectedInput?.Name ?? "audio";

    /// <summary>Gets the tray menu header.</summary>
    public string SendMenuHeader => $"Send {SendInputName}";

    /// <summary>Gets the badge text.</summary>
    public string SendBadgeText => $"SENDING · {SendInputName}";

    /// <summary>Gets the Send button tooltip: the action, or why it is unavailable.</summary>
    public string SendButtonTooltip =>
        _sourceSend.IsSending ? "Stop sending" : _sourceSend.UnavailableReason ?? $"Send {SendInputName}";

    /// <summary>Gets the inputs offered in Settings.</summary>
    public ObservableCollection<CaptureInput> AvailableSendInputs { get; } = new();

    /// <summary>Gets or sets the input selected in Settings.</summary>
    [ObservableProperty]
    private CaptureInput? _settingsSelectedSendInput;

    /// <summary>Gets a value indicating whether to warn about feedback: any input except desktop audio.</summary>
    public bool ShowSendInputFeedbackHint =>
        SettingsSelectedSendInput is { } input && input.Id != CaptureInputCatalog.DesktopId;

    private bool CanToggleSend() => _sourceSend.IsSending || _sourceSend.Availability == SendAvailability.Available;

    [RelayCommand(CanExecute = nameof(CanToggleSend))]
    private Task ToggleSendAsync() => _sourceSend.SetSendingAsync(!_sourceSend.IsSending);

    private void RefreshSendState()
    {
        OnPropertyChanged(nameof(IsSending));
        OnPropertyChanged(nameof(ShowSendingBadge));
        OnPropertyChanged(nameof(SendInputName));
        OnPropertyChanged(nameof(SendMenuHeader));
        OnPropertyChanged(nameof(SendBadgeText));
        OnPropertyChanged(nameof(SendButtonTooltip));
        ToggleSendCommand.NotifyCanExecuteChanged();
    }

    private void LoadSendInputs()
    {
        _loadingSendInputs = true;
        try
        {
            _sourceSend.RefreshInputs();
            AvailableSendInputs.Clear();
            foreach (var input in _sourceSend.Inputs)
            {
                AvailableSendInputs.Add(input);
            }

            // A saved input that is unplugged stays visible, so the choice is never silently replaced.
            if (_sourceSend.InputId is { } id && _sourceSend.SelectedInput is null
                && _sourceSend.DescribeInput(id) is { } missing)
            {
                AvailableSendInputs.Add(missing with { IsAvailable = false });
            }

            SettingsSelectedSendInput = AvailableSendInputs.FirstOrDefault(i => i.Id == _sourceSend.InputId);
        }
        finally
        {
            _loadingSendInputs = false;
        }
    }

    partial void OnSettingsSelectedSendInputChanged(CaptureInput? value)
    {
        OnPropertyChanged(nameof(ShowSendInputFeedbackHint));
        if (_loadingSendInputs || value is null || value.Id == _sourceSend.InputId)
        {
            return;
        }

        ApplySendInputAsync(value.Id).SafeFireAndForget(_logger);
    }

    private async Task ApplySendInputAsync(string inputId)
    {
        await _sourceSend.SelectInputAsync(inputId);
        await _settingsService.UpdateSettingAsync("Audio", "SourceInput", inputId);
    }

    private async Task ReportSourceSignalAsync(bool present)
    {
        // Always to the host: it remembers the signal for every connection it accepts later.
        await _hostService.SetSourceSignalAsync(present);

        if (_manualClient is { } client)
        {
            try
            {
                await client.SetSourceSignalAsync(present);
            }
            catch (Exception ex) when (client.ConnectionState != ConnectionState.Connected)
            {
                // Not connected: the next dial client is seeded in CreateAndConfigureManualClient.
                _logger.LogDebug(ex, "Source signal not sent to a disconnected dial client");
            }
        }
    }

    private void OnSourceRoleStateChanged(object? sender, SourceRoleState state) =>
        RunOnUi(() => _sourceSend.UpdateServer(state));

    private void OnManualClientServerActivate(object? sender, ServerActivatePayload activate)
    {
        if (sender is not SendspinClientService client)
        {
            return;
        }

        var state = new SourceRoleState(
            client.ServerId ?? "dial",
            client.LastServerHello?.ActiveRoles.Any(r => r.StartsWith("source@", StringComparison.Ordinal)) ?? false,
            client.TrustLevel);
        RunOnUi(() => _sourceSend.UpdateServer(state));
    }
```

- [ ] **Step 5: Build and test**

Run: `dotnet build Sendspin.Windows.sln` (plus the `UseSdkSource` flags if 10.1.0 isn't published)
Expected: succeeds with no new warnings. (`SendspinClientService.ServerActivateReceived` is `EventHandler<ServerActivatePayload>`, in `Sendspin.SDK.Protocol.Messages`.)

Run: `dotnet test tests/Sendspin.Windows.Services.Tests/Sendspin.Windows.Services.Tests.csproj`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Sendspin.Windows/App.xaml.cs src/Sendspin.Windows/ViewModels/MainViewModel.cs
git commit -m "Advertise the source role and route Send through both connection modes"
```

### Task 10: The Send UI

**Files:**
- Modify: `src/Sendspin.Windows/Resources/Styles/WelcomeStyles.xaml` (after `BroadcastingIndicator`)
- Modify: `src/Sendspin.Windows/MainWindow.xaml` (Now Playing title ~line 523; Settings before `<!-- Audio Sync Section -->` ~line 1031; volume bar ~line 1399)
- Modify: `src/Sendspin.Windows/App.xaml` (tray menu after Switch Group, line ~37)

**Interfaces:**
- Consumes: the `MainViewModel` members from Task 9.
- Produces: nothing for later tasks.

- [ ] **Step 1: The pulsing red light style**

In `WelcomeStyles.xaml`, directly after the closing `</Style>` of `BroadcastingIndicator`, add:

```xml
    <!-- Red sibling of BroadcastingIndicator: shown while this PC's input is being sent -->
    <Style x:Key="SendingIndicator" TargetType="Ellipse" BasedOn="{StaticResource BroadcastingIndicator}">
        <Setter Property="Fill" Value="{StaticResource ErrorBrush}"/>
    </Style>
```

- [ ] **Step 2: The badge above the title**

In `MainWindow.xaml`, replace:

```xml
                    <!-- Track Title -->
```

with:

```xml
                    <!-- Sending badge: a red pulsing light while this PC's input goes to Music Assistant -->
                    <StackPanel Orientation="Horizontal"
                                HorizontalAlignment="Center"
                                Margin="0,0,0,6"
                                Visibility="{Binding ShowSendingBadge, Converter={StaticResource BoolToVisibilityConverter}}">
                        <Ellipse Style="{StaticResource SendingIndicator}"/>
                        <TextBlock Text="{Binding SendBadgeText}"
                                   Style="{StaticResource CaptionText}"
                                   Foreground="{StaticResource ErrorBrush}"
                                   FontWeight="SemiBold"
                                   VerticalAlignment="Center"/>
                    </StackPanel>

                    <!-- Track Title -->
```

- [ ] **Step 3: The Settings INPUT section**

Replace:

```xml
                            <!-- Audio Sync Section -->
```

with:

```xml
                            <!-- Input Section -->
                            <TextBlock Text="INPUT" Style="{StaticResource CaptionText}"
                                       Foreground="{StaticResource TextMutedBrush}"
                                       Margin="0,8,0,12"/>

                            <StackPanel Margin="0,0,0,16">
                                <TextBlock Text="Send Input" Style="{StaticResource BodyText}"/>
                                <TextBlock Text="What the Send button streams to Music Assistant"
                                           Style="{StaticResource CaptionText}"
                                           Foreground="{StaticResource TextMutedBrush}"
                                           Margin="0,2,0,0"/>
                                <ComboBox Margin="0,8,0,0"
                                          HorizontalAlignment="Stretch"
                                          ItemsSource="{Binding AvailableSendInputs}"
                                          SelectedItem="{Binding SettingsSelectedSendInput}"
                                          DisplayMemberPath="DisplayName"
                                          Style="{StaticResource DarkComboBox}"
                                          ItemContainerStyle="{StaticResource DarkComboBoxItem}"/>
                                <TextBlock Text="If Music Assistant plays this input back on this PC, use headphones or choose another target."
                                           Style="{StaticResource CaptionText}"
                                           Foreground="{StaticResource TextMutedBrush}"
                                           TextWrapping="Wrap"
                                           Margin="0,6,0,0"
                                           Visibility="{Binding ShowSendInputFeedbackHint, Converter={StaticResource BoolToVisibilityConverter}}"/>
                            </StackPanel>

                            <!-- Audio Sync Section -->
```

- [ ] **Step 4: The volume-bar button**

In the volume bar `Grid.ColumnDefinitions`, add a fifth `<ColumnDefinition Width="Auto"/>`. Change the Switch Group button's `Grid.Column="3"` to `Grid.Column="4"`. Then, directly before `<!-- Switch Group: output belongs with the volume row, not the transport row -->`, add:

```xml
                        <!-- Send: lights up while this PC's input goes to Music Assistant -->
                        <Button Grid.Column="3"
                                Command="{Binding ToggleSendCommand}"
                                ToolTip="{Binding SendButtonTooltip}"
                                ToolTipService.ShowOnDisabled="True"
                                Width="32" Height="32"
                                Margin="4,0,0,0">
                            <Button.Style>
                                <Style TargetType="Button" BasedOn="{StaticResource IconButton}">
                                    <Setter Property="Foreground" Value="{StaticResource TextSecondaryBrush}"/>
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding IsSending}" Value="True">
                                            <Setter Property="Foreground" Value="{StaticResource AccentBrush}"/>
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </Button.Style>
                            <Path Width="18" Height="18" Stretch="Uniform"
                                  StrokeThickness="1.7" StrokeLineJoin="Round"
                                  StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                                  Stroke="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"
                                  Data="M12,20 V9 M7.5,13.5 L12,9 L16.5,13.5 M5,4.5 H19"/>
                        </Button>
```

- [ ] **Step 5: The tray item**

In `App.xaml`, directly after `<MenuItem Header="📡 Switch Group" Command="{Binding SwitchGroupCommand}"/>`, add:

```xml
                        <MenuItem Header="{Binding SendMenuHeader}"
                                  IsChecked="{Binding IsSending, Mode=OneWay}"
                                  Command="{Binding ToggleSendCommand}"/>
```

(Not `IsCheckable`. The command flips the state, and the check mark follows `IsSending`.)

- [ ] **Step 6: Build and look at it**

Run: `dotnet build Sendspin.Windows.sln` (plus the flags if needed)
Expected: succeeds.

Run the app: `dotnet run --project src/Sendspin.Windows/Sendspin.Windows.csproj` (plus the flags if needed). Take screenshots, and look at each one, of:
- the Settings INPUT section with the picker open;
- the volume bar with Send disabled, hovering it to see the reason tooltip.

The badge and the lit icon need a paired MA, so they're checked in Task 11. Close the app afterwards.

- [ ] **Step 7: Commit**

```bash
git add src/Sendspin.Windows/Resources/Styles/WelcomeStyles.xaml src/Sendspin.Windows/MainWindow.xaml src/Sendspin.Windows/App.xaml
git commit -m "Add the Send button, the sending badge, the tray item and the input picker"
```

### Task 11: Document, verify end to end, and open the PR

**Files:**
- Modify: `CLAUDE.md` (new subsection under "Key Technical Concepts", after "High-Precision Timer"; File Quick Reference rows; Configuration JSON example)

**Interfaces:**
- Consumes: all of the above.

- [ ] **Step 1: Document**

Add to `CLAUDE.md` after the "High-Precision Timer" section:

```markdown
### Source Input (Send)

The app advertises `source@v1` on every connection and offers one input to Music Assistant's
Live Inputs: desktop audio (WASAPI process loopback, excluding Sendspin's own process tree — so
MA playing it back here cannot feed back) or a recording endpoint. Spec:
`docs/superpowers/specs/2026-10-15-source-input-design.md`.

- **Consent lives in the device, not the protocol.** MA owns whether a stream is open
  (`start`/`stop`); the user owns what fills it. `WindowsAudioCaptureDevice` answers every
  `start` with generated silence (`SilenceSource`, which opens nothing) until Send opens its
  gate. Refusing a `start` instead would look broken to MA, which drops a silent-from-the-start
  source after 5 s.
- **Report `absent` before the first `present`.** MA records a client's first line-sense report
  without acting on it, so `SourceSendController.InitializeAsync` reports `absent` before any
  transport starts; otherwise the first Send press autostarts nothing.
- **The device survives `DisposeAsync`.** The SDK disposes the shared capture device with every
  client that owns its pipelines (dial cleanup, host stop). Disposal only stops it.
- **One fixed format, 48k/16/2.** Windows converts every input (`AUTOCONVERTPCM`), so switching
  input or between silence and capture never re-announces the stream.
- **Host-level signal (SDK 10.1+).** `SendspinHostService.SetSourceSignalAsync` and
  `SourceRoleStateChanged` exist because the host keeps its per-connection clients private;
  don't reach around it.
```

In "File Quick Reference", add the rows:

```markdown
| `src/Sendspin.Windows.Services/Audio/Capture/WindowsAudioCaptureDevice.cs` | Source-role capture device: silence until Send, then the selected input |
| `src/Sendspin.Windows.Services/Audio/Capture/SourceSendController.cs` | Send state, line-sense reporting, availability reasons |
```

In the "Key Configuration Options" JSON, add `"SourceInput": "desktop"` inside `"Audio"`. Under "Audio Buffer Configuration", add a line: `` - `SourceInput`: what Send captures — `"desktop"` or a WASAPI recording endpoint ID (default: desktop when supported). ``

- [ ] **Step 2: Full verification**

Run: `dotnet build Sendspin.Windows.sln -c Release`
Expected: succeeds, 0 errors.

Run: `dotnet test tests/Sendspin.Windows.Services.Tests/Sendspin.Windows.Services.Tests.csproj`
Expected: PASS, all tests. Read the summary line and report the counts.

- [ ] **Step 3: Commit and push**

This requires Sendspin.SDK 10.1.0 to be published (see Task 8 Step 1).

```bash
git add CLAUDE.md
git commit -m "Document the source input"
git push -u origin feat/source-input
```

- [ ] **Step 4: Open the PR into release/3.0.0**

```bash
gh pr create --base release/3.0.0 --title "Send line-in or desktop audio to Music Assistant" --body "Adds the Sendspin source@v1 role. Send (volume bar, tray) streams the input chosen in Settings → INPUT — desktop audio excluding Sendspin, or any recording device — to Music Assistant's Live Inputs, and drives MA's autostart through line-sense. A red pulsing SENDING badge shows while it is live.

- No captured audio leaves the PC until Send: MA's start is answered with generated silence, and no device is opened.
- Desktop capture uses WASAPI process loopback excluding our own process tree, so MA playing it back here cannot feed back.
- Requires Sendspin.SDK 10.1.0 (host-level SetSourceSignalAsync and SourceRoleStateChanged).

Spec: docs/superpowers/specs/2026-10-15-source-input-design.md"
```

- [ ] **Step 5: End-to-end with the user (on the pushed dev build, MA 2.10.7+)**

Ask the user to run through this list and report each result:
1. Pair the PC with MA. It appears under Live Inputs.
2. In MA, play the PC's input on a zone. The zone plays silence, and the Windows microphone indicator stays dark.
3. Press Send (Desktop audio). Audio plays on that zone, the icon lights up, the red SENDING badge pulses, and the tray item is checked.
4. With Send on and no stream in MA, set an autostart target for the PC in MA, press Send off and then on. MA starts playback on the target after about 2 s.
5. Switch the input in Settings while live. Audio switches without the stream restarting.
6. Select a USB interface, send, then unplug it. A "Sending stopped — … was disconnected." toast appears, and the zone falls back to silence.
7. Send off. Audio stops leaving immediately, and MA stops its autostarted playback after about 60 s.
```
