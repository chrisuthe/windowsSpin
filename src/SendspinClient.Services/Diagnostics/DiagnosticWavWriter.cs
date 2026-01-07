// <copyright file="DiagnosticWavWriter.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Text;
using Sendspin.SDK.Diagnostics;

namespace SendspinClient.Services.Diagnostics;

/// <summary>
/// Writes WAV files with embedded cue markers for diagnostic audio analysis.
/// </summary>
/// <remarks>
/// <para>
/// Creates standard PCM WAV files with embedded cue points that can be viewed
/// in audio editors like Audacity. Each cue point includes a label showing
/// sync metrics at that moment.
/// </para>
/// <para>
/// WAV file structure:
/// <list type="bullet">
/// <item>RIFF header</item>
/// <item>fmt chunk (audio format)</item>
/// <item>data chunk (PCM samples)</item>
/// <item>cue chunk (marker positions)</item>
/// <item>LIST adtl chunk (marker labels)</item>
/// </list>
/// </para>
/// </remarks>
public static class DiagnosticWavWriter
{
    /// <summary>
    /// Writes a WAV file with embedded cue markers.
    /// </summary>
    /// <param name="path">The output file path.</param>
    /// <param name="samples">The audio samples (32-bit float).</param>
    /// <param name="sampleRate">The sample rate in Hz.</param>
    /// <param name="channels">The number of channels.</param>
    /// <param name="markers">The metric snapshots to embed as markers.</param>
    /// <param name="startSamplePosition">The sample position of the first sample in the buffer.</param>
    public static void WriteWavWithMarkers(
        string path,
        float[] samples,
        int sampleRate,
        int channels,
        SyncMetricSnapshot[] markers,
        long startSamplePosition)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        // Convert float samples to 16-bit PCM for broader compatibility
        var pcmSamples = ConvertTo16BitPcm(samples);

        // Calculate chunk sizes
        var fmtChunkSize = 16; // Standard PCM format chunk
        var dataChunkSize = pcmSamples.Length;
        var cueChunkSize = markers.Length > 0 ? 4 + (24 * markers.Length) : 0;
        var adtlChunkSize = CalculateAdtlChunkSize(markers, startSamplePosition, sampleRate, channels);

        // RIFF header size = everything except "RIFF" and size field
        var riffSize = 4 + // "WAVE"
                       8 + fmtChunkSize + // fmt chunk
                       8 + dataChunkSize + // data chunk
                       (cueChunkSize > 0 ? 8 + cueChunkSize : 0) + // cue chunk (optional)
                       (adtlChunkSize > 0 ? 8 + 4 + adtlChunkSize : 0); // LIST chunk (optional)

        // RIFF header
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(riffSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        WriteFmtChunk(writer, sampleRate, channels);

        // data chunk
        WriteDataChunk(writer, pcmSamples);

        // cue chunk (if we have markers)
        if (markers.Length > 0)
        {
            WriteCueChunk(writer, markers, startSamplePosition, sampleRate, channels);
            WriteListAdtlChunk(writer, markers, startSamplePosition, sampleRate, channels);
        }
    }

    /// <summary>
    /// Converts 32-bit float samples to 16-bit PCM.
    /// </summary>
    private static byte[] ConvertTo16BitPcm(float[] samples)
    {
        var result = new byte[samples.Length * 2];

        for (var i = 0; i < samples.Length; i++)
        {
            // Clamp to [-1, 1] and convert to 16-bit
            var clamped = Math.Clamp(samples[i], -1.0f, 1.0f);
            var pcm = (short)(clamped * 32767);
            result[i * 2] = (byte)(pcm & 0xFF);
            result[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }

        return result;
    }

    /// <summary>
    /// Writes the fmt chunk (audio format).
    /// </summary>
    private static void WriteFmtChunk(BinaryWriter writer, int sampleRate, int channels)
    {
        const int bitsPerSample = 16;
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // Chunk size (16 for PCM)
        writer.Write((short)1); // Audio format (1 = PCM)
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write((short)bitsPerSample);
    }

    /// <summary>
    /// Writes the data chunk (audio samples).
    /// </summary>
    private static void WriteDataChunk(BinaryWriter writer, byte[] pcmSamples)
    {
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcmSamples.Length);
        writer.Write(pcmSamples);
    }

