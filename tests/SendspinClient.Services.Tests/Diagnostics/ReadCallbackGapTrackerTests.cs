// <copyright file="ReadCallbackGapTrackerTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using SendspinClient.Services.Diagnostics;
using Xunit;

namespace SendspinClient.Services.Tests.Diagnostics;

public class ReadCallbackGapTrackerTests
{
    [Fact]
    public void RegularCallbacks_NoGaps()
    {
        var tracker = new ReadCallbackGapTracker();

        // 10 ms expected interval, callbacks arriving every 10 ms
        for (long t = 0; t < 1000; t += 10)
        {
            tracker.RecordRead(nowMs: t, expectedIntervalMs: 10);
        }

        Assert.Equal(0, tracker.GapCount);
    }

    [Fact]
    public void LateCallback_CountsGap_AndTracksMax()
    {
        var tracker = new ReadCallbackGapTracker();
        tracker.RecordRead(nowMs: 0, expectedIntervalMs: 10);
        tracker.RecordRead(nowMs: 10, expectedIntervalMs: 10);
        tracker.RecordRead(nowMs: 250, expectedIntervalMs: 10); // 240 ms gap

        Assert.Equal(1, tracker.GapCount);
        Assert.Equal(240, tracker.MaxGapMs);
    }

    [Fact]
    public void MaxGapMs_TracksLargest_NotMostRecent()
    {
        var tracker = new ReadCallbackGapTracker();
        tracker.RecordRead(nowMs: 0, expectedIntervalMs: 10);
        tracker.RecordRead(nowMs: 300, expectedIntervalMs: 10);  // 300ms gap - the max
        tracker.RecordRead(nowMs: 450, expectedIntervalMs: 10);  // 150ms gap - smaller

        Assert.Equal(2, tracker.GapCount);
        Assert.Equal(300, tracker.MaxGapMs);
    }

    [Fact]
    public void GapBelowFloor_Ignored()
    {
        var tracker = new ReadCallbackGapTracker();
        tracker.RecordRead(nowMs: 0, expectedIntervalMs: 10);
        tracker.RecordRead(nowMs: 80, expectedIntervalMs: 10); // 80 ms: > 2×expected but < 100 ms floor

        Assert.Equal(0, tracker.GapCount);
    }

    [Fact]
    public void FirstCallbackAfterReset_NotAGap()
    {
        var tracker = new ReadCallbackGapTracker();
        tracker.RecordRead(nowMs: 0, expectedIntervalMs: 10);
        tracker.Reset();
        tracker.RecordRead(nowMs: 5000, expectedIntervalMs: 10); // long pause spans the reset

        Assert.Equal(0, tracker.GapCount);
    }
}
