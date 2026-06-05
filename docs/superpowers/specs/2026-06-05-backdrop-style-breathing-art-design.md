# Backdrop Style Picker + Breathing Art — Design

**Date:** 2026-06-05
**Status:** Approved (pending spec review)

## Goal

Let the user choose *which* ambient visualization they get via a dropdown, and add a
second style — **Breathing Art** (the album cover gently scales and glows with the music) —
alongside the existing **Ambient Glow** reactive backdrop. The dropdown also carries an
**Off** option, replacing the current enable checkbox. The existing intensity slider scales
whichever style is active.

## Decisions

| Question | Decision |
|----------|----------|
| Picker shape | **One dropdown**: Off / Ambient Glow / Breathing Art. Replaces the enable checkbox. The intensity slider is shown whenever the style is not Off. |
| Intensity scope | **Applies to the active style.** For Breathing Art it scales breathe depth + glow; floored to a faint minimum at 0% (same `IntensityFloor` as Glow). |
| Breathing implementation | **Animator on the existing album-art element** (not a new UserControl). Surgical — keeps the album-art markup and the working backdrop view untouched. |
| Glow color | The album's **palette accent** (`BlobColor2`), eased; falls back to the existing default accent when no palette. |

## Background

The reactive signal (energy from loudness, beat/downbeat events, and the color palette)
flows continuously into `AmbientBackdropViewModel` via `ApplyVisualizerFrame` /
`ApplyColorPalette`, independent of what is rendered. Adding a style is therefore adding a
second *consumer* of that signal plus a *mode gate* — no new data plumbing. Ambient Glow and
Breathing Art are mutually exclusive renderers; the dropdown selects the consumer.

## Architecture

```
Visualizer:Mode (config)
  → MainViewModel.SettingsBackdropStyle  (display string, bound to ComboBox, persisted)
  → AmbientBackdropViewModel.SetMode(BackdropMode)   (raises Mode change; recomputes IsActive)
  → consumers:
       • AmbientBackdropView  — shows blob backdrop only when Mode == AmbientGlow (IsActive)
       • BreathingArtAnimator — scales+glows album art only when Mode == BreathingArt
  + AmbientBackdropViewModel.Intensity (floored) scales whichever consumer is active
```

## Component changes

### 1. `BackdropMode` enum — new file `src/SendspinClient/ViewModels/BackdropMode.cs`

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

### 2. `AmbientBackdropViewModel.cs`

- Remove the `Enabled` observable property and `SetEnabled(bool)`.
- Add `[ObservableProperty] private BackdropMode _mode = BackdropMode.AmbientGlow;` with
  `partial void OnModeChanged(BackdropMode value) => UpdateActive();`
- Add `public void SetMode(BackdropMode mode) => Mode = mode;` (symmetry with `SetIntensity`;
  the observable property raises change notification so the animator can react).
- `UpdateActive()` becomes: `IsActive = Mode == BackdropMode.AmbientGlow && _hasPalette;`
  (so the blob backdrop layer shows only in Ambient Glow mode — unchanged binding, new meaning).
- Keep `Intensity`/`SetIntensity`, `TargetEnergy`, `BlobColor1..3`, `BaseColor`, `BeatTriggered`.
- `Reset()` is unchanged except it no longer touches `Enabled`.

The breathing animator consumes: `TargetEnergy`, `BeatTriggered`, `Intensity`, `Mode`
(for hook/unhook), and `BlobColor2` (glow color).

### 3. Breathing math (`AmbientMath.cs`, pure + tested)

Mirrors `BlobScale`/`BlobOpacity`. Rest scale is **1.0** (album art is never shrunk).

