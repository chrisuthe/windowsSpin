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

### Session 2
**Finding: Potential Double Resampling**

Pipeline analysis reveals two resamplers in series:
1. `DynamicResamplerSampleProvider` - Our sync correction (rate 0.98-1.02x)
2. Windows Audio Engine - WASAPI Shared mode resamples if format != system mixer

If incoming audio is 44.1kHz and system mixer is 48kHz:
- Our resampler: 44100 → 44100 (at variable rate)
- Windows: 44100 → 48000 (fixed rate)
- Combined artifacts from two resamplers!

**Potential fixes:**
1. Query system mixer rate, resample once to that rate with sync correction combined
2. Use WASAPI Exclusive mode (bypasses Windows Audio Engine)
3. Always request 48kHz from server (if supported)

### Session 3: Eliminate Double Resampling

**Implementation: Compound Resampling**

Implemented fix #1 - query system mixer rate and resample once with sync correction combined.

**Changes Made:**

1. **WasapiAudioPlayer.cs**:
   - Added `QueryDeviceMixFormat(MMDevice?)` method to query Windows Audio Engine mixer rate
   - Added `DeviceNativeSampleRate` property (defaults to 48000 if query fails)
   - Added `_buffer` field to store buffer reference for device switching
   - Call `QueryDeviceMixFormat()` in `InitializeAsync()` and `SwitchDeviceAsync()`
   - Updated `OutputFormat` property to report actual output rate (device native rate when resampling)
   - Updated `SetSampleSource()` to pass device native rate to resampler
   - Updated `SwitchDeviceAsync()` to recreate resampler with new device's native rate

2. **DynamicResamplerSampleProvider.cs**:
   - Added `targetSampleRate` constructor parameter (0 = use source rate)
   - Added `_targetSampleRate` field
   - Updated `WaveFormat` property to report target rate when different from source
   - Updated `UpdateResamplerRates()` to perform compound conversion:
     ```csharp
     // Combines sample rate conversion + sync correction in single resampler pass
     var sourceRate = (double)_source.WaveFormat.SampleRate;  // Server rate (e.g., 48000)
     var outRate = _targetSampleRate / _playbackRate;         // Device rate / sync factor
     _resampler.SetRates(sourceRate, outRate);
     ```

**Example:**
- Server sends: 48000 Hz
- Device native: 44100 Hz
- Sync correction: 1.02x (2% speedup)
- Compound: `SetRates(48000, 44100/1.02)` = `SetRates(48000, 43235)`
- Single resampler pass handles both rate conversion AND sync correction!

**Stats for Nerds:**
- Output format now shows device native rate (e.g., "PCM 44100Hz 2ch 32-bit float")
- Input vs output sample rate difference indicates sample rate conversion is active

**Next test**: Verify warbling is reduced when server rate differs from device rate

### Session 4: Senior Engineer Pipeline Review

**Objective**: Comprehensive review of audio pipeline for quality issues during sync correction.

**Review Summary**: 10 issues identified (4 HIGH, 4 MEDIUM, 2 LOW priority)

#### Issues Fixed (Confirmed Bugs):

**Issue #1 - Input Sample Calculation Missing Sample Rate Ratio** (HIGH)
- **Problem**: When compound resampling (e.g., 48kHz→44.1kHz + sync), the `Read()` method only accounted for playback rate, not sample rate ratio.
- **Root Cause**: `inputSamplesNeeded = count * currentRate` ignored the 1.088x ratio for 48k→44.1k conversion.
- **Impact**: Resampler starved for input, produced fewer output samples, gaps filled with silence causing audio dropouts.
- **Fix**: Added sample rate ratio to calculation:
  ```csharp
  var sampleRateRatio = (double)_source.WaveFormat.SampleRate / _targetSampleRate;
  var inputSamplesNeeded = (int)Math.Ceiling(count * currentRate * sampleRateRatio);
  ```
- **Commit**: `fbbf099` - fix(audio): account for sample rate ratio in resampler input calculation

**Issue #2 - Audio Thread Memory Allocation** (HIGH)
- **Problem**: Fixed 16384-sample buffer could be exceeded with high sample rate sources (96kHz+), triggering GC on audio thread.
- **Root Cause**: Buffer size didn't account for sample rate conversion ratio.
- **Impact**: GC pauses on audio thread cause audible glitches.
- **Fix**: Pre-calculate buffer size at construction time:
  ```csharp
  const int MaxExpectedOutputRequest = 16384;
  const double SafetyMargin = 1.2; // 20% extra headroom
  var sampleRateRatio = (double)source.WaveFormat.SampleRate / _targetSampleRate;
  var bufferSize = (int)(MaxExpectedOutputRequest * MaxRate * sampleRateRatio * SafetyMargin);
  _sourceBuffer = new float[bufferSize];
  ```
- **Commit**: `8e4b4ed` - fix(audio): pre-allocate resampler buffer to avoid audio thread allocation

#### Issues Verified as Design Choices (Not Bugs):

**Issue #3 - Smoothing Granularity Tied to Audio Callback Frequency** (MEDIUM)
- **Concern**: Exponential smoothing tied to per-buffer callbacks could cause inconsistent smoothing.
- **Finding**: This is standard DSP practice. The 5% factor moving toward target over ~450ms is intentional.
- **Evidence**: Comment documents design: "At 0.05, we move 5% toward target each update (~10ms), reaching 90% in ~450ms."
- **Verdict**: Intentional design choice, no change needed.

