using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Protocol;

public class OptionalJsonConverterTests
{
    [Fact]
    public void Optional_AbsentField_DeserializesToAbsent()
    {
        // JSON with no "progress" field — should be Absent
        var json = """
        {
            "type": "server/state",
            "payload": {
                "metadata": {
                    "title": "Test Song",
                    "artist": "Test Artist"
                }
            }
        }
        """;

        var msg = MessageSerializer.Deserialize<ServerStateMessage>(json);
        Assert.NotNull(msg);
        Assert.NotNull(msg.Payload.Metadata);
        Assert.True(msg.Payload.Metadata.Progress.IsAbsent);
    }

    [Fact]
    public void Optional_ExplicitNull_DeserializesToPresentNull()
    {
        // JSON with "progress": null — means track ended
        var json = """
        {
            "type": "server/state",
            "payload": {
                "metadata": {
                    "title": "Test Song",
                    "artist": "Test Artist",
                    "progress": null
                }
            }
        }
        """;

        var msg = MessageSerializer.Deserialize<ServerStateMessage>(json);
        Assert.NotNull(msg);
        Assert.NotNull(msg.Payload.Metadata);
        Assert.True(msg.Payload.Metadata.Progress.IsPresent);
        Assert.Null(msg.Payload.Metadata.Progress.Value);
    }
}
