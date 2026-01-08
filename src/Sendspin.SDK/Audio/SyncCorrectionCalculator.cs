// <copyright file="SyncCorrectionCalculator.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace Sendspin.SDK.Audio;

/// <summary>
/// Calculates sync correction decisions based on sync error values.
/// </summary>
/// <remarks>
/// <para>
/// This class implements the tiered correction strategy from the CLI:
/// </para>
/// <list type="bullet">
/// <item>Error &lt; deadband (default 1ms): No correction</item>
/// <item>Error in resampling range (default 1-15ms): Proportional playback rate adjustment</item>
/// <item>Error &gt; resampling threshold (default 15ms): Frame drop/insert</item>
/// </list>
/// <para>
/// The calculator is stateless per-update and can be used across threads if synchronized externally.
/// It maintains correction state internally and raises <see cref="CorrectionChanged"/> when parameters change.
/// </para>
/// </remarks>
public sealed class SyncCorrectionCalculator : ISyncCorrectionProvider
{
    private readonly SyncCorrectionOptions _options;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly object _lock = new();

    // Current correction state
    private SyncCorrectionMode _currentMode = SyncCorrectionMode.None;
    private int _dropEveryNFrames;
    private int _insertEveryNFrames;
    private double _targetPlaybackRate = 1.0;

    // Startup tracking
    private long _totalSamplesProcessed;
    private bool _inStartupGracePeriod = true;

    /// <inheritdoc/>
    public SyncCorrectionMode CurrentMode
    {
        get { lock (_lock) return _currentMode; }
    }

    /// <inheritdoc/>
    public int DropEveryNFrames
    {
        get { lock (_lock) return _dropEveryNFrames; }
    }

    /// <inheritdoc/>
    public int InsertEveryNFrames
    {
        get { lock (_lock) return _insertEveryNFrames; }
    }

    /// <inheritdoc/>
    public double TargetPlaybackRate
    {
        get { lock (_lock) return _targetPlaybackRate; }
    }

    /// <inheritdoc/>
    public event Action<ISyncCorrectionProvider>? CorrectionChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncCorrectionCalculator"/> class.
    /// </summary>
    /// <param name="options">Sync correction options. Uses <see cref="SyncCorrectionOptions.Default"/> if null.</param>
    /// <param name="sampleRate">Audio sample rate in Hz (e.g., 48000).</param>
    /// <param name="channels">Number of audio channels (e.g., 2 for stereo).</param>
    public SyncCorrectionCalculator(SyncCorrectionOptions? options, int sampleRate, int channels)
    {
        _options = options?.Clone() ?? SyncCorrectionOptions.Default;
        _options.Validate();
        _sampleRate = sampleRate;
        _channels = channels;
    }

