// <copyright file="ReadCallbackGapTracker.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace SendspinClient.Services.Diagnostics;

/// <summary>
/// Detects audio-thread starvation by timing gaps between sample-source read callbacks.
/// Written from the NAudio audio thread; read from the sync-health monitor thread.
/// </summary>
/// <remarks>
/// A gap is counted when the interval between consecutive reads exceeds
/// max(<see cref="GapFloorMs"/>, 2× expected callback interval). The floor avoids flagging
/// normal WASAPI callback jitter.
/// </remarks>
public sealed class ReadCallbackGapTracker
{
    internal const double GapFloorMs = 100;

    private long _lastReadMs = -1;
    private long _gapCount;
    private long _maxGapMsBits; // double stored via BitConverter for lock-free read

    /// <summary>Gets the number of starvation gaps observed.</summary>
    public long GapCount => Interlocked.Read(ref _gapCount);

    /// <summary>Gets the largest gap observed in milliseconds.</summary>
    public double MaxGapMs => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _maxGapMsBits));

    /// <summary>Records a read callback. Called from the audio thread — must stay allocation-free.</summary>
    /// <param name="nowMs">Current monotonic time (Environment.TickCount64).</param>
    /// <param name="expectedIntervalMs">Expected callback interval from buffer size and format.</param>
    public void RecordRead(long nowMs, double expectedIntervalMs)
    {
        var last = Interlocked.Exchange(ref _lastReadMs, nowMs);
        if (last < 0)
        {
            return; // first read after start/reset
        }

        var gapMs = nowMs - last;
        var threshold = Math.Max(GapFloorMs, 2 * expectedIntervalMs);
        if (gapMs > threshold)
        {
            Interlocked.Increment(ref _gapCount);
            double current;
            while (gapMs > (current = MaxGapMs))
            {
                var currentBits = BitConverter.DoubleToInt64Bits(current);
                var newBits = BitConverter.DoubleToInt64Bits(gapMs);
                if (Interlocked.CompareExchange(ref _maxGapMsBits, newBits, currentBits) == currentBits)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Resets timing state across pipeline restarts so the pause doesn't count as a gap.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _lastReadMs, -1);
    }
}
