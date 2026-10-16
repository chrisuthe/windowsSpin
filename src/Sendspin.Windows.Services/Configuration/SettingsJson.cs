using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sendspin.Windows.Services.Configuration;

/// <summary>
/// Parses the user settings file for every code path that updates it.
/// </summary>
/// <remarks>
/// The app reads its settings through Microsoft.Extensions.Configuration, which skips comments
/// and allows trailing commas. A strict parse rejects both, so a hand-edited file could load
/// fine while every attempt to update it failed. Writers parse with the same leniency, so a file
/// the app can read is a file it can update.
/// </remarks>
public static class SettingsJson
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Parses settings file text into its root object.
    /// </summary>
    /// <param name="json">The file's contents.</param>
    /// <returns>The root object; empty when the file has no content at all.</returns>
    /// <exception cref="JsonException">
    /// The text is not a JSON object. Callers must not treat this as an empty file: writing over it
    /// would replace every setting the user has with the one being changed.
    /// </exception>
    public static JsonObject Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        return JsonNode.Parse(json, documentOptions: DocumentOptions) as JsonObject
            ?? throw new JsonException("The settings file does not contain a JSON object.");
    }
}
