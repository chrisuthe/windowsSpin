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
    public const double ScaleMin = 0.82;

    /// <summary>Blob scale span contributed by energy (0..1).</summary>
    public const double ScaleEnergySpan = 0.50;

    /// <summary>Additional blob scale contributed by a full (1.0) beat pulse.</summary>
    public const double ScalePulseSpan = 0.35;

    /// <summary>Minimum blob opacity (at zero energy). Blobs stay clearly present even when quiet.</summary>
    public const double OpacityMin = 0.55;

    /// <summary>Blob opacity span contributed by energy (0..1).</summary>
    public const double OpacityEnergySpan = 0.42;

    /// <summary>
    /// Minimum effective intensity. The 0% slider position floors to this so the backdrop
    /// stays faintly alive (subtle, slow, dim) rather than going fully dark. Use the enable
    /// toggle to disable the effect entirely. Applied at the ViewModel boundary, not here.
    /// </summary>
    public const double IntensityFloor = 0.15;

    /// <summary>
    /// Blob render scale from eased energy and the current beat pulse, scaled by
    /// <paramref name="intensity"/> (1.0 = default). Energy and pulse are clamped to [0,1];
    /// intensity is clamped to be non-negative. <c>ScaleMin</c> is never scaled, so blobs keep
    /// their minimum size at intensity 0.
    /// </summary>
    public static double BlobScale(double energy, double pulse, double intensity = 1.0)
    {
        var e = Math.Clamp(energy, 0.0, 1.0);
        var p = Math.Clamp(pulse, 0.0, 1.0);
        var i = Math.Max(0.0, intensity);
        return ScaleMin + (i * ((e * ScaleEnergySpan) + (p * ScalePulseSpan)));
    }

    /// <summary>
    /// Blob opacity from eased energy, scaled by <paramref name="intensity"/> (1.0 = default).
    /// The whole opacity scales, so intensity 0 is invisible; the result is clamped to [0,1].
    /// </summary>
    public static double BlobOpacity(double energy, double intensity = 1.0)
    {
        var e = Math.Clamp(energy, 0.0, 1.0);
        var i = Math.Max(0.0, intensity);
        return Math.Clamp(i * (OpacityMin + (e * OpacityEnergySpan)), 0.0, 1.0);
    }

    /// <summary>Album-art breathe scale span from energy (0..1) at intensity 1.</summary>
    public const double BreathScaleEnergySpan = 0.06;

    /// <summary>Album-art breathe scale span from a full beat pulse at intensity 1.</summary>
    public const double BreathScalePulseSpan = 0.04;

    /// <summary>Baseline glow strength so the art keeps a faint aura even when quiet.</summary>
    public const double BreathGlowBase = 0.15;

    /// <summary>Glow strength span contributed by energy (0..1).</summary>
    public const double BreathGlowEnergySpan = 0.85;

    /// <summary>
    /// Album-art breathe scale from eased energy and beat pulse, scaled by
    /// <paramref name="intensity"/> (1.0 = default). Rests at 1.0 (the art is never shrunk);
    /// energy/pulse are clamped to [0,1] and intensity to non-negative.
    /// </summary>
    public static double BreathScale(double energy, double pulse, double intensity = 1.0)
    {
        var e = Math.Clamp(energy, 0.0, 1.0);
        var p = Math.Clamp(pulse, 0.0, 1.0);
        var i = Math.Max(0.0, intensity);
        return 1.0 + (i * ((e * BreathScaleEnergySpan) + (p * BreathScalePulseSpan)));
    }

    /// <summary>
    /// Album-art glow strength (0..1) from eased energy, scaled by <paramref name="intensity"/>
    /// (1.0 = default). The animator maps this to blur/opacity. Clamped to [0,1].
    /// </summary>
    public static double BreathGlow(double energy, double intensity = 1.0)
    {
        var e = Math.Clamp(energy, 0.0, 1.0);
        var i = Math.Max(0.0, intensity);
        return Math.Clamp(i * (BreathGlowBase + (e * BreathGlowEnergySpan)), 0.0, 1.0);
    }
}
