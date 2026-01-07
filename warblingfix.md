# Warbling Fix Investigation Notes

## Problem Statement
Audio exhibits warbling and awkward noises during sync correction, particularly when the resampler adjusts playback rate.

---

## Audio Pipeline Overview

```
Network (WebSocket binary)
    │
    ▼
Decoder (Opus/FLAC/PCM)
    │
    ▼
┌─────────────────────────────────────────────────────────────────────┐
│  TimedAudioBuffer                                                   │
│  - Stores samples with server timestamps                            │
│  - Calculates sync error (elapsed wall clock - samples read time)   │
│  - Decides correction strategy (resampling vs drop/insert)          │
│  - Exposes TargetPlaybackRate (0.98-1.02x typically)               │
│  - Fires TargetPlaybackRateChanged event                           │
└─────────────────────────────────────────────────────────────────────┘
    │
    ▼
BufferedAudioSampleSource (IAudioSampleSource)
    │
    ▼
AudioSampleProviderAdapter (ISampleProvider, adds volume/mute)
    │
    ▼
┌─────────────────────────────────────────────────────────────────────┐
│  DynamicResamplerSampleProvider                                     │
│  - Wraps NAudio's WdlResampler                                     │
│  - Subscribes to TargetPlaybackRateChanged                         │
│  - Applies exponential smoothing to rate changes                    │
│  - Converts rate to resampler input/output ratio                   │
└─────────────────────────────────────────────────────────────────────┘
    │
    ▼
WasapiOut (100ms shared mode latency)
    │
    ▼
Speakers
```

---

## Current Resampling Implementation

### DynamicResamplerSampleProvider Key Details

**Location**: `src/SendspinClient.Services/Audio/DynamicResamplerSampleProvider.cs`

**Resampler Configuration** (line 124):
```csharp
_resampler.SetMode(true, 2, false); // Interpolating, 2-tap sinc filter, linear phase
_resampler.SetFilterParms();        // Default filter parameters
_resampler.SetFeedMode(true);       // Output-driven mode
```

**Rate Smoothing** (lines 54-60):
```csharp
RateSmoothingFactor = 0.05;      // Move 5% toward target each update
RateChangeThreshold = 0.0005;    // 0.05% minimum change to update resampler
```

**Rate Conversion** (line 232):
```csharp
// SetRates(inRate, outRate): to speed up, output rate < input rate
var inRate = WaveFormat.SampleRate;
var outRate = (int)(WaveFormat.SampleRate / _playbackRate);
_resampler.SetRates(inRate, outRate);
```

### Smoothing Behavior

When rate changes from 1.0 to 1.02:
- Each `Read()` call moves 5% closer to target
- At 48kHz stereo, ~10ms per callback = ~100 callbacks/sec
- Time to reach ~90% of target: `log(0.1) / log(0.95) = ~45 callbacks = ~450ms`

This is quite slow - the rate takes nearly half a second to respond to sync corrections.

---

## Potential Warbling Sources

### 1. **Rate Smoothing Too Slow / Too Fast**
- Current: 5% per callback (~450ms to reach target)
- If too slow: sync error grows, causes oscillation between correction methods
- If too fast: resampler filter state disrupted, causes audio artifacts

### 2. **WdlResampler Filter Settings**
- Currently using 2-tap sinc filter (minimal quality)
- Higher tap counts (4, 8, 16) = better quality but more latency
- `SetFilterParms()` uses defaults - may not be optimal

### 3. **Integer Rate Conversion**
```csharp
var outRate = (int)(WaveFormat.SampleRate / _playbackRate);
```
- Truncates to integer, loses precision
- At 1.02x: `48000 / 1.02 = 47058.82...` → `47058`
- Actual rate becomes `48000 / 47058 = 1.020021...` (0.002% error)

### 4. **Frequent SetRates() Calls**
- Every time rate changes by >0.05%, we call `SetRates()`
- WdlResampler may reset internal filter state on each call
- Could cause discontinuities

### 5. **Threshold Hysteresis**
- Entry deadband: 2ms
- Exit deadband: 0.5ms
- When hovering near threshold, rate oscillates between 1.0 and correction value

### 6. **Sync Error Calculation Jitter**
- Wall clock vs samples read comparison
- Any jitter in audio callbacks = jittery sync error = jittery rate