    /// <inheritdoc/>
    public void UpdateFromSyncError(long rawMicroseconds, double smoothedMicroseconds)
    {
        bool changed;
        lock (_lock)
        {
            changed = UpdateCorrectionInternal(smoothedMicroseconds);
        }

        // Fire event outside lock to prevent deadlocks
        if (changed)
        {
            CorrectionChanged?.Invoke(this);
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        bool changed;
        lock (_lock)
        {
            changed = _currentMode != SyncCorrectionMode.None
                || _dropEveryNFrames != 0
                || _insertEveryNFrames != 0
                || Math.Abs(_targetPlaybackRate - 1.0) > 0.0001;

            _currentMode = SyncCorrectionMode.None;
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;
            _targetPlaybackRate = 1.0;
            _totalSamplesProcessed = 0;
            _inStartupGracePeriod = true;
        }

        if (changed)
        {
            CorrectionChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// Notifies the calculator that samples were processed.
    /// Call this after applying corrections to track startup grace period.
    /// </summary>
    /// <param name="samplesProcessed">Number of samples processed.</param>
    public void NotifySamplesProcessed(int samplesProcessed)
    {
        lock (_lock)
        {
            _totalSamplesProcessed += samplesProcessed;

            // Check if we've exited the startup grace period
            if (_inStartupGracePeriod)
            {
                var microsecondsPerSample = 1_000_000.0 / (_sampleRate * _channels);
                var elapsedMicroseconds = (long)(_totalSamplesProcessed * microsecondsPerSample);
                if (elapsedMicroseconds >= _options.StartupGracePeriodMicroseconds)
                {
                    _inStartupGracePeriod = false;
                }
            }
        }
    }

    /// <summary>
    /// Updates correction parameters based on smoothed sync error.
    /// Must be called under lock.
    /// </summary>
    /// <returns>True if correction parameters changed.</returns>
    private bool UpdateCorrectionInternal(double smoothedMicroseconds)
    {
        var previousMode = _currentMode;
        var previousDrop = _dropEveryNFrames;
        var previousInsert = _insertEveryNFrames;
        var previousRate = _targetPlaybackRate;

        // During startup grace period, don't apply corrections
        if (_inStartupGracePeriod)
        {
            _currentMode = SyncCorrectionMode.None;
            _targetPlaybackRate = 1.0;
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;
            return HasChanged(previousMode, previousDrop, previousInsert, previousRate);
        }

        var absError = Math.Abs(smoothedMicroseconds);

        // Thresholds from options
        var deadbandThreshold = _options.EntryDeadbandMicroseconds;
        var resamplingThreshold = _options.ResamplingThresholdMicroseconds;

        // Use exit deadband (hysteresis) if we're currently correcting
        if (_currentMode != SyncCorrectionMode.None && absError < _options.ExitDeadbandMicroseconds)
        {
            _currentMode = SyncCorrectionMode.None;
            _targetPlaybackRate = 1.0;
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;
            return HasChanged(previousMode, previousDrop, previousInsert, previousRate);
        }

        // Tier 1: Deadband - error is small enough to ignore
        if (absError < deadbandThreshold)
        {
            _currentMode = SyncCorrectionMode.None;
            _targetPlaybackRate = 1.0;
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;
            return HasChanged(previousMode, previousDrop, previousInsert, previousRate);
        }

        // Tier 2: Proportional rate correction (small errors)
        if (absError < resamplingThreshold)
        {
            _currentMode = SyncCorrectionMode.Resampling;
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;

            // Calculate proportional correction (matching Python CLI approach)
            // Rate = 1.0 + (error_us / target_seconds / 1,000,000)
            var correctionFactor = smoothedMicroseconds
                / _options.CorrectionTargetSeconds
                / 1_000_000.0;

            // Clamp to configured maximum speed adjustment
            correctionFactor = Math.Clamp(correctionFactor,
                -_options.MaxSpeedCorrection,
                _options.MaxSpeedCorrection);

            _targetPlaybackRate = 1.0 + correctionFactor;
            return HasChanged(previousMode, previousDrop, previousInsert, previousRate);
        }

        // Tier 3: Large errors - use frame drop/insert for faster correction
        _targetPlaybackRate = 1.0;

        // Calculate desired corrections per second to fix error within target time
        // Error in frames = error_us * sample_rate / 1,000,000
        var framesError = absError * _sampleRate / 1_000_000.0;
        var desiredCorrectionsPerSec = framesError / _options.CorrectionTargetSeconds;

        // Calculate frames per second
        var framesPerSecond = (double)_sampleRate;

        // Limit correction rate to max speed adjustment
        var maxCorrectionsPerSec = framesPerSecond * _options.MaxSpeedCorrection;
        var actualCorrectionsPerSec = Math.Min(desiredCorrectionsPerSec, maxCorrectionsPerSec);

        // Calculate how often to apply a correction (every N frames)
        var correctionInterval = actualCorrectionsPerSec > 0
            ? (int)(framesPerSecond / actualCorrectionsPerSec)
            : 0;

        // Minimum interval to prevent too-aggressive correction
        correctionInterval = Math.Max(correctionInterval, _channels * 10);

        if (smoothedMicroseconds > 0)
        {
            // Playing too slow - need to drop frames to catch up
            _currentMode = SyncCorrectionMode.Dropping;
            _dropEveryNFrames = correctionInterval;
            _insertEveryNFrames = 0;
        }
        else
        {
            // Playing too fast - need to insert frames to slow down
            _currentMode = SyncCorrectionMode.Inserting;
            _dropEveryNFrames = 0;
            _insertEveryNFrames = correctionInterval;
        }

        return HasChanged(previousMode, previousDrop, previousInsert, previousRate);
    }

    /// <summary>
    /// Checks if correction parameters changed from previous values.
    /// </summary>
    private bool HasChanged(SyncCorrectionMode previousMode, int previousDrop, int previousInsert, double previousRate)
    {
        return previousMode != _currentMode
            || previousDrop != _dropEveryNFrames
            || previousInsert != _insertEveryNFrames
            || Math.Abs(previousRate - _targetPlaybackRate) > 0.0001;
    }
}
