using System.Net;
using System.Net.Sockets;

namespace Vessel.Tests;

/// <summary>
/// A loopback TCP relay in front of the disposable broker, so a test can make RabbitMQ unreachable and reachable again
/// without touching the broker itself. <see cref="Down"/> stops accepting (new connections are refused) and resets
/// every relayed connection; <see cref="Up"/> accepts again on the same port.
/// </summary>
internal sealed class BrokerProxy : IAsyncDisposable
{
    private readonly string targetHost;
    private readonly int targetPort;
    private readonly int port;
    private readonly List<TcpClient> connections = [];
    private TcpListener? listener;
    private CancellationTokenSource? accepting;
    private bool down = true;

    private BrokerProxy(Uri target)
    {
        targetHost = target.Host;
        targetPort = target.IsDefaultPort || target.Port < 0 ? 5672 : target.Port;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Uri = new UriBuilder(target) { Host = "127.0.0.1", Port = port }.Uri.ToString();
    }

    /// <summary>The broker URI through the relay, with the disposable broker's credentials and virtual host.</summary>
    public string Uri { get; }

    /// <summary>A relay that is down until <see cref="Up"/>: connecting to it is refused.</summary>
    public static BrokerProxy Create() => new(new(Environment.GetEnvironmentVariable("Vessel_TEST_EVENT_RABBIT")!));

    public void Up()
    {
        lock (connections)
        {
            if (!down) return;
            down = false;
        }
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        accepting = new();
        _ = AcceptAsync(listener, accepting.Token);
    }

    public void Down()
    {
        TcpClient[] open;
        lock (connections)
        {
            if (down) return;
            down = true;
            open = connections.ToArray();
            connections.Clear();
        }
        accepting?.Cancel();
        listener?.Stop();
        foreach (var connection in open) Reset(connection);
    }

    private async Task AcceptAsync(TcpListener server, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await server.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = RelayAsync(client);
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        var upstream = new TcpClient();
        try { await upstream.ConnectAsync(targetHost, targetPort); }
        catch
        {
            Reset(client);
            upstream.Dispose();
            return;
        }
        lock (connections)
        {
            if (down)
            {
                Reset(client);
                Reset(upstream);
                return;
            }
            connections.Add(client);
            connections.Add(upstream);
        }
        try
        {
            var a = client.GetStream();
            var b = upstream.GetStream();
            await Task.WhenAny(a.CopyToAsync(b), b.CopyToAsync(a));
        }
        catch { }
        finally
        {
            lock (connections)
            {
                connections.Remove(client);
                connections.Remove(upstream);
            }
            Reset(client);
            Reset(upstream);
        }
    }

    // An abrupt reset, as a broker host or network failure would look to the client.
    private static void Reset(TcpClient connection)
    {
        try { connection.Client.LingerState = new(true, 0); } catch { }
        connection.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Down();
        accepting?.Dispose();
        return ValueTask.CompletedTask;
    }
}