### 7. **Tier Transitions**
- At 15ms error: switches between resampling and drop/insert
- Sudden mode change may cause audible artifacts

---

## WdlResampler Deep Dive

NAudio's WdlResampler is a port of Cockos' WDL resampler.

### SetMode Parameters
```csharp
SetMode(bool interp, int filtercnt, bool sinc)
```
- `interp`: Interpolation mode (true = better quality)
- `filtercnt`: Number of sinc filter taps (1-64)
- `sinc`: Whether to use sinc filter (false = linear)

**Current**: `SetMode(true, 2, false)` - interpolating, 2-tap, linear
**Concern**: Only 2 taps is minimal quality

### SetFilterParms Parameters
```csharp
SetFilterParms(float filt=0.5f, float bw=0.0f)
```
- `filt`: Filter coefficient (affects transition band sharpness)
- `bw`: Bandwidth (0 = default based on ratio)

**Current**: Using defaults, which may not be optimal for small rate changes

### Rate Change Behavior
The resampler maintains internal state (filter history). When `SetRates()` is called:
- If ratio changes significantly, filter coefficients are recomputed
- May cause phase discontinuity if called too frequently

---

## Experiments to Try

### Experiment 1: Increase Filter Taps
```csharp
_resampler.SetMode(true, 8, false);  // 8-tap instead of 2
// or
_resampler.SetMode(true, 16, true);  // 16-tap sinc
```
**Hypothesis**: Better filtering = less aliasing artifacts

### Experiment 2: Slower Rate Smoothing
```csharp
RateSmoothingFactor = 0.01;  // 1% per callback instead of 5%
```
**Hypothesis**: Gentler rate changes = less filter disruption

### Experiment 3: Faster Rate Smoothing with Higher Threshold
```csharp
RateSmoothingFactor = 0.1;     // 10% per callback
RateChangeThreshold = 0.002;   // 0.2% threshold
```
**Hypothesis**: Respond faster to sync, but fewer actual resampler updates

### Experiment 4: Avoid SetRates() Entirely
Instead of changing the resampler ratio, could we:
- Keep ratio at 1.0
- Slightly over-read or under-read from source
- Let buffer level absorb the difference
**Concern**: This is what drop/insert does, defeats purpose of smooth resampling

### Experiment 5: Use Floating-Point Rates
```csharp
// WdlResampler internally uses doubles, but NAudio's wrapper uses ints
// Might need to modify NAudio source or use different resampler
```

### Experiment 6: Increase Deadband
```csharp
EntryDeadbandMicroseconds = 5_000;   // 5ms instead of 2ms
ExitDeadbandMicroseconds = 2_000;    // 2ms instead of 0.5ms
```
**Hypothesis**: Less frequent correction = less warbling

### Experiment 7: Lower Max Speed Correction
```csharp
MaxSpeedCorrection = 0.01;  // 1% instead of 2%
```
**Hypothesis**: Smaller rate changes = less noticeable artifacts

---

## Measurements to Capture

1. **Rate change frequency**: How often is `SetRates()` called per second?
2. **Rate delta distribution**: What are typical rate changes?
3. **Sync error over time**: Is it oscillating or converging?
4. **Buffer level stability**: Is it staying near target?

---

## Reference: Python CLI Approach

The CLI uses:
- PortAudio stream with rate adjustment
- 4% max speed correction (vs our 2%)
- 2 second correction target (vs our 3 seconds)
- Different resampler (libsamplerate via sounddevice?)

Key difference: CLI may be using a different, higher-quality resampler.

---

## Next Steps

1. [ ] Add logging to capture rate change frequency and magnitude
2. [ ] Try 8-tap or 16-tap sinc filter
3. [ ] Experiment with smoothing factor values
4. [ ] Consider alternative resamplers (libsamplerate via NAudio.Extras?)
5. [ ] Profile audio callback timing jitter
6. [ ] Compare behavior with drop/insert only (disable resampling)

---

## Session Log

### Session 1 (Current)
- Documented full pipeline
- Identified 7 potential warbling sources
- Noted WdlResampler uses only 2-tap filter (minimal quality)
- Rate smoothing takes ~450ms to reach target
- Integer truncation in rate calculation

**Changes Made:**
1. Upgraded filter from 2-tap to 16-tap sinc: `SetMode(true, 16, true)`
2. Fixed integer truncation - now using double precision for SetRates()

**Next test**: Run and listen for warbling improvement
