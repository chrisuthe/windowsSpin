namespace Sendspin.SDK.Models;

/// <summary>
/// Represents this player's own volume and mute state.
/// </summary>
/// <remarks>
/// <para>
/// This is distinct from <see cref="GroupState"/> which represents the group average.
/// The server controls player volume via <c>server/command</c> messages, while
/// <c>group/update</c> and <c>server/state</c> provide the group aggregate for display.
/// </para>
/// <para>
/// Per the Sendspin spec, group volume is calculated as the average of all player
/// volumes in the group. When a controller adjusts group volume, the server sends
/// individual <c>server/command</c> messages to each player with their new volume
/// (calculated via the redistribution algorithm that preserves relative differences).
/// </para>
/// </remarks>
public sealed class PlayerState
{
    /// <summary>
    /// This player's volume (0-100). Applied to audio output.
    /// </summary>
    /// <remarks>
    /// Set by <c>server/command</c> messages from the server, or by local user input.
    /// This value is sent back to the server via <c>client/state</c> messages.
    /// </remarks>
    public int Volume { get; set; } = 100;

    /// <summary>
    /// Whether this player is muted. Applied to audio output.
    /// </summary>
    /// <remarks>
    /// Set by <c>server/command</c> messages from the server, or by local user input.
    /// This value is sent back to the server via <c>client/state</c> messages.
    /// </remarks>
    public bool Muted { get; set; }
}
