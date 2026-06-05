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

    /// <summary>
    /// Exponentially eases <paramref name="current"/> toward <paramref name="target"/> for a frame
    /// of <paramref name="dtSeconds"/>. <paramref name="timeConstantSeconds"/> is the e-folding time
    /// (smaller = snappier). A non-positive time constant snaps to the target; dt &lt;= 0 is a no-op.
    /// Frame-rate independent.
    /// </summary>
    public static double Ease(double current, double target, double dtSeconds, double timeConstantSeconds)
    {
        if (dtSeconds <= 0.0)
        {
            return current;
        }

        if (timeConstantSeconds <= 0.0)
        {
            return target;
        }

        var alpha = 1.0 - Math.Exp(-dtSeconds / timeConstantSeconds);
        return current + ((target - current) * alpha);
    }

    /// <summary>
    /// Decays <paramref name="current"/> toward zero with the given <paramref name="halfLifeSeconds"/>
    /// over a frame of <paramref name="dtSeconds"/>. Used for the additive beat-pulse envelope: the
    /// caller adds an impulse on a beat, then calls this each frame. Non-positive half-life returns 0.
    /// </summary>
    public static double Decay(double current, double dtSeconds, double halfLifeSeconds)
    {
        if (dtSeconds <= 0.0)
        {
            return current;
        }

        if (halfLifeSeconds <= 0.0)
        {
            return 0.0;
        }

        return current * Math.Pow(0.5, dtSeconds / halfLifeSeconds);
    }

    /// <summary>Minimum blob scale (at zero energy).</summary>
    public const double ScaleMin = 0.85;

    /// <summary>Blob scale span contributed by energy (0..1).</summary>
    public const double ScaleEnergySpan = 0.30;

    /// <summary>Additional blob scale contributed by a full (1.0) beat pulse.</summary>
    public const double ScalePulseSpan = 0.15;

    /// <summary>Minimum blob opacity (at zero energy).</summary>
    public const double OpacityMin = 0.45;

    /// <summary>Blob opacity span contributed by energy (0..1).</summary>
    public const double OpacityEnergySpan = 0.40;

    /// <summary>Blob render scale from eased energy and the current beat pulse.</summary>
    public static double BlobScale(double energy, double pulse)
    {
        var e = Math.Clamp(energy, 0.0, 1.0);
        var p = Math.Max(pulse, 0.0);
        return ScaleMin + (e * ScaleEnergySpan) + (p * ScalePulseSpan);
    }

    /// <summary>Blob opacity from eased energy.</summary>
    public static double BlobOpacity(double energy)
    {
        var e = Math.Clamp(energy, 0.0, 1.0);
        return OpacityMin + (e * OpacityEnergySpan);
    }
}
