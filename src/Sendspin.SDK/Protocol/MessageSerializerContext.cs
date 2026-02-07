using System.Text.Json;
using System.Text.Json.Serialization;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Protocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = new[] { typeof(SnakeCaseEnumConverter), typeof(OptionalJsonConverterFactory) })]
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

internal sealed class SnakeCaseEnumConverter : JsonStringEnumConverter
{
    public SnakeCaseEnumConverter()
        : base(JsonNamingPolicy.SnakeCaseLower)
    {
    }
}
