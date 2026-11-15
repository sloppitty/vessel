using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class HyperliquidBuilderContinuityIntegrationTests : HyperliquidAdapterTestsSupport
{
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_imports_builder_fills_only_with_metadata_and_never_proves_primary_only_continuity(bool metadataAvailable)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var (account, monitor) = await SeedAsync(db, owner);
        var watermark = monitor.NewestFillAtUtc;
        var gap = Gap(account);
        monitor.HasHistoryGap = true;
        db.Add(gap);
        await db.SaveChangesAsync();
        using var handler = new BuilderHistoryHandler(metadataAvailable);
        using var client = Client(handler);
        var clock = new TestClock();
        // Production explicitly enables builder markets in DependencyInjection.AddInfrastructure.
        var reader = new HyperliquidPerpetualReader(client, clock, builderMarkets: true);
        await MonitorTestPaths.RefreshAsync(db, owner, TestVenues.With(reader), account.Id, time: clock);

        db.ChangeTracker.Clear();
        var fills = await db.Fills.ToListAsync();
        Assert.Equal(metadataAvailable, fills.Any(f => f.ContractId == "BTC"));
        Assert.Equal(metadataAvailable ? 2 : 0, fills.Count);
        Assert.Equal(metadataAvailable, fills.Any(f => f.ContractId == "xyz:GOLD"));
        var coverage = await db.FillCoverage.SingleAsync();
        Assert.Equal(FillCoverageRecord.Refresh, coverage.Source);
        Assert.False(coverage.Complete);
        Assert.Null(coverage.GapFromUtc);
        Assert.Null(coverage.GapToUtc);
        if (metadataAvailable)
        {
            Assert.False(coverage.Truncated);
            Assert.Equal(watermark, coverage.CoveredFromUtc);
            Assert.Equal(clock.GetUtcNow(), coverage.CoveredToUtc);
        }
        else
        {
            Assert.Null(coverage.Truncated);
            Assert.Null(coverage.CoveredFromUtc);
            Assert.Null(coverage.CoveredToUtc);
        }
        monitor = await db.Set<MonitorAccount>().SingleAsync();
        Assert.Equal(metadataAvailable ? DateTimeOffset.FromUnixTimeMilliseconds(StateTime - 1000) : watermark, monitor.NewestFillAtUtc);
        Assert.True(monitor.HasHistoryGap);
        Assert.Null((await db.HistoryGaps.SingleAsync()).ClosedAtUtc);
    }

    [PostgresFact]
    public async Task Failed_builder_history_read_stores_no_page_or_coverage_and_leaves_the_gap_for_retry()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var (account, monitor) = await SeedAsync(db, owner);
        var gap = Gap(account);
        monitor.HasHistoryGap = true;
        db.Add(gap);
        await db.SaveChangesAsync();
        using var handler = new BuilderHistoryHandler(metadataAvailable: false);
        using var client = Client(handler);
        var clock = new TestClock();
        var reader = new HyperliquidPerpetualReader(client, clock, builderMarkets: true);
        var coordinator = MonitorTestPaths.Coordinator(db, owner, TestVenues.With(reader), time: clock);
        var started = await coordinator.StartBackfillAsync(account.Id, gap.Id, default);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        var lease = await store.ClaimBackfillAsync(account.Id, "history-worker", clock.GetUtcNow(), default);
        Assert.NotNull(lease);
        Assert.True(await coordinator.RunAsync(lease, default));

        db.ChangeTracker.Clear();
        Assert.Empty(await db.Fills.ToListAsync());
        Assert.Empty(await db.ExecutionOmissions.ToListAsync());
        Assert.Empty(await db.FillCoverage.ToListAsync());
        var backfill = await db.HistoryBackfills.SingleAsync();
        Assert.Equal(started.Id, backfill.Id);
        Assert.Equal((HistoryBackfill.Queued, 1, 0, 0, 0),
            (backfill.State, backfill.Failures, backfill.Pages, backfill.RawRows, backfill.AddedFills));
        Assert.Equal(gap.FromUtc, backfill.CursorUtc);
        Assert.Null(backfill.StartServed);
        Assert.True(backfill.NextPageAtUtc > clock.GetUtcNow());
        var storedGap = await db.HistoryGaps.SingleAsync();
        Assert.Equal((gap.FromUtc, gap.ToUtc), (storedGap.FromUtc, storedGap.ToUtc));
        Assert.Null(storedGap.ClosedAtUtc);
        Assert.Null(storedGap.ClosedByCoverageId);
        Assert.False(storedGap.BeyondVenueReach);
        Assert.True((await db.Set<MonitorAccount>().SingleAsync()).HasHistoryGap);
    }

    [PostgresFact]
    public async Task Primary_only_builder_fallback_degrades_a_check_even_without_a_stored_gap()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var (account, seededMonitor) = await SeedAsync(db, owner);
        var watermark = seededMonitor.NewestFillAtUtc;
        using var handler = new BuilderHistoryHandler(metadataAvailable: false);
        using var client = Client(handler);
        var clock = new TestClock();
        var reader = new HyperliquidPerpetualReader(client, clock, builderMarkets: true);
        var coordinator = MonitorTestPaths.Coordinator(db, owner, TestVenues.With(reader), time: clock);
        var request = await coordinator.RequestAsync(account.Id, default);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        var lease = await store.ClaimAsync(account.Id, request.RequestId, "check-worker", clock.GetUtcNow(), default);
        Assert.NotNull(lease);
        Assert.True(await coordinator.RunAsync(lease, default));

        db.ChangeTracker.Clear();
        var monitor = await db.Set<MonitorAccount>().SingleAsync();
        Assert.Equal("degraded", monitor.State);
        Assert.False(monitor.HasHistoryGap);
        Assert.Equal(watermark, monitor.NewestFillAtUtc);
        Assert.Empty(await db.Fills.ToListAsync());
        var coverage = await db.FillCoverage.SingleAsync();
        Assert.Equal(FillCoverageRecord.Check, coverage.Source);
        Assert.Null(coverage.Truncated);
        Assert.Null(coverage.CoveredFromUtc);
        Assert.Null(coverage.CoveredToUtc);
    }

    [PostgresFact]
    public async Task Unknown_refresh_cannot_hide_a_builder_gap_from_the_next_capped_refresh()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var (account, monitor) = await SeedAsync(db, owner);
        var watermark = monitor.NewestFillAtUtc;
        using var handler = new BuilderHistoryHandler(metadataAvailable: false);
        using var client = Client(handler);
        var clock = new TestClock();
        var reader = new HyperliquidPerpetualReader(client, clock, builderMarkets: true);
        var venues = TestVenues.With(reader);
        await MonitorTestPaths.RefreshAsync(db, owner, venues, account.Id, time: clock);
        Assert.Empty(await db.Fills.ToListAsync());

        handler.MetadataAvailable = true;
        handler.Capped = true;
        clock.Advance(TimeSpan.FromMinutes(1));
        await MonitorTestPaths.RefreshAsync(db, owner, venues, account.Id, time: clock);

        db.ChangeTracker.Clear();
        Assert.True((await db.Set<MonitorAccount>().SingleAsync()).HasHistoryGap);
        var coverage = await db.FillCoverage.SingleAsync(c => c.Truncated == true);
        Assert.Equal(watermark, coverage.GapFromUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(StateTime - 61_999), coverage.GapToUtc);
    }

    private static async Task<(Account, MonitorAccount)> SeedAsync(VesselDbContext db, Guid owner)
    {
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Builder history");
        account.Configure(null, Address, null);
        account.UpdateSettings(account.Name, null, true, keepExecutionHistory: true);
        var monitor = new MonitorAccount
        {
            AccountId = account.Id, OwnerId = owner,
            NewestFillAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(StateTime).AddHours(-1)
        };
        db.AddRange(account, monitor);
        await db.SaveChangesAsync();
        return (account, monitor);
    }

    private static AccountHistoryGap Gap(Account account) => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id,
        FromUtc = DateTimeOffset.FromUnixTimeMilliseconds(StateTime).AddHours(-1),
        ToUtc = DateTimeOffset.FromUnixTimeMilliseconds(StateTime).AddMinutes(-30),
        OpenedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(StateTime).AddMinutes(-15)
    };

    private sealed class BuilderHistoryHandler(bool metadataAvailable) : HttpMessageHandler
    {
        public bool MetadataAvailable { get; set; } = metadataAvailable;
        public bool Capped { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var type = body.RootElement.GetProperty("type").GetString();
            if (!MetadataAvailable && type == "allPerpMetas")
                return new(System.Net.HttpStatusCode.ServiceUnavailable);
            return Json(type switch
            {
                "meta" => Meta,
                "allPerpMetas" => """[{"collateralToken":0,"universe":[{"name":"xyz:GOLD","szDecimals":4,"maxLeverage":25}]}]""",
                "perpDexs" => """[null,{"name":"xyz","fullName":"XYZ"}]""",
                "perpConciseAnnotations" => "[]",
                "clearinghouseState" => Mutate(State, n => n["assetPositions"] = new JsonArray()),
                "userFills" when Capped => "[" + string.Join(",", Enumerable.Range(0, 2000).Select(i =>
                    Mutate(Fill, n => { n["tid"] = 2000 + i; n["time"] = StateTime - 60_000 - i; }))) + "]",
                "userFills" or "userFillsByTime" =>
                    $"[{Fill},{Mutate(Fill, n => { n["coin"] = "xyz:GOLD"; n["tid"] = 42; n["oid"] = 43; })}]",
                "userAbstraction" => "\"default\"",
                "spotMeta" => """{"tokens":[],"universe":[]}""",
                "spotClearinghouseState" => """{"balances":[]}""",
                "historicalOrders" or "frontendOpenOrders" or "userFunding" => "[]",
                _ => throw new InvalidOperationException($"Unexpected fixture read: {type}")
            });
        }
    }
}
