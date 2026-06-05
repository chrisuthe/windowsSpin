namespace SendspinClient.Services.Visualization;

/// <summary>
/// Pure, dependency-free math for the Ambient Glow backdrop. No WPF or SDK types so it can be
/// unit-tested against any SDK version. The ViewModel adapts SDK frames into these primitives.
/// </summary>
public static class AmbientMath
{
    /// <summary>Maximum raw loudness value carried by a visualizer loudness frame.</summary>
    public const int LoudnessMax = 65535;

    /// <summary>
    /// Maps a raw loudness value (0..65535, already dB-normalized by the server) to a 0..1 energy
    /// level. Null (no loudness frame yet) maps to 0. Out-of-range values are clamped.
    /// </summary>
    public static double NormalizeLoudness(int? rawLoudness)
    {
        if (rawLoudness is not { } raw)
        {
            return 0.0;
        }

        return Math.Clamp(raw / (double)LoudnessMax, 0.0, 1.0);
    }
}
