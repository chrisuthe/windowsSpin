# Radio / unknown-duration progress — design note

Date: 2026-07-20
Branch: `fix/radio-unknown-duration` (stacks on `fix/seek-bar-freeze`)

## Symptom

Playing a live radio stream, the seek bar row rendered `0:00 ──────────── 0:00` with a
permanently empty bar, and the elapsed time never moved.

## Spec ground truth

Validated against [Sendspin/spec](https://github.com/Sendspin/spec) @ `3632c68`.

- README:1447 — `track_duration`: "total track length in milliseconds, **0 for
  unlimited/unknown duration (e.g., live radio streams)**".
- README:1454-1461 — the canonical progress formula:

  ```python
  calculated_progress = metadata.progress.track_progress + (current_time - metadata.timestamp) * metadata.progress.playback_speed / 1000000
  if metadata.progress.track_duration != 0:
      current_track_progress_ms = max(min(calculated_progress, metadata.progress.track_duration), 0)
  else:
      current_track_progress_ms = max(calculated_progress, 0)
  ```

The `else` branch is decisive: with an unknown duration the position **must still
advance**; only the upper clamp is skipped. Cross-checked against aiosendspin
(`server/roles/metadata/group.py`, `_get_current_track_progress`) and SendspinDroid, whose
protocol layer carries an explicit `unknownDuration_notClampedAbove` test. Our freeze was
the outlier.

## Two independent defects

**A — position never advanced (spec violation).**
`src/Sendspin.Windows.Services/Playback/TrackProgressTracker.cs`, `Tick()` bailed out on
`DurationSeconds <= 0`. That condition has no basis in the spec. `ExtrapolateAt` was
already correct (`DurationSeconds > 0 ? Math.Clamp(...) : Math.Max(0, position)`), but its
zero-duration branch was dead code on the periodic path — reachable only from the one-shot
call in `ApplyMetadata`, so a live stream's position jumped on each fresh progress message
and froze in between. Fix: drop the duration condition; extrapolate whenever an anchor
exists and let `ExtrapolateAt` decide about clamping.

Note the SDK models unknown duration as **null** as well as 0 (`PlaybackProgress.TrackDuration`
is `double?`); the tracker already collapses both to `DurationSeconds == 0`, so one fix
covers both.

**B — the row rendered wrong.** `MainViewModel.ProgressPercent` returned 0 for an unknown
duration and `DurationFormatted` formatted 0 as `"0:00"`. The progress row's visibility
binds to `CurrentTrack` being non-null (MainWindow.xaml), so radio always showed the row —
with a dead bar and a bogus `0:00` total.

## UI decision (product choice, not spec-mandated)

The spec says nothing about presentation. The chosen default treatment for an unbounded
stream:

- elapsed time ticks and is shown normally;
- the duration label reads **"LIVE"** instead of `0:00`;
- the progress bar is **collapsed** — a percentage of an unknown total is meaningless, and
  a permanently empty track reads as a bug.

Implementation keeps this cleanly swappable: a single `MainViewModel.HasKnownDuration`
(`Duration > 0`) is the source of truth for both the label and the bar's `Visibility`
(existing `BoolToVisibilityConverter`). `ProgressPercent` still returns 0 in this mode; it
is simply unused. Known-duration tracks are untouched.
