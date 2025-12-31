// <copyright file="OpusDecoder.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Concentus;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Audio.Codecs;

/// <summary>
/// Opus audio decoder using Concentus library.
/// Decodes Opus-encoded audio frames to interleaved float PCM samples.
/// </summary>
public sealed class OpusDecoder : IAudioDecoder
{
    private readonly IOpusDecoder _decoder;
    private readonly short[] _shortBuffer;
    private bool _disposed;

    /// <inheritdoc/>
    public AudioFormat Format { get; }

    /// <inheritdoc/>
    public int MaxSamplesPerFrame { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="OpusDecoder"/> class.
    /// </summary>
    /// <param name="format">Audio format configuration.</param>
    /// <exception cref="ArgumentException">Thrown when format is not Opus.</exception>
    public OpusDecoder(AudioFormat format)
    {
        if (!string.Equals(format.Codec, AudioCodecs.Opus, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Expected Opus format, got {format.Codec}", nameof(format));
        }

        Format = format;

        // Create Concentus decoder using the factory (preferred over deprecated constructor)
        _decoder = OpusCodecFactory.CreateDecoder(format.SampleRate, format.Channels);

        // Opus max frame is 120ms, but typically 20ms (960 samples at 48kHz per channel)
        // Allocate for worst case: 120ms * sampleRate / 1000 * channels
        MaxSamplesPerFrame = (format.SampleRate / 1000 * 120) * format.Channels;
        _shortBuffer = new short[MaxSamplesPerFrame];
    }

    /// <inheritdoc/>
    public int Decode(ReadOnlySpan<byte> encodedData, Span<float> decodedSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Decode to short buffer using Span overload (Concentus outputs interleaved shorts)
        // frameSize is samples per channel
        var maxFrameSize = MaxSamplesPerFrame / Format.Channels;
        var samplesPerChannel = _decoder.Decode(encodedData, _shortBuffer.AsSpan(), maxFrameSize);

        var totalSamples = samplesPerChannel * Format.Channels;

        // Convert shorts to normalized floats [-1.0, 1.0]
        for (int i = 0; i < totalSamples && i < decodedSamples.Length; i++)
        {
            decodedSamples[i] = _shortBuffer[i] / 32768f;
        }

        return Math.Min(totalSamples, decodedSamples.Length);
    }

    /// <inheritdoc/>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _decoder.ResetState();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Concentus decoder doesn't implement IDisposable
    }
}
