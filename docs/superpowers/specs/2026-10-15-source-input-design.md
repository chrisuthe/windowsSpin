# Source input ("Send") — design

Lets this PC offer an audio input to Music Assistant through the Sendspin `source@v1` role, so
that "Send desktop audio" or "Send Line in" is one click. Targets 3.0.0 (SDK 10.0.0, Music
Assistant 2.10.7+).

## Goal

From the main window or the tray, the user turns **Send** on and the chosen input (everything
Windows is playing, or a recording device) streams to Music Assistant, which plays it on
whatever zone it is configured to. Turning Send off stops capture at once.

Success looks like:

- The PC appears under Music Assistant's **Live Inputs** once paired, and selecting it there
  never errors.
- No captured audio leaves the PC until the user presses Send here, and no recording device or
  loopback is even opened before then.
- Sending desktop audio while Music Assistant plays it back on this same PC does not feed back.

## Non-goals

- Opus or FLAC source encoding. PCM is what the SDK encodes and what every server must accept.
- Choosing the destination zone in this app. That is Music Assistant's policy.
- More than one input at a time. One client identity is one source.
- Local passthrough (playing an input on this PC without the server). The spec forbids a
  player+source device from playing its captured input locally anyway.

## Ground truth

### Protocol ([sendspin/spec](https://github.com/sendspin/spec) @ `914981d`, `roles/source/v1.md`)

- A source streams only after the server sends `command: "start"`. The server is the only party
  that initiates streaming; streaming state does not survive reconnection.
- On `start` the client "SHOULD promptly send `client_stream/start`". There is no per-source
  "unavailable" state — the only switch is the client-wide `available: false`, which would also
  take the player offline.
- `signal: 'present' | 'absent'` (line sensing, if `line_sense` is advertised) is a hint the
  server MAY use to decide when to start or stop. The decision is server policy.
- A `client_stream/start` while a stream is open replaces the format in place; chunks MUST be
  ≤150 ms.
- Roles are negotiated per connection. `client/hello` is re-sent only after a re-handshake,
  which only the server can initiate (`connection.md` § Re-handshake). **The client cannot add
  or remove a role mid-connection.**

### SDK 10.0.0

- `IAudioCaptureDevice` — `Format`, `AudioCaptured(CapturedAudio(Pcm, CaptureTimeMicroseconds))`,
  `StartAsync`, `StopAsync`, `IAsyncDisposable`. Supplied via `SendspinClientOptions.CaptureDevice`,
  with `"source@v1"` added to `ClientCapabilities.Roles`.
- `SourceStreamPipeline` reads `_capture.Format` on every `start`, chunks and encodes, and gates
  streaming on trust, an active `source@` role, and availability.
- `DefaultSourceAudioEncoderFactory` supports only `pcm`, and `PcmSourceEncoder` passes bytes
  through untouched — the device must deliver integer little-endian PCM.
- `ISendspinClient.SetSourceSignalAsync(bool)` (requires `SourceRoleSupport.LineSense = true`),
  `TrustLevel`, and `LastServerHello.ActiveRoles` are public.
- **Gap: none of that is reachable in server-initiated mode.** `SendspinHostService` keeps each
  connection's `SendspinClientService` private and exposes only `SendCommandAsync` and
  `SendPlayerStateAsync`; `ConnectedServerInfo` carries id, name, connect time and clock status.
  This design therefore depends on **SDK 10.1.0**, which adds to the host:
  - `SetSourceSignalAsync(bool present)` — applied to every open connection and remembered, so
    each new connection carries it in its initial `client/state`.
  - `SourceRoleStateChanged` (`SourceRoleState(ServerId, IsActive, TrustLevel)`) — raised on
    admission and on every later `server/activate`.
