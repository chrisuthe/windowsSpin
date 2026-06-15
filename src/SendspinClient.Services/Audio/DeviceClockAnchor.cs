// <copyright file="DeviceClockAnchor.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace SendspinClient.Services.Audio;

/// <summary>
/// Anchors the WASAPI device clock (IAudioClock) onto the wall-clock timeline so it can replace
/// the wall clock as the sync-timing source without a discontinuity, and degrades gracefully when
/// the device clock is unavailable or misbehaves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why anchor.</b> The wall clock (<c>HighPrecisionTimer</c>) returns a large Unix-epoch-based
/// value; the device clock position starts near 0 at stream start. The downstream buffer measures
/// <c>elapsed = now - startAnchor</c>. Mixing the two scales (start captured on one, <c>now</c> on
/// the other) yields a multi-billion-microsecond jump that trips the buffer's re-anchor threshold.
/// We capture <c>offset = wall - device</c> once, then return <c>device + offset</c>. The offset
/// cancels in <c>now - start</c>, so the buffer sees true device-paced elapsed time, while the
/// absolute value stays on the wall-clock timeline. A device&#8596;wall transition is then a
/// continuous hand-off, not a cliff.
/// </para>
/// <para>
/// <b>Hardware hiccups this guards against (issue #33 follow-up):</b>
/// </para>
/// <list type="bullet">
///   <item>
///     <b>Device clock not ready at the first reads.</b> <c>IAudioClock</c> may return null for a
///     few milliseconds after <c>Start()</c>. While null, <see cref="Resolve"/> returns the wall
///     clock and anchors later; because the offset makes the device value continuous with the wall
///     clock, the switchover is seamless.
///   </item>
///   <item>
///     <b>Unsupported / throwing IAudioClock on some drivers.</b> The caller passes null on a read
///     failure; this anchor then simply stays on the wall clock. No exception escapes the timing
///     path.
///   </item>
///   <item>
///     <b>Driver resets the position mid-stream</b> (e.g. <c>AUDCLNT</c> reset / format change that
///     escapes the device-switch path). A backward jump beyond <see cref="DisableThresholdMicros"/>
///     is treated as untrustworthy: the device clock is abandoned for the rest of the stream and we
///     fall back to the wall clock. Sub-millisecond backward jitter is tolerated via a high-water
///     mark so a benign blip does not disable a healthy clock.
///   </item>
///   <item>
///     <b>Stream restart / device switch.</b> A new <c>WasapiOut</c> zeroes the position, which
///     would look like a giant backward jump. The owner calls <see cref="Reset"/> at those known
///     re-anchor points so a fresh anchor is taken instead of mistaking the reset for a glitch.
///   </item>
/// </list>
/// <para>
/// <b>What this does NOT fix.</b> A device whose clock is subtly wrong (advances at the wrong rate,
/// or is silently slaved to the system clock and so offers no drift benefit) will simply yield
/// neutral-to-slightly-worse sync; only per-device validation catches that. A machine whose system
/// clock already agrees with its DAC (ratio ~1.0000, as on a wired desktop) sees no change &#8212;
/// the win is on hardware where the two clocks genuinely diverge (e.g. a USB DAC on its own
/// crystal).
/// </para>
/// </remarks>
public sealed class DeviceClockAnchor
{
    /// <summary>
    /// A mid-stream backward jump larger than this (50 ms) is treated as a driver reset/glitch and
    /// the device clock is abandoned for the rest of the stream. A legitimate reset zeroes the
    /// position (backward by the whole elapsed time), so it is caught with wide margin; genuine
    /// monotonic clocks never step back this far.
    /// </summary>
    private const long DisableThresholdMicros = 50_000;

    private long _offsetMicros;
    private long _highWaterDeviceMicros;
    private bool _anchored;
    private bool _disabled;

    /// <summary>Gets a value indicating whether the most recent <see cref="Resolve"/> call anchored
    /// for the first time (one-shot edge flag, for logging).</summary>
    public bool JustEngaged { get; private set; }

    /// <summary>Gets a value indicating whether the most recent <see cref="Resolve"/> call abandoned
    /// the device clock due to a backward jump (one-shot edge flag, for logging).</summary>
    public bool JustDisabled { get; private set; }

    /// <summary>Gets a value indicating whether the device clock is currently the active source.</summary>
    public bool IsActive => _anchored && !_disabled;

    /// <summary>
    /// Maps a device-clock reading (or null when unavailable) onto the wall-clock timeline.
    /// </summary>
    /// <param name="deviceMicros">The device clock position in microseconds, or null if it could
    /// not be read this call.</param>
    /// <param name="wallMicros">The current wall-clock time in microseconds (the fallback).</param>
    /// <returns>The sync time in microseconds on the wall-clock timeline.</returns>
    public long Resolve(long? deviceMicros, long wallMicros)
    {
        JustEngaged = false;
        JustDisabled = false;

        if (_disabled || deviceMicros is null)
        {
            return wallMicros;
        }

        var device = deviceMicros.Value;

        if (!_anchored)
        {
            // Capture the offset so device+offset == wall right now: continuous with whatever the
            // buffer already anchored on (it may have started on the wall clock while the device
            // clock was still warming up).
            _offsetMicros = wallMicros - device;
            _highWaterDeviceMicros = device;
            _anchored = true;
            JustEngaged = true;
            return wallMicros;
        }

        if (device < _highWaterDeviceMicros - DisableThresholdMicros)
        {
            // Large backward jump => driver reset/glitch we did not expect. Abandon for this stream.
            _disabled = true;
            JustDisabled = true;
            return wallMicros;
        }

        if (device > _highWaterDeviceMicros)
        {
            _highWaterDeviceMicros = device;
        }

        return device + _offsetMicros;
    }

    /// <summary>
    /// Clears all anchor state. Call at known re-anchor points (stream start, device switch) where
    /// the device clock position legitimately resets to zero, so the reset is not mistaken for a
    /// backward-jump glitch.
    /// </summary>
    public void Reset()
    {
        _offsetMicros = 0;
        _highWaterDeviceMicros = 0;
        _anchored = false;
        _disabled = false;
        JustEngaged = false;
        JustDisabled = false;
    }
}
