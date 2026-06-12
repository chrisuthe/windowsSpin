// <copyright file="BufferedAudioSampleSource.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using SendspinClient.Services.Diagnostics;

namespace SendspinClient.Services.Audio;

/// <summary>
/// Bridges <see cref="ITimedAudioBuffer"/> to <see cref="IAudioSampleSource"/>.
/// Provides current local time to the buffer for timed sample release.
/// </summary>
/// <remarks>
/// This class is called from NAudio's audio thread and must be fast and non-blocking.
/// It provides the current time to the timed buffer so that audio is released
/// at the correct moment for synchronized playback.
/// </remarks>
public sealed class BufferedAudioSampleSource : IAudioSampleSource
{
    private readonly ITimedAudioBuffer _buffer;
    private readonly Func<long> _getCurrentTimeMicroseconds;
    private readonly ReadCallbackGapTracker? _gapTracker;
    private readonly double _samplesPerMs;

    /// <inheritdoc/>
    public AudioFormat Format => _buffer.Format;

    /// <summary>
    /// Gets the underlying audio buffer.
    /// Used by the player to subscribe to rate changes for resampling sync correction.
    /// </summary>
    public ITimedAudioBuffer Buffer => _buffer;

    /// <summary>
    /// Initializes a new instance of the <see cref="BufferedAudioSampleSource"/> class.
    /// </summary>
    /// <param name="buffer">The timed audio buffer to read from.</param>
    /// <param name="getCurrentTimeMicroseconds">Function that returns current local time in microseconds.</param>
    /// <param name="gapTracker">Optional tracker for audio-thread starvation diagnostics.</param>
    public BufferedAudioSampleSource(
        ITimedAudioBuffer buffer,
        Func<long> getCurrentTimeMicroseconds,
        ReadCallbackGapTracker? gapTracker = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(getCurrentTimeMicroseconds);

        _buffer = buffer;
        _getCurrentTimeMicroseconds = getCurrentTimeMicroseconds;
        _gapTracker = gapTracker;
        _samplesPerMs = (double)buffer.Format.SampleRate * buffer.Format.Channels / 1000.0;
        _gapTracker?.Reset();
    }

    /// <inheritdoc/>
    public int Read(float[] buffer, int offset, int count)
    {
        if (_gapTracker is not null)
        {
            // Expected interval: samples requested ÷ samples per ms (factor cached at construction)
            _gapTracker.RecordRead(Environment.TickCount64, count / _samplesPerMs);
        }

        var currentTime = _getCurrentTimeMicroseconds();

        // Read from the timed buffer using the portion we need to fill
        var span = buffer.AsSpan(offset, count);
        var read = _buffer.Read(span, currentTime);

        // Fill remainder with silence if underrun
        if (read < count)
        {
            buffer.AsSpan(offset + read, count - read).Fill(0f);
        }

        // Always return requested count to keep NAudio happy (silence-filled if needed)
        return count;
    }
}
