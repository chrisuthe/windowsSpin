using Sendspin.SDK.Models;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>
/// The one format every capture source delivers: 48 kHz, 16-bit, stereo PCM.
/// </summary>
/// <remarks>
/// Fixed, so the device's format never changes: switching input — or between generated silence
/// and real capture — then needs no new <c>client_stream/start</c>. Windows converts each input
/// to it (<c>AUTOCONVERTPCM</c>), and it is what Music Assistant's source bridge converts to
/// anyway.
/// </remarks>
public static class CaptureFormat
{
    /// <summary>Sample rate in Hz.</summary>
    public const int SampleRate = 48000;

    /// <summary>Interleaved channel count.</summary>
    public const int Channels = 2;

    /// <summary>Bits per sample (signed little-endian integer).</summary>
    public const int BitsPerSample = 16;

    /// <summary>Bytes in one frame (one sample per channel).</summary>
    public const int BytesPerFrame = Channels * BitsPerSample / 8;

    /// <summary>Creates the SDK description of this format.</summary>
    /// <returns>A new <see cref="AudioFormat"/> for PCM 48000/2/16.</returns>
    public static AudioFormat Create() => new()
    {
        Codec = AudioCodecs.Pcm,
        SampleRate = SampleRate,
        Channels = Channels,
        BitDepth = BitsPerSample,
    };

    /// <summary>Duration of <paramref name="frames"/> frames, rounded down to whole microseconds.</summary>
    /// <param name="frames">Frame count.</param>
    /// <returns>Microseconds.</returns>
    public static long FramesToMicroseconds(long frames) => frames * 1_000_000 / SampleRate;
}
