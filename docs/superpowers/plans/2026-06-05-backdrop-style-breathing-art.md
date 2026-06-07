# Backdrop Style Picker + Breathing Art Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the backdrop enable checkbox with a Off / Ambient Glow / Breathing Art dropdown, and add the Breathing Art style (album art scales + glows with the music), with the existing intensity slider scaling whichever style is active.

**Architecture:** `AmbientBackdropViewModel` gains a `Mode` (Off/AmbientGlow/BreathingArt) that gates two consumers of its existing reactive signal: the current `AmbientBackdropView` (blobs, shown only in AmbientGlow) and a new `BreathingArtAnimator` (scales+glows the album-art element, only in BreathingArt). Mode flows from a new `Visualizer:Mode` config through `MainViewModel.SettingsBackdropStyle` (a ComboBox), migrating from the legacy `Visualizer:Enabled` bool.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm (source-generated `[ObservableProperty]` + `OnXChanged` partials), xUnit.

**Spec:** `docs/superpowers/specs/2026-06-05-backdrop-style-breathing-art-design.md`

**Base:** branched off master after PR #36 (intensity slider) merged, so `AmbientMath.IntensityFloor`, the 3-arg `BlobScale`/`BlobOpacity`, `AmbientBackdropViewModel.Intensity`/`SetIntensity`, `MainViewModel.SettingsAmbientBackdropIntensity`, and the intensity slider all already exist.

---

## File Structure

- **Modify** `src/SendspinClient.Services/Visualization/AmbientMath.cs` — add `BreathScale`/`BreathGlow` + constants.
- **Modify** `tests/SendspinClient.Services.Tests/Visualization/AmbientMathTests.cs` — breathing tests.
- **Create** `src/SendspinClient/ViewModels/BackdropMode.cs` — the mode enum.
- **Modify** `src/SendspinClient/ViewModels/AmbientBackdropViewModel.cs` — replace `Enabled`/`SetEnabled` with `Mode`/`SetMode`.
- **Modify** `src/SendspinClient/ViewModels/MainViewModel.cs` — style property, list, mapping, load/save migration.
- **Modify** `src/SendspinClient/MainWindow.xaml` — style ComboBox; slider visibility; album-art wrapper.
- **Modify** `src/SendspinClient/MainWindow.xaml.cs` — construct the `BreathingArtAnimator`.
- **Create** `src/SendspinClient/Views/BreathingArtAnimator.cs` — the breathing render loop.
- **Modify** `src/SendspinClient/appsettings.json` — `Visualizer:Mode` default.

---

## Task 1: Breathing math in `AmbientMath` (TDD)

**Files:**
- Modify: `src/SendspinClient.Services/Visualization/AmbientMath.cs`
- Test: `tests/SendspinClient.Services.Tests/Visualization/AmbientMathTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `AmbientMathTests.cs` inside the `AmbientMathTests` class:

```csharp
[Theory]
[InlineData(0.0, 0.0, 1.0, 1.0)]    // rest = 1.0 (art never shrinks)
[InlineData(1.0, 0.0, 1.0, 1.06)]   // +6% energy span at intensity 1
[InlineData(1.0, 1.0, 1.0, 1.10)]   // +6% energy +4% pulse
[InlineData(1.0, 1.0, 2.0, 1.20)]   // intensity 2 doubles the reactive part
[InlineData(1.0, 1.0, 0.0, 1.0)]    // intensity 0 -> rest
public void BreathScale_RestsAtOneAndScalesByIntensity(double energy, double pulse, double intensity, double expected)
{
    Assert.Equal(expected, AmbientMath.BreathScale(energy, pulse, intensity), 0.0001);
}

[Theory]
[InlineData(0.0, 1.0, 0.15)]   // quiet -> faint base aura at intensity 1
[InlineData(1.0, 1.0, 1.0)]    // full energy -> clamp to 1
[InlineData(1.0, 2.0, 1.0)]    // intensity 2 -> clamp to 1
[InlineData(0.0, 0.0, 0.0)]    // intensity 0 -> no glow
public void BreathGlow_BaseAuraScalesAndClamps(double energy, double intensity, double expected)
{
    Assert.Equal(expected, AmbientMath.BreathGlow(energy, intensity), 0.0001);
}

[Fact]
public void BreathScale_NegativeIntensity_RestsAtOne()
{
    Assert.Equal(1.0, AmbientMath.BreathScale(1.0, 1.0, -3.0), 0.0001);
}
```

- [ ] **Step 2: Run tests to verify they fail (compile error)**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj --filter "FullyQualifiedName~AmbientMathTests" --nologo`
Expected: FAIL — `BreathScale`/`BreathGlow` undefined.

