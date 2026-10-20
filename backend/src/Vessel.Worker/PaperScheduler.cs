using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vessel.Application.MarketData;
using Vessel.Application.Monitoring;
using Vessel.Application.Ownership;
using Vessel.Application.Paper;
using Vessel.Application.Venues;

namespace Vessel.Worker;

public sealed class PaperWorkerOptions
{
    public const string Section = "Vessel:Paper";
    public int SchedulerTickSeconds { get; set; } = 30;
    public int MaxCandlesPerRead { get; set; } = 1440;
    public int MaxPlaysPerTick { get; set; } = 200;
    public string HeartbeatFile { get; set; } = "/tmp/vessel-paper-heartbeat";
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>The same server-wide read ceiling as real monitoring, never a separate paper budget.</summary>
    public int VenueReadConcurrency { get; private set; } = 4;

    public void Validate()
    {
        if (SchedulerTickSeconds is < 5 or > 300 || MaxCandlesPerRead is < 1 or > 4000 ||
            MaxPlaysPerTick is < 1 or > 1000 || VenueReadConcurrency is < 1 or > 64 || string.IsNullOrWhiteSpace(HeartbeatFile) ||
            HeartbeatTimeout < TimeSpan.FromSeconds(SchedulerTickSeconds * 2) || HeartbeatTimeout > TimeSpan.FromMinutes(15))
            throw new InvalidOperationException("Paper worker scheduling or heartbeat options are outside the supported bounds.");
    }

    public static PaperWorkerOptions From(IConfiguration config)
    {
        var value = new PaperWorkerOptions();
        config.GetSection(Section).Bind(value);
        value.VenueReadConcurrency = config.GetValue<int?>("Vessel:Monitor:VenueReadConcurrency") ?? 4;
        value.Validate();
        return value;
    }
}

