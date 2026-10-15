// <copyright file="AudioSampleProviderAdapter.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using NAudio.Wave;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace Sendspin.Windows.Services.Audio;

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
    /// Gets or sets the volume level (0.0 to 1.0).
    /// Applied in software using a power curve for perceived loudness.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per the Sendspin spec: "Volume values (0-100) represent perceived loudness,
    /// not linear amplitude. Players must convert these values to appropriate
    /// amplitude for their audio hardware."
    /// </para>
    /// <para>
    /// We use a power curve (amplitude = volume^1.5) matching the Python CLI
    /// reference implementation. This provides natural-sounding volume control
    /// that is gentler at high volumes.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// Works on the whole requested block rather than the source's return value, and always reports
    /// the block full. Every <see cref="IAudioSampleSource"/> in this chain fills all
    /// <paramref name="count"/> samples, but the SDK's <c>SyncCorrectedSampleSource</c> returns only
    /// how many of them came from the buffer — the rest being concealed or silence. Volume and mute
    /// must cover that tail too, and NAudio must be told the block is full: <c>WasapiOut</c> treats a
    /// short read as a partial buffer and zero-fills the remainder (re-manufacturing the silence gap
    /// concealment exists to avoid), and a zero read as end of stream.
    /// </remarks>
    /// <param name="buffer">Buffer to fill with samples.</param>
    /// <param name="offset">Offset into buffer.</param>
    /// <param name="count">Number of samples requested.</param>
    /// <returns>Number of samples written, always <paramref name="count"/>.</returns>
    public int Read(float[] buffer, int offset, int count)
    {
        _source.Read(buffer, offset, count);

        if (IsMuted)
        {
            Array.Fill(buffer, 0f, offset, count);
            return count;
        }

        var volume = Volume;
        if (volume < 0.999f)
        {
            var amplitude = (float)Math.Pow(volume, 1.5);

            var span = buffer.AsSpan(offset, count);
            for (var i = 0; i < span.Length; i++)
            {
                span[i] *= amplitude;
            }
        }

        return count;
    }
}
