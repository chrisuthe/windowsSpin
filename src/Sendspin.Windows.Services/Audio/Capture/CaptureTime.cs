namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>Converts WASAPI packet times into the clock the SDK synchronizes.</summary>
public static class CaptureTime
{
    /// <summary>
    /// Converts a packet's QPC position into the <c>HighPrecisionTimer</c> microsecond domain.
    /// </summary>
    /// <remarks>
    /// <c>HighPrecisionTimer</c> is itself built on the Stopwatch (QPC), so the packet's age —
    /// now minus its QPC position — is exact in both domains. Converting by age avoids depending
    /// on the timer's private epoch.
    /// </remarks>
    /// <param name="qpcPosition100ns">The packet's QPC position from <c>GetBuffer</c>, in 100-ns units.</param>
    /// <param name="nowTimerMicroseconds">The timer's current reading.</param>
    /// <param name="nowStopwatchTicks"><c>Stopwatch.GetTimestamp()</c>, read alongside it.</param>
    /// <param name="stopwatchFrequency"><c>Stopwatch.Frequency</c>.</param>
    /// <returns>The packet's capture time in timer microseconds.</returns>
    public static long ToTimerMicroseconds(
        long qpcPosition100ns, long nowTimerMicroseconds, long nowStopwatchTicks, long stopwatchFrequency)
    {
        long now100ns = (long)((Int128)nowStopwatchTicks * 10_000_000 / stopwatchFrequency);
        long age100ns = now100ns - qpcPosition100ns;
        return nowTimerMicroseconds - (age100ns / 10);
    }
}
