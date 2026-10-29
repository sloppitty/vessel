using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

internal sealed class FakeSocketFactory : IWebSocketTransportFactory
{
    private readonly ConcurrentQueue<FakeSocket> sockets = new();
    public volatile bool FailConnect;
    /// <summary>When set, connecting waits for it and then succeeds even if it was cancelled meanwhile.</summary>
    public volatile TaskCompletionSource? ConnectGate;
    public IReadOnlyList<FakeSocket> Sockets => [.. sockets];
    public FakeSocket Latest => sockets.Last();

    public IWebSocketTransport Create()
    {
        var socket = new FakeSocket(FailConnect, ConnectGate);
        sockets.Enqueue(socket);
        return socket;
    }
}

internal sealed class FakeSocket(bool failConnect, TaskCompletionSource? connectGate = null) : IWebSocketTransport
{
    private readonly Channel<object?> inbound = Channel.CreateUnbounded<object?>();
    private readonly ConcurrentQueue<string> sent = new();
    private int reads;
    public volatile bool Connected, Closed, Disposed;

    public IReadOnlyList<string> Sent => [.. sent];
    /// <summary>Number of receive calls; the loop is sequential, so n+1 calls means n messages were handled.</summary>
    public int Reads => Volatile.Read(ref reads);

    public void Push(string json) => inbound.Writer.TryWrite(Encoding.UTF8.GetBytes(json));
    public void Fail() => inbound.Writer.TryWrite(new WebSocketException("dropped"));
    public void PeerClose() => inbound.Writer.TryWrite(null);

    public volatile bool Connecting;

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        Assert.Equal("wss://api.hyperliquid.xyz/ws", uri.ToString());
        if (failConnect) throw new WebSocketException("refused");
        Connecting = true;
        if (connectGate is not null) await connectGate.Task;
        Connected = true;
    }

    public Task SendTextAsync(string message, CancellationToken cancellationToken)
    {
        sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public async Task<byte[]?> ReceiveAsync(int maxBytes, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref reads);
        var item = await inbound.Reader.ReadAsync(cancellationToken);
        return item switch
        {
            Exception ex => throw ex,
            byte[] bytes => bytes,
            _ => null
        };
    }

    public Task CloseAsync(CancellationToken cancellationToken) { Closed = true; return Task.CompletedTask; }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
