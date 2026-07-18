// <copyright file="TrackProgressTrackerTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

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
        var tracker = new TrackProgressTracker(clockSynchronizer: null);

        Assert.Equal(0.0, tracker.PositionSeconds);
        Assert.Equal(0.0, tracker.DurationSeconds);
        Assert.Null(tracker.Tick(T0));
    }

    [Fact]
    public void FreshProgress_AdoptsPositionAndDuration()
    {
        var tracker = new TrackProgressTracker(clockSynchronizer: null);

        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);

        Assert.Equal(120.0, tracker.PositionSeconds, 3);
        Assert.Equal(300.0, tracker.DurationSeconds, 3);
    }

    [Fact]
    public void Tick_AdvancesWithElapsedTime()
    {
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);

        var position = tracker.Tick(T0 + (2 * Second));

        Assert.Equal(122.0, position!.Value, 3);
        Assert.Equal(122.0, tracker.PositionSeconds, 3);
    }

    [Fact]
    public void Tick_ClampsToDuration()
    {
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
        tracker.ApplyMetadata(Track("A", Progress(299_000, 300_000)), PlaybackState.Playing, T0);

        var position = tracker.Tick(T0 + (5 * Second));

        Assert.Equal(300.0, position!.Value, 3);
    }

    [Fact]
    public void Tick_WithoutDuration_DoesNotAdvance()
    {
        // Preserved behavior: duration-less streams show a static server position.
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
        tracker.ApplyMetadata(Track("A", Progress(120_000, 0)), PlaybackState.Playing, T0);

        Assert.Equal(120.0, tracker.PositionSeconds, 3);
        Assert.Null(tracker.Tick(T0 + (2 * Second)));
        Assert.Equal(120.0, tracker.PositionSeconds, 3);
    }

    [Fact]
    public void SameTrack_CarriedProgress_DoesNotRewindPosition()
    {
        // The SDK's Optional merge carries the same PlaybackProgress instance forward
        // when a message omits the progress field.
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
        var progress = Progress(120_000, 300_000);
        tracker.ApplyMetadata(Track("A", progress), PlaybackState.Playing, T0);
        tracker.Tick(T0 + (2 * Second)); // interpolated to 122

        // A group/update re-emits the merged state: same identity, same progress instance.
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
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
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
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
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
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
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
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
        tracker.ApplyMetadata(Track("A", Progress(295_000, 300_000)), PlaybackState.Playing, T0);

        tracker.ApplyMetadata(Track("A", progress: null), PlaybackState.Playing, T0 + (5 * Second));

        Assert.Equal(0.0, tracker.PositionSeconds);
        Assert.Equal(0.0, tracker.DurationSeconds);
        Assert.Null(tracker.Tick(T0 + (10 * Second)));
    }

    [Fact]
    public void NullMetadata_ClearsEverything()
    {
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);

        tracker.ApplyMetadata(null, PlaybackState.Playing, T0 + Second);

        Assert.Equal(0.0, tracker.PositionSeconds);
        Assert.Equal(0.0, tracker.DurationSeconds);
        Assert.Null(tracker.Tick(T0 + (2 * Second)));
    }

    [Fact]
    public void ResetForPendingTrackChange_ZeroesPositionAndStopsExtrapolation()
    {
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);

        tracker.ResetForPendingTrackChange();

        Assert.Equal(0.0, tracker.PositionSeconds);
        Assert.Null(tracker.Tick(T0 + (2 * Second)));
        Assert.Equal(300.0, tracker.DurationSeconds, 3); // duration label persists until new data
    }

    [Fact]
    public void ResetForPendingTrackChange_CarriedProgressEcho_StaysAtZero()
    {
        // After the optimistic reset, a group/update echoing the pre-change merged state
        // (same identity, same stale progress instance) must not resurrect the old anchor.
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
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
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
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
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);
        tracker.Tick(T0 + (2 * Second));

        tracker.Freeze();

        Assert.Null(tracker.Tick(T0 + (60 * Second)));
        Assert.Equal(122.0, tracker.PositionSeconds, 3); // no jump on later resume
    }

    [Fact]
    public void PlaybackSpeed_ScalesExtrapolation()
    {
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000, speed: 500)), PlaybackState.Playing, T0);

        var position = tracker.Tick(T0 + (2 * Second));

        Assert.Equal(121.0, position!.Value, 3); // 2 s wall x 0.5
    }

    [Fact]
    public void PlaybackSpeedZero_FreezesAtServerPosition()
    {
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000, speed: 0)), PlaybackState.Playing, T0);

        var position = tracker.Tick(T0 + (30 * Second));

        Assert.Equal(120.0, position!.Value, 3);
    }

    [Fact]
    public void PauseWithFreshProgress_OmittingSpeed_StaysFrozen()
    {
        // A pause update often carries fresh progress WITHOUT playback_speed. The paused
        // state must override the speed default (1.0): the bar stays at the paused position.
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);

        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Paused, T0 + Second);

        Assert.Equal(120.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (60 * Second));
        Assert.Equal(120.0, position!.Value, 3);
    }

    [Fact]
    public void ResumeWithoutFreshProgress_StaysAtPausedPosition()
    {
        // Resume often arrives with the carried-forward progress instance only. The bar
        // must stay at the paused position instead of jumping by the pause duration.
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);
        var pausedProgress = Progress(120_000, 300_000);
        tracker.ApplyMetadata(Track("A", pausedProgress), PlaybackState.Paused, T0 + Second);

        tracker.ApplyMetadata(Track("A", pausedProgress), PlaybackState.Playing, T0 + (30 * Second));

        Assert.Equal(120.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (32 * Second));
        Assert.Equal(120.0, position!.Value, 3);
    }

    [Fact]
    public void ResumeWithFreshProgress_Reanchors()
    {
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Paused, T0 + Second);

        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0 + (30 * Second));

        Assert.Equal(120.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (32 * Second));
        Assert.Equal(122.0, position!.Value, 3);
    }

    [Fact]
    public void PauseWithFreshProgress_ExplicitNormalSpeed_StaysFrozen()
    {
        // Even when the paused progress carries an explicit playback_speed of 1000, the
        // not-playing state wins: anchoring uses effective speed 0.
        var tracker = new TrackProgressTracker(clockSynchronizer: null);

        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000, speed: 1000)), PlaybackState.Paused, T0);

        Assert.Equal(120.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (30 * Second));
        Assert.Equal(120.0, position!.Value, 3);
    }

    [Fact]
    public void Freeze_ThenFreshProgress_ResumesExtrapolation()
    {
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
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
        var tracker = new TrackProgressTracker(sync);
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
        var tracker = new TrackProgressTracker(sync);
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
        var tracker = new TrackProgressTracker(sync);
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
        var tracker = new TrackProgressTracker(sync);
        var measuredAt = sync.ClientToServerTime(T0 + (3 * Second));

        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000), timestamp: measuredAt), PlaybackState.Playing, T0);

        Assert.Equal(120.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (2 * Second));
        Assert.Equal(122.0, position!.Value, 3);
    }

    [Fact]
    public void FreshProgressWithoutTrackProgress_UpdatesDurationOnly()
    {
        var tracker = new TrackProgressTracker(clockSynchronizer: null);
        tracker.ApplyMetadata(Track("A", Progress(120_000, 300_000)), PlaybackState.Playing, T0);
        tracker.Tick(T0 + (2 * Second)); // 122

        tracker.ApplyMetadata(Track("A", Progress(null, 310_000)), PlaybackState.Playing, T0 + (2 * Second));

        Assert.Equal(310.0, tracker.DurationSeconds, 3);
        Assert.Equal(122.0, tracker.PositionSeconds, 3);
        var position = tracker.Tick(T0 + (4 * Second));
        Assert.Equal(124.0, position!.Value, 3); // anchor undisturbed
    }

    private static TrackMetadata Track(string title, PlaybackProgress? progress, long? timestamp = null) => new TrackMetadata
    {
        Title = title,
        Artist = "Artist",
        Album = "Album",
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
    /// </summary>
    private sealed class FakeClockSynchronizer : IClockSynchronizer
    {
        public long OffsetMicroseconds { get; set; }

        public bool IsConverged { get; set; }

        public bool HasMinimalSync => IsConverged;

        public double StaticDelayMs { get; set; }

        public long ClientToServerTime(long clientTime) => clientTime + OffsetMicroseconds;

        public long ServerToClientTime(long serverTime) => serverTime - OffsetMicroseconds;

        public void ProcessMeasurement(long t1, long t2, long t3, long t4)
        {
        }

        public void Reset()
        {
        }

        public ClockSyncStatus GetStatus() => new ClockSyncStatus();
    }
}
