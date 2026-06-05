using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SendspinClient.Services.Visualization;
using Sendspin.SDK.Models;

namespace SendspinClient.ViewModels;

/// <summary>
/// Target state for the Ambient Glow backdrop. The View (<c>AmbientBackdropView</c>) reads these
/// properties from its render loop and eases the displayed visuals toward them. All mutation happens
/// on the UI thread (MainViewModel marshals SDK events via the Dispatcher), so no locking is needed.
/// </summary>
public partial class AmbientBackdropViewModel : ObservableObject
{
    private static readonly Color FallbackBase = Color.FromRgb(0x1E, 0x1E, 0x2E);
    private static readonly Color FallbackBlob1 = Color.FromRgb(0x6D, 0x28, 0xD9);
    private static readonly Color FallbackBlob2 = Color.FromRgb(0x06, 0xB6, 0xD4);
    private static readonly Color FallbackBlob3 = Color.FromRgb(0xDB, 0x27, 0x77);

    /// <summary>The selected backdrop style. The blob backdrop renders only in AmbientGlow mode.</summary>
    [ObservableProperty]
    private BackdropMode _mode = BackdropMode.AmbientGlow;

    partial void OnModeChanged(BackdropMode value) => UpdateActive();

    /// <summary>True when the style is Ambient Glow and a real color palette has been received.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>Target energy (0..1) from the latest loudness frame; eased by the View.</summary>
    public double TargetEnergy { get; private set; }

    /// <summary>Base (background) color target.</summary>
    public Color BaseColor { get; private set; } = FallbackBase;

    /// <summary>The three blob color targets.</summary>
    public Color BlobColor1 { get; private set; } = FallbackBlob1;
    public Color BlobColor2 { get; private set; } = FallbackBlob2;
    public Color BlobColor3 { get; private set; } = FallbackBlob3;

    private double _intensity = 1.0;

    /// <summary>
    /// Effective intensity (<c>IntensityFloor</c>..2.0) consumed by the renderer. The raw 0
    /// setting is floored so the backdrop never goes fully dark via the slider; choose the Off
    /// style (<see cref="SetMode"/>) to disable entirely.
    /// </summary>
    public double Intensity => Math.Max(AmbientMath.IntensityFloor, _intensity);

    /// <summary>Sets the raw intensity (0..2) from the settings slider. Does not affect IsActive.</summary>
    public void SetIntensity(double raw) => _intensity = Math.Clamp(raw, 0.0, 2.0);

    /// <summary>Raised when a beat frame arrives; strength is ~0.6 for a beat, 1.0 for a downbeat.</summary>
    public event EventHandler<double>? BeatTriggered;

    private bool _hasPalette;

    /// <summary>Applies a color palette (from the <c>color@v1</c> role).</summary>
    public void ApplyColorPalette(ColorPalette palette)
    {
        _hasPalette = palette.Primary is not null || palette.Accent is not null || palette.OnDark is not null
            || palette.BackgroundDark is not null;

        if (!_hasPalette)
        {
            // All-null palette = "clear": deactivate and leave the eased colors where they are.
            UpdateActive();
            return;
        }

        BaseColor = ToColor(palette.BackgroundDark, FallbackBase);
        BlobColor1 = ToColor(palette.Primary, FallbackBlob1);
        BlobColor2 = ToColor(palette.Accent, FallbackBlob2);
        BlobColor3 = ToColor(palette.OnDark, FallbackBlob3);

        UpdateActive();
    }

    /// <summary>Applies one visualizer feature frame (loudness or beat).</summary>
    public void ApplyVisualizerFrame(VisualizerFrame frame)
    {
        if (frame.Loudness is { } loud)
        {
            TargetEnergy = AmbientMath.NormalizeLoudness(loud);
        }

        if (frame.IsDownbeat is { } downbeat)
        {
            BeatTriggered?.Invoke(this, downbeat ? 1.0 : 0.85);
        }
    }

    /// <summary>Sets the backdrop style (from the settings dropdown). Immediate, no reconnect.</summary>
    public void SetMode(BackdropMode mode) => Mode = mode;

    /// <summary>Resets to the inactive/idle state (e.g. on disconnect).</summary>
    public void Reset()
    {
        _hasPalette = false;
        TargetEnergy = 0.0;
        UpdateActive();
    }

    private void UpdateActive() => IsActive = Mode == BackdropMode.AmbientGlow && _hasPalette;

    private static Color ToColor(RgbColor? rgb, Color fallback)
        => rgb is { } c ? Color.FromRgb(c.R, c.G, c.B) : fallback;
}
