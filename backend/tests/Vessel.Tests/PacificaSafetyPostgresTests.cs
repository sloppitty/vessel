using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Pacifica;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>Ports the independent review's P1/P2 economic repros through actual admission, persistence and readers.</summary>
public sealed class PacificaSafetyPostgresTests
{
    [PostgresTheory]
    [InlineData(false, "positions")]
    [InlineData(true, "positions")]
    [InlineData(false, "lending")]
    [InlineData(true, "lending")]
    public async Task Unsupported_refresh_preserves_prior_facts_and_cannot_create_a_closed_activity_sizing_or_review_result(
        bool hasPriorState, string unsupported)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var account = new Account(Guid.NewGuid(), owner, PacificaReader.Id, "Synthetic safety regression");
        account.Configure(null, null, null, PacificaAdapterTests.Source);
        db.Accounts.Add(account);
        var before = PacificaAdapterTests.Now.AddDays(-1);
        if (hasPriorState)
        {
            account.RecordSync(before, "Prior verified snapshot and retained execution.");
            db.Snapshots.Add(new AccountSnapshot
            {
                OwnerId = owner, AccountId = account.Id, ObservedAtUtc = before,
                ValueScope = "pacifica-perpetual-account-equity", AccountValueUsd = 1234.56789123456789m,
                MarginUsedUsd = 11, AvailableMarginUsd = 900, WithdrawableUsd = 800,
                Positions =
                [
                    new() { OwnerId = owner, AccountId = account.Id, ContractId = "BTC", VenueContractId = "BTC",
                        SignedQuantity = 1, EntryPrice = 80, UnrealizedPnlUsd = 10, MarginUsedUsd = 8, Leverage = 10 },
                    new() { OwnerId = owner, AccountId = account.Id, ContractId = "ETH", VenueContractId = "ETH",
                        SignedQuantity = 0.25m, EntryPrice = 70, UnrealizedPnlUsd = 1, MarginUsedUsd = 3, Leverage = 5 }
                ]
            });
            // Older BTC entry is outside retained history, as in P1. An unrelated retained fact must not be lost either.
            db.Fills.Add(new ImportedFill
            {
                Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, ContractId = "ETH", VenueContractId = "ETH",
                SourceFillId = "retained-eth-entry", ExternalOrderId = "retained-eth-order", TransactionHash = "",
                Side = ExecutionFacts.Buy, Direction = "open_long", PositionEffect = ExecutionFacts.Open,
                Price = 70, Quantity = 0.25m, Fee = 0.01m, FeeToken = "USDC", ClosedPnlUsd = -0.01m,
                PnlBasis = ExecutionFacts.PnlNetOfFee, OccurredAtUtc = before.AddDays(-1)
            });
        }
        await db.SaveChangesAsync();
        var retainedFills = JsonSerializer.Serialize(await db.Fills.AsNoTracking().OrderBy(f => f.SourceFillId).ToListAsync());

        var fixture = PacificaAdapterTests.Fixture();
        var trades = fixture["trades"]!["data"]!.AsArray();
        while (trades.Count > 2) trades.RemoveAt(2);
        trades[0]!["amount"] = "1"; trades[0]!["price"] = "100"; trades[0]!["entry_price"] = "90";
        trades[1]!["amount"] = "1"; trades[1]!["price"] = "110"; trades[1]!["entry_price"] = "90"; trades[1]!["pnl"] = "19.9";
        if (unsupported == "positions")
        {
            // Missing earlier long 1@80; these retained buy1@100/sell1@110 do not close the actual long1@90.
            fixture["account"]!["data"]!["positions_count"] = 1;
            fixture["positions"]!["data"]!.AsArray().Add(new JsonObject
                { ["symbol"] = "BTC", ["side"] = "bid", ["amount"] = "1", ["entry_price"] = "90", ["isolated"] = false });
        }
        else
        {
            // P2: zero pending_interest is not evidence that a 1,500-USDC account has opted out of lending.
            fixture["account"]!["data"]!["balance"] = "1500";
            fixture["account"]!["data"]!["account_equity"] = "1500";
            fixture["account"]!["data"]!["total_margin_used"] = "0";
            fixture["settings"]!["data"]!["auto_lend_disabled"] = null;
        }
        var (reader, routes) = PacificaAdapterTests.Reader(fixture);
        var registry = new VenueRegistry([VenueDescriptor.Manual, PacificaReader.Descriptor], [reader], [reader], [reader], [], [], [], []);
        var failure = await Assert.ThrowsAsync<ProblemException>(() => MonitorTestPaths.RefreshAsync(
            db, owner, registry, account.Id, time: new PacificaAdapterTests.Clock()));
        Assert.Equal(502, failure.StatusCode);
        Assert.DoesNotContain(routes.Requests, path => path.StartsWith("/api/v1/trades/history", StringComparison.Ordinal));
        db.ChangeTracker.Clear();
        Assert.Equal(retainedFills, JsonSerializer.Serialize(await db.Fills.AsNoTracking().OrderBy(f => f.SourceFillId).ToListAsync()));
        var snapshot = await db.Snapshots.AsNoTracking().Include(s => s.Positions).SingleOrDefaultAsync();
        if (hasPriorState)
        {
            Assert.NotNull(snapshot);
            Assert.Equal(before, snapshot.ObservedAtUtc); Assert.Equal(1234.56789123456789m, snapshot.AccountValueUsd);
            Assert.Equal(11m, snapshot.MarginUsedUsd); Assert.Equal(900m, snapshot.AvailableMarginUsd); Assert.Equal(800m, snapshot.WithdrawableUsd);
            Assert.Equal(2, snapshot.Positions.Count);
            var btc = Assert.Single(snapshot.Positions, p => p.ContractId == "BTC");
            Assert.Equal((1m, 80m, 10m, 8m, 10), (btc.SignedQuantity, btc.EntryPrice, btc.UnrealizedPnlUsd, btc.MarginUsedUsd, btc.Leverage));
            Assert.Equal(before, (await db.Accounts.SingleAsync()).LastSyncedAtUtc);
        }
        else Assert.Null(snapshot); // Initial import must not persist even an apparently flat snapshot or balanced fills.

        var work = TestUnitOfWork.For(db);
        var workspace = new WorkspaceStore(db, work);
        var clock = new PacificaAdapterTests.Clock();
        var activity = new ActivityService(new ActivityStore(db), workspace, clock);
        var page = await activity.PageAsync(new(AccountId: account.Id), default);
        Assert.DoesNotContain(page.Operations, operation => operation.Status == "closed");
        if (!hasPriorState) Assert.Empty(page.Operations);
        var sizingStore = new SizingStore(db, work);
        var sizing = await new SizingService(sizingStore, new CoreOwner(owner), clock, work, activity, registry).GetAsync(default);
        Assert.Equal(SizingService.SourceNone, sizing.Record.Source);
        Assert.Equal(0, sizing.Record.ClosedPlays); Assert.Equal(0, sizing.Record.DecidedPlays); Assert.Equal(0, sizing.Record.Wins);
        Assert.Empty(sizing.Record.Recent);
        var positions = new ActivityReviewPositions(activity, sizingStore);
        Assert.Empty(await positions.ReadAsync(new HashSet<Guid> { account.Id }, default));
        var review = await new ReviewService(new ReviewStore(db, sizingStore), clock, registry, positions)
            .GetAsync(new(Period: "all", AccountId: account.Id, Positions: "include"), default);
        Assert.Equal(0, review.Metrics.Count); Assert.Equal(0, review.Metrics.Positions); Assert.Equal(0, review.Metrics.Wins);
    }
}
