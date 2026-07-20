# Seek bar freeze — design note

Follow-up to `2026-07-17-seek-bar-reset-design.md`. That work made the bar stop at the
right moments; this one makes it start again.

## Symptom

The playback progress/seek bar stops advancing and never recovers for the rest of the
track, even though audio keeps playing. Reconnecting or skipping to the next track clears
it.

## Root cause

Four app-side pieces combined so that nothing could rebuild the extrapolation anchor:

| Piece | Old behavior |
|---|---|
| `TrackProgressTracker.ApplyMetadata` | The group `playback_state` overrode the progress object's `playback_speed`: any non-`Playing` state anchored at effective speed 0. |
| `TrackProgressTracker.Freeze()` | Nulls `_anchor`. |
| `MainViewModel.OnPlaybackStateChanged` | Called `Freeze()` on every transition away from `Playing`, with no counterpart on the way back. |
| `MainViewModel.OnPositionTimerTick` | Returned early unless `PlaybackState == Playing`. |

Only *fresh* progress — a new `PlaybackProgress` instance, detected by `ReferenceEquals` —
ever rebuilt the anchor. Two real paths produce a `Playing` transition with no fresh
progress:

1. **Stream restart.** The SDK synthesizes state transitions the server never sent:
   `SendSpinClient` forces `PlaybackState.Playing` on `stream/start` and
   `PlaybackState.Idle` on `stream/end` for servers that do not send `group/update`. A
   format renegotiation, or a server that implements seek as end + start, produces Idle
   then Playing, *both* carrying the same carried-forward `PlaybackProgress` instance.
   `Freeze()` ran on Idle; the Playing event could not re-anchor. Bar dead.
2. **Non-conformant resume.** A server that signals resume with a `group/update` carrying
   only `playback_state` — `group/update` has no metadata and no progress.

## Spec ground truth

Validated against [Sendspin/spec](https://github.com/Sendspin/spec) @ `3632c68`.

- **README:681** — `playback_state?`: `'playing' | 'stopped'`. There is **no** `'paused'`
  state.
- **README:1448** — `playback_speed`: "0 = paused". This is the protocol's *only* pause
  signal.
- **README:1446-1448** — inside the `progress` object, `track_progress`, `track_duration`
  and `playback_speed` are all **required** (no `?`). A conformant server always sends
  `playback_speed` whenever it sends progress.
- **README:1445** — the server must send the `progress` object whenever playback state
  changes (play, pause, resume, seek, playback speed change). There is **no** guarantee of
  periodic progress messages, and note that track change is *not* in that list.

The old state-override was therefore modelling a state (`paused`) the protocol does not
have, using a signal (`playback_state`) that is not the pause signal.

## Fix

1. **Derive advance/freeze from `playback_speed` alone.** `ApplyMetadata` now anchors with
   `Math.Max(0, (progress.PlaybackSpeed ?? 1000.0) / 1000.0)`, with no state override. Per
   spec a conformant pause arrives as fresh progress with speed 0 while the group state
   stays `playing`, so this alone still freezes a real pause. The `playbackState` parameter
   is kept for a Debug-level diagnostic when the two disagree.
2. **`Resume(long nowMicroseconds)` makes the anchor lifecycle self-healing.** When there
   is no anchor, or a speed-0 anchor, it re-anchors at the **currently displayed position**
   with `now` as the anchor instant and the last known non-zero speed factor (1.0 until the
   server reports otherwise). Anchoring at `now` is what keeps the paused wall-clock
   duration out of the position — the bar resumes from where it was rather than jumping
   forward. This mirrors the SendspinDroid reference ("Re-anchor the interpolation
   timestamp when transitioning to playing, so `getCurrentPosition()` doesn't include pause
   duration"). It is a no-op while the position is already advancing (fresh progress owns
   the anchor) and while no server-confirmed position exists (nothing to resume from,
   including after `ResetForPendingTrackChange()`).
3. **`Freeze()` is unchanged and still called on every transition to a non-playing state.**
   Capturing the last position on a genuine stop was always correct; the bug was only that
   nothing could un-freeze it.
4. **`MainViewModel.OnPlaybackStateChanged`**: `Playing` → `Resume(now)`, otherwise
   `Freeze()`, using `HighPrecisionTimer.Shared.GetCurrentTimeMicroseconds()` like the
   other tracker call sites.
5. **Removed the `PlaybackState != Playing` early return in `OnPositionTimerTick`.** The
   tracker is now authoritative: it returns null with no anchor and holds the position
   still on a speed-0 anchor, so the gate could only ever suppress updates the tracker had
   already decided were correct. Worse, it was actively harmful — for a conformant pause
   the state stays `playing`, so the gate did nothing useful there, while for the
   SDK-synthesized `Idle` it silently discarded ticks. Keeping it would have re-introduced
   exactly the state coupling this fix removes.

## Behavior matrix

| Scenario | Group state | Progress | Bar |
|---|---|---|---|
| Normal playback | `playing` | fresh, speed 1000 | advances at 1x |
| Conformant pause (README:681, 1448) | `playing` | fresh, speed 0 | frozen at the reported position |
| Conformant resume | `playing` | fresh, speed 1000 | re-anchors and advances |
| Non-conformant resume (`group/update` only) | `stopped` → `playing` | carried forward | `Resume()` re-anchors at the displayed position; advances |
| Stream restart (SDK-synthesized Idle → Playing) | `idle` → `playing` | carried forward | `Freeze()` then `Resume()`; advances from where it stopped |
| Genuine stop | `stopped` | any | `Freeze()`; holds the last position |
| Track change, no fresh progress (README:1445) | any | carried forward | reset to 0, stays put until fresh progress |
| Half-speed pause then resume | `playing` | speed 500, then speed 0 | resumes at 0.5x, the last known non-zero speed |
| Live radio (duration 0) | `playing` | fresh | static server position; `Tick` returns null |

## Tests

`tests/Sendspin.Windows.Services.Tests/Playback/TrackProgressTrackerTests.cs`.

New: `StreamRestart_IdleThenPlayingWithCarriedProgress_ResumesFromSamePosition`,
`ResumeAfterFreeze_WithoutFreshProgress_ResumesFromFrozenPosition`,
`Resume_WithSpeedZeroAnchor_ResumesAtNormalSpeed`,
`Resume_WithSpeedZeroAnchor_ResumesAtLastKnownSpeed`,
`Resume_WhileAlreadyAdvancing_DoesNotReanchor`, `Resume_WithNoPriorProgress_IsNoOp`,
`Resume_AfterPendingTrackChangeReset_IsNoOp`,
`FreshProgressWithSpeed_AdvancesRegardlessOfGroupState`.

Rewritten because they encoded the state-override bug:
`PauseWithFreshProgress_OmittingSpeed_StaysFrozen` →
`ConformantPause_SpeedZeroWhilePlaying_StaysFrozen` (a spec-conformant pause carries speed
0, so the test no longer depends on the state to freeze), and
`PauseWithFreshProgress_ExplicitNormalSpeed_StaysFrozen`, whose entire premise was "the
not-playing state wins over an explicit speed of 1000" — replaced by
`FreshProgressWithSpeed_AdvancesRegardlessOfGroupState`, which asserts the opposite and is
what the spec requires. `ResumeWithoutFreshProgress_StaysAtPausedPosition` and
`ResumeWithFreshProgress_Reanchors` now express the pause as speed 0 rather than a
`PlaybackState.Paused` that the protocol never emits.
