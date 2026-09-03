// <copyright file="OutputLatencyReporter.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace Sendspin.Windows.Services.Audio;

/// <summary>
/// How an output latency figure was arrived at.
/// </summary>
/// <remarks>
/// Output latency feeds the sync error calculation, so a guess and a measurement have very
/// different consequences and must not be indistinguishable to the code - or the human - reading
/// them. A device that reports nothing produced exactly the same number as one reporting a real
/// 100 ms, which made a failed read look like a healthy one (issue #73).
/// </remarks>
public enum OutputLatencyProvenance
{
    /// <summary>Read directly from <c>IAudioClient.StreamLatency</c>.</summary>
    StreamLatency,

    /// <summary>Derived from the initialized client's buffer frame count and the device rate.</summary>
    DeviceBuffer,

    /// <summary>Not measured at all - the requested latency plus assumed engine overhead.</summary>
    Estimated,
}

/// <summary>
/// An output latency figure together with how it was obtained.
/// </summary>
public sealed class OutputLatencyReading
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OutputLatencyReading"/> class.
    /// </summary>
    /// <param name="latencyMs">The latency in milliseconds.</param>
    /// <param name="provenance">How the figure was obtained.</param>
    public OutputLatencyReading(int latencyMs, OutputLatencyProvenance provenance)
    {
        LatencyMs = latencyMs;
        Provenance = provenance;
    }

    /// <summary>Gets the latency in milliseconds.</summary>
    public int LatencyMs { get; }

    /// <summary>Gets how the figure was obtained.</summary>
    public OutputLatencyProvenance Provenance { get; }

    /// <summary>
    /// Gets a value indicating whether this figure is an estimate rather than a measurement.
    /// </summary>
    public bool IsEstimate => Provenance == OutputLatencyProvenance.Estimated;
}

/// <summary>
/// Carries the most recent output latency reading from the audio player to whatever wants to
/// display it.
/// </summary>
/// <remarks>
/// <c>IAudioPlayer</c> is registered transient and the stats view model holds only the
/// pipeline, so there is no direct reference between the two. Registering this as a singleton
/// gives the one value a single home: the player writes it whenever it resolves a latency, and
/// the stats view model reads it when it refreshes.
/// </remarks>
public sealed class OutputLatencyReporter
{
    private OutputLatencyReading? _current;

    /// <summary>
    /// Gets the most recent reading, or <see langword="null"/> if no output has been initialized yet.
    /// </summary>
    public OutputLatencyReading? Current => Volatile.Read(ref _current);

    /// <summary>
    /// Records a newly resolved output latency.
    /// </summary>
    /// <param name="reading">The reading to publish.</param>
    public void Report(OutputLatencyReading reading) => Volatile.Write(ref _current, reading);
}
