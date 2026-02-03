// <copyright file="MonotonicTimer.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;

namespace Sendspin.SDK.Synchronization;

/// <summary>
/// Wraps a timer to enforce monotonicity and filter out erratic jumps.
/// Designed for VM environments where wall clock can be unreliable.
/// </summary>
/// <remarks>
/// <para>
/// In virtual machines, the underlying timer (Stopwatch/QueryPerformanceCounter)
/// can exhibit erratic behavior due to hypervisor scheduling:
/// - Forward jumps: Timer suddenly advances by hundreds of milliseconds
/// - Backward jumps: Timer returns lower values than previous calls
/// </para>
/// <para>
/// This wrapper filters these anomalies while preserving the ability to detect
/// real DAC clock drift. Real drift accumulates slowly (~50ppm = 3ms/minute),
/// so clamping per-callback deltas to 50ms doesn't hide actual sync issues.
/// </para>
/// </remarks>
public sealed class MonotonicTimer : IHighPrecisionTimer
{
    private readonly IHighPrecisionTimer _inner;
    private readonly ILogger? _logger;

    private long _lastRawTime;
    private long _lastReturnedTime;
    private bool _initialized;

    /// <summary>
    /// Maximum allowed time advance per call in microseconds.
    /// </summary>
    /// <remarks>
    /// Audio callbacks are typically 10-20ms with ±5ms jitter.
    /// 50ms allows for worst-case scheduling while filtering VM timer jumps.
    /// </remarks>
    public long MaxDeltaMicroseconds { get; set; } = 50_000; // 50ms

    /// <summary>
    /// Initializes a new instance of the <see cref="MonotonicTimer"/> class.
    /// </summary>
    /// <param name="inner">The underlying timer to wrap. If null, uses HighPrecisionTimer.Shared.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public MonotonicTimer(IHighPrecisionTimer? inner = null, ILogger? logger = null)
    {
        _inner = inner ?? HighPrecisionTimer.Shared;
        _logger = logger;
    }

    /// <inheritdoc/>
    public long GetCurrentTimeMicroseconds()
    {
        var rawTime = _inner.GetCurrentTimeMicroseconds();

        if (!_initialized)
        {
            _lastRawTime = rawTime;
            _lastReturnedTime = rawTime;
            _initialized = true;
            return rawTime;
        }

        var rawDelta = rawTime - _lastRawTime;
        _lastRawTime = rawTime;

        // Handle backward jump (timer went backwards)
        if (rawDelta < 0)
        {
            _logger?.LogDebug(
                "Timer went backward by {DeltaMs:F2}ms, holding at last value",
                -rawDelta / 1000.0);
            // Return last value (time doesn't go backward)
            return _lastReturnedTime;
        }

        // Handle forward jump (timer jumped ahead)
        if (rawDelta > MaxDeltaMicroseconds)
        {
            _logger?.LogDebug(
                "Timer jumped forward by {DeltaMs:F2}ms, clamping to {MaxMs}ms",
                rawDelta / 1000.0,
                MaxDeltaMicroseconds / 1000.0);
            rawDelta = MaxDeltaMicroseconds;
        }

        _lastReturnedTime += rawDelta;
        return _lastReturnedTime;
    }

    /// <inheritdoc/>
    public long GetElapsedMicroseconds(long fromTimeMicroseconds)
    {
        return GetCurrentTimeMicroseconds() - fromTimeMicroseconds;
    }

    /// <summary>
    /// Resets the timer state. Call when playback restarts.
    /// </summary>
    /// <remarks>
    /// This resets the internal state so the next call to GetCurrentTimeMicroseconds
    /// will re-initialize from the underlying timer. Use this when starting a new
    /// playback session to avoid carrying over stale state.
    /// </remarks>
    public void Reset()
    {
        _initialized = false;
        _lastRawTime = 0;
        _lastReturnedTime = 0;
    }
}
