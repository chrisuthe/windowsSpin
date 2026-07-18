// <copyright file="TrackProgressTracker.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.Windows.Services.Playback;

/// <summary>
/// Tracks the displayed playback position and duration for the seek bar. Owns the anchor
/// state used to extrapolate the position between server progress updates and implements
/// the Sendspin spec's progress semantics on top of the SDK's merged
/// <see cref="GroupState"/> metadata snapshots.
/// </summary>
/// <remarks>
/// <para>
/// The SDK merges <c>server/state</c> metadata with <c>Optional&lt;T&gt;</c> semantics and
/// carries the previous <see cref="PlaybackProgress"/> instance forward when the field is
/// absent from a message. This class therefore distinguishes <em>fresh</em> progress (a new
/// instance, meaning the server actually sent the field) from <em>carried-forward</em>
/// progress by reference. Carried progress never re-anchors the position: on the same track
/// it leaves the extrapolation undisturbed, and on a track-identity change it is treated as
/// no progress at all — the position resets to zero until the first fresh progress arrives.
/// </para>
/// <para>
/// Extrapolation follows the spec formula: displayed position is the anchored
/// <c>track_progress</c> plus elapsed time scaled by <c>playback_speed</c> (0 freezes the
/// position), clamped to <c>[0, duration]</c>. The playback state carried by the same
/// update overrides the progress object's speed: whenever the group is not playing, fresh
/// progress anchors with an effective speed of 0, so a pause update cannot silently
/// un-freeze the bar and a later resume without fresh progress stays at the paused
/// position instead of jumping forward by the pause duration. When the clock synchronizer
/// has converged and the metadata carries a plausible server timestamp, the anchor is that
/// timestamp converted to client time (compensating network delay); otherwise the anchor
/// falls back to the receipt time.
/// </para>
/// <para>
/// All times are client-domain microseconds from <see cref="IHighPrecisionTimer"/>.
/// The class is not thread-safe; callers are expected to invoke it from the UI dispatcher.
/// </para>
/// </remarks>
public sealed class TrackProgressTracker
{
    /// <summary>
    /// Maximum age of a converted metadata timestamp before it is considered implausible
    /// and the anchor falls back to receipt time. Guards against a stale carried-forward
    /// timestamp arriving next to fresh progress (the SDK merges metadata fields
    /// independently).
    /// </summary>
    private const long MaxAnchorAgeMicroseconds = 5_000_000;

    private readonly IClockSynchronizer? _clockSynchronizer;
    private readonly ILogger<TrackProgressTracker> _logger;