```csharp
public const double BreathScaleEnergySpan = 0.06;   // +6% at full energy, intensity 1
public const double BreathScalePulseSpan  = 0.04;   // +4% on a full beat, intensity 1
public const double BreathGlowBase        = 0.15;   // faint aura even when quiet
public const double BreathGlowEnergySpan  = 0.85;

/// <summary>Album-art breathe scale (rests at 1.0). Intensity scales the reactive part.</summary>
public static double BreathScale(double energy, double pulse, double intensity = 1.0)
{
    var e = Math.Clamp(energy, 0.0, 1.0);
    var p = Math.Clamp(pulse, 0.0, 1.0);
    var i = Math.Max(0.0, intensity);
    return 1.0 + (i * ((e * BreathScaleEnergySpan) + (p * BreathScalePulseSpan)));
}

/// <summary>Album-art glow strength 0..1 (mapped to blur/opacity by the animator).</summary>
public static double BreathGlow(double energy, double intensity = 1.0)
{
    var e = Math.Clamp(energy, 0.0, 1.0);
    var i = Math.Max(0.0, intensity);
    return Math.Clamp(i * (BreathGlowBase + (e * BreathGlowEnergySpan)), 0.0, 1.0);
}
```

Expected test values: `BreathScale(0,0,1)=1.0`, `(1,0,1)=1.06`, `(1,1,1)=1.10`,
`(1,1,2)=1.20`, `(1,1,0)=1.0`; `BreathGlow(0,1)=0.15`, `(1,1)=1.0`, `(1,2)=1.0`, `(0,0)=0.0`.

### 4. `BreathingArtAnimator` — new file `src/SendspinClient/Views/BreathingArtAnimator.cs`

A small class (not a UserControl) that animates a target element. Constructed with the
`ScaleTransform`, the `DropShadowEffect`, and the `AmbientBackdropViewModel`. Mirrors the
easing in `AmbientBackdropView`:

- Subscribes to `vm.PropertyChanged`; when `Mode` becomes `BreathingArt`, hooks
  `CompositionTarget.Rendering`; when it leaves, eases the art back to rest over a few frames
  then unhooks (so no idle render cost in Off/Glow modes). Also subscribes to
  `vm.BeatTriggered` for pulse impulses while hooked.
- Per frame: ease energy toward `vm.TargetEnergy`; decay+ease the beat pulse (same constants
  as `AmbientBackdropView`: energy τ≈0.45, beat half-life 0.30, attack 0.06). Read
  `intensity = vm.Intensity` (already floored).
- Apply `scale = AmbientMath.BreathScale(energy, pulse, intensity)` to the `ScaleTransform`
  (X and Y); compute `g = AmbientMath.BreathGlow(energy, intensity)` and map to the glow:
  `BlurRadius = g * MaxGlowBlur` (≈40), `Opacity = g * MaxGlowOpacity` (≈0.85), `Color` eased
  toward `vm.BlobColor2`.
- At rest: scale 1.0, glow opacity 0.

The mapping constants (`MaxGlowBlur`, `MaxGlowOpacity`, easing τ) live in the animator (view
concern); `AmbientMath` returns only normalized 0..1 / scale values.

### 5. Album-art XAML (`MainWindow.xaml`)

Wrap the existing 280×280 album-art `Border` in a thin outer `Border` that carries the
breathing transform + glow, so the inner border keeps its existing black depth-shadow:

```xml
<Border x:Name="AlbumArtBreath"
        HorizontalAlignment="Center" Margin="0,0,0,24"
        RenderTransformOrigin="0.5,0.5">
    <Border.RenderTransform>
        <ScaleTransform x:Name="AlbumArtScale"/>
    </Border.RenderTransform>
    <Border.Effect>
        <DropShadowEffect x:Name="AlbumArtGlow" ShadowDepth="0" BlurRadius="0" Opacity="0"/>
    </Border.Effect>
    <!-- existing 280x280 Border (depth shadow + artwork/placeholder Grid) moves here,
         minus its own HorizontalAlignment/Margin which now live on the wrapper -->
</Border>
```

The artwork `Image` and placeholder bindings are unchanged. `MainWindow` code-behind
constructs the `BreathingArtAnimator` in `OnDataContextChanged` (where it already gets the
`MainViewModel`), passing `AlbumArtScale`, `AlbumArtGlow`, and `vm.Ambient`.