- Disposing a client with `ownsPipelines: true` (the dial cleanup, and the host's stop) disposes
  the shared `CaptureDevice`. The device must treat `DisposeAsync` as "stop" and stay reusable.

### Music Assistant

Verified 2026-10-15 against `music_assistant/providers/sendspin_source` on `dev` (README,
`provider.py`, `constants.py`); the provider is also on `stable`.

- Every connected client with an active `source` role is one AudioSource under **Live Inputs**.
- The source role activates only on paired connections.
- MA holds silence when source audio stops rather than ending the stream, for up to
  `SOURCE_TIMEOUT_S` = 30 s. A client that never answers `start` within `COLD_START_TIMEOUT_S`
  = 5 s is treated as broken — so refusing a `start` would look like a fault.
- `line_sense` drives autostart, configured per device in MA: signal present → playback on the
  chosen target after a 2 s debounce; absent → stop after 60 s. A device that is also a player
  defaults to playing its own input.
- **Only transitions act, and the first report for a client is recorded silently**
  (`_on_signal_reported`: `if previous is None: return`). So the client must report `absent`
  from the moment it connects; otherwise the first Send press is that silent first report and
  nothing starts.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| What Send does | Toggle that reports line-sense `signal` | The only client→server "play me" lever the protocol has. MA's Live Inputs still work as well. |
| Consent | Captured audio leaves only while Send is on | A server must not be able to open a microphone remotely. |
| Role advertisement | `source@v1` always advertised | Roles can't change mid-connection; advertising always means no "Off" setting and no restart. Cost: every *paired* Windows client appears in Live Inputs, like a speaker with an AUX jack. |
| `start` while Send is off | Accept, stream **generated** silence | Honors the protocol SHOULD; no device is opened, so the Windows microphone indicator stays dark. Selecting the PC in MA ahead of time, then pressing Send, goes live without another step. |
| Inputs | "Desktop audio" + every active recording endpoint | One picker; endpoints remembered by ID like Playback Device. |
| Desktop capture | WASAPI process loopback excluding Sendspin's process tree | Feedback-free by construction when MA plays the input back on this PC. |
| Wire format | Fixed 48 kHz / 16-bit / stereo PCM for every source | `Format` never changes, so switching input mid-stream needs no new `client_stream/start`; Windows does the conversion; matches what MA converts to. |
| Failure while sending | Send turns off, stream falls back to silence, toast with reason | Never silently capture something the user didn't pick; MA's stream stays up. |
| Send at launch | Always off, and `absent` reported before any connection | Consent is per session; MA ignores a client's first signal report. |
| SDK surface | Add host-level signal and source-role state in SDK 10.1.0 | The host owns its connections; the app must not reach around it. |

## UX

**Volume bar** (visible while connected): a Send icon button beside Switch Group. Accent color
while sending; tooltip "Send Desktop audio" / "Stop sending", or the reason Send is unavailable
when disabled.

**Now Playing**: while `IsSending && IsConnected`, a red pulsing **● SENDING · Desktop audio**
badge sits above the track title. Album art and track info are unchanged — this PC can play and
send at the same time. The pulse is a sibling of the existing `BroadcastingIndicator` style
(`Resources/Styles/WelcomeStyles.xaml`) in red.

**Tray**: a checkable "Send Desktop audio" item (input name substituted), disabled with the
same conditions as the button.

**Settings → INPUT** (new section after AUDIO): "Send input" picker. Entries: "Desktop audio
(all apps except Sendspin)" when supported, then each active recording endpoint by friendly
name. A saved endpoint that is not present shows as "<name> — not connected". When an endpoint
is selected, a hint below reads: *"If Music Assistant plays this input back on this PC, use
headphones or choose another target."* Changing the input applies immediately, including while
sending.

## Architecture

### Services — `src/Sendspin.Windows.Services/Audio/Capture/`

| Unit | Responsibility |
|---|---|
| `WindowsAudioCaptureDevice : IAudioCaptureDevice` | The single device handed to the SDK. Holds the selected input and the Send gate. While the SDK has it started, it runs exactly one inner source: `SilenceSource` while the gate is closed, the selected input's source while open. While the SDK has not started it, gate and input changes are only recorded and nothing runs. Raises `Faulted` when an inner source fails. Format is always 48000/16/2 `pcm`. |
| `ICaptureSource` (internal) | `Start`, `Stop`, and a frames event carrying interleaved 48k/16/2 PCM plus capture time (µs). |
| `SilenceSource` | Emits 20 ms frames of zeros paced from `HighPrecisionTimer`, timestamp of each = previous + 20 ms. Opens no device. |
| `ProcessLoopbackCaptureSource` | `ActivateAudioInterfaceAsync` on `VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK` with `PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE` for our PID, requesting 48k/16/2. Own COM interop (NAudio has none). Requires Windows 10 2004 (build 19041). |
| `EndpointCaptureSource` | WASAPI shared-mode capture on one endpoint with `AUTOCONVERTPCM \| SRC_DEFAULT_QUALITY` to 48k/16/2. |
| `CaptureInputCatalog` | Lists picker entries: Desktop audio (if the OS supports it) and active capture endpoints (`MMDeviceEnumerator`, `DataFlow.Capture`). |

**Timestamps.** Both capture sources take each packet's QPC position from
`IAudioCaptureClient::GetBuffer` (100 ns units) and convert it into `HighPrecisionTimer`'s
microsecond domain as `now − (qpcNow − qpcPacket)`. This needs a capture loop over the client
interfaces rather than NAudio's `WasapiCapture`, which doesn't surface the QPC position.

**Switching.** On a gate or input change the device stops the outgoing inner source before
starting the incoming one, and guarantees emitted timestamps are monotonic and non-overlapping:
a frame starting before the previous frame's end is trimmed. Gaps are allowed; MA's resampling
bridge absorbs them. The SDK's stream stays open throughout.

### App — `src/Sendspin.Windows/`

- **App.xaml.cs** always constructs `WindowsAudioCaptureDevice`, sets it as
  `SendspinClientOptions.CaptureDevice`, adds `"source@v1"` to `ClientCapabilities.Roles`, and
  sets `SourceRoleSupport = new() { LineSense = true }` — the same shape as the existing
  `visualizer@v1` registration.
- **Setting** `Audio:SourceInput`: `"desktop"` or an endpoint ID. Default `"desktop"` when
  supported, otherwise the first active endpoint, otherwise empty. (Flat key: the settings
  service merges properties within a top-level section only.)
- **`SourceSendController`** (new, in the Services project so it is unit-testable) owns
  `IsSending`:
  - At startup, before any transport starts → report `absent` (seeds every connection).
  - Send on → open the gate, then report `present`.
  - Send off → report `absent`, close the gate (back to silence).
  - Reporting goes to `SendspinHostService.SetSourceSignalAsync` (which remembers it for new
    connections) and, in client-initiated mode, to the dial client, which is seeded the same
    way before it connects.
  - Device `Faulted` → Send off, notification with the reason.
  - Computes availability and the reason Send is unavailable (see below), from the
    `SourceRoleState` the host raises or, in client-initiated mode, from the dial client's
    `ServerActivateReceived`.
- **`MainViewModel`** surfaces `IsSending`, `CanSend`, `SendUnavailableReason`, `SendInputName`,
  and `ToggleSendCommand`, delegating to the controller. Tray and volume bar bind to these.

### Data flow

```
[Send click] → SourceSendController ─ gate open ─▶ WindowsAudioCaptureDevice (Silence → selected)
                     └─ SetSourceSignalAsync(true) ─▶ MA ─ autostart on its target
MA ─ start ─▶ SDK SourceStreamPipeline ─ StartAsync ─▶ device ─▶ inner source
inner source ─ PCM 48k/16/2 + µs ─▶ AudioCaptured ─▶ SDK chunks/encodes ─▶ MA ─▶ any zone
```

## Errors and edge cases

**Send unavailable** — button and tray item disabled, tooltip gives the reason:

| Condition | Detected by | Reason shown |
|---|---|---|
| Disconnected | connection state | (volume bar hidden; tray item disabled) |
| Not paired | `TrustLevel` unpaired and no `source@` in `ActiveRoles` | "Pair this player with Music Assistant to send audio" |
| Server doesn't accept inputs | paired, but no `source@` in `ActiveRoles` | "This server doesn't accept audio inputs" |
| Selected input missing | not in `CaptureInputCatalog` | "<name> isn't connected" |
| Nothing to capture | catalog empty | "No audio inputs available" |

**While sending** — each turns Send off, falls back to silence, and shows a toast:

- Endpoint removed: "Sending stopped — <name> was disconnected."
- Windows microphone privacy denies capture (`E_ACCESSDENIED`; applies to every recording
  endpoint, line-in included): the message names Settings › Privacy & security › Microphone ›
  "Let desktop apps access your microphone". (The toast service has no action buttons, so no
  deep link.)
- Endpoint held in exclusive mode (`AUDCLNT_E_DEVICE_IN_USE`): "<name> is in use by another app."
- New input fails to start after a switch: Send off; no fallback to the previous input.

**Consent and lifecycle**

- `start` while Send is off: stream opens with generated silence; logged at Information.
- Send stays on across a reconnect within the session; the badge shows only while connected.
  The new connection reports `present` in its first `client/state`, which MA records without
  acting; MA's own reconnect recovery resumes a stream that was already playing.
- `DisposeAsync` on the device stops it and leaves it reusable (the SDK disposes it with each
  owning client). Shutdown stops it through the existing `StopStepAsync` sequence.

**Known limitation.** Desktop audio is feedback-free by construction. A microphone or a
"Stereo Mix"-type endpoint is not: if MA plays this PC's input back on this PC — MA's default
autostart target for a player+source device — a mic will howl and Stereo Mix will loop. Neither
can be detected reliably, so the picker hint above is the mitigation.

## Testing

**Unit** (`tests/Sendspin.Windows.Services.Tests`, fakes behind `ICaptureSource` and the timer):

- `WindowsAudioCaptureDevice`: gate closed → only `SilenceSource` runs and the real source is
  **never constructed**; gate open → selected source; gate closed again → silence; `Format`
  constant throughout; timestamps monotonic and non-overlapping across every switch; input
  change mid-send; inner-source fault → `Faulted` and silence.
- `SilenceSource`: 20 ms zero frames, timestamp contiguity, no accumulated drift over thousands
  of frames.
- `SourceSendController` with a recording signal delegate: `absent` reported at startup;
  on/off → `present`/`absent`; fault → off + notification; each availability reason.
- SDK 10.1.0 (in sendspin-dotnet, loopback `FakeServer`): a signal set before a connection rides
  its initial `client/state`; a later one reaches the admitted server; `SourceRoleStateChanged`
  reports the active role and trust on admission and on a later activate.
- Settings: `Audio:SourceInput` default selection.

**SDK contract**: the device driven through SDK 10.0.0's public `SourceStreamPipeline` with the
send delegates recorded — `start` announces pcm/48000/16/2; chunks are zero while the gate is
closed and carry the source's bytes once open; Send off produces no `client_stream/end`.

**Interop probe (throwaway, scratchpad)** before building on process loopback: play a tone from
our process and from a second process, capture with exclusion, confirm the first is absent and
the second present. CI runners have no audio devices, so the interop sources are verified by
this probe and by hand, not in CI.

**End to end** on a pushed dev build against MA 2.10.7: pair → appears in Live Inputs; select in
MA → silence; Send → audio, badge, tray check; switch input while live; unplug a USB input →
toast and silence; MA start with Send off → no capture, microphone indicator dark.

## Resolved during planning

- MA facts: verified against the provider source (see Ground truth); this added the
  "report absent first" requirement.
- NAudio 2.2.1 exposes `AudioCaptureClient.GetBuffer(out frames, out flags, out devicePosition,
  out qpcPosition)` and `AudioClientStreamFlags.AutoConvertPcm | SrcDefaultQuality`, so endpoint
  capture stays on NAudio. Its `AudioClient` constructor is internal, so process loopback uses
  its own minimal COM interop.
- `AUTOCONVERTPCM` producing 48k/16/2 is confirmed by the interop probe (plan Task 3) and the
  manual source check (plan Task 6), not assumed.
- The spec text the SDK cites for source activation on unpaired sessions differs from spec
  @ `914981d` ("MUST only run on a paired connection"). Not a blocker — the app leaves
  `Connection:UnpairedAccessEnabled` off by default and MA requires pairing — and availability
  keys on the source role being active, which is correct either way.
