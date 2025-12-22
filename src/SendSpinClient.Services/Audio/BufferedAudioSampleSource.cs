// <copyright file="BufferedAudioSampleSource.cs" company="SendSpin">
// Copyright (c) SendSpin. All rights reserved.
// </copyright>

using SendSpinClient.Core.Audio;
using SendSpinClient.Core.Models;

namespace SendSpinClient.Services.Audio;

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

    /// <inheritdoc/>
    public AudioFormat Format => _buffer.Format;

    /// <summary>
    /// Initializes a new instance of the <see cref="BufferedAudioSampleSource"/> class.
    /// </summary>
    /// <param name="buffer">The timed audio buffer to read from.</param>
    /// <param name="getCurrentTimeMicroseconds">Function that returns current local time in microseconds.</param>
    public BufferedAudioSampleSource(
        ITimedAudioBuffer buffer,
        Func<long> getCurrentTimeMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(getCurrentTimeMicroseconds);

        _buffer = buffer;
        _getCurrentTimeMicroseconds = getCurrentTimeMicroseconds;
    }

    /// <inheritdoc/>
    public int Read(float[] buffer, int offset, int count)
    {
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