**Issue #4 - Event Fired Under Lock** (MEDIUM)
- **Concern**: `TargetPlaybackRateChanged` event fired while holding lock could cause deadlock.
- **Finding**: Explicitly documented as intentional trade-off in code comments:
  ```csharp
  // Fire event outside of lock would be safer, but since this is called frequently
  // during audio callbacks, we fire inline to avoid allocation/queuing overhead.
  // Subscribers should be lightweight (just store the value).
  ```
- **Evidence**: Subscriber `OnTargetPlaybackRateChanged` IS lightweight (just sets property value).
- **Verdict**: Intentional design choice with documented reasoning, no change needed.

#### Remaining Issues (Lower Priority, Not Addressed):

- Issue #5: Resampler Not Flushed on Stream End (MEDIUM) - May cut off final samples
- Issue #6: No Underrun Detection (MEDIUM) - Silent gaps go undetected
- Issue #7: Rate Change During Read (LOW) - Theoretical race condition
- Issue #8: Magic Numbers for Buffer Sizing (LOW) - Could use config

**Result**: Two critical bugs fixed that could cause audio gaps and glitches during sample rate conversion scenarios. Two medium-priority items verified as intentional design choices.

### Session 5: Complete Remaining Pipeline Review Issues

**Objective**: Carefully examine remaining 4 issues (Issues #5-8) from the senior engineer review.

#### Issue #5: Resampler Not Flushed on Stream End (MEDIUM)

**Concern**: When source returns 0 samples, the WDL resampler's internal filter state is not flushed, potentially losing the last ~0.33ms of audio at track transitions.

**Investigation**:
- 16-tap sinc filter at 48kHz = ~0.33ms of latency (16/48000 seconds)
- When source returns 0, code fills with silence but doesn't flush resampler
- WdlResampler CAN be flushed by passing fewer samples than requested

**Finding**: In Sendspin's streaming architecture:
- Source returning 0 means "buffer temporarily empty", not "stream ended"
- Track changes trigger `Clear()` on buffer, not graceful stream end
- Any filter tail at track transition decays quickly with new samples
- Sub-millisecond impact is inaudible

**Verdict**: Design trade-off. The ~0.33ms impact at track transitions is imperceptible. Adding proper flush handling would require plumbing reset signals through multiple layers (SDK buffer to Services resampler) for negligible benefit.

#### Issue #6: No Underrun Detection (MEDIUM) - FIXED

**Concern**: When resampler produces fewer samples than requested, code silently fills with zeros, masking buffer underruns.

**Finding**: This IS a real observability issue. While filling with silence is correct behavior (WASAPI needs samples), lack of visibility makes debugging difficult.

**Fix Applied**:
- Added `SourceEmptyCount` property to track buffer empty events
- Added `ResamplerShortCount` property to track resampler short output
- Added rate-limited logging (max once per 5 seconds) to avoid flooding
- Logging includes cumulative counts for both underrun types

**Commit**: `5bd04b8` - fix(audio): add underrun detection and logging to resampler

#### Issue #7: Rate Change During Read (LOW)

**Concern**: Rate can change between when Read() captures currentRate and when Resample() executes.

**Investigation**:
- Read() gets rate under lock, releases lock, then does calculations
- PlaybackRate setter can update rate and call UpdateResamplerRates()
- Potential mismatch between input calculation and resampler configuration

**Key Protections Already in Place**:
1. `Math.Min(inputFrames, framesNeeded)` in Resample() prevents buffer overflow
2. Silence padding handles any output shortfall
3. Rate smoothing (5% factor) limits max change per buffer to ~0.4%
4. At 0.4% mismatch on 2048 samples = ~8 samples difference

**Verdict**: Design trade-off. The existing protections handle edge cases correctly. Rate smoothing ensures minimal practical impact. Holding lock during entire Read() would cause contention with rate updates during ~10ms audio callbacks.

#### Issue #8: Magic Numbers for Buffer Sizing (LOW)

**Concern**: Hardcoded values like `MaxExpectedOutputRequest = 16384` should be configurable.

**Review of Current Code**:
- Lines 181-187 document the calculation rationale
- Comment explains derivation: "Max WASAPI buffer request (~16384 samples for 100ms+ latency at 48kHz stereo)"
- Safety margin documented as "20% extra headroom"
- Formula explicitly stated in comments
- Runtime reallocation with warning handles edge cases (now with logging from Issue #6)

**Verdict**: Code style preference, not a bug. The magic numbers are:
1. Well-documented with clear rationale
2. Reasonable defaults for all typical configurations
3. Protected by safety net (reallocation with warning)
4. Not user-configurable by design (users don't understand audio buffering)

**Session Summary**:
- **1 bug fixed** (Issue #6): Added underrun detection with logging and counters
- **3 design choices verified** (Issues #5, #7, #8): All have appropriate trade-offs documented

**Total from Sessions 4+5**:
- 3 bugs fixed (Issues #1, #2, #6)
- 5 design choices verified (Issues #3, #4, #5, #7, #8)