- [ ] **Step 3: Add constants + functions**

In `AmbientMath.cs`, after the `BlobOpacity` method (before the final closing brace of the class), add:

```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj --filter "FullyQualifiedName~AmbientMathTests" --nologo`
Expected: PASS — all existing + new (9 new) cases green.

- [ ] **Step 5: Commit**

```bash
git add src/SendspinClient.Services/Visualization/AmbientMath.cs tests/SendspinClient.Services.Tests/Visualization/AmbientMathTests.cs
git commit -m "feat: add breathing-art scale and glow math"
```

---

## Task 2: Mode model + plumbing (enum, ViewModel, MainViewModel)

The `Enabled`→`Mode` type change ripples across the ViewModel and MainViewModel, so they change together to keep the build green. (The XAML still binds the old `SettingsEnableAmbientBackdrop` until Task 3 — that produces only a non-fatal runtime binding warning, not a build error.)

**Files:**
- Create: `src/SendspinClient/ViewModels/BackdropMode.cs`
- Modify: `src/SendspinClient/ViewModels/AmbientBackdropViewModel.cs`
- Modify: `src/SendspinClient/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Create the enum**

Create `src/SendspinClient/ViewModels/BackdropMode.cs`:

```csharp
namespace SendspinClient.ViewModels;

/// <summary>Which ambient visualization (if any) is shown.</summary>
public enum BackdropMode
{
    Off,
    AmbientGlow,
    BreathingArt,
}
```

- [ ] **Step 2: ViewModel — replace `Enabled` with `Mode`**

In `AmbientBackdropViewModel.cs`:

(a) Replace the `Enabled` property block:
```csharp
    /// <summary>Whether the feature is enabled in settings.</summary>
    [ObservableProperty]
    private bool _enabled = true;
```
with:
```csharp
    /// <summary>The selected backdrop style. The blob backdrop renders only in AmbientGlow mode.</summary>
    [ObservableProperty]
    private BackdropMode _mode = BackdropMode.AmbientGlow;

    partial void OnModeChanged(BackdropMode value) => UpdateActive();
```

(b) Update the `IsActive` doc comment:
```csharp
    /// <summary>True when a real color palette has been received and the feature is enabled.</summary>
```
to:
```csharp
    /// <summary>True when the style is Ambient Glow and a real color palette has been received.</summary>
```

(c) Fix the `Intensity` doc comment's stale cref (it referenced `SetEnabled`):
```csharp
    /// setting is floored so the backdrop never goes fully dark via the slider; use
    /// <see cref="SetEnabled"/> to disable entirely.
```
to:
```csharp
    /// setting is floored so the backdrop never goes fully dark via the slider; choose the Off
    /// style (<see cref="SetMode"/>) to disable entirely.
```

(d) Replace the `SetEnabled` method:
```csharp
    /// <summary>Enables/disables the effect (from the settings toggle). Immediate, no reconnect.</summary>
    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        UpdateActive();
    }
```
with:
```csharp
    /// <summary>Sets the backdrop style (from the settings dropdown). Immediate, no reconnect.</summary>
    public void SetMode(BackdropMode mode) => Mode = mode;
```

(e) Replace `UpdateActive`:
```csharp
    private void UpdateActive() => IsActive = Enabled && _hasPalette;
```
with:
```csharp
    private void UpdateActive() => IsActive = Mode == BackdropMode.AmbientGlow && _hasPalette;
```

- [ ] **Step 3: MainViewModel — replace the enable property with a style property**

In `MainViewModel.cs`, replace:
```csharp
    /// <summary>
    /// Gets or sets whether the Ambient Glow reactive backdrop is enabled.
    /// </summary>
    [ObservableProperty]
    private bool _settingsEnableAmbientBackdrop = true;
```
with:
```csharp
    /// <summary>Available backdrop style options for the settings dropdown.</summary>
    public string[] AvailableBackdropStyles { get; } = new[]
    {
        "Off",
        "Ambient Glow",
        "Breathing Art",
    };

    /// <summary>Gets or sets the selected backdrop style (display string, bound to the dropdown).</summary>
    [ObservableProperty]
    private string _settingsBackdropStyle = "Ambient Glow";

    /// <summary>True when a style other than Off is selected (drives the intensity slider's visibility).</summary>
    public bool IsBackdropEnabled => SettingsBackdropStyle != "Off";
