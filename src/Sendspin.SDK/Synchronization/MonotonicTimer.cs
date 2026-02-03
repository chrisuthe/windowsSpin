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

    // Telemetry counters for diagnosing timer behavior
    private long _totalCalls;
    private long _backwardJumpCount;
    private long _forwardJumpCount;
    private long _totalBackwardJumpMicroseconds;
    private long _totalForwardJumpMicroseconds;
    private long _maxBackwardJumpMicroseconds;
    private long _maxForwardJumpMicroseconds;

    /// <summary>
    /// Gets the total number of GetCurrentTimeMicroseconds calls.
    /// </summary>
    public long TotalCalls => _totalCalls;

    /// <summary>
    /// Gets the number of backward timer jumps that were filtered.
    /// </summary>
    public long BackwardJumpCount => _backwardJumpCount;

    /// <summary>
    /// Gets the number of forward timer jumps that were clamped.
    /// </summary>
    public long ForwardJumpCount => _forwardJumpCount;

    /// <summary>
    /// Gets the total microseconds of backward jumps that were absorbed.
    /// </summary>
    public long TotalBackwardJumpMicroseconds => _totalBackwardJumpMicroseconds;

    /// <summary>
    /// Gets the total microseconds of forward jumps that were clamped (amount over threshold).
    /// </summary>
    public long TotalForwardJumpClampedMicroseconds => _totalForwardJumpMicroseconds;

    /// <summary>
    /// Gets the maximum backward jump observed in microseconds.
    /// </summary>
    public long MaxBackwardJumpMicroseconds => _maxBackwardJumpMicroseconds;

    /// <summary>
    /// Gets the maximum forward jump observed in microseconds.
    /// </summary>
    public long MaxForwardJumpMicroseconds => _maxForwardJumpMicroseconds;

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
        _totalCalls++;
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
            var absJump = -rawDelta;
            _backwardJumpCount++;
            _totalBackwardJumpMicroseconds += absJump;
            if (absJump > _maxBackwardJumpMicroseconds)
            {
                _maxBackwardJumpMicroseconds = absJump;
            }

            _logger?.LogDebug(
                "Timer went backward by {DeltaMs:F2}ms, holding at last value (total backward jumps: {Count})",
                absJump / 1000.0,
                _backwardJumpCount);
            // Return last value (time doesn't go backward)
            return _lastReturnedTime;
        }

        // Handle forward jump (timer jumped ahead)
        if (rawDelta > MaxDeltaMicroseconds)
        {
            var excessMicroseconds = rawDelta - MaxDeltaMicroseconds;
            _forwardJumpCount++;
            _totalForwardJumpMicroseconds += excessMicroseconds;
            if (rawDelta > _maxForwardJumpMicroseconds)
            {
                _maxForwardJumpMicroseconds = rawDelta;
            }

            _logger?.LogDebug(
                "Timer jumped forward by {DeltaMs:F2}ms, clamping to {MaxMs}ms (total forward jumps: {Count})",
                rawDelta / 1000.0,
                MaxDeltaMicroseconds / 1000.0,
                _forwardJumpCount);
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
    /// <param name="resetTelemetry">If true, also resets the telemetry counters.</param>
    /// <remarks>
    /// This resets the internal state so the next call to GetCurrentTimeMicroseconds
    /// will re-initialize from the underlying timer. Use this when starting a new
    /// playback session to avoid carrying over stale state.
    /// </remarks>
    public void Reset(bool resetTelemetry = false)
    {
        _initialized = false;
        _lastRawTime = 0;
        _lastReturnedTime = 0;

        if (resetTelemetry)
        {
            _totalCalls = 0;
            _backwardJumpCount = 0;
            _forwardJumpCount = 0;
            _totalBackwardJumpMicroseconds = 0;
            _totalForwardJumpMicroseconds = 0;
            _maxBackwardJumpMicroseconds = 0;
            _maxForwardJumpMicroseconds = 0;
        }
    }

    /// <summary>
    /// Gets a formatted summary of the timer's filtering activity.
    /// Useful for diagnostics and "Stats for Nerds" displays.
    /// </summary>
    /// <returns>A string summarizing timer jump filtering stats.</returns>
    public string GetStatsSummary()
    {
        if (_totalCalls == 0)
        {
            return "No timer calls yet";
        }

        var backwardRate = _totalCalls > 0 ? (double)_backwardJumpCount / _totalCalls * 100 : 0;
        var forwardRate = _totalCalls > 0 ? (double)_forwardJumpCount / _totalCalls * 100 : 0;

        if (_backwardJumpCount == 0 && _forwardJumpCount == 0)
        {
            return $"No timer jumps filtered ({_totalCalls:N0} calls)";
        }

        return $"Backward: {_backwardJumpCount:N0} ({backwardRate:F2}%, max {_maxBackwardJumpMicroseconds / 1000.0:F1}ms), " +
               $"Forward: {_forwardJumpCount:N0} ({forwardRate:F2}%, max {_maxForwardJumpMicroseconds / 1000.0:F1}ms), " +
               $"Total calls: {_totalCalls:N0}";
    }
}