    /// <summary>
    /// Writes the cue chunk (marker positions).
    /// </summary>
    private static void WriteCueChunk(
        BinaryWriter writer,
        SyncMetricSnapshot[] markers,
        long startSamplePosition,
        int sampleRate,
        int channels)
    {
        // Cue chunk header
        writer.Write(Encoding.ASCII.GetBytes("cue "));
        writer.Write(4 + (24 * markers.Length)); // Chunk size
        writer.Write(markers.Length); // Number of cue points

        for (var i = 0; i < markers.Length; i++)
        {
            var marker = markers[i];

            // Calculate sample offset within the WAV file (in frames, not samples)
            var sampleOffset = marker.SamplePosition - startSamplePosition;
            var frameOffset = (int)(sampleOffset / channels); // Convert to frames

            if (frameOffset < 0)
            {
                frameOffset = 0;
            }

            writer.Write(i + 1); // Cue point ID (1-indexed)
            writer.Write(frameOffset); // Position (sample offset in playlist order)
            writer.Write(Encoding.ASCII.GetBytes("data")); // Data chunk ID
            writer.Write(0); // Chunk start (0 for data chunk)
            writer.Write(0); // Block start
            writer.Write(frameOffset); // Sample offset within data chunk
        }
    }

    /// <summary>
    /// Calculates the size of all labl and note sub-chunks.
    /// </summary>
    private static int CalculateAdtlChunkSize(
        SyncMetricSnapshot[] markers,
        long startSamplePosition,
        int sampleRate,
        int channels)
    {
        if (markers.Length == 0)
        {
            return 0;
        }

        var size = 0;

        foreach (var marker in markers)
        {
            // labl sub-chunk: 4 (chunk ID) + 4 (size) + 4 (cue ID) + label bytes + padding
            var label = marker.FormatShortLabel();
            var labelBytes = Encoding.ASCII.GetByteCount(label) + 1; // +1 for null terminator
            var labelPadded = (labelBytes + 1) & ~1; // Pad to even
            size += 8 + 4 + labelPadded;
        }

        return size;
    }

    /// <summary>
    /// Writes the LIST adtl chunk (marker labels).
    /// </summary>
    private static void WriteListAdtlChunk(
        BinaryWriter writer,
        SyncMetricSnapshot[] markers,
        long startSamplePosition,
        int sampleRate,
        int channels)
    {
        var adtlSize = CalculateAdtlChunkSize(markers, startSamplePosition, sampleRate, channels);

        // LIST chunk header
        writer.Write(Encoding.ASCII.GetBytes("LIST"));
        writer.Write(4 + adtlSize); // Chunk size (type ID + sub-chunks)
        writer.Write(Encoding.ASCII.GetBytes("adtl"));

        // Write labl sub-chunks
        for (var i = 0; i < markers.Length; i++)
        {
            var marker = markers[i];
            var label = marker.FormatShortLabel();
            var labelBytes = Encoding.ASCII.GetBytes(label + '\0'); // Null-terminated
            var paddedSize = (labelBytes.Length + 1) & ~1; // Pad to even boundary

            writer.Write(Encoding.ASCII.GetBytes("labl"));
            writer.Write(4 + labelBytes.Length); // Size = cue ID + label
            writer.Write(i + 1); // Cue point ID (1-indexed)
            writer.Write(labelBytes);

            // Pad to even boundary if needed
            if (labelBytes.Length % 2 != 0)
            {
                writer.Write((byte)0);
            }
        }
    }
}
