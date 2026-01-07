// <copyright file="DiagnosticAudioRingBuffer.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace Sendspin.SDK.Diagnostics;

/// <summary>
/// Lock-free single-producer single-consumer (SPSC) circular buffer for audio samples.
/// Designed for minimal overhead on the audio thread.
/// </summary>
/// <remarks>
/// <para>
/// This buffer is specifically designed for diagnostic audio capture where:
/// <list type="bullet">
/// <item>Audio thread writes samples (producer) - must never block</item>
/// <item>Save thread reads samples (consumer) - can take its time</item>
/// </list>
/// </para>
/// <para>
/// The buffer uses a power-of-2 size for efficient modulo operations and
/// <see cref="Volatile"/> read/write for thread safety without locks.
/// When the buffer is full, old samples are overwritten (circular behavior).
/// </para>
/// </remarks>
public sealed class DiagnosticAudioRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacity;
    private readonly int _mask;

    // Write index - only modified by producer (audio thread)
    private long _writeIndex;

    // Sample rate and channel info for WAV output
    private readonly int _sampleRate;
    private readonly int _channels;

    /// <summary>
    /// Gets the sample rate of the audio in this buffer.
    /// </summary>
    public int SampleRate => _sampleRate;

    /// <summary>
    /// Gets the number of channels in the audio.
    /// </summary>
    public int Channels => _channels;

    /// <summary>
    /// Gets the buffer capacity in samples.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Gets the total number of samples written since creation.
    /// This is a cumulative count that can exceed capacity (wraps around).
    /// </summary>
    public long TotalSamplesWritten => Volatile.Read(ref _writeIndex);

    /// <summary>
    /// Gets the duration of audio currently in the buffer in seconds.
    /// </summary>
    public double BufferedSeconds
    {
        get
        {
            var samplesWritten = Volatile.Read(ref _writeIndex);
            var samplesAvailable = Math.Min(samplesWritten, _capacity);
            return (double)samplesAvailable / _sampleRate / _channels;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosticAudioRingBuffer"/> class.
    /// </summary>
    /// <param name="sampleRate">The audio sample rate (e.g., 48000).</param>
    /// <param name="channels">The number of audio channels (e.g., 2 for stereo).</param>
    /// <param name="durationSeconds">The buffer duration in seconds. Will be rounded up to power of 2.</param>
    public DiagnosticAudioRingBuffer(int sampleRate, int channels, int durationSeconds = 45)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(channels, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(durationSeconds, 0);

        _sampleRate = sampleRate;
        _channels = channels;

        // Calculate required capacity and round up to next power of 2
        var requiredSamples = sampleRate * channels * durationSeconds;
        _capacity = RoundUpToPowerOfTwo(requiredSamples);
        _mask = _capacity - 1;
        _buffer = new float[_capacity];
    }

    /// <summary>
    /// Writes samples to the buffer. Called from the audio thread.
    /// </summary>
    /// <remarks>
    /// This method is designed to be as fast as possible:
    /// <list type="bullet">
    /// <item>No locks</item>
    /// <item>No allocations</item>
    /// <item>No branches in the hot path (except the loop)</item>
    /// </list>
    /// When the buffer is full, old samples are overwritten.
    /// </remarks>
    /// <param name="samples">The samples to write.</param>
    public void Write(ReadOnlySpan<float> samples)
    {
        var writeIdx = Volatile.Read(ref _writeIndex);

        // Copy samples to buffer using mask for wrap-around
        // This is the hot path - keep it simple
        foreach (var sample in samples)
        {
            _buffer[writeIdx & _mask] = sample;
            writeIdx++;
        }

        Volatile.Write(ref _writeIndex, writeIdx);
    }

    /// <summary>
    /// Captures a snapshot of the current buffer contents.
    /// Called from the save thread (not audio thread).
    /// </summary>
    /// <returns>
    /// A tuple containing:
    /// <list type="bullet">
    /// <item>samples: Array of captured audio samples</item>
    /// <item>startIndex: The cumulative sample index of the first sample in the array</item>
    /// </list>
    /// </returns>
    /// <remarks>
    /// This method allocates a new array and copies the buffer contents.
    /// It should only be called from a background thread, not the audio thread.
    /// The returned startIndex can be used to correlate with <see cref="SyncMetricSnapshot.SamplePosition"/>.
    /// </remarks>
    public (float[] Samples, long StartIndex) CaptureSnapshot()
    {
        // Read the current write position
        var writeIdx = Volatile.Read(ref _writeIndex);

        // Calculate how many samples we have (up to capacity)
        var samplesAvailable = (int)Math.Min(writeIdx, _capacity);
        var startIdx = writeIdx - samplesAvailable;

        // Allocate result array
        var result = new float[samplesAvailable];

        // Copy samples in correct order
        for (var i = 0; i < samplesAvailable; i++)
        {
            result[i] = _buffer[(startIdx + i) & _mask];
        }

        return (result, startIdx);
    }

    /// <summary>
    /// Resets the buffer to empty state.
    /// </summary>
    /// <remarks>
    /// This should only be called when the audio thread is not writing.
    /// </remarks>
    public void Clear()
    {
        Volatile.Write(ref _writeIndex, 0);
        Array.Clear(_buffer);
    }

    /// <summary>
    /// Rounds a value up to the next power of 2.
    /// </summary>
    private static int RoundUpToPowerOfTwo(int value)
    {
        if (value <= 0)
        {
            return 1;
        }

        // Subtract 1 to handle exact powers of 2
        value--;

        // Spread the highest bit to all lower positions
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;

        // Add 1 to get the next power of 2
        return value + 1;
    }
}
