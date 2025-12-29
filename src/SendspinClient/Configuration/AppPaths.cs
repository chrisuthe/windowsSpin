using System.IO;

namespace SendspinClient.Configuration;

/// <summary>
/// Provides consistent paths for application data storage.
/// User settings and logs are stored in %LocalAppData%\WindowsSpin\ to ensure
/// write access regardless of installation location (e.g., Program Files).
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// The application name used for folder naming.
    /// </summary>
    public const string AppName = "WindowsSpin";

    /// <summary>
    /// Gets the user data directory for storing settings, logs, and other user-specific data.
    /// Located at %LocalAppData%\WindowsSpin\.
    /// </summary>
    public static string UserDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppName);

    /// <summary>
    /// Gets the path to the user settings file.
    /// Located at %LocalAppData%\WindowsSpin\appsettings.json.
    /// </summary>
    public static string UserSettingsPath { get; } = Path.Combine(UserDataDirectory, "appsettings.json");

    /// <summary>
    /// Gets the log directory for storing application logs.
    /// Located at %LocalAppData%\WindowsSpin\logs\.
    /// </summary>
    public static string LogDirectory { get; } = Path.Combine(UserDataDirectory, "logs");

    /// <summary>
    /// Gets the installation directory where the application executable is located.
    /// </summary>
    public static string InstallDirectory { get; } = AppDomain.CurrentDomain.BaseDirectory;

    /// <summary>
    /// Gets the path to the default settings file in the installation directory.
    /// This file contains factory defaults and is read-only.
    /// </summary>
    public static string DefaultSettingsPath { get; } = Path.Combine(InstallDirectory, "appsettings.json");

    /// <summary>
    /// Ensures the user data directory exists.
    /// </summary>
    public static void EnsureUserDataDirectoryExists()
    {
        Directory.CreateDirectory(UserDataDirectory);
    }

    /// <summary>
    /// Ensures the log directory exists.
    /// </summary>
    public static void EnsureLogDirectoryExists()
    {
        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>
    /// Copies the default settings to the user settings location if no user settings exist.
    /// This provides first-run initialization with factory defaults.
    /// </summary>
    /// <returns>True if settings were copied, false if user settings already exist.</returns>
    public static bool InitializeUserSettingsIfNeeded()
    {
        if (File.Exists(UserSettingsPath))
        {
            return false;
        }

        EnsureUserDataDirectoryExists();

        if (File.Exists(DefaultSettingsPath))
        {
            File.Copy(DefaultSettingsPath, UserSettingsPath);
            return true;
        }

        // No default settings file, create an empty one
        File.WriteAllText(UserSettingsPath, "{}");
        return true;
    }
}
