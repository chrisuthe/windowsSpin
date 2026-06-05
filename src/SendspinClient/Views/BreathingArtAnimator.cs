using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Effects;
using SendspinClient.Services.Visualization;
using SendspinClient.ViewModels;

namespace SendspinClient.Views;

/// <summary>
/// Drives the Breathing Art backdrop style: scales the album-art element and adds a palette-accent
/// glow that ease toward the loudness/beat signal from <see cref="AmbientBackdropViewModel"/>.
/// Runs a render loop only while <see cref="AmbientBackdropViewModel.Mode"/> is
/// <see cref="BackdropMode.BreathingArt"/>; in any other mode the art rests at scale 1.0 with no glow.
/// Mirrors the easing constants used by <c>AmbientBackdropView</c>.
/// </summary>
public sealed class BreathingArtAnimator
{
    private const double EnergyTimeConstant = 0.45;
    private const double BeatHalfLife = 0.30;
    private const double BeatAttack = 0.06;
    private const double GlowColorTimeConstant = 0.8;

    // BreathGlow (0..1) -> effect mapping (view concern).
    private const double MaxGlowBlur = 40.0;
    private const double MaxGlowOpacity = 0.85;

    private readonly ScaleTransform _scale;
    private readonly DropShadowEffect _glow;
    private readonly AmbientBackdropViewModel _vm;

    private readonly Stopwatch _clock = new();
    private long _lastTicks;

    private double _energy;
    private double _pulse;
    private double _pulseTarget;

    private double _glowR, _glowG, _glowB;
    private bool _glowColorInitialized;

    private bool _hooked;

    public BreathingArtAnimator(ScaleTransform scale, DropShadowEffect glow, AmbientBackdropViewModel vm)
    {
        _scale = scale;
        _glow = glow;
        _vm = vm;

        // Animator and VM are both app-lifetime singletons, so this subscription lives for the
        // life of the process; no unsubscribe/IDisposable is needed (unlike AmbientBackdropView,
        // whose VM can be swapped via DataContextChanged).
        _vm.PropertyChanged += OnVmPropertyChanged;
        ApplyModeState();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AmbientBackdropViewModel.Mode))
        {
            ApplyModeState();
        }
    }

    private void ApplyModeState()
    {
        if (_vm.Mode == BackdropMode.BreathingArt)
        {
            Hook();
        }
        else
        {
            Unhook();
            ResetToRest();
        }
    }

    private void Hook()
    {
        if (_hooked)
        {
            return;
        }

        _clock.Restart();
        _lastTicks = _clock.ElapsedTicks;
        _vm.BeatTriggered += OnBeat;
        CompositionTarget.Rendering += OnRendering;
        _hooked = true;
    }

    private void Unhook()
    {
        if (!_hooked)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _vm.BeatTriggered -= OnBeat;
        _clock.Stop();
        _hooked = false;
    }

    private void OnBeat(object? sender, double strength) => _pulseTarget += strength;

    private void ResetToRest()
    {
        _energy = 0.0;
        _pulse = 0.0;
        _pulseTarget = 0.0;
        _scale.ScaleX = 1.0;
        _scale.ScaleY = 1.0;
        _glow.BlurRadius = 0.0;
        _glow.Opacity = 0.0;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock.ElapsedTicks;
        var dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = now;
        if (dt <= 0.0)
        {
            return;
        }

        _energy = AmbientMath.Ease(_energy, _vm.TargetEnergy, dt, EnergyTimeConstant);
        _pulseTarget = AmbientMath.Decay(_pulseTarget, dt, BeatHalfLife);
        _pulse = AmbientMath.Ease(_pulse, _pulseTarget, dt, BeatAttack);

        var intensity = _vm.Intensity;
        var scale = AmbientMath.BreathScale(_energy, _pulse, intensity);
        _scale.ScaleX = scale;
        _scale.ScaleY = scale;

        var g = AmbientMath.BreathGlow(_energy, intensity);
        _glow.BlurRadius = g * MaxGlowBlur;
        _glow.Opacity = g * MaxGlowOpacity;

        var c = _vm.BlobColor2;
        if (!_glowColorInitialized)
        {
            (_glowR, _glowG, _glowB) = (c.R, c.G, c.B);
            _glowColorInitialized = true;
        }

        _glowR = AmbientMath.Ease(_glowR, c.R, dt, GlowColorTimeConstant);
        _glowG = AmbientMath.Ease(_glowG, c.G, dt, GlowColorTimeConstant);
        _glowB = AmbientMath.Ease(_glowB, c.B, dt, GlowColorTimeConstant);
        _glow.Color = Color.FromRgb(
            (byte)Math.Clamp(_glowR, 0, 255),
            (byte)Math.Clamp(_glowG, 0, 255),
            (byte)Math.Clamp(_glowB, 0, 255));
    }
}
