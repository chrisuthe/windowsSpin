// <copyright file="HighPrecisionTimer.cs" company="SendSpin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Diagnostics;

namespace SendSpin.SDK.Synchronization;

/// <summary>
/// Provides high-precision time measurement using hardware performance counters.
/// </summary>
/// <remarks>
/// <para>
/// This class provides microsecond-precision timing by combining:
/// - System UTC time for absolute reference (synchronized once at startup)
/// - Stopwatch for precise incremental timing between measurements
/// </para>
/// <para>
/// Why not use DateTimeOffset.UtcNow directly?
/// - DateTime resolution on Windows is typically ~15ms (system timer interrupt)
/// - Stopwatch uses QueryPerformanceCounter with ~100ns precision
/// - For audio synchronization, we need microsecond-level accuracy
/// </para>
/// </remarks>
public sealed class HighPrecisionTimer : IHighPrecisionTimer
{
    private readonly long _startTimestampTicks;
    private readonly long _startTimeUnixMicroseconds;
    private readonly double _ticksToMicroseconds;

    /// <summary>
    /// Gets the shared instance of the high-precision timer.
    /// </summary>
    /// <remarks>
    /// Using a singleton ensures all components share the same time base,
    /// avoiding drift between different timer instances.
    /// </remarks>
    public static IHighPrecisionTimer Shared { get; } = new HighPrecisionTimer();

    /// <summary>
    /// Initializes a new instance of the <see cref="HighPrecisionTimer"/> class.
    /// </summary>
    public HighPrecisionTimer()
    {
        // Capture both Stopwatch and system time as close together as possible
        // to minimize the offset between them
        _startTimestampTicks = Stopwatch.GetTimestamp();
        _startTimeUnixMicroseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

        // Pre-calculate conversion factor for ticks to microseconds
        // Stopwatch.Frequency = ticks per second
        _ticksToMicroseconds = 1_000_000.0 / Stopwatch.Frequency;
    }

    /// <inheritdoc/>
    public long GetCurrentTimeMicroseconds()
    {
        var currentTicks = Stopwatch.GetTimestamp();
        var elapsedTicks = currentTicks - _startTimestampTicks;
        var elapsedMicroseconds = (long)(elapsedTicks * _ticksToMicroseconds);

        return _startTimeUnixMicroseconds + elapsedMicroseconds;
    }

    /// <inheritdoc/>
    public long GetElapsedMicroseconds(long fromTimeMicroseconds)
    {
        return GetCurrentTimeMicroseconds() - fromTimeMicroseconds;
    }

    /// <summary>
    /// Gets the resolution of the timer in nanoseconds.
    /// </summary>
    /// <returns>Timer resolution in nanoseconds.</returns>
    public static double GetResolutionNanoseconds()
    {
        return 1_000_000_000.0 / Stopwatch.Frequency;
    }

    /// <summary>
    /// Gets whether the underlying hardware supports high-resolution timing.
    /// </summary>
    public static bool IsHighResolution => Stopwatch.IsHighResolution;
}

/// <summary>
/// Interface for high-precision time measurement.
/// </summary>
public interface IHighPrecisionTimer
{
    /// <summary>
    /// Gets the current time in microseconds since Unix epoch.
    /// </summary>
    /// <returns>Current time in microseconds.</returns>
    long GetCurrentTimeMicroseconds();

    /// <summary>
    /// Gets the elapsed time since a given timestamp.
    /// </summary>
    /// <param name="fromTimeMicroseconds">Starting time in microseconds.</param>
    /// <returns>Elapsed time in microseconds.</returns>
    long GetElapsedMicroseconds(long fromTimeMicroseconds);
}
