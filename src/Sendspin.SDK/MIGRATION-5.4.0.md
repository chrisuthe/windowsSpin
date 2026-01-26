# Sendspin.SDK 5.4.0 Migration Guide

## Overview

Version 5.4.0 introduces proper separation between **player volume** (this client's actual volume) and **group volume** (the average displayed to controllers). This fixes group volume control issues where the Windows client wasn't responding correctly to server commands.

---

## Breaking Changes

### 1. `GroupStateChanged` No Longer Contains Player Volume

**Before (5.3.x)**: `GroupState.Volume` and `GroupState.Muted` represented this player's current state.

**After (5.4.0)**: `GroupState.Volume` and `GroupState.Muted` now represent the **group average** (for display only). Your player's actual volume is in the new `PlayerStateChanged` event.

**Migration**:
```csharp
// ❌ OLD - Don't use GroupState for player volume anymore
client.GroupStateChanged += (s, group) => {
    myVolumeSlider.Value = group.Volume;  // Wrong! This is group average
    myMuteButton.IsChecked = group.Muted; // Wrong! This is group muted
};

// ✅ NEW - Use PlayerStateChanged for player volume
client.PlayerStateChanged += (s, playerState) => {
    myVolumeSlider.Value = playerState.Volume;  // Correct!
    myMuteButton.IsChecked = playerState.Muted; // Correct!
};

// GroupState is still used for playback state, metadata, etc.
client.GroupStateChanged += (s, group) => {
    myPlaybackState = group.PlaybackState;  // Still correct
    myTrackTitle.Text = group.Metadata?.Title;  // Still correct
};
```

---

## New Features

### 2. New `PlayerState` Model

A new model class to represent this player's volume and mute state:

```csharp
namespace Sendspin.SDK.Models;

public sealed class PlayerState
{
    public int Volume { get; set; } = 100;  // 0-100
    public bool Muted { get; set; }
}
```

### 3. New `PlayerStateChanged` Event

Fires when the server sends a `server/command` to change this player's volume or mute state:

```csharp
public interface ISendSpinClient
{
    // NEW in 5.4.0
    event EventHandler<PlayerState>? PlayerStateChanged;
    PlayerState CurrentPlayerState { get; }

    // Existing (unchanged)
    event EventHandler<GroupState>? GroupStateChanged;
    GroupState? CurrentGroup { get; }
}
```

### 4. Automatic ACK After `server/command`

The SDK now automatically sends a `client/state` acknowledgement when it receives and applies a `server/command` for volume/mute. **You don't need to do anything** - this happens internally.

This fixes the spec compliance issue where the server didn't know the player applied the change.

### 5. `ClientCapabilities.InitialVolume` and `InitialMuted`

New properties to set the player's initial volume/mute when connecting:

```csharp
var capabilities = new ClientCapabilities
{
    ClientName = "My Player",
    InitialVolume = 75,      // NEW - Start at 75% volume
    InitialMuted = false     // NEW - Start unmuted
};

var client = new SendspinClientService(logger, connection,
    clockSync, capabilities, pipeline);
```

These values are:
1. Sent to the server in the initial `client/state` handshake
2. Applied to the audio pipeline on connection
3. Used to initialize `CurrentPlayerState`

---

## Recommended Implementation Pattern

```csharp
public class MyPlayer
{
    private SendspinClientService _client;
    private int _playerVolume = 100;
    private bool _isPlayerMuted = false;
    private bool _isUpdatingFromServer = false;

    public void Initialize()
    {
        // Load persisted volume from settings
        _playerVolume = LoadVolumeFromSettings();
        _isPlayerMuted = LoadMutedFromSettings();

        var capabilities = new ClientCapabilities
        {
            InitialVolume = _playerVolume,
            InitialMuted = _isPlayerMuted
        };

        _client = new SendspinClientService(logger, connection,
            clockSync, capabilities, pipeline);

        // Subscribe to PLAYER state (your volume)
        _client.PlayerStateChanged += OnPlayerStateChanged;

        // Subscribe to GROUP state (playback info, metadata)
        _client.GroupStateChanged += OnGroupStateChanged;
    }

    private void OnPlayerStateChanged(object? sender, PlayerState state)
    {
        // Server changed our volume via controller
        _isUpdatingFromServer = true;
        try
        {
            _playerVolume = state.Volume;
            _isPlayerMuted = state.Muted;
            UpdateUI();

            // Optionally persist the new values
            SaveVolumeToSettings(state.Volume);
            SaveMutedToSettings(state.Muted);
        }
        finally
        {
            _isUpdatingFromServer = false;
        }
    }

    private void OnGroupStateChanged(object? sender, GroupState group)
    {
        // Update playback state, track info, etc.
        // Do NOT update volume/mute from here
        UpdatePlaybackState(group.PlaybackState);
        UpdateTrackMetadata(group.Metadata);
    }

    public void OnUserChangedVolume(int newVolume)
    {
        if (_isUpdatingFromServer) return;  // Avoid feedback loop

        _playerVolume = newVolume;

        // Apply to audio immediately
        _client.SetVolume(newVolume);

        // Notify server
        _ = _client.SendPlayerStateAsync(newVolume, _isPlayerMuted);

        // Persist
        SaveVolumeToSettings(newVolume);
    }
}
```

---

## Audio Volume Curve (Optional Enhancement)

The SDK doesn't enforce a volume curve, but for perceived loudness matching the reference CLI, apply a **power curve** in your audio output:

```csharp
// In your audio sample provider
float amplitude = (float)Math.Pow(volume / 100.0, 1.5);
sample *= amplitude;
```

| Linear Volume | Power Curve Amplitude | Perceived Effect |
|---------------|----------------------|------------------|
| 100% | 1.0 | Full volume |
| 50% | 0.35 | Half perceived loudness |
| 25% | 0.125 | Quarter perceived loudness |
| 10% | 0.03 | Very quiet |

---

## Summary Checklist

- [ ] Subscribe to `PlayerStateChanged` for volume/mute updates from server
- [ ] Stop reading `Volume`/`Muted` from `GroupStateChanged`
- [ ] Set `ClientCapabilities.InitialVolume`/`InitialMuted` for persistence
- [ ] Use `_isUpdatingFromServer` flag to prevent feedback loops
- [ ] (Optional) Apply power curve `amplitude = volume^1.5` for perceived loudness

---

## Why This Matters for Groups

When you have players at different volumes (e.g., 15% and 45%), the server calculates a group average (30%). Before 5.4.0, Windows would incorrectly apply this 30% to its own audio. Now it correctly ignores the group average and only responds to explicit `server/command` messages that target this specific player.
