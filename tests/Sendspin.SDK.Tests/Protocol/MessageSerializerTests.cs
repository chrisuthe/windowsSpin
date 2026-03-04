using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Protocol;

public class MessageSerializerTests
{
    [Fact]
    public void Serialize_ClientTimeMessage_RoundTrips()
    {
        var original = new ClientTimeMessage
        {
            Payload = new ClientTimePayload { ClientTransmitted = 123456789 }
        };

        var json = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize<ClientTimeMessage>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("client/time", deserialized.Type);
        Assert.Equal(123456789, deserialized.Payload.ClientTransmitted);
    }

    [Fact]
    public void Serialize_UsesSnakeCaseNaming()
    {
        var msg = new ClientTimeMessage
        {
            Payload = new ClientTimePayload { ClientTransmitted = 100 }
        };

        var json = MessageSerializer.Serialize(msg);

        Assert.Contains("\"client_transmitted\"", json);
        Assert.DoesNotContain("\"ClientTransmitted\"", json);
    }

    [Fact]
    public void Deserialize_ServerHelloMessage_ParsesCorrectly()
    {
        var json = """
        {
            "type": "server/hello",
            "payload": {
                "server_id": "test-server",
                "name": "Test Server",
                "version": 1,
                "active_roles": ["player@v1"],
                "connection_reason": "discovery"
            }
        }
        """;

        var msg = MessageSerializer.Deserialize(json) as ServerHelloMessage;

        Assert.NotNull(msg);
        Assert.Equal("test-server", msg.ServerId);
        Assert.Equal("Test Server", msg.Name);
        Assert.Equal(1, msg.Version);
        Assert.Single(msg.ActiveRoles);
        Assert.Equal("discovery", msg.ConnectionReason);
    }

    [Fact]
    public void Deserialize_UnknownType_ReturnsNull()
    {
        var json = """{"type": "unknown/type", "payload": {}}""";
        var result = MessageSerializer.Deserialize(json);
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_AllServerMessageTypes_Succeeds()
    {
        var testCases = new Dictionary<string, Type>
        {
            ["server/hello"] = typeof(ServerHelloMessage),
            ["server/time"] = typeof(ServerTimeMessage),
            ["stream/start"] = typeof(StreamStartMessage),
            ["stream/end"] = typeof(StreamEndMessage),
            ["stream/clear"] = typeof(StreamClearMessage),
            ["group/update"] = typeof(GroupUpdateMessage),
            ["server/command"] = typeof(ServerCommandMessage),
        };

        foreach (var (type, expectedType) in testCases)
        {
            var json = $$"""{ "type": "{{type}}", "payload": {} }""";
            var msg = MessageSerializer.Deserialize(json);
            Assert.NotNull(msg);
            Assert.IsType(expectedType, msg);
        }
    }
}
