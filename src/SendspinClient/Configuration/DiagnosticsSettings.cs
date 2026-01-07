// <copyright file="DiagnosticsSettings.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace SendspinClient.Configuration;

/// <summary>
/// Configuration settings for diagnostic audio recording.
/// These settings can be modified in appsettings.json.
/// </summary>
public class DiagnosticsSettings
{
    /// <summary>
    /// The configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "Diagnostics";

    /// <summary>
    /// Gets or sets whether diagnostic recording is enabled by default.
    /// When false (default), recording must be manually enabled via the Stats window.
    /// Default: false.
    /// </summary>
    public bool EnableRecording { get; set; }

    /// <summary>
    /// Gets or sets the duration of the circular audio buffer in seconds.
    /// Determines how many seconds of audio can be captured when saving.
    /// Higher values use more memory (~384KB per second for 48kHz stereo).
    /// Default: 45 seconds (~17MB).
    /// </summary>
    public int BufferSeconds { get; set; } = 45;

    /// <summary>
    /// Gets or sets the interval in milliseconds for recording sync metrics.
    /// Lower values provide more detailed markers but use more memory.
    /// Default: 100ms.
    /// </summary>
    public int MetricIntervalMs { get; set; } = 100;

    /// <summary>
    /// Gets or sets the directory for diagnostic recordings.
    /// If empty, defaults to %LocalAppData%\WindowsSpin\diagnostics\.
    /// </summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets the effective output directory, using the default if none specified.
    /// </summary>
    public string GetEffectiveOutputDirectory()
    {
        if (!string.IsNullOrWhiteSpace(OutputDirectory))
        {
            return Environment.ExpandEnvironmentVariables(OutputDirectory);
        }

        return AppPaths.DiagnosticsDirectory;
    }
}
