# Implementation Plan: Sample Interpolation for Drop/Insert Operations

> **Status**: Planned (saved for future implementation)
> **Created**: 2026-01-04
> **Priority**: After playback rate correction (#3) is implemented

## Executive Summary

The goal is to improve audio quality during sync correction operations by using sample interpolation instead of simple frame repetition. Currently, when dropping or inserting samples for clock drift correction, the Windows client simply repeats the last output frame. This can produce audible "clicks" or discontinuities. The JS client uses linear interpolation to blend samples at boundaries, producing smoother transitions.

## Current Implementation

**Location**: `src/Sendspin.SDK/Audio/TimedAudioBuffer.cs`

**Current Approach** (lines 696-794):
- **Frame structure**: One frame = `_channels` samples (e.g., 2 samples for stereo: [L, R])
- **DROP operation**: Reads two frames from buffer, outputs `_lastOutputFrame` (the previous frame)
- **INSERT operation**: Outputs `_lastOutputFrame` without reading from buffer (pure repeat)

**Current state storage**:
- `_lastOutputFrame`: A `float[]` of size `_channels` storing the most recently output frame

**Problem**: Both operations output the exact same sample values as the previous frame, which creates:
1. Flat sections in the waveform (for INSERT - repeated samples)
2. Discontinuities when dropping (sudden jump from repeated last frame to next actual frame)

## Proposed Interpolation Strategy

**Linear interpolation** is recommended because:
1. **Low computational cost**: Only 1 multiply and 1 add per sample
2. **Acceptable quality**: At 48kHz sample rate, corrections are infrequent
3. **Real-time safe**: No memory allocation, predictable execution time
4. **Simplicity**: Easy to implement and debug

**Interpolation Formula for Stereo**:
```csharp
interpolated_L = (previous_L + next_L) / 2
interpolated_R = (previous_R + next_R) / 2
```

## New State Requirements

**New Field**:
```csharp
private float[]? _nextInputFrame;    // Lookahead frame for interpolation
```

**Modified Field Usage**:
- `_lastOutputFrame`: Frame just output (unchanged role)
- `_nextInputFrame`: Frame about to be output (new - needed for DROP/INSERT interpolation)

## Implementation Steps

### Step 1: Add New State Field

Add to the field declarations section (around line 88):
```csharp
private float[]? _nextInputFrame;     // Lookahead frame for interpolation during sync correction
```

### Step 2: Initialize New Field

In `ReadWithSyncCorrection` (line 700-701), extend initialization:
```csharp
_lastOutputFrame ??= new float[frameSamples];
_nextInputFrame ??= new float[frameSamples];
```

### Step 3: Reset New Field in Clear() and ResetSyncTracking()

In `Clear()` (around line 399) and `ResetSyncTracking()` (around line 438):
```csharp
_lastOutputFrame = null;
_nextInputFrame = null;
```

### Step 4: Add PeekSamplesFromBuffer Helper Method

Add after `ReadSamplesFromBuffer` (around line 520):

```csharp
/// <summary>
/// Peeks samples from the circular buffer without advancing read position.
/// Used for interpolation lookahead during sync correction.
/// Must be called under lock.
/// </summary>
/// <param name="destination">Buffer to copy peeked samples into.</param>
/// <param name="count">Number of samples to peek.</param>
/// <returns>Number of samples actually peeked.</returns>
private int PeekSamplesFromBuffer(Span<float> destination, int count)
{
    var toPeek = Math.Min(count, _count);
    var peeked = 0;
    var tempReadPos = _readPos;

    while (peeked < toPeek)
    {
        var chunkSize = Math.Min(toPeek - peeked, _buffer.Length - tempReadPos);
        _buffer.AsSpan(tempReadPos, chunkSize).CopyTo(destination.Slice(peeked, chunkSize));
        tempReadPos = (tempReadPos + chunkSize) % _buffer.Length;
        peeked += chunkSize;
    }

    return peeked;
}
```

### Step 5: Modify DROP Logic with Interpolation

**Current** (lines 744-765):
```csharp
if (_dropEveryNFrames > 0 && _framesSinceLastCorrection >= _dropEveryNFrames)
{
    _framesSinceLastCorrection = 0;

    // Read the frame we're replacing (consume it)
    ReadSamplesFromBuffer(tempFrame);
    samplesConsumed += frameSamples;

    // Read the frame we're DROPPING (consume it too)
    if (_count - samplesConsumed >= frameSamples)
    {
        ReadSamplesFromBuffer(tempFrame);
        samplesConsumed += frameSamples;
    }

    // Output the LAST frame instead (smoother transition)
    _lastOutputFrame.AsSpan().CopyTo(buffer.Slice(outputPos, frameSamples));
    outputPos += frameSamples;
    _samplesDroppedForSync += frameSamples;
    continue;
}
```

**Proposed** (with interpolation):
```csharp
if (_dropEveryNFrames > 0 && _framesSinceLastCorrection >= _dropEveryNFrames)
{
    _framesSinceLastCorrection = 0;

    // Read the frame we're replacing (consume it - this becomes "frameA")
    ReadSamplesFromBuffer(tempFrame);
    samplesConsumed += frameSamples;

    // Read the frame we're DROPPING (consume it - this becomes "frameB" for interpolation)
    Span<float> droppedFrame = stackalloc float[frameSamples];
    if (_count - samplesConsumed >= frameSamples)
    {
        ReadSamplesFromBuffer(droppedFrame);
        samplesConsumed += frameSamples;
    }
    else
    {
        // Fallback: not enough data, use tempFrame as both
        tempFrame.CopyTo(droppedFrame);
    }

    // Output interpolated frame: blend lastOutput with next frame after the drop
    var outputSpan = buffer.Slice(outputPos, frameSamples);
    for (int i = 0; i < frameSamples; i++)
    {
        outputSpan[i] = (_lastOutputFrame[i] + droppedFrame[i]) * 0.5f;
    }

    // Update last output frame for next iteration
    outputSpan.CopyTo(_lastOutputFrame);

    outputPos += frameSamples;
    _samplesDroppedForSync += frameSamples;
    continue;
}
```

### Step 6: Modify INSERT Logic with Interpolation

**Current** (lines 768-780):
```csharp
if (_insertEveryNFrames > 0 && _framesSinceLastCorrection >= _insertEveryNFrames)
{
    _framesSinceLastCorrection = 0;

    // Output last frame WITHOUT consuming input (slows down playback)
    _lastOutputFrame.AsSpan().CopyTo(buffer.Slice(outputPos, frameSamples));
    outputPos += frameSamples;
    _samplesInsertedForSync += frameSamples;

    continue;
}
```

**Proposed** (with interpolation):
```csharp
if (_insertEveryNFrames > 0 && _framesSinceLastCorrection >= _insertEveryNFrames)
{
    _framesSinceLastCorrection = 0;

    // Peek at the next frame for interpolation (without consuming)
    var remainingInBuffer = _count - samplesConsumed;
    if (remainingInBuffer >= frameSamples)
    {
        // Peek at next frame (don't consume - we'll read it on next iteration)
        PeekSamplesFromBuffer(_nextInputFrame, frameSamples);

        // Output interpolated frame: blend lastOutput with next frame
        var outputSpan = buffer.Slice(outputPos, frameSamples);
        for (int i = 0; i < frameSamples; i++)
        {
            outputSpan[i] = (_lastOutputFrame[i] + _nextInputFrame[i]) * 0.5f;
        }

        // Update last output frame
        outputSpan.CopyTo(_lastOutputFrame);
    }
    else
    {
        // Fallback: not enough data to peek, just repeat last frame
        _lastOutputFrame.AsSpan().CopyTo(buffer.Slice(outputPos, frameSamples));
    }

    outputPos += frameSamples;
    _samplesInsertedForSync += frameSamples;
    continue;
}
```

## Performance Considerations

**Correction frequency**: At 4% max correction rate with 48kHz stereo audio:
- Max corrections per second: 48000 * 0.04 = 1920 frames
- Actual corrections: Usually much lower (only when sync error exceeds 2ms deadband)
- Typical: 0-100 corrections per second during stable playback

**Net CPU impact**: Approximately equal to current approach (replaces copy with simple arithmetic).

## Testing Approach

### Unit Tests

Create `tests/Sendspin.SDK.Tests/Audio/TimedAudioBufferInterpolationTests.cs`:

```csharp
public class TimedAudioBufferInterpolationTests
{
    [Fact]
    public void Insert_ShouldInterpolateBetweenLastAndNextFrame()
    {
        // Setup: Buffer with known samples [1.0, 1.0], [3.0, 3.0], [5.0, 5.0]
        // Trigger INSERT between first two frames
        // Expected output: [1.0, 1.0], [2.0, 2.0], [3.0, 3.0], ...
    }

    [Fact]
    public void Drop_ShouldInterpolateAcrossDroppedFrame()
    {
        // Setup: Buffer with [1.0, 1.0], [2.0, 2.0], [5.0, 5.0]
        // Trigger DROP on middle frame
        // Expected interpolated frame: (1+5)/2 = 3.0
    }

    [Fact]
    public void Insert_WithInsufficientBuffer_ShouldFallbackToRepeat()
    {
        // When buffer is nearly empty, should fall back to repeating last frame
    }
}
```

### Manual Audio Quality Test

1. Generate a test sine wave at 440Hz
2. Force sync corrections at known intervals
3. Record output and analyze for click artifacts
4. Compare A/B: old (repeat) vs new (interpolate)

## Future Enhancements

1. **Configurable interpolation method**: Allow switching between linear and cubic
2. **Higher-order interpolation**: 4-point Catmull-Rom spline (~40dB sideband rejection)
3. **Statistics tracking**: Add interpolation metrics to `AudioBufferStats`

## Files to Modify

- `src/Sendspin.SDK/Audio/TimedAudioBuffer.cs` - Core logic changes
- `src/Sendspin.SDK/Audio/ITimedAudioBuffer.cs` - Optional stats updates
- `tests/Sendspin.SDK.Tests/` - New unit tests