```

- [ ] **Step 4: MainViewModel — replace the change handler + add mapping helpers**

In `MainViewModel.cs`, replace:
```csharp
    partial void OnSettingsEnableAmbientBackdropChanged(bool value)
    {
        _ambient.SetEnabled(value);
    }
```
with:
```csharp
    partial void OnSettingsBackdropStyleChanged(string value)
    {
        _ambient.SetMode(StyleToMode(value));
        OnPropertyChanged(nameof(IsBackdropEnabled));
    }

    private static BackdropMode StyleToMode(string style) => style switch
    {
        "Off" => BackdropMode.Off,
        "Breathing Art" => BackdropMode.BreathingArt,
        _ => BackdropMode.AmbientGlow,
    };

    private static string StyleToToken(string style) => style switch
    {
        "Off" => "Off",
        "Breathing Art" => "BreathingArt",
        _ => "AmbientGlow",
    };

    private static string TokenToStyle(string token) => token switch
    {
        "Off" => "Off",
        "BreathingArt" => "Breathing Art",
        _ => "Ambient Glow",
    };
```

- [ ] **Step 5: MainViewModel — load with migration**

In `MainViewModel.cs`, replace:
```csharp
        // Load Ambient Glow backdrop setting and apply immediately
        SettingsEnableAmbientBackdrop = _configuration.GetValue<bool>("Visualizer:Enabled", true);
        _ambient.SetEnabled(SettingsEnableAmbientBackdrop);
        SettingsAmbientBackdropIntensity = _configuration.GetValue<double>("Visualizer:Intensity", 1.0);
        _ambient.SetIntensity(SettingsAmbientBackdropIntensity);
```
with:
```csharp
        // Load backdrop style (migrating from the legacy Visualizer:Enabled bool) and apply immediately
        var backdropModeToken = _configuration.GetValue<string?>("Visualizer:Mode", null);
        if (string.IsNullOrEmpty(backdropModeToken))
        {
            backdropModeToken = _configuration.GetValue<bool>("Visualizer:Enabled", true) ? "AmbientGlow" : "Off";
        }
        SettingsBackdropStyle = TokenToStyle(backdropModeToken);
        _ambient.SetMode(StyleToMode(SettingsBackdropStyle));
        SettingsAmbientBackdropIntensity = _configuration.GetValue<double>("Visualizer:Intensity", 1.0);
        _ambient.SetIntensity(SettingsAmbientBackdropIntensity);
```

- [ ] **Step 6: MainViewModel — save the mode (keep legacy key in sync)**

In `MainViewModel.cs`, replace:
```csharp
            var visualizerSection = root["Visualizer"]?.AsObject() ?? new JsonObject();
            visualizerSection["Enabled"] = SettingsEnableAmbientBackdrop;
            visualizerSection["Intensity"] = SettingsAmbientBackdropIntensity;
            root["Visualizer"] = visualizerSection;
```
with:
```csharp
            var visualizerSection = root["Visualizer"]?.AsObject() ?? new JsonObject();
            visualizerSection["Mode"] = StyleToToken(SettingsBackdropStyle);
            visualizerSection["Enabled"] = SettingsBackdropStyle != "Off";
            visualizerSection["Intensity"] = SettingsAmbientBackdropIntensity;
            root["Visualizer"] = visualizerSection;
```

- [ ] **Step 7: Build**

Run: `dotnet build src/SendspinClient/SendspinClient.csproj --nologo`
Expected: Build succeeded, 0 errors. (Pre-existing warnings in unrelated files are fine.)

- [ ] **Step 8: Commit**

```bash
git add src/SendspinClient/ViewModels/BackdropMode.cs src/SendspinClient/ViewModels/AmbientBackdropViewModel.cs src/SendspinClient/ViewModels/MainViewModel.cs
git commit -m "feat: replace backdrop enable flag with Off/AmbientGlow/BreathingArt mode"
```

---

## Task 3: Settings UI — style dropdown + slider visibility

**Files:**
- Modify: `src/SendspinClient/MainWindow.xaml`

- [ ] **Step 1: Replace the checkbox row with a style dropdown**

In `MainWindow.xaml`, replace this block (the "Ambient Glow reactive backdrop" Grid):
```xml
                            <!-- Ambient Glow reactive backdrop -->
                            <Grid Margin="0,0,0,16">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="Auto"/>
                                </Grid.ColumnDefinitions>
                                <StackPanel Grid.Column="0">
                                    <TextBlock Text="Reactive backdrop (Ambient Glow)" Style="{StaticResource BodyText}"/>
                                    <TextBlock Text="Animate the background with colors and energy from the music"
                                               Style="{StaticResource CaptionText}"
                                               Foreground="{StaticResource TextMutedBrush}"
                                               Margin="0,2,0,0"/>
                                </StackPanel>
                                <CheckBox Grid.Column="1"
                                          IsChecked="{Binding SettingsEnableAmbientBackdrop}"
                                          VerticalAlignment="Center"/>
                            </Grid>
