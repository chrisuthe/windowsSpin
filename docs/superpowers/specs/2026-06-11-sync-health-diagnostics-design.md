# Sync Health Diagnostics — Design

**Date:** 2026-06-11
**Issue:** [#33](https://github.com/chrisuthe/windowsSpin/issues/33) — stutters/drops with no way to tell network, clock sync, device clock, or local timing problems apart from logs.
**SDK dependency:** Sendspin.SDK 9.0.2 (chunk arrival stats, RTT stats, reanchor counter — specced 2026-06-11).

## Problem

When playback stutters, the only evidence is raw counters (drops: 40,000) and point-in-time
debug lines. Triage requires users to enable file logging, reproduce, and re-attach — and even
then the logs don't say *why*. The causes have distinguishable signatures across signals we
already (or now, with 9.0.2) collect; nothing correlates them.

## Goal

Continuously watch sync signals, detect *episodes* of correction activity, classify each episode
with a deterministic, explainable rule set, and write compact episode records to an always-on,
size-capped `sync-health.log`. Surface the latest verdict as one line in Stats for Nerds.
"Attach sync-health.log" becomes step 1 of every stutter report.

## Non-goals (v1)

- No auto-mitigation (no buffer bumping, no auto rate trim from skew estimates).
- No toasts/notifications; no main-window UI.
- No configuration surface — thresholds are constants until real-world logs justify knobs.
- No telemetry/upload of any kind.

## Architecture

All new code lives in `SendspinClient.Services/Diagnostics/` (testable in the existing
`SendspinClient.Services.Tests` project). One new always-on singleton service:

```
SyncHealthMonitor (singleton, hosts the parts below)
 ├── Sampler        10 Hz background timer → SyncHealthSample ring buffer (~30 s)
 ├── EpisodeDetector state machine: Quiet → Active → (cooldown) → close episode
 ├── EpisodeClassifier pure function: EpisodeRecord → Verdict + evidence
 └── SyncHealthLog   always-on writer, %LOCALAPPDATA%\Sendspin\logs\sync-health.log
```

`StatsViewModel` reads `SyncHealthMonitor.LatestVerdict` for the UI line. `MainWindow`/pipeline
wiring stays untouched; the monitor observes `IAudioPipeline` state itself.

### Sampler

- `System.Threading.Timer` at 100 ms while pipeline is active (idle otherwise; samples while
  `BufferStats != null`).
- Each tick captures a `SyncHealthSample` (readonly record struct):
  - From `AudioBufferStats`: smoothed/raw sync error, buffered ms, target ms, underrun count,
    samples dropped/inserted for sync, reanchor count, correction mode, playback rate,
    chunks received, bytes received, last chunk age, max chunk gap, chunk jitter,
    `MinBufferedMsRecent` (if present in 9.0.2; else buffered ms).
  - From `ClockSyncStatus`: offset µs, drift ppm, offset uncertainty, last/avg RTT, RTT jitter,
    adaptive-forgetting trigger count, measurement count.
  - From `ReadCallbackGapTracker` (new DI singleton; `BufferedAudioSampleSource` is created
    per-pipeline-start by a factory, so it records into the shared tracker): callback gap count +
    max gap ms — measures audio-thread starvation (local CPU/timing signal).
- Samples feed the detector, which keeps a 100-entry (10 s) pre-roll ring — sized to the pre-roll
  requirement, its only consumer. No allocations per tick beyond the struct write.

### EpisodeDetector

State machine evaluated per tick on counter **deltas** between consecutive samples:

- **Trigger** (any). Triggers are audibility-anchored: if a user could possibly hear it, it
  opens an episode.
  - drop delta > 0 or insert delta > 0 — waveform discontinuity (click/pop)
  - underrun delta > 0 — silence gap
  - reanchor delta > 0 — buffer clear (gap + jump)
  - |smoothed sync error| > the configured correction deadband (default 2 ms) — the corrector's
    own "imperceptible" line; beyond it, same-room speaker pairs can audibly phase and the
    correction system is actively working
  - read-callback gap delta > 0 — audio-thread starvation (glitch)
  - |playback rate − 1.0| ≥ 90 % of `MaxSpeedCorrection` — correction saturated; not audible
    itself but escalation to audible drops is imminent, and capturing the lead-up preserves the
    best pre-roll evidence
- **Quiet → Active**: first trigger tick opens an episode; the preceding 10 s of ring samples are
  copied as pre-roll.
- **Active → closed**: 3 s with no trigger ticks (hysteresis), or 120 s hard cap (close + reopen
  so chronic conditions produce periodic, bounded records instead of one unbounded one).
- On close, build an `EpisodeRecord`: start/end wall-clock + duration, pre-roll aggregates,
  episode aggregates (totals of each counter delta; min buffer; max |sync error|; max chunk gap;
  max chunk age; RTT jitter min/max; offset travel; correction-direction flip count; callback gap
  stats; drops-per-second and direction), and the session context (format, device, buffer target,
  output latency).

### EpisodeClassifier

Pure static function, deterministic ordered rules — first match wins, every verdict carries the
evidence values that fired it. Classification only *labels*; every episode is logged regardless
of verdict, so nothing audible is ever filtered out — at worst it lands as `unknown` with full
aggregates:

1. **Network starvation** — episode had underruns, OR min buffer < 40 % of target AND
   (max chunk gap > 1 s OR max chunk age > 1 s).
   *Evidence: min buffer, max chunk gap, underruns, bytes/sec.*
2. **Clock-sync instability** — correction direction flipped ≥ 2 times AND (RTT jitter > 5 ms OR
   adaptive-forgetting triggered during episode OR offset traveled > 5 ms).
   *Evidence: flips, RTT jitter, offset travel, forgetting count.*
3. **Device clock skew** — corrections one-directional, min buffer ≥ 60 % of target, max chunk
   gap < 500 ms, RTT jitter ≤ 5 ms, and sustained correction (episode duration ≥ 10 s).
   Reports estimated skew: `ppm = drops_per_sec ÷ (sample_rate × channels) × 1e6` (negative for
   inserts; the SDK counter counts samples including channels).
   *Evidence: direction, rate, ppm estimate, buffer health.*
4. **Local timing/CPU** — read-callback gap count > 0 (or max gap > 100 ms) while buffer ≥ 60 %
   of target and chunk arrival healthy.
   *Evidence: gap count, max gap, buffer health.*
5. **Unknown** — none matched; record carries the aggregates so the rule set can be tuned.

Thresholds are `internal const` in the classifier, unit-tested per rule with synthetic episodes.

### SyncHealthLog

- Always on. Path: `%LOCALAPPDATA%\Sendspin\logs\sync-health.log`. Cap 1 MB; on overflow rotate
  to `sync-health.1.log` (keep one backup; ≤ 2 MB total on disk).
- Session header on pipeline start: app + SDK version, OS build, output device, format, buffer
  target, detected output latency, static delay.
- One block per episode — human-readable, `key=value` parseable, no track metadata (timings and
  counters only):

```
[2026-06-11 21:14:32] EPISODE verdict=network-starvation duration=8.2s
  drops=18234 inserts=0 underruns=3 reanchors=0 maxSyncErr=+142.1ms
  minBuffer=12ms/250ms maxChunkGap=2310ms chunkAge=2280ms bytesPerSec=89kB
  rttJitter=1.2ms offsetTravel=0.4ms dirFlips=0 cbGaps=0
  evidence="buffer fell to 12ms 400ms before first drop burst; chunk gap 2.3s"
```

### Stats window

One new row in the sync section: `Health:` showing `LatestVerdict` ("Network starvation suspected
(2 episodes)", green "No issues detected" when none). Updated by the existing 10 Hz stats poll.

## Error handling

- The monitor must never affect playback: sampler tick wrapped in try/catch-log-once; log writes
  happen on the monitor's timer thread (never audio or UI threads) and swallow failures, degrading
  to in-memory only (verdict line still works).
- Counter resets (pipeline restart) detected via `TotalSamplesWritten` decreasing → ring and
  detector state reset, no false episode.

## Testing

- `EpisodeClassifier`: pure → table-driven tests, one per rule + boundary cases + unknown.
- `EpisodeDetector`: synthetic sample sequences → assert open/close timing, pre-roll content,
  aggregate math, hard-cap reopen, counter-reset handling.
- `SyncHealthLog`: rotation at cap; format round-trip (parse the key=value back).
- `ReadCallbackGapTracker`: gap detection thresholds, reset semantics.
- Sampler/monitor: thin orchestration of unit-tested parts; covered by build + manual smoke.
- Manual: reproduce #33 scenarios (pull cable mid-stream → network verdict; USB DAC long-run →
  skew verdict with ppm).

## Implementation order

1. SDK bump 9.0.0 → 9.0.2 in both csproj.
2. `SyncHealthSample` + `Sampler` + ring buffer.
3. `EpisodeDetector` + `EpisodeRecord` (TDD).
4. `EpisodeClassifier` (TDD).
5. `SyncHealthLog` writer + session header.
6. `SyncHealthMonitor` wiring + DI registration in `App.xaml.cs`.
7. `IReadCallbackGapSource` in `BufferedAudioSampleSource`.
8. Stats window `Health:` row.
