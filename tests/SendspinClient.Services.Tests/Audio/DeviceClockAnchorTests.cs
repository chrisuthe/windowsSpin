// <copyright file="DeviceClockAnchorTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using SendspinClient.Services.Audio;
using Xunit;

namespace SendspinClient.Services.Tests.Audio;

public class DeviceClockAnchorTests
{
    [Fact]
    public void UnavailableDeviceClock_ReturnsWallClock()
    {
        var anchor = new DeviceClockAnchor();

        Assert.Equal(5_000_000_000L, anchor.Resolve(deviceMicros: null, wallMicros: 5_000_000_000L));
        Assert.False(anchor.IsActive);
        Assert.False(anchor.JustEngaged);
    }

    [Fact]
    public void FirstAnchor_IsContinuousWithWallClock()
    {
        var anchor = new DeviceClockAnchor();

        // Device position starts near zero; wall clock is a huge epoch value. The first reading must
        // hand off continuously - returning the wall value, not the tiny device value.
        var result = anchor.Resolve(deviceMicros: 1_000_000L, wallMicros: 5_000_000_000L);

        Assert.Equal(5_000_000_000L, result);
        Assert.True(anchor.JustEngaged);
        Assert.True(anchor.IsActive);
    }

    [Fact]
    public void AfterAnchor_ElapsedTracksDeviceClock_NotWallClock()
    {
        var anchor = new DeviceClockAnchor();

        // Anchor: device pos 1.0s, wall 5000.0s.
        var start = anchor.Resolve(deviceMicros: 1_000_000L, wallMicros: 5_000_000_000L);

        // DAC runs faster than the system clock: device advanced 1.0s while wall advanced only 0.5s.
        var now = anchor.Resolve(deviceMicros: 2_000_000L, wallMicros: 5_000_500_000L);

        // The whole point of the anchor: elapsed reflects the DEVICE's pacing (1.0s), so downstream
        // sync sees the real drift instead of wall-clock jitter.
        Assert.Equal(1_000_000L, now - start);
    }

    [Fact]
    public void LateAnchor_AfterWallClockStart_HandsOffContinuously()
    {
        var anchor = new DeviceClockAnchor();

        // First reads: device clock not ready yet -> wall clock.
        var t0 = anchor.Resolve(deviceMicros: null, wallMicros: 5_000_000_000L);
        Assert.Equal(5_000_000_000L, t0);
        Assert.False(anchor.IsActive);

        // Device becomes available a bit later. The hand-off returns the wall value (continuous),
        // so there is no jump on the timeline the buffer already started measuring against.
        var t1 = anchor.Resolve(deviceMicros: 40_000L, wallMicros: 5_000_050_000L);
        Assert.Equal(5_000_050_000L, t1);
        Assert.True(anchor.JustEngaged);

        // From here it tracks the device clock.
        var t2 = anchor.Resolve(deviceMicros: 1_040_000L, wallMicros: 5_001_060_000L);
        Assert.Equal(1_000_000L, t2 - t1); // device advanced exactly 1.0s
    }

    [Fact]
    public void LargeBackwardJump_DisablesDeviceClockForStream()
    {
        var anchor = new DeviceClockAnchor();

        anchor.Resolve(deviceMicros: 1_000_000L, wallMicros: 5_000_000_000L);
        anchor.Resolve(deviceMicros: 2_000_000L, wallMicros: 5_001_000_000L);

        // Driver reset the position (back to ~0): far beyond the 50 ms tolerance -> abandon.
        var afterReset = anchor.Resolve(deviceMicros: 0L, wallMicros: 5_001_500_000L);

        Assert.Equal(5_001_500_000L, afterReset); // fell back to wall clock
        Assert.True(anchor.JustDisabled);
        Assert.False(anchor.IsActive);

        // Stays on the wall clock for the rest of the stream, even though the device clock recovers.
        var later = anchor.Resolve(deviceMicros: 3_000_000L, wallMicros: 5_002_000_000L);
        Assert.Equal(5_002_000_000L, later);
        Assert.False(anchor.JustDisabled); // edge flag is one-shot
        Assert.False(anchor.IsActive);
    }

    [Fact]
    public void SmallBackwardJitter_IsTolerated()
    {
        var anchor = new DeviceClockAnchor();

        anchor.Resolve(deviceMicros: 1_000_000L, wallMicros: 5_000_000_000L);
        anchor.Resolve(deviceMicros: 2_000_000L, wallMicros: 5_001_000_000L);

        // 10 ms backward blip (< 50 ms tolerance): keep tracking the device clock, do not disable.
        var jittered = anchor.Resolve(deviceMicros: 1_990_000L, wallMicros: 5_001_010_000L);

        Assert.False(anchor.JustDisabled);
        Assert.True(anchor.IsActive);
        Assert.Equal(1_990_000L + (5_000_000_000L - 1_000_000L), jittered); // device + original offset
    }

    [Fact]
    public void Reset_TakesAFreshAnchorAgainstNewBaseline()
    {
        var anchor = new DeviceClockAnchor();

        anchor.Resolve(deviceMicros: 1_000_000L, wallMicros: 5_000_000_000L);
        anchor.Resolve(deviceMicros: 2_000_000L, wallMicros: 5_001_000_000L);

        // Stream restart / device switch: new WasapiOut zeroes the position. Reset() so the zero is a
        // fresh anchor rather than a backward-jump glitch.
        anchor.Reset();
        Assert.False(anchor.IsActive);

        var reanchored = anchor.Resolve(deviceMicros: 0L, wallMicros: 6_000_000_000L);
        Assert.Equal(6_000_000_000L, reanchored); // continuous with the new wall value
        Assert.True(anchor.JustEngaged);
        Assert.True(anchor.IsActive);
    }
}
