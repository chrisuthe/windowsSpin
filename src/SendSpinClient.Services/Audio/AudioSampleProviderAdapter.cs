// <copyright file="AudioSampleProviderAdapter.cs" company="SendSpin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using NAudio.Wave;
using SendSpinClient.Core.Audio;
using SendSpinClient.Core.Models;

namespace SendSpinClient.Services.Audio;

/// <summary>
/// Adapts <see cref="IAudioSampleSource"/> to NAudio's <see cref="ISampleProvider"/> interface.
/// This allows our audio pipeline to integrate with NAudio's playback infrastructure.
/// </summary>
internal sealed class AudioSampleProviderAdapter : ISampleProvider
{
    private readonly IAudioSampleSource _source;

    /// <summary>
    /// Gets the wave format for NAudio.
    /// </summary>
    public WaveFormat WaveFormat { get; }

    /// <summary>
    /// Gets or sets the volume multiplier (0.0 to 1.0).
    /// Applied in software by multiplying samples.
    /// </summary>
    public float Volume { get; set; } = 1.0f;

    /// <summary>
    /// Gets or sets whether output is muted.
    /// When muted, zeros are written instead of actual audio.
    /// </summary>
    public bool IsMuted { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioSampleProviderAdapter"/> class.
    /// </summary>
    /// <param name="source">The audio sample source to adapt.</param>
    /// <param name="format">Audio format configuration.</param>
    public AudioSampleProviderAdapter(IAudioSampleSource source, AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(format);

        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);
    }

    /// <summary>
    /// Reads samples from the source and fills the buffer.
    /// Called by NAudio from its audio playback thread.
    /// </summary>
    /// <param name="buffer">Buffer to fill with samples.</param>
    /// <param name="offset">Offset into buffer.</param>
    /// <param name="count">Number of samples requested.</param>
    /// <returns>Number of samples written.</returns>
    public int Read(float[] buffer, int offset, int count)
    {
        if (IsMuted)
        {
            // Fill with silence when muted
            Array.Fill(buffer, 0f, offset, count);
            return count;
        }

        var samplesRead = _source.Read(buffer, offset, count);

        // Apply volume if not at full (avoid unnecessary multiply operations)
        var volume = Volume;
        if (volume < 0.999f)
        {
            var span = buffer.AsSpan(offset, samplesRead);
            for (var i = 0; i < span.Length; i++)
            {
                span[i] *= volume;
            }
        }

        return samplesRead;
    }
}
