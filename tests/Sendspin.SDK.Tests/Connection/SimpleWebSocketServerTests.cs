using System.Net.WebSockets;
using Sendspin.SDK.Connection;

namespace Sendspin.SDK.Tests.Connection;

public class SimpleWebSocketServerTests : IAsyncDisposable
{
    private readonly SimpleWebSocketServer _server = new();

    [Fact]
    public async Task Server_AcceptsWebSocketConnection()
    {
        _server.Start(0); // port 0 = OS assigns a random available port

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/sendspin"), CancellationToken.None);

        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(serverConn);
        Assert.Equal("/sendspin", serverConn.Path);
        Assert.Equal(WebSocketState.Open, client.State);

        await serverConn.DisposeAsync();
    }

    [Fact]
    public async Task Server_SendsAndReceivesTextMessages()
    {
        _server.Start(0);

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/test"), CancellationToken.None);

        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Client sends, server receives
        var received = new TaskCompletionSource<string>();
        serverConn.OnMessage = msg => received.TrySetResult(msg);

        var msgBytes = System.Text.Encoding.UTF8.GetBytes("hello from client");
        await client.SendAsync(msgBytes, WebSocketMessageType.Text, true, CancellationToken.None);

        var text = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello from client", text);

        // Server sends, client receives
        await serverConn.SendAsync("hello from server");

        var buffer = new byte[1024];
        var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        var response = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
        Assert.Equal("hello from server", response);

        await serverConn.DisposeAsync();
    }

    [Fact]
    public async Task Server_SendsAndReceivesBinaryMessages()
    {
        _server.Start(0);

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/test"), CancellationToken.None);

        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var received = new TaskCompletionSource<byte[]>();
        serverConn.OnBinary = data => received.TrySetResult(data);

        var payload = new byte[] { 0x04, 0x00, 0x01, 0x02, 0x03 };
        await client.SendAsync(payload, WebSocketMessageType.Binary, true, CancellationToken.None);

        var data = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(payload, data);

        await serverConn.DisposeAsync();
    }

    [Fact]
    public async Task Server_RaisesOnClose_WhenClientDisconnects()
    {
        _server.Start(0);

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/test"), CancellationToken.None);

        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var closed = new TaskCompletionSource<bool>();
        serverConn.OnClose = () => closed.TrySetResult(true);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

        var wasClosed = await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(wasClosed);

        await serverConn.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
    }
}
