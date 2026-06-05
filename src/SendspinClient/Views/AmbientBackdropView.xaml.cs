using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SendspinClient.Services.Visualization;
using SendspinClient.ViewModels;

namespace SendspinClient.Views;

/// <summary>
/// Pure-WPF Ambient Glow renderer. Reads target state from its <see cref="AmbientBackdropViewModel"/>
/// DataContext and eases the displayed blobs toward it each frame via CompositionTarget.Rendering.
/// </summary>
public partial class AmbientBackdropView : UserControl
{
    // Easing time constants (seconds).
    private const double EnergyTimeConstant = 0.35;
    private const double ColorTimeConstant = 0.8;
    private const double BeatHalfLife = 0.18;

    private readonly Stopwatch _clock = new();
    private long _lastTicks;

    private double _energy;
    private double _pulse;

    // Eased colors (R,G,B as doubles for smooth interpolation).
    private double _b1r, _b1g, _b1b, _b2r, _b2g, _b2b, _b3r, _b3g, _b3b;
    private double _baseR, _baseG, _baseB;

    // Stays true across Loaded/Unloaded cycles; eased values carry over intentionally so the
    // effect resumes smoothly rather than snapping from black on re-show.
    private bool _colorsInitialized;

    // The base-fill brush is created once and mutated in place to avoid per-frame allocations.
    private readonly SolidColorBrush _baseBrush = new(Colors.Transparent);

    private bool _renderingHooked;
    private bool _beatSubscribed;

    private AmbientBackdropViewModel? _vm;

    public AmbientBackdropView()
    {
        InitializeComponent();
        BaseFill.Fill = _baseBrush;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnsubscribeBeat();
        _vm = e.NewValue as AmbientBackdropViewModel;
        if (IsLoaded)
        {
            SubscribeBeat();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _clock.Restart();
        _lastTicks = _clock.ElapsedTicks;
        if (!_renderingHooked)
        {
            CompositionTarget.Rendering += OnRendering;
            _renderingHooked = true;
        }

        SubscribeBeat();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_renderingHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }

        _clock.Stop();
        UnsubscribeBeat();
    }

    private void SubscribeBeat()
    {
        if (!_beatSubscribed && _vm is not null)
        {
            _vm.BeatTriggered += OnBeat;
            _beatSubscribed = true;
        }
    }

    private void UnsubscribeBeat()
    {
        if (_beatSubscribed && _vm is not null)
        {
            _vm.BeatTriggered -= OnBeat;
            _beatSubscribed = false;
        }
    }

    private void OnBeat(object? sender, double strength) => _pulse += strength;

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        var now = _clock.ElapsedTicks;
        var dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = now;
        if (dt <= 0.0)
        {
            return;
        }

        // Ease energy toward the VM target; decay the beat pulse.
        _energy = AmbientMath.Ease(_energy, _vm.TargetEnergy, dt, EnergyTimeConstant);
        _pulse = AmbientMath.Decay(_pulse, dt, BeatHalfLife);

        var scale = AmbientMath.BlobScale(_energy, _pulse);
        var opacity = AmbientMath.BlobOpacity(_energy);

        ApplyBlob(Blob1Scale, Blob1, scale, opacity);
        ApplyBlob(Blob2Scale, Blob2, scale * 0.95, opacity * 0.9);
        ApplyBlob(Blob3Scale, Blob3, scale * 1.05, opacity * 0.8);

        // Idle drift: slow sinusoidal motion so the scene is alive at low energy.
        var t = now / (double)Stopwatch.Frequency;
        Blob1Translate.X = Math.Sin(t * 0.13) * 40.0;
        Blob1Translate.Y = Math.Cos(t * 0.11) * 30.0;
        Blob2Translate.X = Math.Sin(t * 0.09 + 2.0) * 50.0;
        Blob2Translate.Y = Math.Cos(t * 0.15 + 1.0) * 35.0;
        Blob3Translate.X = Math.Sin(t * 0.17 + 4.0) * 30.0;
        Blob3Translate.Y = Math.Cos(t * 0.08 + 3.0) * 45.0;

        EaseColors(dt);
    }

    private static void ApplyBlob(ScaleTransform scaleT, FrameworkElement blob, double scale, double opacity)
    {
        scaleT.ScaleX = scale;
        scaleT.ScaleY = scale;
        blob.Opacity = Math.Clamp(opacity, 0.0, 1.0);
    }

    private void EaseColors(double dt)
    {
        var c1 = _vm!.BlobColor1;
        var c2 = _vm.BlobColor2;
        var c3 = _vm.BlobColor3;
        var cb = _vm.BaseColor;

        if (!_colorsInitialized)
        {
            (_b1r, _b1g, _b1b) = (c1.R, c1.G, c1.B);
            (_b2r, _b2g, _b2b) = (c2.R, c2.G, c2.B);
            (_b3r, _b3g, _b3b) = (c3.R, c3.G, c3.B);
            (_baseR, _baseG, _baseB) = (cb.R, cb.G, cb.B);
            _colorsInitialized = true;
        }

        _b1r = AmbientMath.Ease(_b1r, c1.R, dt, ColorTimeConstant);
        _b1g = AmbientMath.Ease(_b1g, c1.G, dt, ColorTimeConstant);
        _b1b = AmbientMath.Ease(_b1b, c1.B, dt, ColorTimeConstant);
        _b2r = AmbientMath.Ease(_b2r, c2.R, dt, ColorTimeConstant);
        _b2g = AmbientMath.Ease(_b2g, c2.G, dt, ColorTimeConstant);
        _b2b = AmbientMath.Ease(_b2b, c2.B, dt, ColorTimeConstant);
        _b3r = AmbientMath.Ease(_b3r, c3.R, dt, ColorTimeConstant);
        _b3g = AmbientMath.Ease(_b3g, c3.G, dt, ColorTimeConstant);
        _b3b = AmbientMath.Ease(_b3b, c3.B, dt, ColorTimeConstant);
        _baseR = AmbientMath.Ease(_baseR, cb.R, dt, ColorTimeConstant);
        _baseG = AmbientMath.Ease(_baseG, cb.G, dt, ColorTimeConstant);
        _baseB = AmbientMath.Ease(_baseB, cb.B, dt, ColorTimeConstant);

        Brush1Stop0.Color = FromDoubles(_b1r, _b1g, _b1b);
        Brush2Stop0.Color = FromDoubles(_b2r, _b2g, _b2b);
        Brush3Stop0.Color = FromDoubles(_b3r, _b3g, _b3b);

        var baseColor = FromDoubles(_baseR, _baseG, _baseB);
        if (_baseBrush.Color != baseColor)
        {
            _baseBrush.Color = baseColor;
        }
    }

    private static Color FromDoubles(double r, double g, double b)
        => Color.FromRgb((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
}