    private string? _trackIdentity;
    private PlaybackProgress? _lastProgress;
    private Anchor? _anchor;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackProgressTracker"/> class.
    /// </summary>
    /// <param name="clockSynchronizer">Optional clock synchronizer used to anchor fresh
    /// progress at its server timestamp. When null or not converged, anchors fall back to
    /// the receipt time.</param>
    /// <param name="logger">Logger for anchor and reset diagnostics (Debug level; these
    /// fire on every progress update).</param>
    public TrackProgressTracker(IClockSynchronizer? clockSynchronizer, ILogger<TrackProgressTracker> logger)
    {
        _clockSynchronizer = clockSynchronizer;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current displayed position in seconds.
    /// </summary>
    public double PositionSeconds { get; private set; }

    /// <summary>
    /// Gets the current track duration in seconds (0 when unknown).
    /// </summary>
    public double DurationSeconds { get; private set; }

    /// <summary>
    /// Applies a merged metadata snapshot from a group state update.
    /// </summary>
    /// <param name="metadata">The merged track metadata, or null when no track is active.</param>
    /// <param name="playbackState">The group playback state carried by the same update.
    /// When not <see cref="PlaybackState.Playing"/>, fresh progress anchors with an
    /// effective speed of 0 regardless of the progress object's <c>playback_speed</c>.</param>
    /// <param name="nowMicroseconds">Current client time in microseconds.</param>
    public void ApplyMetadata(TrackMetadata? metadata, PlaybackState playbackState, long nowMicroseconds)
    {
        if (metadata is null)
        {
            _trackIdentity = null;
            _lastProgress = null;
            ResetPosition();
            return;
        }

        // Same identity convention as the track-change notification logic: TrackMetadata
        // has no track id/URI, so title+artist+album is the best available identity.
        var identity = $"{metadata.Title}|{metadata.Artist}|{metadata.Album}";
        var identityChanged = identity != _trackIdentity;
        var previousIdentity = _trackIdentity;
        _trackIdentity = identity;

        var progress = metadata.Progress;
        var isFresh = progress is not null && !ReferenceEquals(progress, _lastProgress);
        _lastProgress = progress;

        if (identityChanged)
        {
            // New track: never trust position state that belonged to the previous track.
            _logger.LogDebug(
                "Track identity changed ({PreviousIdentity} -> {Identity}); resetting seek bar position",
                previousIdentity ?? "(none)",
                identity);
            ResetPosition();
        }

        if (progress is null)
        {
            // Explicit-null progress (track ended) or no progress yet: clear position and
            // duration, matching the CLI's update_metadata handling.
            _logger.LogDebug("Progress is null (track ended or none yet); clearing position and duration");
            ResetPosition();
            return;
        }

        if (!isFresh)
        {
            // Carried-forward instance: the server did not send progress in this message.
            // Same track: leave the running extrapolation undisturbed (prevents rewinding
            // to a stale position). New track: stay at the reset applied above.
            return;
        }

        if (progress.TrackDuration.HasValue)
        {
            DurationSeconds = Math.Max(0, progress.TrackDuration.Value / 1000.0);
        }

        if (progress.TrackProgress.HasValue)
        {
            // The update's playback state overrides the progress object's speed: while not
            // playing the effective speed is 0, so a pause update carrying fresh progress
            // (with or without playback_speed) anchors frozen, and a later resume without
            // fresh progress stays at the paused position instead of jumping forward by
            // the pause duration.
            var speedFactor = playbackState == PlaybackState.Playing
                ? Math.Max(0, (progress.PlaybackSpeed ?? 1000.0) / 1000.0)
                : 0;
            _anchor = new Anchor(
                ResolveAnchor(metadata.Timestamp, nowMicroseconds),
                Math.Max(0, progress.TrackProgress.Value / 1000.0),
                speedFactor);
            PositionSeconds = ExtrapolateAt(_anchor.Value, nowMicroseconds);
        }
    }

    /// <summary>
    /// Optimistically resets the position to zero when the user requests a track change
    /// (next/previous), pending server confirmation. Extrapolation stops until the next
    /// fresh progress re-anchors it; the known duration is kept for display. This also
    /// covers the server restarting the <em>same</em> track at zero on "previous", where
    /// the track identity never changes.
    /// </summary>
    public void ResetForPendingTrackChange()
    {
        _logger.LogDebug(
            "Optimistic seek bar reset pending track change (position was {PositionSeconds}s)",
            PositionSeconds);
        PositionSeconds = 0;
        _anchor = null;

        // _lastProgress is intentionally kept: a group/update echoing the pre-change
        // state carries the same stale progress instance and must not look fresh.
    }

    /// <summary>
    /// Stops extrapolation when playback leaves the playing state, keeping the last
    /// displayed position. Resuming without fresh progress stays frozen rather than
    /// jumping forward by the paused duration.
    /// </summary>
    public void Freeze()
    {
        _anchor = null;
    }

    /// <summary>
    /// Recomputes the extrapolated position. Call periodically while playing.
    /// </summary>
    /// <param name="nowMicroseconds">Current client time in microseconds.</param>
    /// <returns>The updated position in seconds, or null when there is nothing to advance
    /// (no anchor, or the duration is unknown).</returns>
    public double? Tick(long nowMicroseconds)
    {
        if (_anchor is not { } anchor || DurationSeconds <= 0)
        {
            return null;
        }

        PositionSeconds = ExtrapolateAt(anchor, nowMicroseconds);
        return PositionSeconds;
    }

    private void ResetPosition()
    {
        PositionSeconds = 0;
        DurationSeconds = 0;
        _anchor = null;
    }

    private double ExtrapolateAt(Anchor anchor, long nowMicroseconds)
    {
        var elapsedSeconds = Math.Max(0, nowMicroseconds - anchor.AtMicroseconds) / 1_000_000.0;
        var position = anchor.PositionSeconds + (elapsedSeconds * anchor.SpeedFactor);
        return DurationSeconds > 0 ? Math.Clamp(position, 0, DurationSeconds) : Math.Max(0, position);
    }

    private long ResolveAnchor(long? serverTimestampMicroseconds, long nowMicroseconds)
    {
        if (!serverTimestampMicroseconds.HasValue)
        {
            _logger.LogDebug("Anchor fell back to receipt time: metadata carries no server timestamp");
            return nowMicroseconds;
        }

        if (_clockSynchronizer?.IsConverged != true)
        {
            _logger.LogDebug("Anchor fell back to receipt time: clock synchronizer not converged");
            return nowMicroseconds;
        }

        var converted = _clockSynchronizer.ServerToClientTime(serverTimestampMicroseconds.Value);
        var age = nowMicroseconds - converted;
        if (age < 0 || age > MaxAnchorAgeMicroseconds)
        {
            _logger.LogDebug(
                "Anchor fell back to receipt time: converted timestamp age {AgeMicroseconds}µs outside [0, {MaxAgeMicroseconds}µs]",
                age,
                MaxAnchorAgeMicroseconds);
            return nowMicroseconds;
        }

        return converted;
    }

    /// <summary>
    /// Extrapolation anchor captured from fresh progress: the track position at a
    /// client-domain instant, plus the effective speed. Null whenever extrapolation
    /// is stopped (no fresh progress yet, frozen, or reset).
    /// </summary>
    /// <remarks>
    /// A plain readonly struct rather than the equivalent positional record struct:
    /// the StyleCop version in use crashes on record struct declarations (AD0001 from
    /// SA1201) and flags positional parameters as SA1313, which would push the build's
    /// warning count above its baseline. Value equality is not needed here.
    /// </remarks>
    private readonly struct Anchor(long atMicroseconds, double positionSeconds, double speedFactor)
    {
        public long AtMicroseconds { get; } = atMicroseconds;

        public double PositionSeconds { get; } = positionSeconds;

        public double SpeedFactor { get; } = speedFactor;
    }
}
