using Microsoft.Extensions.Configuration;

namespace Sendspin.Windows.Configuration;

/// <summary>
/// Reads the player's output delay from configuration, honouring the pre-SDK-10 key name.
/// </summary>
public static class OutputDelaySettings
{
    /// <summary>
    /// Configuration key for the output delay, in milliseconds.
    /// </summary>
    public const string Key = "Audio:OutputDelayMs";

    /// <summary>
    /// Name this setting had before SDK 10 renamed "static delay" to "output delay".
    /// </summary>
    public const string LegacyKey = "Audio:StaticDelayMs";

    /// <summary>
    /// Reads the configured output delay in milliseconds, clamped to the non-negative range
    /// the server accepts.
    /// </summary>
    /// <param name="configuration">The merged application configuration.</param>
    /// <returns>The output delay in milliseconds, 0 when nothing is configured.</returns>
    public static double ReadOutputDelayMs(this IConfiguration configuration)
    {
        // Config migration: an install that predates the SDK 10 rename has its calibrated
        // value under the old key in the user's appsettings.json, so fall back to it when
        // the new key is unset. The shipped appsettings.json deliberately seeds neither key
        // — a shipped default would shadow the user's old value and defeat the fallback.
        // The next save writes the new key only, so the old one stops being consulted.
        var delayMs = configuration.GetValue<double?>(Key)
            ?? configuration.GetValue<double>(LegacyKey, 0);

        // Must be non-negative: the Sendspin server (aiosendspin) validates the reported
        // delay in 0-5000 and drops the connection if a client reports a negative value,
        // so clamp on read (a stale negative config becomes 0).
        return Math.Max(0, delayMs);
    }
}
