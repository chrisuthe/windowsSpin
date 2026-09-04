// <copyright file="SessionHeaderLatencyTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.Windows.Services.Audio;
using Sendspin.Windows.Services.Diagnostics;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Diagnostics;

/// <summary>
/// Covers the session header's output latency field.
/// </summary>
/// <remarks>
/// The header is written on the first monitor tick that sees an output format, which can run a few
/// hundred milliseconds before the player's latency ladder resolves. A real session recorded
/// <c>outputLatency=115ms</c> - the pre-Init estimate - while the ladder logged a measured 100 ms
/// 0.4 s later, which left the header stating an unlabelled guess for the one field it exists to
/// report. Naming the provenance is what makes the two reconcilable after the fact.
/// </remarks>
public class SessionHeaderLatencyTests
{
    [Fact]
    public void MeasuredReading_IsNamedAsMeasured()
    {
        var reading = new OutputLatencyReading(100, OutputLatencyProvenance.DeviceBuffer);

        Assert.Equal(
            "100ms (DeviceBuffer)",
            SyncHealthMonitor.FormatOutputLatency(reading, pipelineLatencyMs: 100));
    }

    [Fact]
    public void StreamLatencyReading_IsNamedAsMeasured()
    {
        var reading = new OutputLatencyReading(30, OutputLatencyProvenance.StreamLatency);

        Assert.Equal(
            "30ms (StreamLatency)",
            SyncHealthMonitor.FormatOutputLatency(reading, pipelineLatencyMs: 30));
    }

    /// <summary>
    /// The case that motivated this: a header written before the ladder resolved used to record the
    /// estimate as if it were a measurement. It must now say which it is.
    /// </summary>
    [Fact]
    public void EstimatedReading_IsNamedAsAnEstimate()
    {
        var reading = new OutputLatencyReading(115, OutputLatencyProvenance.Estimated);

        var formatted = SyncHealthMonitor.FormatOutputLatency(reading, pipelineLatencyMs: 115);

        Assert.Equal("115ms (Estimated)", formatted);
        Assert.Contains("Estimated", formatted, StringComparison.Ordinal);
    }

    /// <summary>
    /// The header comes from the reporter, not the pipeline, so the two cannot disagree. When the
    /// pipeline is still carrying a stale figure the reading wins.
    /// </summary>
    [Fact]
    public void ReadingWins_WhenThePipelineStillCarriesADifferentFigure()
    {
        var reading = new OutputLatencyReading(100, OutputLatencyProvenance.DeviceBuffer);

        Assert.Equal(
            "100ms (DeviceBuffer)",
            SyncHealthMonitor.FormatOutputLatency(reading, pipelineLatencyMs: 115));
    }

    /// <summary>
    /// Before any reading exists the pipeline's figure is all there is, and it is marked so rather
    /// than passing as something the player vouched for.
    /// </summary>
    [Fact]
    public void NoReadingYet_FallsBackToThePipelineAndSaysSo()
    {
        Assert.Equal(
            "115ms (unreported)",
            SyncHealthMonitor.FormatOutputLatency(reading: null, pipelineLatencyMs: 115));
    }
}
