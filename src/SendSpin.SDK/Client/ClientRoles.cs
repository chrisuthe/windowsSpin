namespace SendSpin.SDK.Client;

/// <summary>
/// Client role identifiers used in the SendSpin protocol.
/// Roles define what capabilities a client has.
/// </summary>
public static class ClientRoles
{
    /// <summary>
    /// Player role - outputs synchronized audio.
    /// </summary>
    public const string Player = "player";

    /// <summary>
    /// Controller role - can control group playback (play, pause, volume, etc.).
    /// </summary>
    public const string Controller = "controller";

    /// <summary>
    /// Metadata role - receives track metadata updates.
    /// </summary>
    public const string Metadata = "metadata";

    /// <summary>
    /// Artwork role - receives album artwork.
    /// </summary>
    public const string Artwork = "artwork";

    /// <summary>
    /// Visualizer role - receives audio visualization data.
    /// </summary>
    public const string Visualizer = "visualizer";
}

/// <summary>
/// Commands that can be sent to control playback.
/// </summary>
public enum PlayerCommand
{
    Play,
    Pause,
    Stop,
    Next,
    Previous,
    Shuffle,
    Repeat
}

/// <summary>
/// Volume adjustment modes.
/// </summary>
public enum VolumeMode
{
    /// <summary>
    /// Set absolute volume level.
    /// </summary>
    Absolute,

    /// <summary>
    /// Adjust volume by delta.
    /// </summary>
    Relative
}
