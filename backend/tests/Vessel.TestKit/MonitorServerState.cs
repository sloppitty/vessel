using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Vessel.Application.MarketData;
using Vessel.Application.Monitoring;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>Real independent server-state connections, without retaining pools after a disposable schema is dropped.</summary>
internal sealed class MonitorServerState(string? connection = null) : IServerStateStore
{
    private readonly Dictionary<string, DateTimeOffset> deadlines = [];
    private ServerMonitorHeartbeat? heartbeat;
    public static IServerStateStore For(VesselDbContext db) => new MonitorServerState(db.GetService<IDbContextOptions>().Extensions.OfType<RelationalOptionsExtension>().Single().ConnectionString!);
    public static AccountReadPolicy Reads(VesselDbContext? db = null, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        return new(db is null ? new MonitorServerState() : For(db), new UserReadLimits(time), time);
    }

    private async Task<T> Use<T>(Func<IServerStateStore, Task<T>> action)
    {
        await using var data = NpgsqlDataSource.Create(connection!);
        return await action(new ServerStateStore(data, new(["monitor"])));
    }

    public Task<DateTimeOffset?> VenueRetryAtAsync(string venueId, CancellationToken ct) => connection is not null
        ? Use(s => s.VenueRetryAtAsync(venueId, ct)) : Task.FromResult(deadlines.TryGetValue(venueId, out var at) ? (DateTimeOffset?)at : null);
    public Task<IReadOnlyDictionary<string, DateTimeOffset>> VenueRetriesAsync(CancellationToken ct) => connection is not null
        ? Use(s => s.VenueRetriesAsync(ct)) : Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(deadlines);
    public async Task DeferVenueAsync(string venueId, DateTimeOffset retryAtUtc, DateTimeOffset now, CancellationToken ct)
    {
        if (connection is not null) await Use(async s => { await s.DeferVenueAsync(venueId, retryAtUtc, now, ct); return true; });
        else deadlines[venueId] = Vessel.Application.Venues.VenueRateLimitException.Clamp(retryAtUtc, now);
    }
    public Task<ServerMonitorHeartbeat?> HeartbeatAsync(CancellationToken ct) => connection is not null
        ? Use(s => s.HeartbeatAsync(ct)) : Task.FromResult(heartbeat);
    public async Task RecordHeartbeatAsync(string deployment, DateTimeOffset now, CancellationToken ct)
    {
        if (connection is not null) await Use(async s => { await s.RecordHeartbeatAsync(deployment, now, ct); return true; });
        else heartbeat = new(deployment, now);
    }
}
