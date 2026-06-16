# Sync clock architecture plan — resolving the resampler-stutter root cause

> **⚠️ Superseded (2026-06-16): the WASAPI audio clock below was a wrong turn.** Wiring the device
> clock in as the sync-timing source (commit `d18dd27`) made the player **~100ms out of sync with
> other players**: the device clock reads the DAC-*rendered* position, which permanently lags the
> buffer read pointer by the ~100ms WASAPI output prefill, so the corrector fights a constant −100ms
> error. The wall clock (2.1.0) tracks real playback 1:1 and holds sync with no offset. On the
> machines tested the DAC clock reports a 1.0000 ratio (agrees with the system clock = no drift
> benefit), and the #33 stutter was actually fixed by PR #46 (concealment + output-driven feeding),
> not the clock. The device clock is therefore **default-OFF** (`Audio:SyncCorrection:UseDeviceClock`,
> opt-in only for genuinely divergent DAC clocks). See `MultiRoomSyncAlignmentTests` for the proof.

Status: proposed. Author handoff doc for issue #33 follow-up.

## Why this exists

PR #46 makes the #33 stutter **inaudible** (zero-order-hold concealment + output-driven
resampler feeding). It does not remove the **cause**. This plan covers the architectural
change that does.

The stutter was never the SDK sync corrections — fletchowns' sync-health logs show
`drops=0 inserts=0` throughout. It was `DynamicResamplerSampleProvider` padding momentary
resampler shortfalls with digital silence. Those shortfalls happen because the playback
**rate keeps churning**, and the rate churns because sync is timed against the **wall clock**
(`MonotonicTimer`), not the audio device clock:

```
wall-clock timing → jittery sync error → jittery rate → constant SetRates → WDL filter transients → shortfalls
```

A USB DAC's true drift is a near-constant ~ppm; an error measured against the DAC's own
clock would be smooth, the rate would settle, `SetRates` would fire rarely, and the
shortfalls would largely disappear at the source.

### Validation (researched, primary sources)

- **WASAPI `IAudioClock::GetPosition` reflects the real hardware clock including drift.**
  MS docs: `GetFrequency` reports a nominal constant, but "the position reported by the
  GetPosition method takes all such variations [drift/jitter vs the system clock] into
  account to report an accurate position value each time it is called."
  <https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclock-getfrequency>