/// <summary>
/// A broker-free, opt-in worker in the existing binary. Public candles are shared per instrument; decisions and
/// ledger/checkpoint/lifecycle mutations use owner scopes and the same account transaction as owner commands.
/// </summary>
public sealed class PaperScheduler(IOwnerScopeFactory scopes, IJournalOwnerDirectory owners, NpgsqlDataSource data,
    IServerStateStore server, PaperWorkerOptions options, TimeProvider time, ILogger<PaperScheduler> logger) : BackgroundService
{
    private Guid? afterOwner;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
                await WriteHeartbeatAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                logger.LogWarning("Paper scheduling failed with {ErrorType}; no healthy heartbeat was written.", e.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(options.SchedulerTickSeconds), time, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        await using var connection = await data.OpenConnectionAsync(ct);
        // Verify explicit migration even when the owner directory has no active owners.
        await using (var schema = new NpgsqlCommand("SELECT 1 FROM paper_settings LIMIT 0", connection))
            await schema.ExecuteScalarAsync(ct);
        // ponytail: one bounded paper scan per server, rather than a lease renewal protocol. Account locks still
        // fence every write; replace this with sharded scan leases only if one scan becomes a throughput limit.
        await using var claim = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended('vessel-paper-scheduler',0))", connection);
        if (await claim.ExecuteScalarAsync(ct) is not true) return;
        try
        {
            var now = time.GetUtcNow();
            var accounts = new List<PaperAccountCandidate>();
            var seen = new HashSet<(Guid Owner, Guid Account)>();
            var playBudget = 0;
            // The budget is soft between accounts. Keyset reads expand EVERY sibling of an admitted account;
            // neither the discovery page nor this tick's remaining budget can split a shared capital timeline.
            var page = await owners.ActiveOwnersAsync(afterOwner, 100, ct);
            if (page.Count == 0)
            {
                afterOwner = null;
                page = await owners.ActiveOwnersAsync(null, 100, ct);
            }
            foreach (var owner in page)
            {
                afterOwner = owner;
                if (await owners.StatusAsync(owner, ct) != JournalOwnerStatus.Active) continue;
                await using var scope = scopes.Create(owner);
                var candidates = await scope.Services.GetRequiredService<IPaperStore>()
                    .DueAsync(now, Math.Max(1, options.MaxPlaysPerTick - playBudget), ct);
                foreach (var candidate in candidates)
                {
                    if (!seen.Add((owner, candidate.AccountId))) continue;
                    var account = await scope.Services.GetRequiredService<PaperTradingService>().PrepareAccountAsync(candidate.AccountId, ct);
                    if (account.Plays.Count == 0) continue;
                    accounts.Add(account);
                    playBudget += account.Plays.Count;
                    if (playBudget >= options.MaxPlaysPerTick) break;
                }
                if (playBudget >= options.MaxPlaysPerTick) break;
            }
            if (accounts.Count == 0 || await server.VenueRetryAtAsync("hyperliquid", ct) is { } retry && retry > now) return;
            var windows = new Dictionary<string, (IReadOnlyList<PaperCandle> Candles, DateTimeOffset Through)>(StringComparer.Ordinal);
            var failedAccounts = new HashSet<Guid>();
            // Fetch public windows first. Applying an instrument group here would spend that group's LATER P&L
            // before another instrument's EARLIER entry on the same account.
            foreach (var group in accounts.SelectMany(a => a.Plays).GroupBy(x => x.ContractId, StringComparer.Ordinal))
            {
                var from = group.Min(x => x.FromUtc);
                var through = new[] { PaperTradingService.FloorMinute(now), from.AddMinutes(options.MaxCandlesPerRead) }.Min();
                if (through <= from) continue;
                await using var readScope = scopes.Create(group.First().OwnerId);
                var reader = readScope.Services.GetRequiredService<IVenueRegistry>().Candles("hyperliquid")
                    ?? throw new InvalidOperationException("Paper public candle reader is unavailable.");
                try
                {
                    var raw = await ReadWithinVenueCapacityAsync(connection, reader, group.Key, from, through, ct);
                    if (raw is null) return;
                    if (raw.Count > options.MaxCandlesPerRead + 1)
                        throw new VenueReadException("Paper candle read exceeded its bound.");
                    var parsed = raw.Where(x => x.OpenTime >= from.ToUnixTimeMilliseconds() && x.OpenTime < through.ToUnixTimeMilliseconds())
                        .Select(ParseCandle).OrderBy(x => x.OpenAtUtc).ToArray();
                    windows.Add(group.Key, (parsed, through));
                }
                catch (Exception error) when (error is VenueReadException or HttpRequestException or FormatException or OverflowException ||
                    error is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    await server.DeferVenueAsync("hyperliquid", error is VenueRateLimitException limited
                        ? limited.RetryAtUtc : now.AddMinutes(1), now, ct);
                    foreach (var account in accounts.Where(a => a.Plays.Any(p => p.ContractId == group.Key)))
                    {
                        failedAccounts.Add(account.AccountId);
                        await using var scope = scopes.Create(account.OwnerId);
                        await scope.Services.GetRequiredService<PaperTradingService>().RecordAccountReadFailureAsync(account, ct);
                    }
                    logger.LogWarning("Paper public candle read failed with {ErrorType}; account checkpoints were retained.", error.GetType().Name);
                    break;
                }
            }
            var shared = windows.ToDictionary(x => x.Key, x => x.Value.Candles, StringComparer.Ordinal);
            foreach (var account in accounts)
            {
                if (failedAccounts.Contains(account.AccountId) || await owners.StatusAsync(account.OwnerId, ct) != JournalOwnerStatus.Active) continue;
                var from = account.Plays.Min(p => p.FromUtc);
                var through = new[] { PaperTradingService.FloorMinute(now), from.AddMinutes(options.MaxCandlesPerRead) }.Min();
                foreach (var play in account.Plays.Where(p => p.FromUtc < through))
                {
                    if (!windows.TryGetValue(play.ContractId, out var window)) { through = from; break; }
                    if (window.Through < through) through = window.Through;
                }
                if (through <= from) continue;
                await using var scope = scopes.Create(account.OwnerId);
                await scope.Services.GetRequiredService<PaperTradingService>().EvaluateAccountAsync(account, shared, through, ct);
            }
        }
        finally
        {
            // Explicit unlock before returning the physical connection to the pool, including cancellation.
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended('vessel-paper-scheduler',0))", connection);
            await release.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    private async Task<IReadOnlyList<VenueCandle>?> ReadWithinVenueCapacityAsync(NpgsqlConnection connection, ICandleReader reader,
        string contractId, DateTimeOffset from, DateTimeOffset through, CancellationToken ct)
    {
        // Monitor admission takes this same advisory key until its lease commits. Holding it over the one bounded
        // public read prevents a new real claim using this slot; existing running leases are counted first.
        await using var claim = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended('vessel-venue-budget:hyperliquid',0))", connection);
        if (await claim.ExecuteScalarAsync(ct) is not true) return null;
        try
        {
            await using var capacity = new NpgsqlCommand("""
                SELECT COUNT(*) FROM monitor_accounts m JOIN accounts a
                  ON a."Id"=m."AccountId" AND a."OwnerId"=m."OwnerId"
                WHERE a."VenueId"='hyperliquid' AND a."TradingMode"='real'
                  AND m."ManualState"='running' AND m."LeaseUntilUtc">@now
                """, connection);
            capacity.Parameters.AddWithValue("now", time.GetUtcNow());
            if ((long)(await capacity.ExecuteScalarAsync(ct))! >= options.VenueReadConcurrency) return null;
            return await reader.ReadCandlesAsync(contractId, "1m", from.ToUnixTimeMilliseconds(), through.ToUnixTimeMilliseconds() - 1, ct);
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended('vessel-venue-budget:hyperliquid',0))", connection);
            await release.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    public static PaperCandle ParseCandle(VenueCandle candle)
    {
        var open = DateTimeOffset.FromUnixTimeMilliseconds(candle.OpenTime);
        var close = open.AddMinutes(1);
        // Hyperliquid reports the inclusive millisecond end. Accept the exclusive boundary too, but nothing else.
        if (candle.CloseTime != close.ToUnixTimeMilliseconds() - 1 && candle.CloseTime != close.ToUnixTimeMilliseconds())
            throw new FormatException("A closed one-minute candle is required.");
        decimal Number(string text) => decimal.Parse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return new(open, close, Number(candle.Open), Number(candle.High), Number(candle.Low), Number(candle.Close));
    }

    private async Task WriteHeartbeatAsync(CancellationToken ct)
    {
        var value = $"{time.GetUtcNow().ToUnixTimeSeconds()} {(long)options.HeartbeatTimeout.TotalSeconds}";
        var temporary = options.HeartbeatFile + "." + Environment.ProcessId + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, value, ct);
            File.Move(temporary, options.HeartbeatFile, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