```
with:
```xml
                            <!-- Backdrop style -->
                            <StackPanel Margin="0,0,0,16">
                                <TextBlock Text="Backdrop style" Style="{StaticResource BodyText}"/>
                                <TextBlock Text="Animate the window with the music, or breathe the album art — or turn it off"
                                           Style="{StaticResource CaptionText}"
                                           Foreground="{StaticResource TextMutedBrush}"
                                           Margin="0,2,0,0"
                                           TextWrapping="Wrap"/>
                                <ComboBox Margin="0,8,0,0"
                                          HorizontalAlignment="Stretch"
                                          ItemsSource="{Binding AvailableBackdropStyles}"
                                          SelectedItem="{Binding SettingsBackdropStyle}"
                                          Style="{StaticResource DarkComboBox}"
                                          ItemContainerStyle="{StaticResource DarkComboBoxItem}"/>
                            </StackPanel>
```

- [ ] **Step 2: Point the intensity block's visibility at the new property**

In the same file, in the intensity `StackPanel` immediately below, replace:
```xml
                                        Visibility="{Binding SettingsEnableAmbientBackdrop, Converter={StaticResource BoolToVisibilityConverter}}">
```
with:
```xml
                                        Visibility="{Binding IsBackdropEnabled, Converter={StaticResource BoolToVisibilityConverter}}">
```

- [ ] **Step 3: Build**

Run: `dotnet build src/SendspinClient/SendspinClient.csproj --nologo`
Expected: Build succeeded, 0 errors (XAML parses; `DarkComboBox`/`DarkComboBoxItem`/`BoolToVisibilityConverter` already resolve in this view).

- [ ] **Step 4: Commit**

```bash
git add src/SendspinClient/MainWindow.xaml
git commit -m "feat: replace backdrop checkbox with style dropdown in settings"
```

---

## Task 4: `BreathingArtAnimator`

**Files:**
- Create: `src/SendspinClient/Views/BreathingArtAnimator.cs`

- [ ] **Step 1: Create the animator class**

Create `src/SendspinClient/Views/BreathingArtAnimator.cs`:

```csharp
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
```

- [ ] **Step 2: Build**

Run: `dotnet build src/SendspinClient/SendspinClient.csproj --nologo`
Expected: Build succeeded, 0 errors (uses `AmbientMath.Ease`/`Decay`/`BreathScale`/`BreathGlow`, all present).

- [ ] **Step 3: Commit**

```bash
git add src/SendspinClient/Views/BreathingArtAnimator.cs
git commit -m "feat: add BreathingArtAnimator for album-art breathing style"
```

---

## Task 5: Album-art wrapper + wire the animator

**Files:**
- Modify: `src/SendspinClient/MainWindow.xaml`
- Modify: `src/SendspinClient/MainWindow.xaml.cs`

- [ ] **Step 1: Wrap the album-art Border with the breathing transform + glow**

In `MainWindow.xaml`, replace the opening of the album-art Border:
```xml
                    <!-- Large Album Art with Shadow (280x280) -->
                    <Border Width="280" Height="280"
                            CornerRadius="12"
                            HorizontalAlignment="Center"
                            Margin="0,0,0,24">
```
with (add the outer wrapper; the inner Border loses its `HorizontalAlignment`/`Margin`, which move to the wrapper):
```xml
                    <!-- Large Album Art with Shadow (280x280); outer wrapper carries the Breathing Art transform + glow -->
                    <Border x:Name="AlbumArtBreath"
                            HorizontalAlignment="Center"
                            Margin="0,0,0,24"
                            RenderTransformOrigin="0.5,0.5">
                        <Border.RenderTransform>
                            <ScaleTransform x:Name="AlbumArtScale"/>
                        </Border.RenderTransform>
                        <Border.Effect>
                            <DropShadowEffect x:Name="AlbumArtGlow" ShadowDepth="0" BlurRadius="0" Opacity="0"/>
                        </Border.Effect>
                        <Border Width="280" Height="280"
                                CornerRadius="12">
```

Then find the matching close of the original album-art Border — the `</Border>` that pairs with the `<Border Width="280" Height="280" ...>` (immediately after the inner artwork/placeholder `</Grid>`):
```xml
                        </Grid>
                    </Border>
