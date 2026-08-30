using Sendspin.SDK.Client;

namespace Sendspin.Windows.Services.Configuration;

/// <summary>
/// Maps connection mode between its persisted config value, its settings display name, and
/// <see cref="ConnectionMode"/>.
/// </summary>
public static class ConnectionModeMapping
{
    /// <summary>Display name for server-initiated mode.</summary>
    public const string AdvertiseOnlyDisplayName = "Let servers connect to me";

    /// <summary>Display name for client-initiated mode.</summary>
    public const string DiscoverOnlyDisplayName = "I choose a server";

    private const string AdvertiseOnlyConfigValue = "AdvertiseOnly";
    private const string DiscoverOnlyConfigValue = "DiscoverOnly";

    /// <summary>Gets the display names offered in the settings dropdown, in order.</summary>
    public static string[] DisplayNames { get; } =
    {
        AdvertiseOnlyDisplayName,
        DiscoverOnlyDisplayName,
    };

    /// <summary>Maps a persisted config value to a mode.</summary>
    /// <param name="configValue">The stored value, which may be legacy or absent.</param>
    /// <returns>The mode; never <see cref="ConnectionMode.Auto"/>.</returns>
    /// <remarks>
    /// Anything unrecognized — including the legacy "Auto", which ran both transports in
    /// violation of the spec — resolves to <see cref="ConnectionMode.AdvertiseOnly"/>.
    /// </remarks>
    public static ConnectionMode FromConfigValue(string? configValue)
        => configValue == DiscoverOnlyConfigValue
            ? ConnectionMode.DiscoverOnly
            : ConnectionMode.AdvertiseOnly;

    /// <summary>Maps a mode to its persisted config value.</summary>
    /// <param name="mode">The mode to persist.</param>
    /// <returns>The config value.</returns>
    public static string ToConfigValue(ConnectionMode mode)
        => mode == ConnectionMode.DiscoverOnly
            ? DiscoverOnlyConfigValue
            : AdvertiseOnlyConfigValue;

    /// <summary>Maps a mode to its settings display name.</summary>
    /// <param name="mode">The mode to display.</param>
    /// <returns>The display name.</returns>
    public static string ToDisplayName(ConnectionMode mode)
        => mode == ConnectionMode.DiscoverOnly
            ? DiscoverOnlyDisplayName
            : AdvertiseOnlyDisplayName;

    /// <summary>Maps a settings display name to a mode.</summary>
    /// <param name="displayName">The selected display name.</param>
    /// <returns>The mode; never <see cref="ConnectionMode.Auto"/>.</returns>
    public static ConnectionMode FromDisplayName(string? displayName)
        => displayName == DiscoverOnlyDisplayName
            ? ConnectionMode.DiscoverOnly
            : ConnectionMode.AdvertiseOnly;
}
