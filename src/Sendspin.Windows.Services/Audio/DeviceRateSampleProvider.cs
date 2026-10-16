// <copyright file="DeviceRateSampleProvider.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using NAudio.Dsp;
using NAudio.Wave;

namespace Sendspin.Windows.Services.Audio;

/// <summary>
/// An <see cref="ISampleProvider"/> that converts the stream's sample rate to the output device's
/// native mixer rate, so the Windows Audio Engine does not resample it a second time.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>device-format</b> concern, not a sync concern. Sync correction lives upstream in
/// the SDK's <see cref="Sendspin.SDK.Audio.SyncCorrectedSampleSource"/>, which is platform-neutral
/// and deliberately does no rate conversion. In WASAPI shared mode Windows resamples everything to
/// the mixer rate anyway; converting here with a filtered resampler means the audio crosses one
/// known-good conversion instead of an opaque one.
/// </para>
/// <para>
/// The conversion ratio is <b>constant</b> for the life of the provider, which is what makes the
/// anti-alias chain safe to enable unconditionally. WDL runs its IIR low-pass chain only while the
/// ratio is off 1.0 and never clears the filter history, so a ratio that toggles across unity
/// re-engages four biquads against stale state — the soft click of issue #63. Nothing toggles the
/// ratio here: the only rate that used to move it is applied upstream now.
/// </para>
/// </remarks>
public sealed class DeviceRateSampleProvider : ISampleProvider
{
    private const long UnderrunLogIntervalTicks = TimeSpan.TicksPerSecond * 5;

    private readonly ISampleProvider _source;
    private readonly WdlResampler _resampler;
    private readonly ILogger? _logger;

    /// <summary>Last frame actually produced, held across a shortfall instead of writing silence.</summary>
    private readonly float[] _lastFrame;

    private long _sourceEmptyCount;
    private long _resamplerShortCount;
    private long _lastUnderrunLogTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceRateSampleProvider"/> class.
    /// </summary>
    /// <param name="source">The upstream sample provider to read from.</param>
    /// <param name="deviceSampleRate">
    /// The output device's native mixer rate. Passing the source's own rate makes this an identity
    /// pass, which is wasted work — the player omits the stage entirely in that case.
    /// </param>
    /// <param name="logger">Optional logger for underrun diagnostics.</param>
    public DeviceRateSampleProvider(ISampleProvider source, int deviceSampleRate, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deviceSampleRate, 0);

        _source = source;
        _logger = logger;

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(deviceSampleRate, source.WaveFormat.Channels);
        _lastFrame = new float[WaveFormat.Channels];

        // Linear interpolation plus the IIR low-pass chain. (SetMode's second argument is NOT a
        // sinc tap count - with sinc=false it is the number of biquad passes, clamped to 4.)
        // The chain earns its keep here: a genuine rate conversion folds content back below Nyquist
        // without it. It is safe here because the ratio is fixed - see the class remarks.
        _resampler = new WdlResampler();
        _resampler.SetMode(true, 4, false);
        _resampler.SetFilterParms(0.90f, 0.60f); // 90% Nyquist, sharper transition (less processing)

        // Output-driven: ResamplePrepare is told how much output is wanted and answers with the
        // input needed for it, net of what it already holds. The alternative (wantInputDriven:
        // true) answers with whatever it was passed, so a caller asking for N output frames reads
        // exactly N input frames whatever the ratio is. Upsampling then strands the unconsumed
        // remainder inside the resampler on every callback - unbounded latency growth. Fixed in
        // the SDK's correction source by PR #246; the same inversion was here.
        _resampler.SetFeedMode(wantInputDriven: false);
        _resampler.SetRates(source.WaveFormat.SampleRate, deviceSampleRate);

