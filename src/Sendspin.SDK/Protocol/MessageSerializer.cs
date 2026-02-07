using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Protocol;

/// <summary>
/// Handles serialization and deserialization of Sendspin protocol messages.
/// </summary>
public static class MessageSerializer
{
    private static readonly MessageSerializerContext s_context = MessageSerializerContext.Default;

    private static JsonTypeInfo<T> GetTypeInfo<T>() => (JsonTypeInfo<T>)s_context.GetTypeInfo(typeof(T))!;

    /// <summary>
    /// Serializes a message to JSON string.
    /// </summary>
    public static string Serialize<T>(T message) where T : IMessage
    {
        return JsonSerializer.Serialize(message, GetTypeInfo<T>());
    }

    /// <summary>
    /// Serializes a message to UTF-8 bytes.
    /// </summary>
    public static byte[] SerializeToBytes<T>(T message) where T : IMessage
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, GetTypeInfo<T>());
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
            MessageTypes.ServerHello => JsonSerializer.Deserialize(json, s_context.ServerHelloMessage),
            MessageTypes.ServerTime => JsonSerializer.Deserialize(json, s_context.ServerTimeMessage),
            MessageTypes.StreamStart => JsonSerializer.Deserialize(json, s_context.StreamStartMessage),
            MessageTypes.StreamEnd => JsonSerializer.Deserialize(json, s_context.StreamEndMessage),
            MessageTypes.StreamClear => JsonSerializer.Deserialize(json, s_context.StreamClearMessage),
            MessageTypes.GroupUpdate => JsonSerializer.Deserialize(json, s_context.GroupUpdateMessage),
            MessageTypes.ServerCommand => JsonSerializer.Deserialize(json, s_context.ServerCommandMessage), // Player commands (volume/mute)
            _ => null // Unknown message type
        };
    }

    /// <summary>
    /// Deserializes a specific message type.
    /// </summary>
    public static T? Deserialize<T>(string json) where T : class, IMessage
    {
        return JsonSerializer.Deserialize(json, GetTypeInfo<T>());
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
