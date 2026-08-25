// <copyright file="TrackProgressTrackerTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;
using Sendspin.Windows.Services.Playback;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Playback;

public class TrackProgressTrackerTests
{
    private const long T0 = 5_000_000_000_000L; // arbitrary client time (microseconds)
    private const long Second = 1_000_000L;

    [Fact]
    public void NoMetadataYet_DefaultsToZero()
    {
        var tracker = CreateTracker();

        Assert.Equal(0.0, tracker.PositionSeconds);
        Assert.Equal(0.0, tracker.DurationSeconds);
        Assert.Null(tracker.Tick(T0));
    }

    [Fact]
    public void FreshProgress_AdoptsPositionAndDuration()
    {
        var tracker = CreateTracker();

        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);

        Assert.Equal(120.0, tracker.PositionSeconds, 3);
        Assert.Equal(300.0, tracker.DurationSeconds, 3);
    }

    [Fact]
    public void Tick_AdvancesWithElapsedTime()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);

        var position = tracker.Tick(T0 + (2 * Second));

        Assert.Equal(122.0, position!.Value, 3);
        Assert.Equal(122.0, tracker.PositionSeconds, 3);
    }

    [Fact]
    public void Tick_ClampsToDuration()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(299_000, 300_000)), PlaybackState.Playing, T0);

        var position = tracker.Tick(T0 + (5 * Second));

        Assert.Equal(300.0, position!.Value, 3);
    }

    [Fact]
    public void Tick_ZeroDuration_AdvancesUnclamped()
    {
        // Spec (README:1447, 1454-1461): track_duration = 0 means unlimited/unknown (live
        // radio). The position MUST still advance; only the upper clamp is skipped.
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 0)), PlaybackState.Playing, T0);

        Assert.Equal(120.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (5 * Second));

        Assert.Equal(125.0, position!.Value, 3);
        var later = tracker.Tick(T0 + (10 * Second));
        Assert.Equal(130.0, later!.Value, 3);
    }

    [Fact]
    public void Tick_NullDuration_AdvancesUnclamped()
    {
        // The SDK models unknown duration as null as well as 0; both must advance.
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, durationMs: null)), PlaybackState.Playing, T0);

        Assert.Equal(0.0, tracker.DurationSeconds);
        var position = tracker.Tick(T0 + (5 * Second));

        Assert.Equal(125.0, position!.Value, 3);
    }

    [Fact]
    public void Tick_UnknownDuration_GrowsWithoutBound()
    {
        // No plausible track length caps a live stream: two hours in, still counting.
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(0, 0)), PlaybackState.Playing, T0);

        var position = tracker.Tick(T0 + (7200 * Second));

        Assert.Equal(7200.0, position!.Value, 3);
    }

    [Fact]
    public void Tick_UnknownDuration_ScalesWithPlaybackSpeed()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 0, speed: 500)), PlaybackState.Playing, T0);

        var position = tracker.Tick(T0 + (2 * Second));

        Assert.Equal(121.0, position!.Value, 3); // 2 s wall x 0.5
    }

    [Fact]
    public void Tick_UnknownDuration_SpeedZeroStaysFrozen()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 0, speed: 0)), PlaybackState.Playing, T0);

        var position = tracker.Tick(T0 + (30 * Second));

        Assert.Equal(120.0, position!.Value, 3);
    }

    [Fact]
    public void SameTrack_CarriedProgress_DoesNotRewindPosition()
    {
        // The SDK's Optional merge carries the same PlaybackProgress instance forward
        // when a message omits the progress field.
        var tracker = CreateTracker();
        var progress = Progress(120_000, 300_000);
        tracker.ApplyMetadata(Track("A", progress), PlaybackState.Playing, T0);
        tracker.Tick(T0 + (2 * Second)); // interpolated to 122

        // A group-state event re-emits the merged state: same identity, same progress
        // instance (metadata travels in server/state; group/update carries none).
        tracker.ApplyMetadata(Track("A", progress), PlaybackState.Playing, T0 + (2 * Second));

        Assert.Equal(122.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (4 * Second));
        Assert.Equal(124.0, position!.Value, 3); // anchor undisturbed
    }

    [Fact]
    public void TrackChange_WithCarriedStaleProgress_ResetsToZero()
    {
        // The reported bug: track changes but the merged metadata still carries the OLD
        // track's progress instance (server sent no progress field for the new track).
        var tracker = CreateTracker();
        var stale = Progress(120_000, 300_000);
        tracker.ApplyMetadata(Track("A", stale), PlaybackState.Playing, T0);

        tracker.ApplyMetadata(Track("B", stale), PlaybackState.Playing, T0 + Second);

        Assert.Equal(0.0, tracker.PositionSeconds);
        Assert.Equal(0.0, tracker.DurationSeconds);
        Assert.Null(tracker.Tick(T0 + (10 * Second))); // frozen until fresh progress
    }

    [Fact]
    public void TrackChange_WithFreshProgress_AdoptsNewTrackPosition()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);

        tracker.ApplyMetadata(Track("B", Progress(0, 200_000)), PlaybackState.Playing, T0 + Second);

        Assert.Equal(0.0, tracker.PositionSeconds, 3);
        Assert.Equal(200.0, tracker.DurationSeconds, 3);
        var position = tracker.Tick(T0 + (3 * Second));
        Assert.Equal(2.0, position!.Value, 3);
    }

    [Fact]
    public void TrackChange_WithNullProgress_ResetsToZero()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);

        tracker.ApplyMetadata(Track("B", progress: null), PlaybackState.Playing, T0 + Second);

        Assert.Equal(0.0, tracker.PositionSeconds);
        Assert.Equal(0.0, tracker.DurationSeconds);
        Assert.Null(tracker.Tick(T0 + (10 * Second)));
    }

    [Fact]
    public void ExplicitNullProgress_SameTrack_ClearsPosition()
    {
        // Matches the CLI's update_metadata: explicit null clears progress AND duration.
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(295_000, 300_000)), PlaybackState.Playing, T0);

        tracker.ApplyMetadata(Track("A", progress: null), PlaybackState.Playing, T0 + (5 * Second));

        Assert.Equal(0.0, tracker.PositionSeconds);
        Assert.Equal(0.0, tracker.DurationSeconds);
        Assert.Null(tracker.Tick(T0 + (10 * Second)));
    }

    [Fact]
    public void NullMetadata_ClearsEverything()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);

        tracker.ApplyMetadata(null, PlaybackState.Playing, T0 + Second);

        Assert.Equal(0.0, tracker.PositionSeconds);
        Assert.Equal(0.0, tracker.DurationSeconds);
        Assert.Null(tracker.Tick(T0 + (2 * Second)));
    }

    [Fact]
    public void ResetForPendingTrackChange_ZeroesPositionAndStopsExtrapolation()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);

        tracker.ResetForPendingTrackChange();

        Assert.Equal(0.0, tracker.PositionSeconds);
        Assert.Null(tracker.Tick(T0 + (2 * Second)));
        Assert.Equal(300.0, tracker.DurationSeconds, 3); // duration label persists until new data
    }

    [Fact]
    public void ResetForPendingTrackChange_CarriedProgressEcho_StaysAtZero()
    {
        // After the optimistic reset, a group-state event echoing the pre-change merged
        // state (same identity, same stale progress instance) must not resurrect the
        // old anchor.
        var tracker = CreateTracker();
        var progress = Progress(120_000, 300_000);
        tracker.ApplyMetadata(Track("A", progress), PlaybackState.Playing, T0);

        tracker.ResetForPendingTrackChange();
        tracker.ApplyMetadata(Track("A", progress), PlaybackState.Playing, T0 + Second);

        Assert.Equal(0.0, tracker.PositionSeconds);
        Assert.Null(tracker.Tick(T0 + (2 * Second)));
    }

    [Fact]
    public void ResetForPendingTrackChange_FreshProgressReanchors()
    {
        // "Previous" can legitimately restart the SAME track at 0 — identity unchanged,
        // so only fresh progress can re-anchor after the optimistic reset.
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);

        tracker.ResetForPendingTrackChange();
        tracker.ApplyMetadata(Track("A", Progress(500, 300_000)), PlaybackState.Playing, T0 + Second);

        Assert.Equal(0.5, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (2 * Second));
        Assert.Equal(1.5, position!.Value, 3);
    }

    [Fact]
    public void Freeze_StopsExtrapolation_KeepsLastPosition()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);
        tracker.Tick(T0 + (2 * Second));

        tracker.Freeze();

        Assert.Null(tracker.Tick(T0 + (60 * Second)));
        Assert.Equal(122.0, tracker.PositionSeconds, 3); // no jump on later resume
    }

    [Fact]
    public void PlaybackSpeed_ScalesExtrapolation()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000, speed: 500)), PlaybackState.Playing, T0);

        var position = tracker.Tick(T0 + (2 * Second));

        Assert.Equal(121.0, position!.Value, 3); // 2 s wall x 0.5
    }

    [Fact]
    public void PlaybackSpeedZero_FreezesAtServerPosition()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000, speed: 0)), PlaybackState.Playing, T0);

        var position = tracker.Tick(T0 + (30 * Second));

        Assert.Equal(120.0, position!.Value, 3);
    }

    [Fact]
    public void ConformantPause_SpeedZeroWhilePlaying_StaysFrozen()
    {
        // Spec: there is no 'paused' playback_state (README:681); playback_speed = 0 is the
        // protocol's only pause signal (README:1448) and stays inside a 'playing' group.
        // Deriving the freeze from the speed alone must therefore still freeze a real pause.
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);

        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000, speed: 0)), PlaybackState.Playing, T0 + Second);

        Assert.Equal(120.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (60 * Second));
        Assert.Equal(120.0, position!.Value, 3);
    }

    [Fact]
    public void ResumeWithoutFreshProgress_StaysAtPausedPosition()
    {
        // Resume can arrive with the carried-forward progress instance only. The bar must
        // stay at the paused position instead of jumping by the pause duration.
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);
        var pausedProgress = Progress(120_000, 300_000, speed: 0);
        tracker.ApplyMetadata(Track("A", pausedProgress), PlaybackState.Playing, T0 + Second);

        tracker.ApplyMetadata(Track("A", pausedProgress), PlaybackState.Playing, T0 + (30 * Second));

        Assert.Equal(120.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (32 * Second));
        Assert.Equal(120.0, position!.Value, 3);
    }

    [Fact]
    public void ResumeWithFreshProgress_Reanchors()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000, speed: 0)), PlaybackState.Playing, T0 + Second);

        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0 + (30 * Second));

        Assert.Equal(120.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (32 * Second));
        Assert.Equal(122.0, position!.Value, 3);
    }

    [Fact]
    public void FreshProgressWithSpeed_AdvancesRegardlessOfGroupState()
    {
        // The group playback state no longer overrides the progress object's speed: per
        // spec (README:1446-1448) playback_speed is required whenever progress is sent and
        // is the authority on whether the position advances. A carried-forward group state
        // (e.g. the SDK's synthesized Idle on stream/end) must not silently freeze fresh,
        // explicitly-advancing progress.
        var tracker = CreateTracker();

        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000, speed: 1000)), PlaybackState.Idle, T0);

        Assert.Equal(120.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (30 * Second));
        Assert.Equal(150.0, position!.Value, 3);
    }

    [Fact]
    public void Freeze_ThenFreshProgress_ResumesExtrapolation()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);
        tracker.Tick(T0 + (2 * Second)); // 122

        tracker.Freeze();
        tracker.ApplyMetadata(Track("A", Progress(130_000, 300_000)), PlaybackState.Playing, T0 + (5 * Second));

        Assert.Equal(130.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (7 * Second));
        Assert.Equal(132.0, position!.Value, 3);
    }

    [Fact]
    public void ConvergedClock_AnchorsAtServerTimestamp()
    {
        // Progress was measured 200 ms (server time) before we received it; the displayed
        // position starts 200 ms ahead of track_progress (network-delay compensation).
        var sync = new FakeClockSynchronizer { OffsetMicroseconds = 7_000_000_000_000L, IsConverged = true };
        var tracker = CreateTracker(sync);
        var measuredAt = sync.ClientToServerTime(T0 - 200_000);

        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000), timestamp: measuredAt), PlaybackState.Playing, T0);

        Assert.Equal(120.2, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (2 * Second));
        Assert.Equal(122.2, position!.Value, 3);
    }

    [Fact]
    public void UnconvergedClock_FallsBackToReceiptAnchor()
    {
        var sync = new FakeClockSynchronizer { OffsetMicroseconds = 7_000_000_000_000L, IsConverged = false };
        var tracker = CreateTracker(sync);
        var measuredAt = sync.ClientToServerTime(T0 - 200_000);

        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000), timestamp: measuredAt), PlaybackState.Playing, T0);

        Assert.Equal(120.0, tracker.PositionSeconds, 3);
    }

    [Fact]
    public void ImplausiblyOldTimestamp_FallsBackToReceiptAnchor()
    {
        // The SDK merges metadata fields independently, so fresh progress can arrive next
        // to a stale carried-forward timestamp. Distrust anything older than a few seconds.
        var sync = new FakeClockSynchronizer { OffsetMicroseconds = 7_000_000_000_000L, IsConverged = true };
        var tracker = CreateTracker(sync);
        var measuredAt = sync.ClientToServerTime(T0 - (10 * Second));

        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000), timestamp: measuredAt), PlaybackState.Playing, T0);

        Assert.Equal(120.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (2 * Second));
        Assert.Equal(122.0, position!.Value, 3);
    }

    [Fact]
    public void FutureTimestamp_FallsBackToReceiptAnchor()
    {
        var sync = new FakeClockSynchronizer { OffsetMicroseconds = 7_000_000_000_000L, IsConverged = true };
        var tracker = CreateTracker(sync);
        var measuredAt = sync.ClientToServerTime(T0 + (3 * Second));

        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000), timestamp: measuredAt), PlaybackState.Playing, T0);

        Assert.Equal(120.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (2 * Second));
        Assert.Equal(122.0, position!.Value, 3);
    }

    [Fact]
    public void FreshProgressWithoutTrackProgress_UpdatesDurationOnly()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);
        tracker.Tick(T0 + (2 * Second)); // 122

        tracker.ApplyMetadata(Track("A", Progress(null, 310_000)), PlaybackState.Playing, T0 + (2 * Second));

        Assert.Equal(310.0, tracker.DurationSeconds, 3);
        Assert.Equal(122.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (4 * Second));
        Assert.Equal(124.0, position!.Value, 3); // anchor undisturbed
    }

    [Fact]
    public void FreshProgress_NullDuration_KeepsPreviousDuration()
    {
        // Duration tri-state: the SDK models unknown duration as null, so fresh progress
        // with TrackDuration = null keeps the previously known duration.
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);

        tracker.ApplyMetadata(Track("A", Progress(130_000, durationMs: null)), PlaybackState.Playing, T0 + Second);

        Assert.Equal(300.0, tracker.DurationSeconds, 3);
        Assert.Equal(130.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (3 * Second));
        Assert.Equal(132.0, position!.Value, 3); // re-anchored and still advancing
    }

    [Fact]
    public void FreshProgress_ZeroDuration_SetsDurationToZero()
    {
        // Unlike null (unknown), an explicit 0 is a value and must be adopted.
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);

        tracker.ApplyMetadata(Track("A", Progress(130_000, 0)), PlaybackState.Playing, T0 + Second);

        Assert.Equal(0.0, tracker.DurationSeconds);
        Assert.Equal(130.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (3 * Second));
        Assert.Equal(132.0, position!.Value, 3); // unbounded from here on
    }

    [Fact]
    public void NullDurationFromTrackStart_AdvancesFromServerPosition()
    {
        // Radio case: the track never reports a duration. The position still advances,
        // and the duration stays 0 so the UI can render it as unbounded.
        var tracker = CreateTracker();

        tracker.ApplyMetadata(Track("A", Progress(120_000, durationMs: null)), PlaybackState.Playing, T0);

        Assert.Equal(0.0, tracker.DurationSeconds);
        Assert.Equal(120.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (2 * Second));
        Assert.Equal(122.0, position!.Value, 3);
    }

    [Fact]
    public void OutputDelay_DoesNotShiftDisplayAnchor()
    {
        // ServerToClientTime targets audio scheduling and subtracts the configured static
        // delay; the display anchor converts with the clock offset alone, so the seek bar
        // reflects measurement time whatever the hardware delay is set to.
        var sync = new FakeClockSynchronizer { OffsetMicroseconds = 7_000_000_000_000L, IsConverged = true, OutputDelayMs = 400 };
        var tracker = CreateTracker(sync);
        var measuredAt = sync.ClientToServerTime(T0 - 200_000);

        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000), timestamp: measuredAt), PlaybackState.Playing, T0);

        Assert.Equal(120.2, tracker.PositionSeconds, 3); // identical to the zero-delay case
    }

    [Fact]
    public void NegativeOutputDelay_StillUsesServerAnchor()
    {
        // A negative delay used to push every converted anchor into the future, tripping
        // the plausibility guard so the spec-anchor path silently never engaged.
        var sync = new FakeClockSynchronizer { OffsetMicroseconds = 7_000_000_000_000L, IsConverged = true, OutputDelayMs = -400 };
        var tracker = CreateTracker(sync);
        var measuredAt = sync.ClientToServerTime(T0 - 200_000);

        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000), timestamp: measuredAt), PlaybackState.Playing, T0);

        Assert.Equal(120.2, tracker.PositionSeconds, 3);
    }

    [Fact]
    public void IdentityCollision_DelimiterAmbiguity_TreatedAsDifferentTracks()
    {
        // "A|B" + "C" and "A" + "B|C" collided under a joined-string identity; the tuple
        // identity must treat them as different tracks and reset the stale position.
        var tracker = CreateTracker();
        var stale = Progress(120_000, 300_000);
        tracker.ApplyMetadata(Track("A|B", stale, artist: "C"), PlaybackState.Playing, T0);

        tracker.ApplyMetadata(Track("A", stale, artist: "B|C"), PlaybackState.Playing, T0 + Second);

        Assert.Equal(0.0, tracker.PositionSeconds);
        Assert.Equal(0.0, tracker.DurationSeconds);
        Assert.Null(tracker.Tick(T0 + (2 * Second)));
    }

    [Fact]
    public void AnchorAge_ExactlyAtBoundary_UsesServerAnchor()
    {
        var sync = new FakeClockSynchronizer { OffsetMicroseconds = 7_000_000_000_000L, IsConverged = true };
        var tracker = CreateTracker(sync);
        var measuredAt = sync.ClientToServerTime(T0 - (5 * Second));

        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000), timestamp: measuredAt), PlaybackState.Playing, T0);

        Assert.Equal(125.0, tracker.PositionSeconds, 3); // 5 s of network-delay compensation
    }

    [Fact]
    public void NegativePlaybackSpeed_ClampsToZero()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000, speed: -500)), PlaybackState.Playing, T0);

        var position = tracker.Tick(T0 + (30 * Second));

        Assert.Equal(120.0, position!.Value, 3); // the bar never runs backwards
    }

    [Fact]
    public void NegativeProgressAndDuration_ClampToZero()
    {
        var tracker = CreateTracker();

        tracker.ApplyMetadata(Track("A", Progress(-5_000, -10_000)), PlaybackState.Playing, T0);

        Assert.Equal(0.0, tracker.PositionSeconds);
        Assert.Equal(0.0, tracker.DurationSeconds);

        // A non-positive duration is indistinguishable from "unknown", so the position
        // advances from the clamped zero rather than freezing.
        var position = tracker.Tick(T0 + (2 * Second));
        Assert.Equal(2.0, position!.Value, 3);
    }

    [Fact]
    public void StreamRestart_IdleThenPlayingWithCarriedProgress_ResumesFromSamePosition()
    {
        // The reported freeze: a stream restart (format renegotiation, or a server that
        // implements seek as stream/end + stream/start) makes the SDK synthesize Idle then
        // Playing, BOTH carrying the same PlaybackProgress instance. Nothing is fresh, so
        // only Resume() can rebuild the anchor Freeze() dropped.
        var tracker = CreateTracker();
        var progress = Progress(120_000, 300_000, speed: 1000);
        tracker.ApplyMetadata(Track("A", progress), PlaybackState.Playing, T0);

        tracker.Tick(T0 + Second); // the 250 ms UI timer has carried the bar to 121 s
        tracker.ApplyMetadata(Track("A", progress), PlaybackState.Idle, T0 + Second);
        tracker.Freeze();
        tracker.ApplyMetadata(Track("A", progress), PlaybackState.Playing, T0 + (2 * Second));
        tracker.Resume(T0 + (2 * Second));

        // Resumed from where the bar was (121 s at the freeze), not jumped by the gap.
        Assert.Equal(121.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (4 * Second));
        Assert.Equal(123.0, position!.Value, 3);
    }

    [Fact]
    public void ResumeAfterFreeze_WithoutFreshProgress_ResumesFromFrozenPosition()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);
        tracker.Freeze();

        tracker.Resume(T0 + (30 * Second));

        // No jump by the 30 s of paused wall time.
        Assert.Equal(120.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (32 * Second));
        Assert.Equal(122.0, position!.Value, 3);
    }

    [Fact]
    public void Resume_WithSpeedZeroAnchor_ResumesAtNormalSpeed()
    {
        // A speed-0 anchor is frozen but present; Resume must restart it at 1.0 rather
        // than leaving it stuck at 0.
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000, speed: 0)), PlaybackState.Playing, T0);

        tracker.Resume(T0 + (30 * Second));

        var position = tracker.Tick(T0 + (32 * Second));
        Assert.Equal(122.0, position!.Value, 3);
    }

    [Fact]
    public void Resume_WithSpeedZeroAnchor_ResumesAtLastKnownSpeed()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000, speed: 500)), PlaybackState.Playing, T0);
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000, speed: 0)), PlaybackState.Playing, T0 + Second);

        tracker.Resume(T0 + (30 * Second));

        var position = tracker.Tick(T0 + (32 * Second));
        Assert.Equal(121.0, position!.Value, 3); // 2 s wall x 0.5
    }

    [Fact]
    public void Resume_WhileAlreadyAdvancing_DoesNotReanchor()
    {
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);

        tracker.Resume(T0 + (2 * Second));

        var position = tracker.Tick(T0 + (4 * Second));
        Assert.Equal(124.0, position!.Value, 3); // original anchor kept
    }

    [Fact]
    public void Resume_WithNoPriorProgress_IsNoOp()
    {
        var tracker = CreateTracker();

        tracker.Resume(T0);

        Assert.Equal(0.0, tracker.PositionSeconds);
        Assert.Equal(0.0, tracker.DurationSeconds);
        Assert.Null(tracker.Tick(T0 + (10 * Second)));
    }

    [Fact]
    public void Resume_AfterPendingTrackChangeReset_IsNoOp()
    {
        // The optimistic reset deliberately stops extrapolation until the server confirms
        // the new track with fresh progress; Resume must not restart it from zero.
        var tracker = CreateTracker();
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);
        tracker.ResetForPendingTrackChange();

        tracker.Resume(T0 + Second);

        Assert.Equal(0.0, tracker.PositionSeconds);
        Assert.Null(tracker.Tick(T0 + (3 * Second)));
    }

    private static TrackProgressTracker CreateTracker(IClockSynchronizer? clockSynchronizer = null) =>
        new TrackProgressTracker(clockSynchronizer, NullLogger<TrackProgressTracker>.Instance);

    private static TrackMetadata Track(string title, PlaybackProgress? progress, long? timestamp = null, string? artist = "Artist", string? album = "Album") => new TrackMetadata
    {
        Title = title,
        Artist = artist,
        Album = album,
        Progress = progress,
        Timestamp = timestamp,
    };

    private static PlaybackProgress Progress(double? progressMs, double? durationMs, double? speed = null) => new PlaybackProgress
    {
        TrackProgress = progressMs,
        TrackDuration = durationMs,
        PlaybackSpeed = speed,
    };

    /// <summary>
    /// Minimal clock synchronizer with a fixed offset: server = client + offset.
    /// Mirrors the real Kalman contract where <see cref="ServerToClientTime"/> also
    /// subtracts the configured output delay (audio-scheduling compensation), while
    /// <see cref="ServerToClientTimeUncompensated"/> and <see cref="ClientToServerTime"/>
    /// are the pure domain shift — exact inverses of each other, used to fabricate
    /// server-side measurement timestamps and convert them back.
    /// </summary>
    private sealed class FakeClockSynchronizer : IClockSynchronizer
    {
        public long OffsetMicroseconds { get; set; }

        public bool IsConverged { get; set; }

        public bool HasMinimalSync => IsConverged;

        public double OutputDelayMs { get; set; }

        public long ClientToServerTime(long clientTime) => clientTime + OffsetMicroseconds;

        public long ServerToClientTime(long serverTime) => serverTime - OffsetMicroseconds - (long)(OutputDelayMs * 1000);

        public long ServerToClientTimeUncompensated(long serverTime) => serverTime - OffsetMicroseconds;

        public void ProcessMeasurement(long t1, long t2, long t3, long t4)
        {
        }

        public void Reset()
        {
        }

        public ClockSyncStatus GetStatus() => new ClockSyncStatus();
    }
}