        _logger?.LogInformation(
            "Device-rate conversion configured: {SourceRate}Hz -> {DeviceRate}Hz",
            source.WaveFormat.SampleRate,
            deviceSampleRate);
    }

    /// <inheritdoc/>
    public WaveFormat WaveFormat { get; }

    /// <summary>
    /// Gets the count of callbacks where the source returned no samples at all (buffer empty).
    /// </summary>
    /// <remarks>
    /// High counts indicate the upstream buffer is frequently empty, suggesting
    /// network issues or insufficient buffering.
    /// </remarks>
    public long SourceEmptyCount => Interlocked.Read(ref _sourceEmptyCount);

    /// <summary>
    /// Gets the count of callbacks where the resampler produced fewer samples than requested and
    /// the residual had to be concealed.
    /// </summary>
    public long ResamplerShortCount => Interlocked.Read(ref _resamplerShortCount);

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Always fills <paramref name="count"/> samples and returns <paramref name="count"/>: NAudio's
    /// <c>WasapiOut</c> treats a short read as a partial buffer and zero-fills the rest (which would
    /// manufacture the very silence gap the chain works to avoid), and a zero read as end of stream.
    /// </para>
    /// <para>
    /// The drain loop covers the pass that comes up short because <c>ResamplePrepare</c> can only
    /// return an integer input count: converting 48 kHz to 44.1 kHz needs 480.17 input frames per
    /// 441 output, and the dropped fraction accumulates until a pass produces one frame less than
    /// asked. Feeding the small remainder and asking again fills the block from real content. The
    /// extra input is read only on the short callbacks, so the average converges on the true
    /// requirement rather than over-reading every time.
    /// </para>
    /// </remarks>
    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var channels = WaveFormat.Channels;
        var outputFrames = count / channels;
        var totalFramesGenerated = 0;

        while (totalFramesGenerated < outputFrames)
        {
            var framesWanted = outputFrames - totalFramesGenerated;
            var framesNeeded = _resampler.ResamplePrepare(framesWanted, channels, out var inBuffer, out var inBufferOffset);

            // ResamplePrepare returns 0 when it already holds enough input to make more output
            // without reading; only a NON-zero request that returns nothing is a genuine stall.
            var framesRead = framesNeeded > 0
                ? _source.Read(inBuffer, inBufferOffset, framesNeeded * channels) / channels
                : 0;

            if (framesNeeded > 0 && framesRead == 0)
            {
                break;
            }

            var framesGenerated = _resampler.ResampleOut(
                buffer, offset + (totalFramesGenerated * channels), framesRead, framesWanted, channels);

            if (framesGenerated == 0)
            {
                // No forward progress despite the read (the filter still needs lookahead it does
                // not have). Bail rather than spin; the residual is concealed below.
                break;
            }

            totalFramesGenerated += framesGenerated;
        }

        var samplesGenerated = totalFramesGenerated * channels;

        if (samplesGenerated == 0)
        {
            // Nothing at all this callback - silence is correct here; holding a sample across a
            // sustained stall parks a DC offset on the speaker and thumps when it releases.
            Interlocked.Increment(ref _sourceEmptyCount);
            LogUnderrunIfNeeded("source empty");
            Array.Fill(buffer, 0f, offset, count);
            return count;
        }

        Array.Copy(buffer, offset + samplesGenerated - channels, _lastFrame, 0, channels);

        if (samplesGenerated < count)
        {
            // Conceal the residual by holding the last produced frame: a held sample keeps the
            // waveform continuous, a silence gap is a step to zero and back, i.e. a click.
            Interlocked.Increment(ref _resamplerShortCount);
            LogUnderrunIfNeeded($"resampler short: got {samplesGenerated}, needed {count}");

            for (var i = samplesGenerated; i < count; i++)
            {
                buffer[offset + i] = _lastFrame[i % channels];
            }
        }

        return count;
    }

    /// <summary>
    /// Logs an underrun event with rate limiting to avoid flooding logs.
    /// </summary>
    /// <param name="reason">Description of the underrun cause.</param>
    private void LogUnderrunIfNeeded(string reason)
    {
        var now = DateTime.UtcNow.Ticks;
        var lastLog = Interlocked.Read(ref _lastUnderrunLogTicks);

        if (now - lastLog <= UnderrunLogIntervalTicks)
        {
            return;
        }

        // May fail if another thread beat us here; that thread logs instead.
        if (Interlocked.CompareExchange(ref _lastUnderrunLogTicks, now, lastLog) == lastLog)
        {
            _logger?.LogWarning(
                "Device-rate conversion underrun ({Reason}). Total: sourceEmpty={SourceEmpty}, resamplerShort={ResamplerShort}",
                reason,
                Interlocked.Read(ref _sourceEmptyCount),
                Interlocked.Read(ref _resamplerShortCount));
        }
    }
}
