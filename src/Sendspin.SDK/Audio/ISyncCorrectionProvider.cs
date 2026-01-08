// <copyright file="ISyncCorrectionProvider.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace Sendspin.SDK.Audio;

/// <summary>
/// Provides sync correction decisions based on sync error from <see cref="ITimedAudioBuffer"/>.
/// </summary>
/// <remarks>
/// <para>
/// This interface abstracts the correction strategy, allowing platforms to implement
/// their own correction logic. The SDK provides <see cref="SyncCorrectionCalculator"/>
/// as a default implementation that mirrors the CLI's tiered correction approach.
/// </para>
/// <para>
/// Usage pattern:
/// 1. Call <see cref="UpdateFromSyncError"/> with error values from <see cref="ITimedAudioBuffer"/>
/// 2. Read correction properties (<see cref="DropEveryNFrames"/>, <see cref="InsertEveryNFrames"/>, <see cref="TargetPlaybackRate"/>)
/// 3. Apply corrections externally (drop/insert samples, adjust resampler rate)
/// 4. Call <see cref="ITimedAudioBuffer.NotifyExternalCorrection"/> to report applied corrections
/// </para>
/// </remarks>
public interface ISyncCorrectionProvider
{
    /// <summary>
    /// Gets the current sync correction mode.
    /// </summary>
    SyncCorrectionMode CurrentMode { get; }

    /// <summary>
    /// Gets the interval for dropping frames (when playing too slow).
    /// Drop one frame every N frames. Zero means no dropping.
    /// </summary>
    /// <remarks>
    /// Only applicable when <see cref="CurrentMode"/> is <see cref="SyncCorrectionMode.Dropping"/>.
    /// A smaller value means more aggressive correction.
    /// </remarks>
    int DropEveryNFrames { get; }

    /// <summary>
    /// Gets the interval for inserting frames (when playing too fast).
    /// Insert one frame every N frames. Zero means no inserting.
    /// </summary>
    /// <remarks>
    /// Only applicable when <see cref="CurrentMode"/> is <see cref="SyncCorrectionMode.Inserting"/>.
    /// A smaller value means more aggressive correction.
    /// </remarks>
    int InsertEveryNFrames { get; }

    /// <summary>
    /// Gets the target playback rate for resampling-based sync correction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Values: 1.0 = normal speed, &gt;1.0 = speed up (behind), &lt;1.0 = slow down (ahead).
    /// </para>
    /// <para>
    /// Only meaningful when <see cref="CurrentMode"/> is <see cref="SyncCorrectionMode.Resampling"/>.
    /// The caller should apply this rate to a resampler (e.g., WDL, SoundTouch) for smooth correction.
    /// </para>
    /// </remarks>
    double TargetPlaybackRate { get; }

    /// <summary>
    /// Event raised when correction parameters change.
    /// </summary>
    /// <remarks>
    /// Subscribers can use this to update resamplers or other correction components
    /// without polling. The event provides the provider instance for accessing updated values.
    /// </remarks>
    event Action<ISyncCorrectionProvider>? CorrectionChanged;

    /// <summary>
    /// Updates correction decisions based on current sync error values.
    /// </summary>
    /// <param name="rawMicroseconds">Raw sync error in microseconds from <see cref="ITimedAudioBuffer.SyncErrorMicroseconds"/>.</param>
    /// <param name="smoothedMicroseconds">Smoothed sync error from <see cref="ITimedAudioBuffer.SmoothedSyncErrorMicroseconds"/>.</param>
    /// <remarks>
    /// <para>
    /// Call this method after each read from <see cref="ITimedAudioBuffer.ReadRaw"/>.
    /// The provider uses the smoothed error for correction decisions to avoid jittery behavior.
    /// </para>
    /// <para>
    /// Sign convention (same as <see cref="ITimedAudioBuffer"/>):
    /// - Positive = playing behind (need to speed up/drop frames)
    /// - Negative = playing ahead (need to slow down/insert frames)
    /// </para>
    /// </remarks>
    void UpdateFromSyncError(long rawMicroseconds, double smoothedMicroseconds);

    /// <summary>
    /// Resets the provider to initial state (no correction).
    /// </summary>
    /// <remarks>
    /// Call this when the buffer is cleared or playback restarts to prevent
    /// stale correction decisions from affecting new playback.
    /// </remarks>
    void Reset();
}
