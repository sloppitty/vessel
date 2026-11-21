using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vessel.Messaging.Modules;
using Vessel.Persistence.Events;

namespace Vessel.Messaging;

/// <summary>
/// One dedicated PostgreSQL connection per host that <c>LISTEN</c>s on the channels of the subscriptions and commands
/// it runs, and hands each payload to that channel's handler, one at a time per channel. Notifications are hints:
/// if this connection is lost, the listener reconnects with backoff (1 s doubling to 30 s), and anything notified
/// meanwhile is found by catch-up (events) or the scheduler tick (monitor requests). It never starts a transaction.
/// </summary>
public sealed class PostgresNotificationListener(MessagingHost host, IConfiguration configuration, IServiceProvider services,
    ILogger<PostgresNotificationListener> logger) : BackgroundService
{
    internal static readonly TimeSpan Ping = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan LastRetry = TimeSpan.FromSeconds(30);
    private const int Capacity = 10_000;
    private int connections;
    private int processId;

    /// <summary>How many times this listener has subscribed; a reconnect increments it.</summary>
    public int Connections => Volatile.Read(ref connections);

    /// <summary>The PostgreSQL backend of the current listening connection, or 0.</summary>
    public int ProcessId => Volatile.Read(ref processId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var routes = Routes();
        if (routes.Count == 0) return;
        var consumers = routes.Select(route => ConsumeAsync(route, stoppingToken)).ToArray();
        var delay = FirstRetry;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(routes, () => delay = FirstRetry, stoppingToken);
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                // Only the type: driver messages can contain the host name.
                logger.LogWarning("The notification listener lost its PostgreSQL connection ({ErrorType}); reconnecting in {Delay}. " +
                    "Catch-up and the scheduler cover anything notified meanwhile.", error.GetType().Name, delay);
            }
            finally
            {
                Volatile.Write(ref processId, 0);
            }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, LastRetry.Ticks));
        }
        foreach (var route in routes) route.Queue.Writer.TryComplete();
        await Task.WhenAll(consumers);
    }

    private async Task ListenAsync(IReadOnlyList<Route> routes, Action connected, CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("Vessel")
            ?? throw new InvalidOperationException("The event database is not configured.");
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            // Its own physical connection: LISTEN is session state and must never return to a pool.
            Pooling = false,
            ApplicationName = "vessel-listen-" + host.Deployment
        };
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync(ct);
        var byChannel = new Dictionary<string, Route>(StringComparer.Ordinal);
        // Attached first: a notification can be read while a later LISTEN runs.
        connection.Notification += (_, notification) =>
        {
            if (byChannel.TryGetValue(notification.Channel, out var route) && !route.Queue.Writer.TryWrite(notification.Payload))
                logger.LogWarning("The {Channel} notification queue is full; a wake-up was dropped and catch-up covers it.", route.Name);
        };
        foreach (var route in routes)
        {
            await using var resolve = new NpgsqlCommand($"SELECT {VesselNotifications.ChannelSql}", connection);
            resolve.Parameters.AddWithValue("channel", route.Name);
            var channel = (string)(await resolve.ExecuteScalarAsync(ct))!;
            byChannel[channel] = route;
            await using var listen = new NpgsqlCommand($"LISTEN \"{channel.Replace("\"", "\"\"", StringComparison.Ordinal)}\"", connection);
            await listen.ExecuteNonQueryAsync(ct);
        }
        Volatile.Write(ref processId, connection.ProcessID);
        Interlocked.Increment(ref connections);
        connected();
        while (true)
        {
            if (await connection.WaitAsync(Ping, ct)) continue;
            // Idle: prove the connection still answers, so a silently dropped one is replaced.
            await using var ping = new NpgsqlCommand("SELECT 1", connection);
            await ping.ExecuteScalarAsync(ct);
        }
    }

    private async Task ConsumeAsync(Route route, CancellationToken ct)
    {
        try
        {
            await foreach (var payload in route.Queue.Reader.ReadAllAsync(ct))
            {
                try { await route.Handler.HandleAsync(payload, ct); }
                catch (Exception error) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("A {Channel} notification failed with {ErrorType}; catch-up covers it.", route.Name, error.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private List<Route> Routes()
    {
        var routes = new List<Route>();
        if (host.RunningSubscriptions.Count != 0)
            routes.Add(new(VesselNotifications.Events, ActivatorUtilities.CreateInstance<EventSubscriptionListener>(services)));
        foreach (var command in host.ConsumedCommands)
            routes.Add(new(command.Channel, (INotificationHandler)ActivatorUtilities.CreateInstance(services, command.Handler!)));
        return routes;
    }

    private sealed record Route(string Name, INotificationHandler Handler)
    {
        public Channel<string> Queue { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
    }
}