```
and add one more closing `</Border>` for the new wrapper:
```xml
                        </Grid>
                        </Border>
                    </Border>
```

(Net: the inner `<Border Width="280" Height="280" CornerRadius="12">` keeps its `DropShadowEffect` black depth-shadow and the artwork `Grid` unchanged; only the outer wrapper is new.)

- [ ] **Step 2: Construct the animator in code-behind**

In `MainWindow.xaml.cs`, add a field next to the constants:
```csharp
    private BreathingArtAnimator? _breathingAnimator;
```

Add `using SendspinClient.Views;` if not already present (the class lives in that namespace; `MainWindow` is in `SendspinClient`).

In `OnDataContextChanged`, after the existing `newVm` subscription block, add:
```csharp
        if (_breathingAnimator is null && e.NewValue is MainViewModel mainVm)
        {
            _breathingAnimator = new BreathingArtAnimator(AlbumArtScale, AlbumArtGlow, mainVm.Ambient);
        }
```

(`AlbumArtScale` and `AlbumArtGlow` are the `x:Name`d elements from Step 1 — available as generated fields after `InitializeComponent`. The DataContext is set once at startup, and the `_breathingAnimator is null` guard keeps it a single instance.)

- [ ] **Step 3: Build**

Run: `dotnet build src/SendspinClient/SendspinClient.csproj --nologo`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/SendspinClient/MainWindow.xaml src/SendspinClient/MainWindow.xaml.cs
git commit -m "feat: wire breathing animator to the album art element"
```

---

## Task 6: Config default

**Files:**
- Modify: `src/SendspinClient/appsettings.json`

- [ ] **Step 1: Add the Mode default**

In `appsettings.json`, change the `Visualizer` section from:
```json
  "Visualizer": {
    "Enabled": true,
    "Intensity": 1.0,
    "RateMax": 30,
    "BufferCapacity": 4096
  }
```
to:
```json
  "Visualizer": {
    "Mode": "AmbientGlow",
    "Enabled": true,
    "Intensity": 1.0,
    "RateMax": 30,
    "BufferCapacity": 4096
  }
```

- [ ] **Step 2: Commit**

```bash
git add src/SendspinClient/appsettings.json
git commit -m "chore: add Visualizer:Mode default to appsettings"
```

---

## Task 7: Final verification

- [ ] **Step 1: Full build**

Run: `dotnet build SendspinClient.sln --nologo`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Full test run**

Run: `dotnet test --nologo`
Expected: All tests pass (existing + new breathing-math cases).

- [ ] **Step 3: Manual smoke test**

Run `dotnet run --project src/SendspinClient/SendspinClient.csproj`, connect, play audio, open Settings:
- "Backdrop style" dropdown shows Off / Ambient Glow / Breathing Art and switches live (no reconnect).
- **Ambient Glow:** the blob backdrop behaves as before; album art is static.
- **Breathing Art:** the blob backdrop is gone; the album art gently scales and glows (palette-accent) with the music; beats give a small bump.
- **Off:** neither effect; the intensity slider is hidden.
- Intensity slider scales the active style (Glow and Breathing); at 0% the active style is faint/subtle, not dead.
- Change style, restart the app: selection persists (`%LOCALAPPDATA%\Sendspin\appsettings.json` → `Visualizer:Mode`).
- A config with only the legacy `Visualizer:Enabled` (no `Mode`) opens as Ambient Glow (true) or Off (false).

---

## Self-Review Notes

- **Spec coverage:** mode enum (T2), VM mode + IsActive gating (T2), breathing math (T1), animator (T4), album-art wrapper + wiring (T5), dropdown + slider visibility (T3), MainViewModel style/migration/save (T2), config default (T6), tests (T1). All spec sections mapped.
- **Type/name consistency:** `BackdropMode.{Off,AmbientGlow,BreathingArt}`, `SetMode`, `Mode`, `SettingsBackdropStyle`, `AvailableBackdropStyles`, `IsBackdropEnabled`, `StyleToMode`/`StyleToToken`/`TokenToStyle`, `AlbumArtScale`/`AlbumArtGlow`/`AlbumArtBreath`, `BreathScale`/`BreathGlow` are used identically across tasks.
- **Build-green ordering:** T2 changes VM+MainViewModel together (the type change is atomic); the XAML still references the removed `SettingsEnableAmbientBackdrop` between T2 and T3, which is a non-fatal WPF runtime binding warning, not a build error — resolved in T3.
- **Floor reuse:** breathing uses `vm.Intensity` (already floored), so 0% is faint-but-alive in both styles with no new floor logic.