### 6. Settings UI (`MainWindow.xaml`)

- Replace the "Reactive backdrop (Ambient Glow)" checkbox row with a **"Backdrop style"**
  row: label + caption + a `ComboBox` (`ItemsSource="{Binding AvailableBackdropStyles}"`,
  `SelectedItem="{Binding SettingsBackdropStyle}"`, `DarkComboBox` / `DarkComboBoxItem`
  styles — same pattern as Connection Mode).
- The intensity slider block's visibility binds to `IsBackdropEnabled` (style ≠ Off) instead
  of the removed `SettingsEnableAmbientBackdrop`. Caption generalized to
  "Scale how strongly the backdrop reacts, glows, and moves".

### 7. `MainViewModel.cs`

- Remove `SettingsEnableAmbientBackdrop` and `OnSettingsEnableAmbientBackdropChanged`.
- Add:
  ```csharp
  public IReadOnlyList<string> AvailableBackdropStyles { get; } =
      new[] { "Off", "Ambient Glow", "Breathing Art" };

  [ObservableProperty]
  private string _settingsBackdropStyle = "Ambient Glow";

  /// <summary>True when a style other than Off is selected (drives the intensity slider's visibility).</summary>
  public bool IsBackdropEnabled => SettingsBackdropStyle != "Off";

  partial void OnSettingsBackdropStyleChanged(string value)
  {
      _ambient.SetMode(StyleToMode(value));
      OnPropertyChanged(nameof(IsBackdropEnabled));
  }
  ```
- Mapping helpers (display ↔ `BackdropMode` ↔ config token), a small switch:
  - "Off" ↔ `Off` ↔ `"Off"`
  - "Ambient Glow" ↔ `AmbientGlow` ↔ `"AmbientGlow"`
  - "Breathing Art" ↔ `BreathingArt` ↔ `"BreathingArt"`
- Keep `SettingsAmbientBackdropIntensity` + `OnSettingsAmbientBackdropIntensityChanged`.
- **Load:**
  ```csharp
  var modeToken = _configuration.GetValue<string?>("Visualizer:Mode", null);
  if (string.IsNullOrEmpty(modeToken))
  {
      // migrate from legacy bool
      modeToken = _configuration.GetValue<bool>("Visualizer:Enabled", true) ? "AmbientGlow" : "Off";
  }
  SettingsBackdropStyle = TokenToStyle(modeToken);   // sets ComboBox; handler calls SetMode
  ```
  (Intensity load unchanged.)
- **Save** (in the Visualizer section block):
  ```csharp
  visualizerSection["Mode"] = StyleToToken(SettingsBackdropStyle);
  visualizerSection["Enabled"] = SettingsBackdropStyle != "Off";   // keep legacy key in sync
  visualizerSection["Intensity"] = SettingsAmbientBackdropIntensity;
  ```

### 8. Config default (`appsettings.json`)

Add `"Mode": "AmbientGlow"` to the `Visualizer` section (keep `Enabled: true` and the
`Intensity: 1.0` added previously).

## Out of scope

- No additional styles beyond Off / Ambient Glow / Breathing Art.
- No per-style intensity (one shared slider).
- Breathing animates the existing album-art element only; it does not introduce a ring,
  rim-light, or other effects from the earlier sketch.
- No rename of `AmbientBackdropViewModel` (it remains the shared reactive-signal VM; renaming
  is deferred to avoid churn across DI/bindings).

## Verification

- `dotnet build SendspinClient.sln` clean; `dotnet test` green (new `AmbientMath` breathing tests).
- Manual: dropdown switches Off / Ambient Glow / Breathing Art live (no reconnect); Breathing
  Art scales + glows the cover with the music; intensity slider scales the active style and
  hides when Off; 0% is faint-but-alive in both styles; selection persists across restart
  (`Visualizer:Mode`); legacy configs with only `Visualizer:Enabled` migrate correctly.
