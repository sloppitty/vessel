using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Application.Workspace;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class CorePostgresTests
{
    private static WorkspaceService Service(VesselDbContext db, Guid owner, FixtureReader reader) => new(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TestVenues.With(reader), TestUnitOfWork.For(db));
    /// <summary>The owner's account Refresh through the monitor path, as POST /api/accounts/{id}/sync runs it.</summary>
    private static Task<AccountDto> Refresh(VesselDbContext db, Guid owner, FixtureReader reader, Guid id) =>
        MonitorTestPaths.RefreshAsync(db, owner, TestVenues.With(reader), id);
    private static async Task<Guid> CreateVenueAccount(WorkspaceService service)
    {
        var portfolio = await service.CreatePortfolioAsync(new("Trading"), default);
        return (await service.CreateAccountAsync(new(portfolio.Id, "Hyperliquid", "hyperliquid", "0x" + Guid.NewGuid().ToString("N") + "00000000"), default)).Id;
    }

    [PostgresFact]
    public async Task Available_margin_migration_and_refresh_preserve_exact_nullable_owner_scoped_values()
    {
        // Upgrade real preceding schema; the migration must leave old snapshots unknown.
        await using var database = await CoreDatabase.CreateAsync("20261010210015_PortfolioIcons");
        var owner = Guid.NewGuid(); var id = Guid.NewGuid();
        await using (var db = database.Context(owner))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO accounts (\"Id\", \"OwnerId\", \"VenueId\", \"Name\", \"SyncStatus\", \"IsEnabled\", \"SettingsRevision\") VALUES ({id}, {owner}, 'manual', 'Legacy', 'manual', true, 1)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO account_snapshots (\"OwnerId\", \"AccountId\", \"ObservedAtUtc\", \"ValueScope\") VALUES ({owner}, {id}, {DateTimeOffset.UtcNow}, 'legacy')");
            await db.Database.MigrateAsync();
            Assert.Null((await db.Snapshots.SingleAsync()).AvailableMarginUsd);
            Assert.False(db.Database.HasPendingModelChanges());
        }
        var reader = new FixtureReader(); Guid synced;
        const decimal exact = -0.1234567890123456789012345678m;
        reader.Result = reader.Result with { Snapshot = reader.Result.Snapshot with { AvailableMarginUsd = exact } };
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner, reader); synced = await CreateVenueAccount(service);
            var account = await Refresh(db, owner, reader, synced);
            Assert.Equal("-0.1234567890123456789012345678", account.AvailableBalanceUsd);
            Assert.Equal("margin", account.AvailableBalanceSource);
        }
        await using (var db = database.Context(Guid.NewGuid())) Assert.Empty(await db.Snapshots.ToListAsync());
        await using (var db = database.Context(owner))
        {
            Assert.Equal(exact, (await db.Snapshots.SingleAsync(s => s.AccountId == synced)).AvailableMarginUsd);
            Assert.Equal("-0.1234567890123456789012345678", (await Service(db, owner, reader).SnapshotAsync(synced, default))!.AvailableMarginUsd);
            reader.Fail = true;
            await Assert.ThrowsAsync<ProblemException>(() => Refresh(db, owner, reader, synced));
            Assert.Equal(exact, (await db.Snapshots.SingleAsync(s => s.AccountId == synced)).AvailableMarginUsd);
        }
        reader.Fail = false;
        foreach (var amount in new decimal?[] { 0m, null })
        {
            reader.Result = reader.Result with { Snapshot = reader.Result.Snapshot with { AvailableMarginUsd = amount, ObservedAtUtc = reader.Result.Snapshot.ObservedAtUtc.AddHours(1) } };
            await using var db = database.Context(owner);
            await Refresh(db, owner, reader, synced);
            db.ChangeTracker.Clear();
            Assert.Equal(amount, (await db.Snapshots.SingleAsync(s => s.AccountId == synced)).AvailableMarginUsd);
        }
    }

    [PostgresFact]
    public async Task Migration_upgrades_actual_foundation_heads_without_losing_concurrent_plays()
    {
        await using var database = await CoreDatabase.CreateAsync("20260930201658_InitialAccountPlayHeads");
        var owner = Guid.NewGuid(); var accountId = Guid.NewGuid();
        await using var db = database.Context(owner);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO accounts (\"Id\", \"OwnerId\", \"VenueId\", \"Name\") VALUES ({accountId}, {owner}, 'manual', 'Legacy')");
        var playA = Guid.NewGuid(); var playB = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO plays (\"Id\", \"OwnerId\", \"AccountId\", \"VenueId\", \"ContractId\", \"Status\") VALUES ({playA}, {owner}, {accountId}, 'manual', 'BTC', 'Active'), ({playB}, {owner}, {accountId}, 'manual', 'BTC', 'Active')");
        await db.Database.MigrateAsync();
        var account = await db.Accounts.SingleAsync();
        Assert.Null(account.PortfolioId); Assert.Null(account.ManualAccountValueUsd); Assert.Equal("manual", account.SyncStatus);
        Assert.Equal(2, await db.Plays.CountAsync()); Assert.All(await db.Plays.ToListAsync(), p => Assert.Equal(PlayStatus.Planned, p.Status));
        Assert.Empty(await db.Snapshots.ToListAsync()); Assert.False(db.Database.HasPendingModelChanges());
        var overview = await Service(db, owner, new FixtureReader()).OverviewAsync(default);
        Assert.Null(overview.Totals.TotalAccountValueUsd); Assert.Empty(overview.Portfolios); Assert.Single(overview.Accounts);
    }

    [PostgresFact]
    public async Task Refresh_persists_exact_facts_replaces_positions_and_preserves_prior_snapshot_on_provider_failure()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var reader = new FixtureReader(); Guid id;
        await using (var db = database.Context(owner)) id = await CreateVenueAccount(Service(db, owner, reader));
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner, reader); var dto = await Refresh(db, owner, reader, id);
            Assert.Equal("1234.1234567890123456789012345", dto.AccountValueUsd); Assert.Equal("synced", dto.SyncStatus);
            var snapshot = (await service.SnapshotAsync(id, default))!;
            Assert.Equal("-0.0123456789012345678901234567", snapshot.Positions.Single().SignedQuantity);
            var fill = (await service.FillsAsync(id, default)).Single(); Assert.Null(fill.PlayId);
            Assert.Equal("0.0000000000000000000000000001", fill.Quantity); Assert.Equal("USDC", fill.FeeToken);
        }
        reader.Result = reader.Result with { Snapshot = reader.Result.Snapshot with { ObservedAtUtc = reader.Result.Snapshot.ObservedAtUtc.AddHours(1), AccountValueUsd = 7.000000000000000000000000001m, Positions = [] } };
        await using (var db = database.Context(owner))
        {
            await Refresh(db, owner, reader, id);
            Assert.Empty(await db.Positions.ToListAsync()); Assert.Single(await db.Fills.ToListAsync());
        }
        reader.Fail = true;
        await using (var db = database.Context(owner))
        {
            var error = await Assert.ThrowsAsync<ProblemException>(() => Refresh(db, owner, reader, id)); Assert.Equal(502, error.StatusCode);
            Assert.DoesNotContain("secret", error.Message);
        }
        await using (var db = database.Context(owner))
        {
            var account = await db.Accounts.SingleAsync(); var snapshot = await db.Snapshots.SingleAsync();
            Assert.Equal("error", account.SyncStatus); Assert.DoesNotContain("secret", account.LastSyncError!);
            Assert.Equal(7.000000000000000000000000001m, snapshot.AccountValueUsd); Assert.Single(await db.Fills.ToListAsync());
        }
    }

    [PostgresFact]
    public async Task Concurrent_syncs_deduplicate_per_account_contract_and_source_id()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var reader = new FixtureReader(); Guid id; Guid second;
        // Duplicate within one response; same source ID on another contract is a distinct fact.
        var fill = reader.Result.Fills.Single();
        reader.Result = reader.Result with { Fills = [fill, fill, fill with { ContractId = "ETH" }] };
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner, reader); id = await CreateVenueAccount(service); second = await CreateVenueAccount(service);
            var account = (await db.Accounts.SingleAsync(a => a.Id == id));
            db.Plays.AddRange(TestPlays.Create(account, new PerpetualInstrument("hyperliquid", "BTC")), TestPlays.Create(account, new PerpetualInstrument("hyperliquid", "BTC")));
            await db.SaveChangesAsync();
        }
        // Concurrent refreshes of one account are admitted one at a time; the others are told to retry (429).
        var refreshed = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var db = database.Context(owner);
            try { await Refresh(db, owner, reader, id); return true; }
            catch (Vessel.Application.Monitoring.MonitorAdmissionException) { return false; }
        }));
        Assert.Contains(true, refreshed);
        // A repeated refresh of the same response imports nothing twice.
        await using (var db = database.Context(owner)) await Refresh(db, owner, reader, id);
        await using (var db = database.Context(owner))
        {
            await Refresh(db, owner, reader, second);
            Assert.Equal(4, await db.Fills.CountAsync()); Assert.Equal(2, await db.Snapshots.CountAsync()); Assert.Equal(2, await db.Plays.CountAsync());
            Assert.Equal(2, await db.Fills.CountAsync(f => f.AccountId == id));
            Assert.All(await db.Plays.ToListAsync(), p => Assert.Equal(PlayStatus.Draft, p.Status));
        }
    }

    [PostgresFact]
    public async Task Owner_composite_constraints_and_query_filters_cover_every_core_entity()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var foreign = Guid.NewGuid(); var reader = new FixtureReader(); Guid id; Guid portfolioId;
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner, reader); id = await CreateVenueAccount(service); await Refresh(db, owner, reader, id);
            portfolioId = (await db.Portfolios.SingleAsync()).Id;
        }
        await using (var db = database.Context(foreign))
        {
            Assert.Empty(await db.Portfolios.ToListAsync()); Assert.Empty(await db.Accounts.ToListAsync()); Assert.Empty(await db.Snapshots.ToListAsync());
            Assert.Empty(await db.Positions.ToListAsync()); Assert.Empty(await db.Fills.ToListAsync());
            var service = Service(db, foreign, reader);
            Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.CreateAccountAsync(new(portfolioId, "Foreign", "manual"), default))).StatusCode);
            Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => Refresh(db, foreign, reader, id))).StatusCode);
            var ownedBySecondOwner = await CreateVenueAccount(service);
            await Refresh(db, foreign, reader, ownedBySecondOwner);
            Assert.Single(await db.Portfolios.ToListAsync()); Assert.Single(await db.Accounts.ToListAsync());
            Assert.Single(await db.Snapshots.ToListAsync()); Assert.Single(await db.Positions.ToListAsync());
            Assert.Single(await db.Fills.ToListAsync());
            var foreignId = Guid.NewGuid();
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO accounts (\"Id\", \"OwnerId\", \"PortfolioId\", \"Name\", \"VenueId\", \"SyncStatus\") VALUES ({foreignId}, {foreign}, {portfolioId}, 'Foreign', 'manual', 'manual')"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
            var snapshotError = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO account_snapshots (\"OwnerId\", \"AccountId\", \"ObservedAtUtc\", \"ValueScope\") VALUES ({foreign}, {id}, now(), 'foreign')"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, snapshotError.SqlState);
            var loaded = await db.Fills.IgnoreQueryFilters().FirstAsync(f => f.OwnerId == owner); loaded.Quantity = 999;
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
        }
    }

    [PostgresFact]
    public async Task Real_api_refresh_uses_fixture_reader_and_returns_safe_failure_and_foreign_404()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var reader = new FixtureReader();
        var clock = new RefreshClock(reader.Result.Snapshot.ObservedAtUtc);
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString, reader: reader,
            configure: services => services.AddSingleton<TimeProvider>(clock)); using var client = factory.AuthorizedClient();
        var portfolioResponse = await client.PostAsJsonAsync("/api/portfolios", new CreatePortfolioRequest("API")); Assert.Equal(HttpStatusCode.Created, portfolioResponse.StatusCode);
        var portfolio = (await portfolioResponse.Content.ReadFromJsonAsync<PortfolioDto>())!;
        var accountResponse = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest(portfolio.Id, "Venue", "hyperliquid", "0x1111111111111111111111111111111111111111"));
        Assert.Equal(HttpStatusCode.Created, accountResponse.StatusCode); var account = (await accountResponse.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.Equal("null", await client.GetStringAsync($"/api/accounts/{account.Id}/snapshot"));
        var response = await client.PostAsync($"/api/accounts/{account.Id}/sync", null); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, reader.Reads);
        var fills = (await client.GetFromJsonAsync<FillDto[]>($"/api/accounts/{account.Id}/fills"))!; Assert.Single(fills); Assert.Null(fills[0].PlayId);
        var body = await client.GetStringAsync("/api/overview"); Assert.DoesNotContain("secret", body); Assert.DoesNotContain("ownerId", body); Assert.DoesNotContain("rawJson", body);
        reader.Fail = true;
        var cooling = await client.PostAsync($"/api/accounts/{account.Id}/sync", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, cooling.StatusCode); Assert.Equal(1, reader.Reads);
        Assert.Equal(TimeSpan.FromSeconds(60), cooling.Headers.RetryAfter?.Delta);
        clock.Now += TimeSpan.FromMinutes(1);
        var failure = await client.PostAsync($"/api/accounts/{account.Id}/sync", null); Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode);
        Assert.Equal(2, reader.Reads);
        Assert.DoesNotContain("secret", await failure.Content.ReadAsStringAsync());
        Assert.NotNull(await client.GetFromJsonAsync<SnapshotDto>($"/api/accounts/{account.Id}/snapshot"));
        await using var foreignFactory = new CoreApiFactory(Guid.NewGuid(), connection: database.ConnectionString, reader: reader); using var foreignClient = foreignFactory.AuthorizedClient();
        Assert.Equal(HttpStatusCode.NotFound, (await foreignClient.GetAsync($"/api/accounts/{account.Id}/snapshot")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreignClient.GetAsync($"/api/accounts/{account.Id}/fills")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreignClient.PostAsync($"/api/accounts/{account.Id}/sync", null)).StatusCode);
        Assert.Empty((await foreignClient.GetFromJsonAsync<OverviewDto>("/api/overview"))!.Accounts);
    }

    private sealed class RefreshClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [PostgresFact]
    public async Task Recent_fills_are_bounded_ordered_and_not_linked_to_plays()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var reader = new FixtureReader();
        var example = reader.Result.Fills.Single();
        reader.Result = reader.Result with
        {
            Fills = Enumerable.Range(0, 125).Select(i => example with
            {
                SourceFillId = i.ToString(),
                OccurredAtUtc = example.OccurredAtUtc.AddSeconds(i)
            }).ToList()
        };
        await using var db = database.Context(owner); var service = Service(db, owner, reader);
        var id = await CreateVenueAccount(service); await Refresh(db, owner, reader, id);
        var fills = await service.FillsAsync(id, default);
        Assert.Equal(100, fills.Count); Assert.Equal("124", fills[0].SourceFillId); Assert.Equal("25", fills[^1].SourceFillId);
        Assert.All(fills, f => Assert.Null(f.PlayId));
        var overview = await service.OverviewAsync(default);
        Assert.Equal(125, overview.Totals.ImportedFillCount); Assert.Equal(100, overview.RecentActivity.Count);
        Assert.Empty(await db.Plays.ToListAsync());
    }

    [PostgresFact]
    public async Task Persistence_failure_rolls_back_snapshot_replacement_and_fill_insert()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var reader = new FixtureReader(); Guid id;
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner, reader); id = await CreateVenueAccount(service); await Refresh(db, owner, reader, id);
        }
        // Fault injection after the old positions are deleted in the transaction.
        // Actual adapter outputs satisfy schema bounds; persistence must still remain atomic.
        reader.Result = reader.Result with { Snapshot = reader.Result.Snapshot with { ObservedAtUtc = reader.Result.Snapshot.ObservedAtUtc.AddHours(1), AccountValueUsd = 999, Positions = [] }, HistoryNotice = new string('x', 1001) };
        // The refresh reports a safe failure; the persistence error never reaches the owner.
        await using (var db = database.Context(owner))
            Assert.Equal(502, (await Assert.ThrowsAsync<ProblemException>(() => Refresh(db, owner, reader, id))).StatusCode);
        await using (var db = database.Context(owner))
        {
            Assert.Equal(1234.1234567890123456789012345m, (await db.Snapshots.SingleAsync()).AccountValueUsd);
            Assert.Single(await db.Positions.ToListAsync()); Assert.Single(await db.Fills.ToListAsync());
            var account = await db.Accounts.SingleAsync();
            Assert.Equal(("error", "The venue refresh failed. Try again later."), (account.SyncStatus, account.LastSyncError));
            Assert.Equal(reader.Result.Snapshot.ObservedAtUtc.AddHours(-1), account.LastSyncedAtUtc);
        }
    }

    [PostgresFact]
    public async Task Portfolio_coverage_and_nullable_snapshot_balances_roundtrip_in_PostgreSQL()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); await using var db = database.Context(owner);
        var service = Service(db, owner, new FixtureReader());
        var first = await service.CreatePortfolioAsync(new("First"), default); var second = await service.CreatePortfolioAsync(new("Second"), default);
        await service.CreateAccountAsync(new(first.Id, "Known", "manual", ManualAccountValueUsd: "12.00000000000000000000000001"), default);
        await service.CreateAccountAsync(new(first.Id, "Unknown", "manual"), default);
        var venue = await service.CreateAccountAsync(new(second.Id, "Venue", "hyperliquid", "0x1111111111111111111111111111111111111111"), default);
        db.Snapshots.Add(new AccountSnapshot { OwnerId = owner, AccountId = venue.Id, ValueScope = "partial", ObservedAtUtc = DateTimeOffset.UtcNow, AccountValueUsd = null, WithdrawableUsd = null, MarginUsedUsd = 0.0000000000000000000000000001m });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var overview = await service.OverviewAsync(default); Assert.Equal("partial", overview.Portfolios.Single(p => p.Id == first.Id).ValueCoverage);
        Assert.Equal("unavailable", overview.Portfolios.Single(p => p.Id == second.Id).ValueCoverage);
        Assert.Equal(1, overview.Totals.ValuedAccountCount); Assert.Equal("12.00000000000000000000000001", overview.Totals.TotalAccountValueUsd);
        var snapshot = (await service.SnapshotAsync(venue.Id, default))!; Assert.Null(snapshot.AccountValueUsd); Assert.Null(snapshot.WithdrawableUsd);
        Assert.Equal("0.0000000000000000000000000001", snapshot.MarginUsedUsd);
    }
}
