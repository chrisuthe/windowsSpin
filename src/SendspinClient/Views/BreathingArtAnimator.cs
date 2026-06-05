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

    // Continuous idle breath so the art stays alive while playing even when no loudness frames
    // arrive (e.g. after a same-track resume). Energy/beats add reactivity on top.
    private const double IdleBreathSpan = 0.02;   // +/- 2% scale at intensity 1
    private const double IdleBreathSpeed = 1.25;  // rad/s -> ~5s breath cycle
    private const double PlayLevelTimeConstant = 0.45; // ease in/out of the "alive" level on play/pause

    private readonly ScaleTransform _scale;
    private readonly DropShadowEffect _glow;
    private readonly AmbientBackdropViewModel _vm;

    private readonly Stopwatch _clock = new();
    private long _lastTicks;

    private double _energy;
    private double _pulse;
    private double _pulseTarget;
    private double _idlePhase;
    private double _playLevel;

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
        _playLevel = 0.0;
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

        // Breathe only while playing. When paused/stopped, ease the play-level and the energy
        // target to zero so the art settles to a clean rest — the visualizer signal does not
        // reliably resume on a same-track resume, so liveness is gated on playback, not energy.
        _playLevel = AmbientMath.Ease(_playLevel, _vm.IsPlaying ? 1.0 : 0.0, dt, PlayLevelTimeConstant);
        var energyTarget = _vm.IsPlaying ? _vm.TargetEnergy : 0.0;
        _energy = AmbientMath.Ease(_energy, energyTarget, dt, EnergyTimeConstant);
        _pulseTarget = AmbientMath.Decay(_pulseTarget, dt, BeatHalfLife);
        _pulse = AmbientMath.Ease(_pulse, _pulseTarget, dt, BeatAttack);

        var intensity = _vm.Intensity;

        // Continuous idle breath (energy-independent), faded by the play-level so it only breathes
        // while playing; the eased energy/beat reactivity from BreathScale adds on top.
        _idlePhase += dt;
        var idle = intensity * IdleBreathSpan * Math.Sin(_idlePhase * IdleBreathSpeed) * _playLevel;
        var scale = AmbientMath.BreathScale(_energy, _pulse, intensity) + idle;
        _scale.ScaleX = scale;
        _scale.ScaleY = scale;

        var g = AmbientMath.BreathGlow(_energy, intensity) * _playLevel;
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
