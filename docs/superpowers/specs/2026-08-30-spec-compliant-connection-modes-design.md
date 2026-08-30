# Spec-compliant connection modes — design note

Removes the `Auto` connection mode, makes the two transports mutually exclusive, and defaults
to server-initiated. Tracked as [#76](https://github.com/chrisuthe/windowsSpin/issues/76).

## Symptom

With two Music Assistant servers on the network, a second server connecting server-initiated
kills the *active* client-initiated session. Playback stops, the clock synchronizer resets, and
the pipeline goes `Playing → Stopping → Idle`. Observed 2026-08-30 at 12:19:17.

## Spec ground truth

Validated against [sendspin/spec](https://github.com/sendspin/spec) @ `9d8a4dc`, `connection.md`.

> Sendspin has two standard ways to establish connections: Server and Client initiated. **Server
> Initiated connections are recommended** as they provide standardized multi-server behavior, but
> require mDNS which may not be available in all environments.
>
> Servers must support both methods described below. **Clients MUST use exactly one of the two
> methods at a time**, advertising or discovering accordingly.

Restated as two prohibitions:

> **Note:** Do not manually connect to servers if you are advertising `_sendspin._tcp`.
>
> **Note:** Do not advertise `_sendspin._tcp` if the client plans to initiate the connection.

Multi-server admission is fully specified for server-initiated and explicitly *not* specified for
client-initiated:

| | Server-initiated | Client-initiated |
|---|---|---|
| Multi-server rules | Standardized: one admitted connection, ranked `management` > `playback` > `pairing` (empty lowest); incoming accepted on higher-or-equal priority; provisional until first `server/activate`, dropped at 30s; last-playback-server tiebreak; `another_server` / `concurrent_attempt` goodbyes | "How clients handle multiple discovered servers, server selection, and switching is **implementation-defined**" |

That asymmetry is the reason the spec recommends server-initiated, and the reason we default to it.

## Root cause

`Connection:Mode = "Auto"` — the shipped default — starts **both** transports:

```csharp
// MainViewModel.InitializeAsync
if (mode != ConnectionMode.DiscoverOnly)  { await _hostService.StartAsync();     IsHosting = true; }
if (mode != ConnectionMode.AdvertiseOnly) { await _serverDiscovery.StartAsync(); }
```

Two independent `if`s, each excluding one mode. `Auto` is excluded by neither, so both run. This is
the MUST violation, and everything below follows from it.

Three consequences:

1. **Arbitration decides with half the picture.** `SendspinHostService` tracks only host-mode
   connections, so it logs `Arbitration: Accepting <id> (no existing connection)` while a
   client-initiated session is playing. It is not wrong — per spec that state cannot occur.
2. **The app overrides the SDK with a sledgehammer.** `MainViewModel.OnServerConnected` calls
   `_hostService.DisconnectAllAsync("already_connected")` to reject one unwanted socket.
   `already_connected` is not a spec reason (`concurrent_attempt` is).
3. **Shared singletons carry the blast radius.** The two transports are separate
   `SendspinClientService` instances (`_manualClient` vs the host service's own), but both are
   constructed against the same injected `IAudioPipeline` and `IClockSynchronizer`. Tearing down
   one resets state the other is using.

### Evidence

```
12:19:17.461  SendspinListener:    WebSocket connection opened from 10.0.2.8
12:19:17.471  Arbitration: Accepting OraobU4... (no existing connection)
12:19:17.471  MainViewModel: Rejecting server-initiated connection ... already connected via
                             client-initiated mode
12:19:17.471  SendspinHostService: Disconnecting server OraobU4...: already_connected
12:19:17.471  KalmanClockSynchronizer: Clock synchronizer reset
12:19:17.478  SendspinConnection:  Server closed connection: "NormalClosure"     <- the live one
12:19:17.503  SendspinClientService: Disconnecting: restart
12:19:17.503  AudioPipeline: Pipeline state: "Playing" -> "Stopping"
```

## Design

### 1. Exclusive transports

`InitializeAsync` becomes an if/else:

```csharp
if (mode == ConnectionMode.AdvertiseOnly)
{
    await _hostService.StartAsync();
    IsHosting = true;
}
else
{
    await _serverDiscovery.StartAsync();
}
```

Exclusivity is then structural — no comment or convention is holding the invariant. The same
change applies to the runtime mode-switch path (`ApplyConnectionModeAsync`, ~line 2116).

### 2. Mode mapping and migration

`Auto` currently appears in four places, three of them as a `_ =>` fallback:

| Site | Current |
|---|---|
| `AvailableConnectionModes` | `{ "Auto", "Advertise Only", "Discover Only" }` |
| `ParseConnectionMode` | `_ => ConnectionMode.Auto` |
| `OnSettingsConnectionModeChanged` | `_ => "Auto"` |
| Settings load (~line 2360) | `_ => "Auto"` |

Every one of those defaults becomes wrong at once, and missing a single one silently resurrects the
violating mode. This is the part that can fail quietly, so it is the part that gets extracted and
tested.

Add `ConnectionModeMapping` to `src/Sendspin.Windows.Services/Configuration/` — pure, no WPF
dependency, unit-testable in the existing `Sendspin.Windows.Services.Tests` project:

- `FromConfigValue(string?) → ConnectionMode` — `"DiscoverOnly"` → `DiscoverOnly`; everything else,
  including legacy `"Auto"`, `null`, empty, and unrecognized strings → `AdvertiseOnly`.
- `ToConfigValue(ConnectionMode) → string`
- `ToDisplayName` / `FromDisplayName` for the settings dropdown.

The SDK's `ConnectionMode` enum is retained as-is for 2.2.x. `Auto` remains *representable* but the
mapping never produces it. Removing it upstream is
[sendspin-dotnet#254](https://github.com/Sendspin/sendspin-dotnet/issues/254), targeted at v10.

Migration is silent and one-way: a stored `"Auto"` loads as `AdvertiseOnly` and is rewritten on the
next settings save. Logged at information level. No prompt — `Auto` was never a valid
configuration, so there is no user intent to preserve.

### 3. Defer to SDK arbitration

Delete the rejection block in `OnServerConnected` entirely:

```csharp
// DELETED
if (_manualClient?.ConnectionState == ConnectionState.Connected)
{
    _hostService.DisconnectAllAsync("already_connected").SafeFireAndForget(_logger);
    return;
}
```

With exclusive modes, a client-initiated connection cannot coexist with the host service, so the
guard is unreachable by construction. `SendspinHostService` already arbitrates correctly in v9.3.0 —
priority comparison, `LastPlayedServerId` tiebreak, `DisconnectExistingAsync(existing,
"another_server")` for a displaced connection and `newConnection.DisconnectAsync(...)` for a rejected
one. Nothing app-side needs to help.

### 4. UI

- **`AdvertiseOnly` (default):** no server picker. Status reads as advertising and waiting for a
  server. The discovered-servers list, the "connect once / connect always" dialog, and
  `AutoConnectServerId` are all hidden — they express a choice this mode does not have. Pairing
  affordances are future work, out of scope here.
- **`DiscoverOnly`:** current picker behaviour, unchanged.
- Settings dropdown offers exactly two options. Labels describe what happens rather than naming a
  transport, and are fixed as:

  | Display name | Config value | `ConnectionMode` |
  |---|---|---|
  | `Let servers connect to me` | `AdvertiseOnly` | `AdvertiseOnly` |
  | `I choose a server` | `DiscoverOnly` | `DiscoverOnly` |

  These strings are the mapping's contract in both directions, so they are pinned here rather than
  left to implementation choice.

### 5. Config keys

Both keys stay, each scoped to one mode:

- `AutoConnectServerId` — `DiscoverOnly` only. Set by "connect always".
- `LastPlayedServerId` — `AdvertiseOnly` only. The spec's last-playback server, already passed to
  `SendspinHostService` at construction and used as the arbitration tiebreak.

## Explicitly not doing

- **No connection coordinator or transport abstraction.** An if/else over two mutually exclusive
  options is already exclusive by construction; wrapping it in a class would add a type whose tests
  could only assert against mocks.
- **Not removing client-initiated.** The spec permits it, and it is the documented fallback for
  networks where the server cannot discover or reach the client.
- **Not changing the SDK enum in this work.** Tracked upstream as #254.
- **No user prompt on migration.**

## Testing

Unit tests (`Sendspin.Windows.Services.Tests`) for `ConnectionModeMapping`:

- legacy `"Auto"` → `AdvertiseOnly`
- `null` / empty / whitespace / unrecognized → `AdvertiseOnly`
- `"DiscoverOnly"` → `DiscoverOnly`, `"AdvertiseOnly"` → `AdvertiseOnly`
- config-value and display-name round trips
- `FromConfigValue` never returns `ConnectionMode.Auto` for any input, including `"Auto"`

`MainViewModel` has no test project, so the if/else and UI gating are verified manually:

1. Fresh config → advertises, does not discover; server connects and plays.
2. Config carrying `"Auto"` → loads as `AdvertiseOnly`, migration logged.
3. `DiscoverOnly` → discovers, does not advertise; picker present.
4. Two servers, `AdvertiseOnly` → second server arbitrated by the SDK; the playing session is not
   dropped. This is the #76 regression check.
5. Mode switch at runtime tears down one transport before starting the other.

## Rollout

Land on `release/2.2.4`, forward-port to `master`. v2.2.4 is not yet tagged, so the default flip
lands before release rather than changing behaviour under existing users.

`claude.md` guidance was corrected ahead of this work in
[#77](https://github.com/chrisuthe/windowsSpin/pull/77), which targets `master`; it needs the same
forward/back-port so both lines describe the spec-correct stance.

## Open risk

Cross-subnet reachability is unverified. `claude.md` previously claimed client-initiated was "more
reliable for cross-subnet scenarios", but the 12:19:17 trace shows `10.0.2.8` successfully reaching
this client server-initiated across a subnet boundary, which is evidence against that claim. If
server-initiated turns out not to work in some network we care about, `DiscoverOnly` remains
available as an explicit opt-in — that is precisely why it is being kept.
