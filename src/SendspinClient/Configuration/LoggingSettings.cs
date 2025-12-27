using System.IO;

namespace SendspinClient.Configuration;

/// <summary>
/// Configuration settings for application logging.
/// These settings can be modified in appsettings.json.
/// </summary>
public class LoggingSettings
{
    /// <summary>
    /// The configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "Logging";

    /// <summary>
    /// Gets or sets the minimum log level.
    /// Valid values: Verbose, Debug, Information, Warning, Error, Fatal.
    /// Default: Information.
    /// </summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// Gets or sets whether to enable file logging.
    /// When enabled, logs are written to %LocalAppData%\Sendspin\logs\.
    /// Default: true.
    /// </summary>
    public bool EnableFileLogging { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to enable console logging.
    /// Useful for debugging when running from command line.
    /// Default: false.
    /// </summary>
    public bool EnableConsoleLogging { get; set; }

    /// <summary>
    /// Gets or sets the directory for log files.
    /// If empty, defaults to %LocalAppData%\Sendspin\logs\.
    /// </summary>
    public string LogDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum size of each log file in megabytes.
    /// When exceeded, a new file is created.
    /// Default: 10 MB.
    /// </summary>
    public int MaxFileSizeMB { get; set; } = 10;

    /// <summary>
    /// Gets or sets the number of log files to retain.
    /// Older files are automatically deleted.
    /// Default: 5.
    /// </summary>
    public int RetainedFileCount { get; set; } = 5;

    /// <summary>
    /// Gets or sets the output template for log messages.
    /// Uses Serilog template syntax.
    /// </summary>
    public string OutputTemplate { get; set; } =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Gets the effective log directory, using the default if none specified.
    /// </summary>
    public string GetEffectiveLogDirectory()
    {
        if (!string.IsNullOrWhiteSpace(LogDirectory))
        {
            return Environment.ExpandEnvironmentVariables(LogDirectory);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sendspin",
            "logs");
    }
}
