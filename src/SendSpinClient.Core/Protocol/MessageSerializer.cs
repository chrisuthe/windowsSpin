using System.Text.Json;
using System.Text.Json.Serialization;
using SendSpinClient.Core.Protocol.Messages;

namespace SendSpinClient.Core.Protocol;

/// <summary>
/// Handles serialization and deserialization of SendSpin protocol messages.
/// </summary>
public static class MessageSerializer
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)
        }
    };

    /// <summary>
    /// Serializes a message to JSON string.
    /// </summary>
    public static string Serialize<T>(T message) where T : IMessage
    {
        return JsonSerializer.Serialize(message, s_options);
    }

    /// <summary>
    /// Serializes a message to UTF-8 bytes.
    /// </summary>
    public static byte[] SerializeToBytes<T>(T message) where T : IMessage
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, s_options);
    }

    /// <summary>
    /// Deserializes a JSON message, returning the appropriate message type.
    /// </summary>
    public static IMessage? Deserialize(string json)
    {
        // First, parse to get the message type
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("type", out var typeProp))
        {
            return null;
        }

        var messageType = typeProp.GetString();
        return messageType switch
        {
            MessageTypes.ServerHello => JsonSerializer.Deserialize<ServerHelloMessage>(json, s_options),
            MessageTypes.ServerTime => JsonSerializer.Deserialize<ServerTimeMessage>(json, s_options),
            MessageTypes.StreamStart => JsonSerializer.Deserialize<StreamStartMessage>(json, s_options),
            MessageTypes.StreamEnd => JsonSerializer.Deserialize<StreamEndMessage>(json, s_options),
            MessageTypes.StreamClear => JsonSerializer.Deserialize<StreamClearMessage>(json, s_options),
            MessageTypes.GroupUpdate => JsonSerializer.Deserialize<GroupUpdateMessage>(json, s_options),
            _ => null // Unknown message type
        };
    }

    /// <summary>
    /// Deserializes a specific message type.
    /// </summary>
    public static T? Deserialize<T>(string json) where T : class, IMessage
    {
        return JsonSerializer.Deserialize<T>(json, s_options);
    }

    /// <summary>
    /// Gets the message type from a JSON string without full deserialization.
    /// </summary>
    public static string? GetMessageType(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var typeProp))
            {
                return typeProp.GetString();
            }
        }
        catch (JsonException)
        {
            // Invalid JSON
        }
        return null;
    }

    /// <summary>
    /// Gets the message type from a UTF-8 byte span without full deserialization.
    /// </summary>
    public static string? GetMessageType(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName &&
                    reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    return reader.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Invalid JSON
        }
        return null;
    }
}
