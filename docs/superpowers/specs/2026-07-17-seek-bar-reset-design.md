# Seek Bar Reset on Track Change — Design

**Date**: 2026-07-17
**Status**: Implemented
**Bug**: Clicking previous/next at e.g. 120s into a track leaves the seek bar at ~120s; the
interpolation timer keeps adding wall-clock time against the old duration, so the bar creeps
and pins near the end until the server happens to send a progress object.

## Root Causes

1. **No local reset on track change.** Neither group-state handler nor the track-change
   detection in `OnCurrentTrackChanged` resets Position/Duration when the track identity
   changes without an accompanying progress object.
2. **Handler drift.** The client-initiated (primary) handler `OnManualClientGroupStateChanged`
   never got the progress tri-state handling the host-mode handler `OnGroupStateChanged` has —
   two copies of the same merge logic diverged.
3. **Explicit-null progress never zeroes Position.** Per the tri-state (present-null = track
   ended), both handlers at best stopped interpolation but left the stale Position on screen.
   The Python CLI clears progress *and* duration in this case (`update_metadata` in
   `sendspin/tui/app.py`).

An SDK-level detail sharpens cause 1: `SendspinClientService.HandleServerState` (declared in
`SendSpinClient.cs`) merges metadata with
`Optional<T>` semantics and **carries the previous `PlaybackProgress` instance forward** when
the field is absent. A track change whose `server/state` lacks progress therefore arrives as
*new identity + the old track's stale progress object*, and the old code re-anchored to that
stale position. Both connection modes flow through `SendspinClientService` (the host service
wraps one per incoming connection), so the same semantics apply everywhere.

## Fix

Extract one shared, plain, unit-testable class — `TrackProgressTracker`
(`src/Sendspin.Windows.Services/Playback/`) — that owns all position/duration/anchor state.
`MainViewModel` becomes a thin caller: both group-state handlers call `ApplyMetadata`, the
250 ms UI timer calls `Tick`, next/previous call `ResetForPendingTrackChange`, and the
pause/stop transition calls `Freeze`. `ApplyMetadata` also receives the playback state from
the **same group update** (not the VM property): fresh progress arriving while the group is
not playing anchors with effective speed 0, so a pause update cannot silently un-freeze the
bar and the handlers' assignment order (`PlaybackState` before/after progress) stops
mattering. Fresh progress is distinguished from carried-forward
stale progress by reference: the SDK deserializes a **new** `PlaybackProgress` instance
exactly when the server sent the field, and carries the **same** instance forward when absent.

## Behavior Matrix

| Scenario | Old behavior | New behavior |
|---|---|---|
| Same-track update with fresh progress | Adopt position/duration, re-anchor | Same |
| Same-track update, progress absent (carried ref) | Re-anchored to stale server position (bar rewound) | Keep current interpolated position untouched |
| Track change **with** fresh progress | Adopt (worked) | Reset, then adopt (same result) |
| Track change **without** progress (carried stale ref or null) | Kept old position; timer crept vs old duration (**the bug**) | Position 0, Duration 0, frozen until first fresh progress |
| Explicit-null progress, same track (track ended) | Host: kept final position; manual: kept anchor | Position 0, Duration 0, frozen (matches CLI clearing) |
| Previous/Next clicked | Nothing until server progress | Optimistic: Position 0 immediately, frozen; next fresh progress re-anchors (covers server restarting the *same* track at 0) |
| Pause update **with** fresh progress (speed absent or explicit) | (initial tracker implementation) anchor re-armed at the progress speed (default 1.0), undoing `Freeze`; a later resume without fresh progress jumped the bar by the whole pause duration | Anchored at the paused position with effective speed 0 — the update's playback state overrides the progress speed |
| Pause → resume without fresh progress | Frozen at last position | Frozen at the paused position until fresh progress re-anchors (`Freeze` clears the anchor; not-playing updates anchor at speed 0) |
| Metadata null (track cleared) | Zeroed | Same, via tracker |
| Paused progress with `playback_speed` 0 | n/a | Anchored but frozen (state and speed agree) |

Preserved quirk: extrapolation still only advances when duration > 0 (same gate as the old
timer tick), so duration-less streams keep today's static display.

## Extrapolation Formula (spec adoption — full branch)

The SDK 9.1.0 surface exposes everything the spec formula needs, so it is implemented
app-side with no SDK changes:

- `PlaybackProgress.PlaybackSpeed` (double?, 1000 = 1.0x) — scales elapsed time; 0 freezes.
- `TrackMetadata.Timestamp` (long?, server-domain µs) — when the progress was measured.
- `IClockSynchronizer.ServerToClientTime` / `IsConverged` — server→client conversion.

On fresh progress the anchor is `ServerToClientTime(metadata.Timestamp)` (network-delay
compensation per spec), displayed position = anchor position + elapsed × speed, clamped to
[0, duration]. Fallback to receipt-time anchoring when: no timestamp, clock not converged,
converted time implausibly old (> 5 s before receipt — guards against a stale carried-forward
timestamp merged next to fresh progress), or converted time in the future. Speed defaults to
1.0 when absent, but the update's playback state overrides it: any not-playing state anchors
with effective speed 0.

## Out of Scope / Deferred

- The SDK never clears `GroupState.Metadata` on group switch (the CLI does). A group switch
  to a group playing an identically-titled track could briefly show stale progress; fixing
  that needs an SDK change (e.g. surfacing group identity with metadata resets).
- Advancing the position label for duration-less streams (radio) — behavior intentionally
  unchanged.
