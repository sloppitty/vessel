using System.Net;
using System.Net.Sockets;

namespace Vessel.Tests;

/// <summary>
/// A loopback TCP relay in front of the disposable broker, so a test can make RabbitMQ unreachable and reachable again
/// without touching the broker itself. <see cref="Down"/> stops accepting (new connections are refused) and resets
/// every relayed connection; <see cref="Up"/> accepts again on the same port.
/// </summary>
/// <remarks>
/// With <c>Vessel_TEST_BROKER_SWITCH</c> set to a directory shared with the machine running the broker, the relay stays
/// transparent and each switch asks that machine to stop or start the real broker instead: it writes <c>down</c> or
/// <c>up</c> to <c>want</c> in that directory and waits until the broker's port answers accordingly.
/// </remarks>
internal sealed class BrokerProxy : IAsyncDisposable
{
    private readonly string targetHost;
    private readonly int targetPort;
    private readonly int port;
    private readonly string? external = Environment.GetEnvironmentVariable("Vessel_TEST_BROKER_SWITCH");
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

    public async Task UpAsync()
    {
        if (external is not null) await SwitchExternalAsync("up", reachable: true);
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

    public async Task DownAsync()
    {
        if (external is not null)
        {
            // The real broker closes its own connections; the relay keeps passing whatever it says.
            await SwitchExternalAsync("down", reachable: false);
            return;
        }
        Close();
    }

    private void Close()
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

    private async Task SwitchExternalAsync(string want, bool reachable)
    {
        await File.WriteAllTextAsync(Path.Combine(external!, "want"), want);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        while (await ReachableAsync() != reachable)
            await Task.Delay(250, timeout.Token);
    }

    private async Task<bool> ReachableAsync()
    {
        try
        {
            using var probe = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await probe.ConnectAsync(targetHost, targetPort, timeout.Token);
            return true;
        }
        catch { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (external is not null) await SwitchExternalAsync("up", reachable: true);
        Close();
        accepting?.Dispose();
    }
}
