// <copyright file="OutputLatencyResolutionTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.Windows.Services.Audio;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio;

/// <summary>
/// Covers the three-tier output latency ladder from issue #73. Output latency is subtracted from
/// elapsed time when computing sync error, so a fabricated value is not a cosmetic problem - and
/// the old code fabricated one silently, running a failed <c>StreamLatency</c> read of 0 through
/// <c>Math.Max(latencyMs, requestedLatencyMs)</c> to produce exactly the requested 100 ms.
/// </summary>
public class OutputLatencyResolutionTests
{
    private const int DeviceSampleRate = 192000;

    /// <summary>
    /// Tier 1: a device that reports a stream latency is believed, and the figure is not floored
    /// at the requested latency. 40 ms is deliberately below the 100 ms request - the old clamp
    /// would have raised it to 100.
    /// </summary>
    [Theory]
    [InlineData(400_000, 40)] // 40 ms
    [InlineData(1_150_000, 115)] // 115 ms
    public void PositiveStreamLatency_IsUsedAsMeasured(long streamLatency100Ns, int expectedMs)
    {
        var reading = WasapiAudioPlayer.ResolveOutputLatency(streamLatency100Ns, bufferFrames: 19200, DeviceSampleRate);

        Assert.Equal(OutputLatencyProvenance.StreamLatency, reading.Provenance);
        Assert.Equal(expectedMs, reading.LatencyMs);
        Assert.False(reading.IsEstimate);
    }

    /// <summary>
    /// Tier 2: a zero (or negative) stream latency is a FAILED read, not a small measurement, so it
    /// falls through to the device's buffer size rather than being clamped into a plausible-looking
    /// number. This is the 192 kHz DAC in the issue: <c>StreamLatency: 0 (100ns units) = 0ms</c>,
    /// with a perfectly good buffer behind it.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void FailedStreamLatency_FallsThroughToTheDeviceBuffer(long streamLatency100Ns)
    {
        // 9600 frames at 192 kHz == 50 ms.
        var reading = WasapiAudioPlayer.ResolveOutputLatency(streamLatency100Ns, bufferFrames: 9600, DeviceSampleRate);

        Assert.Equal(OutputLatencyProvenance.DeviceBuffer, reading.Provenance);
        Assert.Equal(50, reading.LatencyMs);
        Assert.False(reading.IsEstimate);
    }

    /// <summary>
    /// Tier 3: with nothing measurable at all, the constant is returned but flagged as an estimate
    /// so callers - and Stats for Nerds - can tell it apart from a reading.
    /// </summary>
    [Theory]
    [InlineData(0, 0)] // client unreachable entirely
    [InlineData(0, DeviceSampleRate)] // buffer size unavailable
    [InlineData(9600, 0)] // buffer rate unknown
    public void NothingMeasurable_YieldsAnEstimateFlaggedAsSuch(int bufferFrames, int bufferSampleRate)
    {
        var reading = WasapiAudioPlayer.ResolveOutputLatency(0, bufferFrames, bufferSampleRate);

        Assert.Equal(OutputLatencyProvenance.Estimated, reading.Provenance);
        Assert.True(reading.IsEstimate);
        Assert.Equal(115, reading.LatencyMs); // 100 ms requested + 15 ms assumed engine overhead
    }

    /// <summary>
    /// The 115 / 100 disagreement from the issue: the pre-<c>Init()</c> placeholder logged 115 ms
    /// while the post-attach path clamped a zero read down to 100 ms, for one unchanged device
    /// condition. Both paths now produce the same estimate, so they cannot disagree.
    /// </summary>
    [Fact]
    public void UnmeasurableDevice_ReportsOneNumber_NotTheOld115Versus100Split()
    {
        var reading = WasapiAudioPlayer.ResolveOutputLatency(0, bufferFrames: 0, bufferSampleRate: 0);

        Assert.Equal(115, reading.LatencyMs);
        Assert.NotEqual(100, reading.LatencyMs);
    }

    /// <summary>
    /// Both measured tiers round rather than truncate. Truncation is biased downward with a mean
    /// error of half a millisecond, and this figure is subtracted from the sync error, so the bias
    /// lands as a constant offset against every other player - a small instance of exactly the
    /// defect the ladder exists to remove.
    /// </summary>
    [Theory]
    [InlineData(1_004_999, 100)] // 100.4999 ms
    [InlineData(1_005_000, 101)] // 100.5 ms - truncation would report 100
    [InlineData(1_009_999, 101)] // 100.9999 ms - truncation would report 100
    public void StreamLatency_IsRounded_NotTruncated(long streamLatency100Ns, int expectedMs)
    {
        var reading = WasapiAudioPlayer.ResolveOutputLatency(streamLatency100Ns, bufferFrames: 0, bufferSampleRate: 0);

        Assert.Equal(OutputLatencyProvenance.StreamLatency, reading.Provenance);
        Assert.Equal(expectedMs, reading.LatencyMs);
    }

    /// <summary>
    /// The buffer tier rounds too. 9700 frames at 192 kHz is 50.52 ms, which truncation reports
    /// as 50.
    /// </summary>
    [Fact]
    public void DeviceBufferLatency_IsRounded_NotTruncated()
    {
        var reading = WasapiAudioPlayer.ResolveOutputLatency(0, bufferFrames: 9700, DeviceSampleRate);

        Assert.Equal(OutputLatencyProvenance.DeviceBuffer, reading.Provenance);
        Assert.Equal(51, reading.LatencyMs);
    }

    /// <summary>
    /// The buffer frame count must be divided by the rate of the format the audio client was
    /// INITIALIZED with, not by the device's mix rate - they are only the same sometimes. NAudio's
    /// WasapiOut passes our provider's format through verbatim in shared mode and lets the engine
    /// convert, so under the Combined strategy the client runs at the device rate while under
    /// DropInsertOnly it runs at the stream rate. Both cases below describe the same 100 ms of real
    /// buffering; dividing the 48 kHz frame count by 192000 would report 25 ms.
    /// </summary>
    [Theory]
    [InlineData(4800, 48000)] // DropInsertOnly: client initialized at the 48 kHz stream rate
    [InlineData(19200, 192000)] // Combined: client initialized at the 192 kHz device rate
    public void DeviceBufferTier_CountsFramesAtTheInitializedStreamRate(int bufferFrames, int bufferSampleRate)
    {
        var reading = WasapiAudioPlayer.ResolveOutputLatency(0, bufferFrames, bufferSampleRate);

        Assert.Equal(OutputLatencyProvenance.DeviceBuffer, reading.Provenance);
        Assert.Equal(100, reading.LatencyMs);
    }

    /// <summary>
    /// The reporter is the route from the transient player to the stats view model; until an output
    /// is initialized it must say "nothing known" rather than a default that reads as a measurement.
    /// </summary>
    [Fact]
    public void Reporter_HasNoReading_UntilOneIsPublished()
    {
        var reporter = new OutputLatencyReporter();
        Assert.Null(reporter.Current);

        reporter.Report(new OutputLatencyReading(50, OutputLatencyProvenance.DeviceBuffer));
        Assert.Equal(50, reporter.Current!.LatencyMs);
        Assert.False(reporter.Current.IsEstimate);
    }
}