- **Mature synchronized players time against the local soundcard clock, not wall clock.**
  Snapcast uses ALSA `snd_pcm_avail_delay()` to get real frames-queued-at-the-DAC and feeds
  it in as `outputBufferDacTime`
  (<https://github.com/badaix/snapcast/blob/develop/client/player/alsa_player.cpp>).
  shairport-sync grades outputs by device-timing accuracy and labels anything without it
  "partial synchronisation" (<https://github.com/mikebrady/shairport-sync/blob/master/README.md>).
- **Zero-order hold beats silence on underrun** — standard packet-loss concealment; silence
  is a step discontinuity = a broadband click. (Implemented in PR #46.)

## The fix has two layers

| Layer | Where | Effort | Effect |
|---|---|---|---|
| **A. WASAPI audio clock** | app (`WasapiAudioPlayer`) | medium | Removes the jitter at the source → rate settles → shortfalls largely vanish. Also fixes inter-room accuracy. |
| **B. Tighten the correction loop** | SDK | small–medium | Median-filter the error + ppm-level rate cap → smooth, slowly-varying rate. Independently valuable. |
| (done) Concealment + feeding | app (PR #46) | done | Makes any residual inaudible. |

---

## Part A — Implement the WASAPI audio clock (app side)

> **Status: implemented (Phase 0 probe + Phase 1 wiring).** The probe confirmed `IAudioClock`
> reads sane, monotonically-advancing values at ratio ~1.0000 in shared mode over ~100 s on a
> wired desktop — refuting the interface comment's "shared mode → null" assumption. Phase 1 wires
> it in as the live sync-timing source.
>
> Because the app moved sync correction **app-side**, the real integration point is **not** the
> SDK's `GetAudioClockMicroseconds()` (the SDK only calls that once, at pipeline setup). It is the
> `GetSyncTimeMicroseconds` delegate that `WasapiAudioPlayer` passes to `SyncCorrectedSampleSource`
> — invoked per render read, which is the actual playback-sync timing path. That delegate now reads
> `IAudioClock` and returns it via [`DeviceClockAnchor`](../src/SendspinClient.Services/Audio/DeviceClockAnchor.cs).
>
> **Anchoring.** The device position starts near 0; the wall clock is a huge epoch value. We capture
> `offset = wall − device` once, then return `device + offset`. The offset cancels in the buffer's
> `elapsed = now − start`, so downstream sees true device-paced elapsed while the absolute value
> rides the wall-clock timeline — making device↔wall transitions a continuous hand-off, not a cliff.
>
> **Escape hatch + auto-fallback.** `Audio:SyncCorrection:UseDeviceClock` (default `true`) disables
> it entirely; at runtime, a read failure falls back to the wall clock and a large mid-stream
> backward jump abandons the device clock for that stream. So bad hardware degrades to today's
> behavior instead of breaking.
>
> **Documented hardware hiccups** (full detail in `DeviceClockAnchor`'s XML docs):
> - Device clock not ready for the first few ms after `Start()` → wall clock until it anchors.
> - Drivers that don't support / throw on `IAudioClock` → null read → wall clock, no exception escapes.
> - Mid-stream position reset/glitch (backward jump > 50 ms) → abandon device clock for the stream.
> - Stream restart / device switch zeroes the position → `Reset()` re-anchors (`SetState`→Playing and
>   `SwitchDeviceAsync`) so the zero is a fresh anchor, not mistaken for a glitch.
> - Ratio ~1.0000 on a machine whose system clock already agrees with its DAC means **no change** —
>   the win is only where the two clocks genuinely diverge (e.g. fletchowns' USB DAC). **The decisive
>   validation is still a verbose re-test on that USB DAC.**
> - A subtly-wrong device clock (wrong rate, or silently slaved to the system clock) isn't caught by
>   the guards; it just yields neutral-to-slightly-worse sync. Only per-device validation finds that.

### Goal

Provide the SDK with the device playback position so it times sync against the DAC clock,
not the wall clock. The SDK already consumes this: `AudioPipeline` calls
`IAudioPlayer.GetAudioClockMicroseconds()`, logs `[Timing] Using audio hardware clock for
sync timing (VM-immune)` when it returns a value, and falls back to `MonotonicTimer` (the
current state — every log shows `audio clock not available`) when it returns null.

`WasapiAudioPlayer` currently does **not** override `GetAudioClockMicroseconds`, so the
default (null) is used.

**This was a documented decision, not an oversight.** The SDK's `IAudioPlayer`
interface comment states: *"Return null if hardware clock is not available (e.g., WASAPI
shared mode). The SDK will fall back to wall clock timing."* The audio-clock API was built
for the VM-immunity problem on the cross-platform players — its doc lists implementations
for PortAudio / PulseAudio / ALSA / CoreAudio, and CLAUDE.md's "Output Latency Problem"
records the Windows belief that *"WASAPI doesn't expose"* a DAC-time equivalent.

That belief is what Microsoft's docs contradict (`IAudioClock::GetPosition` *does* work in
shared mode, in mix-format frame units, via `p/f`). But docs-say-yes vs interface-comment-
says-no is an **empirical** disagreement, so this must be **probed on real hardware before
it is trusted as the timing source** — not assumed either way.

### Phase 0 — probe (do this first, zero risk)

Implement `GetAudioClockMicroseconds()` to *read* `IAudioClock` (via NAudio's
`AudioClient.AudioClockClient`, reached through the existing reflection on WasapiOut's
private `audioClient` field) and **log** position/frequency and its advance ratio vs wall
time — but **return null** so the SDK keeps wall-clock timing and behaviour is unchanged.
Have the affected USB-DAC machine run it and read the `[AudioClockProbe]` lines:

- If the clock reads sane, monotonically-advancing values at ratio ~1.0 → shared mode works;
  the interface comment is outdated; proceed to wire it in (below).
- If it throws / returns zeros / doesn't advance → the comment was right; stop, and document
  why on Windows specifically.

### Implementation steps (`src/SendspinClient.Services/Audio/WasapiAudioPlayer.cs`)

1. **Reach the `IAudioClock`.** NAudio's `WasapiOut` does not expose it publicly. After
   `Init()` has created the underlying `AudioClient`, obtain the clock. NAudio exposes
   `AudioClient.AudioClockClient` (wraps `IAudioClock` → `GetPosition`/`GetFrequency`/
   `AdjustedPosition`). The `AudioClient` itself is held in a private field of `WasapiOut`;
   confirm the installed NAudio version's surface — if `WasapiOut` doesn't expose the
   `AudioClient`, either bump NAudio, or reflect the private field once at init (the codebase
   already uses reflection for `StreamLatency` at `WasapiAudioPlayer.cs:704`, so there's
   precedent and a fallback pattern). Cache the clock client; do **not** query it per call
   through reflection.

2. **Read position + frequency in `GetAudioClockMicroseconds()`.**
   - `frequency = clock.Frequency` (device clock frequency, constant — can be cached once).
   - `position = clock.AdjustedPosition` (or `Position`) — frames played, in the stream's
     position units.
   - `seconds = position / frequency`; return `seconds * 1_000_000` as `long` µs.
   - **Always** use `position / frequency`. Do **not** assume `position` equals your app
     sample count — in shared mode it is in the **mix format's** frame units, which may
     differ from both your stream rate and the device rate. The `p/f` ratio is unit-agnostic
     and correct.

3. **Handle the documented edge cases** (all from MS docs):
   - **Startup zero:** position "might remain 0 for a few ms" after start. While it reads 0,
     return null so the SDK stays on the monotonic fallback; switch over once it advances.
   - **`S_FALSE`:** `GetPosition` can return `S_FALSE` if the call took too long. Retry once
     or twice; never loop infinitely. On persistent failure, return null (fallback).
   - **Device invalidated** (`AUDCLNT_E_DEVICE_INVALIDATED`, unplug/format change): catch,
     return null, and let the existing device-switch/re-anchor path handle it.
   - **Pause/stop/track-change:** position freezes on stop, resumes on start, zeroes on
     reset. Make sure the SDK re-anchors on these transitions rather than treating a frozen
     position as drift (the pipeline already has reconnect/stream-start re-anchoring).

4. **(Optional but recommended) Retire the `OutputLatencyMicroseconds` constant.** The
   codebase's "Output Latency Problem" (CLAUDE.md) subtracts a fixed ~115 ms estimate to
   approximate "how much audio actually reached the speaker." `IAudioClock` answers that
   directly and correctly (it *is* the played-out position), so the constant-latency
   subtraction can be removed for the audio-clock path. Keep it only for the monotonic
   fallback.

### Verification

- Log line flips to `[Timing] Using audio hardware clock for sync timing`.
- With debug `SyncCorrection` logging: steady-state `rate` settles to a **small near-constant
  ppm** offset (e.g. `1.00012x`) instead of swinging; `mode` mostly `None`/`Resampling` with
  rare changes.
- `resamplerShort` counter stays at ~0 (vs 861/21s on the USB DAC before).
- fletchowns re-test on the actual USB DAC at verbose level — the decisive check.
- Multi-room: this client's position should align with others without manual `StaticDelay`.

### Risk / cost

- Medium effort, mostly in the NAudio reach-in and the edge-case handling.
- Reflection on `WasapiOut`'s private `AudioClient` is the main fragility; pin it behind a
  cached accessor with a clean null-fallback so a NAudio change degrades to the monotonic
  path rather than crashing.
- Low blast radius: if `GetAudioClockMicroseconds` returns null for any reason, behaviour is
  exactly today's (monotonic timing). It's a strict, reversible improvement.

---

## Part B — Tighten the correction loop (SDK handoff)

This is `Sendspin.SDK` territory (subject to the usual wariness about SDK changes), but the
research shows our loop is far looser than the reference implementations, which is what lets
the rate chase jitter in the first place.

1. **Rate cap is 40–80× too loose.** Snapcast caps steady-state soft correction at
   **±500 ppm (0.05%)**; shairport's continuous interpolation is similarly tiny. We use
   **±2% (`MaxSpeedCorrection = 0.02`, Windows) / ±4% (CLI)**. Real crystal drift is tens to
   low-hundreds of ppm. A large cap is fine as headroom for initial convergence/re-anchor,
   but the *steady-state* rate should sit near a small ppm constant. Action: log steady-state
   rate; if it swings by whole percent, that confirms jitter-chasing. Consider a tighter
   steady-state clamp once converged.
   - Snapcast: `client/stream.cpp`, `rate = 1.0 ± min(rate, 0.0005)`.

2. **Median-filter the sync error, not just EMA.** Snapcast drives corrections off
   **multi-horizon medians** (mini/short/long buffers), reacting to the persistent drift
   trend and rejecting per-chunk network jitter. We use EMA smoothing. Median filtering is
   what makes the correction smooth and jitter-immune. Action: add median filtering of the
   error before the rate controller in `TimedAudioBuffer`/`SyncCorrectionOptions`.
   - Snapcast: `client/stream.cpp`, `median_`, `shortMedian_`, `miniMedian`.

These two combined would smooth the rate even **before** the audio clock lands — and once
the audio clock is in, the input to this loop is already clean, so the two compound.

---

## Sequencing

1. **Done:** PR #46 — feeding + hold-pad concealment. Ship for fletchowns re-test.
2. **Next:** Part A (audio clock) — the root fix, app-side, reversible, our territory.
   Verify on the USB DAC.
3. **Then, if residual remains / opportunistically:** Part B (SDK loop tightening), with the
   Snapcast citations, handed to the SDK dev.

Do **not** start with the resampler-backend swap (WDL → SoundTouch/Farrow). With Parts A+B
the rate barely moves, so the backend choice stops mattering; only revisit if a residual
survives both.

## References

- WASAPI `IAudioClock::GetPosition` / `GetFrequency` / `IAudioClock2::GetDevicePosition` —
  learn.microsoft.com (audioclient).
- Snapcast client sync — `badaix/snapcast` `client/stream.cpp`, `client/player/alsa_player.cpp`.
- shairport-sync clock sync / interpolation — `mikebrady/shairport-sync` README + man page.
- NAudio canonical resampler feed pattern — `naudio/NAudio`
  `WdlResamplingSampleProvider.cs`, `WdlResampler.cs`.
- Packet-loss concealment (ZOH vs silence) — en.wikipedia.org/wiki/Packet_loss_concealment.
