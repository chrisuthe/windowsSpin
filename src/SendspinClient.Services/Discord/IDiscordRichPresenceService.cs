using Sendspin.SDK.Models;

namespace SendspinClient.Services.Discord;

/// <summary>
/// Service interface for Discord Rich Presence integration.
/// </summary>
/// <remarks>
/// <para>
/// This service manages the connection to Discord and updates the user's
/// Rich Presence to display currently playing track information.
/// </para>
/// <para>
/// The service handles Discord rate limiting internally (max ~15 updates/minute)
/// and gracefully handles scenarios where Discord is not running.
/// </para>
/// </remarks>
public interface IDiscordRichPresenceService : IDisposable
{
    /// <summary>
    /// Gets whether the Discord Rich Presence feature is enabled.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Gets whether the service is currently connected to Discord.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Enables Discord Rich Presence and connects to Discord.
    /// </summary>
    /// <remarks>
    /// If Discord is not running, the service will silently wait for it.
    /// Connection status can be monitored via <see cref="IsConnected"/>.
    /// </remarks>
    void Enable();

    /// <summary>
    /// Disables Discord Rich Presence and disconnects from Discord.
    /// </summary>
    /// <remarks>
    /// This will clear the current presence before disconnecting.
    /// </remarks>
    void Disable();

    /// <summary>
    /// Updates the Discord Rich Presence with current playback information.
    /// </summary>
    /// <param name="track">The currently playing track metadata, or null if nothing is playing.</param>
    /// <param name="state">The current playback state.</param>
    /// <param name="positionSeconds">The current playback position in seconds.</param>
    /// <remarks>
    /// <para>
    /// Updates are debounced to respect Discord's rate limits. Redundant updates
    /// (same track and state) are skipped automatically.
    /// </para>
    /// <para>
    /// When <paramref name="state"/> is not Playing, the presence will show
    /// the track info without elapsed time, or will be cleared if no track is set.
    /// </para>
    /// </remarks>
    void UpdatePresence(TrackMetadata? track, PlaybackState state, double positionSeconds);

    /// <summary>
    /// Clears the current Discord Rich Presence.
    /// </summary>
    /// <remarks>
    /// Call this when playback stops or the application is disconnecting
    /// from the media server.
    /// </remarks>
    void ClearPresence();
}
