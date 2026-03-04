using System.Text.Json;
using System.Text.Json.Serialization;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Protocol;

/// <summary>
/// Source-generated JSON serializer context for all Sendspin protocol messages.
/// Enables NativeAOT-compatible serialization without runtime reflection.
/// </summary>
/// <remarks>
/// When adding a new message type, add a [JsonSerializable(typeof(NewMessageType))]
/// attribute here to include it in source generation.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(SnakeCaseEnumConverter), typeof(OptionalJsonConverterFactory)])]
[JsonSerializable(typeof(ClientHelloMessage))]
[JsonSerializable(typeof(ClientGoodbyeMessage))]
[JsonSerializable(typeof(ClientTimeMessage))]
[JsonSerializable(typeof(ClientCommandMessage))]
[JsonSerializable(typeof(ClientStateMessage))]
[JsonSerializable(typeof(ClientSyncOffsetMessage))]
[JsonSerializable(typeof(ClientSyncOffsetAckMessage))]
[JsonSerializable(typeof(StreamRequestFormatMessage))]
[JsonSerializable(typeof(ServerHelloMessage))]
[JsonSerializable(typeof(ServerTimeMessage))]
[JsonSerializable(typeof(StreamStartMessage))]
[JsonSerializable(typeof(StreamEndMessage))]
[JsonSerializable(typeof(StreamClearMessage))]
[JsonSerializable(typeof(GroupUpdateMessage))]
[JsonSerializable(typeof(ServerCommandMessage))]
[JsonSerializable(typeof(ServerStateMessage))]
internal partial class MessageSerializerContext : JsonSerializerContext
{
}

/// <summary>
/// Concrete enum converter for source generation (JsonStringEnumConverter cannot be
/// used directly in [JsonSourceGenerationOptions] Converters array).
/// </summary>
internal sealed class SnakeCaseEnumConverter : JsonStringEnumConverter
{
    public SnakeCaseEnumConverter()
        : base(JsonNamingPolicy.SnakeCaseLower)
    {
    }
}
