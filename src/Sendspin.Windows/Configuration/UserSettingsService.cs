using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sendspin.Windows.Services.Configuration;

namespace Sendspin.Windows.Configuration;

/// <summary>
/// Service for reading and writing user settings to appsettings.json.
/// Centralizes the pattern of loading JSON, modifying a section, and writing it back.
/// </summary>
public interface IUserSettingsService
{
    /// <summary>
    /// Updates a single setting value in the user settings file.
    /// Creates the file and section if they don't exist.
    /// </summary>
    /// <param name="sectionName">The top-level section name (e.g., "Audio", "Player").</param>
    /// <param name="propertyName">The property name within the section.</param>
    /// <param name="value">The value to set (string, int, double, bool, or null).</param>
    Task UpdateSettingAsync(string sectionName, string propertyName, object? value);

    /// <summary>
    /// Updates multiple settings within a single section atomically.
    /// Creates the file and section if they don't exist.
    /// </summary>
    /// <param name="sectionName">The top-level section name.</param>
    /// <param name="settings">Dictionary of property names to values.</param>
    Task UpdateSectionAsync(string sectionName, Dictionary<string, object?> settings);

    /// <summary>
    /// Updates multiple sections atomically in a single write operation.
    /// Creates the file and sections if they don't exist.
    /// </summary>
    /// <param name="sections">Dictionary of section names to their property dictionaries.</param>
    Task UpdateMultipleSectionsAsync(Dictionary<string, Dictionary<string, object?>> sections);

    /// <summary>
    /// Reads the current value of a setting.
    /// </summary>
    /// <typeparam name="T">The expected type of the setting.</typeparam>
    /// <param name="sectionName">The top-level section name.</param>
    /// <param name="propertyName">The property name within the section.</param>
    /// <param name="defaultValue">Default value if setting doesn't exist.</param>
    /// <returns>The setting value or default.</returns>
    Task<T?> ReadSettingAsync<T>(string sectionName, string propertyName, T? defaultValue = default);
}

/// <summary>
/// Implementation of IUserSettingsService that persists to JSON files.
/// </summary>
/// <remarks>
/// Writes are serialized on one lock and land atomically (written beside the file, then renamed over
/// it), so a reader never observes a partially written file. Nothing here needs the caller's
/// synchronization context, so every await drops it and callers may block on these tasks.
/// </remarks>
public class UserSettingsService : IUserSettingsService, IDisposable
{
    private const int ReplaceAttempts = 5;
    private const int ReplaceRetryDelayMs = 100;

    private readonly ILogger<UserSettingsService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private bool _disposed;

    public UserSettingsService(ILogger<UserSettingsService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _fileLock.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async Task UpdateSettingAsync(string sectionName, string propertyName, object? value)
    {
        await UpdateSectionAsync(sectionName, new Dictionary<string, object?> { { propertyName, value } }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateSectionAsync(string sectionName, Dictionary<string, object?> settings)
    {
        await UpdateMultipleSectionsAsync(new Dictionary<string, Dictionary<string, object?>>
        {
            { sectionName, settings }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateMultipleSectionsAsync(Dictionary<string, Dictionary<string, object?>> sections)
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            AppPaths.EnsureUserDataDirectoryExists();
            var root = await LoadSettingsFileAsync().ConfigureAwait(false);

            foreach (var (sectionName, settings) in sections)
            {
                var section = root[sectionName]?.AsObject() ?? new JsonObject();

                foreach (var (propertyName, value) in settings)
                {
                    section[propertyName] = ConvertToJsonNode(value);
                }

                root[sectionName] = section;
            }

            await SaveSettingsFileAsync(root).ConfigureAwait(false);

            _logger.LogDebug("Settings updated: {Sections}", string.Join(", ", sections.Keys));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update settings");
            throw;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<T?> ReadSettingAsync<T>(string sectionName, string propertyName, T? defaultValue = default)
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(AppPaths.UserSettingsPath))
            {
                return defaultValue;
            }

            var json = await File.ReadAllTextAsync(AppPaths.UserSettingsPath).ConfigureAwait(false);
            var root = SettingsJson.Parse(json);

            var value = root[sectionName]?[propertyName];
            if (value == null)
            {
                return defaultValue;
            }

            return value.Deserialize<T>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read setting {Section}.{Property}", sectionName, propertyName);
            return defaultValue;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Loads the settings file for an update.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The file cannot be parsed. The update is abandoned and the file left as it is. Reading it as
    /// empty, as this used to, made the save replace every setting the user had with the one being
    /// changed.
    /// </exception>
    private static async Task<JsonObject> LoadSettingsFileAsync()
    {
        if (!File.Exists(AppPaths.UserSettingsPath))
        {
            return new JsonObject();
        }

        var json = await File.ReadAllTextAsync(AppPaths.UserSettingsPath).ConfigureAwait(false);
        try
        {
            return SettingsJson.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"The settings file {AppPaths.UserSettingsPath} is not valid JSON, so it was not changed. Fix or remove it to save settings again.",
                ex);
        }
    }

    private static async Task SaveSettingsFileAsync(JsonNode root)
    {
        // Written beside the target and renamed over it, so a concurrent reader (including the
        // configuration reload that watches this file) never sees a partially written file.
        var tempPath = AppPaths.UserSettingsPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, root.ToJsonString(WriteOptions)).ConfigureAwait(false);

        // Windows refuses to replace a file that another handle holds open without delete sharing,
        // which that same reload reader, and virus scanners, do for a few milliseconds at a time.
        for (var attempt = 1; attempt <= ReplaceAttempts; attempt++)
        {
            try
            {
                File.Move(tempPath, AppPaths.UserSettingsPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < ReplaceAttempts && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(ReplaceRetryDelayMs).ConfigureAwait(false);
            }
        }
    }

    private static JsonNode? ConvertToJsonNode(object? value)
    {
        return value switch
        {
            null => null,
            string s => JsonValue.Create(s),
            bool b => JsonValue.Create(b),
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            double d => JsonValue.Create(d),
            float f => JsonValue.Create(f),
            _ => JsonValue.Create(value.ToString())
        };
    }
}
