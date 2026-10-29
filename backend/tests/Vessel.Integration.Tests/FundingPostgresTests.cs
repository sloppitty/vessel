using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class FundingPostgresTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private sealed class Reader : IPerpetualVenueReader, IVenueFundingReader
    {
        public string VenueId => "hyperliquid";
        public bool FailFunding, CancelFunding;
        public int FundingReads;
        public DateTimeOffset? FundingRetryAtUtc;
        public decimal Amount = -0.1234567890123456789012345678m;
        public DateTimeOffset Observation = At;
        public VenueFundingReadResult? Funding;
        public Func<CancellationToken, Task>? BeforeFunding;
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VenueInstrument>>([]);
        public Task<PerpetualVenueReadResult> ReadAsync(string source, CancellationToken ct) =>
            Task.FromResult(new PerpetualVenueReadResult(new(Observation, "fixture", 100, 80, 0, []), [], [], "Synthetic bounded executions."));
        public async Task<VenueFundingReadResult> ReadFundingAsync(Account account, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        {
            FundingReads++;
            if (BeforeFunding is not null) await BeforeFunding(ct);
            ct.ThrowIfCancellationRequested();
            if (CancelFunding) throw new OperationCanceledException(ct);
            if (FundingRetryAtUtc is { } retryAt) throw new VenueRateLimitException(retryAt);
            if (FailFunding) throw new VenueReadException("Provider secret must not appear.");
            return Funding ?? new([new("funding-1", "BTC", "1", Amount, "USDC", At.AddDays(-1),
                "Raw payload must not be stored")], from, to, true, "Synthetic settled funding.", FundingFacts.Excluded);
        }
    }
    private static WorkspaceService Service(VesselDbContext db, Guid owner, Reader reader) =>
        new(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TestVenues.With(reader), TestUnitOfWork.For(db));
    private static Task<AccountDto> Refresh(VesselDbContext db, Guid owner, Reader reader, Guid id, CancellationToken ct = default) =>
        MonitorTestPaths.RefreshAsync(db, owner, TestVenues.With(reader), id, ct: ct);
    private static async Task<Guid> Account(VesselDbContext db, Guid owner)
    {
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic funding");
        account.Configure(null, "0x1111111111111111111111111111111111111111", null);
        db.Accounts.Add(account); await db.SaveChangesAsync();
        return account.Id;
    }

    [PostgresFact]
    public async Task Migration_owner_isolation_concurrent_sync_dedup_exact_money_and_delete()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = new Reader(); Guid id;
        await using (var db = database.Context(owner))
        {
            Assert.False(db.Database.HasPendingModelChanges());
            id = await Account(db, owner);
        }
        var refreshed = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var db = database.Context(owner);
            try { await Refresh(db, owner, reader, id); return true; }
            catch (MonitorAdmissionException) { return false; }
        }));
        Assert.Contains(true, refreshed);
        await using (var db = database.Context(owner))
        {
            await Refresh(db, owner, reader, id);
            var payment = await db.FundingPayments.SingleAsync();
            Assert.Equal(reader.Amount, payment.Amount);
            Assert.Equal(At.AddDays(-30), (await db.FundingStates.SingleAsync()).CoveredFromUtc);
            Assert.Equal(At, (await db.FundingStates.SingleAsync()).CoveredToUtc);
            Assert.DoesNotContain(db.Model.FindEntityType(typeof(FundingPayment))!.GetProperties(), p => p.Name.Contains("Raw"));
        }
        await using (var db = database.Context(Guid.NewGuid()))
        {
            Assert.Empty(await db.FundingPayments.ToListAsync()); Assert.Empty(await db.FundingStates.ToListAsync());
        }
        await using (var db = database.Context(owner))
        {
            await Service(db, owner, reader).DeleteAccountAsync(id, default);
            Assert.Empty(await db.FundingPayments.ToListAsync()); Assert.Empty(await db.FundingStates.ToListAsync());
            Assert.Empty(await db.Accounts.ToListAsync());
        }
    }

    [PostgresTheory]
    [InlineData("amount")]
    [InlineData("native")]
    [InlineData("asset")]
    [InlineData("time")]
    public async Task Conflicting_funding_identity_rolls_back_the_entire_refresh_without_overwriting_facts(string field)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = new Reader(); Guid id;
        await using (var db = database.Context(owner))
        {
            id = await Account(db, owner); await Refresh(db, owner, reader, id);
        }
        var original = reader.Amount;
        reader.Observation = At.AddHours(1);
        reader.Funding = new([new("funding-1", "RENAMED-BTC", field == "native" ? "2" : "1",
            field == "amount" ? 50 : original, field == "asset" ? "USDG" : "USDC",
            At.AddDays(-1).AddMilliseconds(field == "time" ? 1 : 0), "{}")],
            reader.Observation.AddDays(-30), reader.Observation, true, "Synthetic conflicting funding.");
        await using (var db = database.Context(owner))
        {
            var error = await Assert.ThrowsAsync<ProblemException>(() => Refresh(db, owner, reader, id));
            Assert.Equal(502, error.StatusCode);
        }
        await using (var db = database.Context(owner))
        {
            var payment = await db.FundingPayments.SingleAsync();
            Assert.Equal(original, payment.Amount); Assert.Equal("BTC", payment.ContractId);
            Assert.Equal("1", payment.VenueContractId); Assert.Equal("USDC", payment.Asset);
            Assert.Equal(At.AddDays(-1), payment.OccurredAtUtc);
            Assert.Equal(At, (await db.Snapshots.SingleAsync()).ObservedAtUtc);
            Assert.Equal(At, (await db.FundingStates.SingleAsync()).CoveredToUtc);
        }
    }

    [PostgresTheory]
    [InlineData("1")]
    [InlineData(null)]
    public async Task Derived_canonical_rename_preserves_native_settlement_but_legacy_identity_conflicts(string? native)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = new Reader(); Guid id;
        VenueFundingReadResult Result(string contract) => new(
            [new("funding-1", contract, native, reader.Amount, "USDC", At.AddDays(-1), "{}")],
            reader.Observation.AddDays(-30), reader.Observation, true, "Synthetic settled funding.", FundingFacts.Excluded);
        reader.Funding = Result("BTC");
        await using (var db = database.Context(owner))
        {
            id = await Account(db, owner); await Refresh(db, owner, reader, id);
        }
        reader.Observation = At.AddHours(1);
        reader.Funding = Result("RENAMED-BTC");
        await using (var db = database.Context(owner))
        {
            if (native is null)
                Assert.Equal(502, (await Assert.ThrowsAsync<ProblemException>(() => Refresh(db, owner, reader, id))).StatusCode);
            else await Refresh(db, owner, reader, id);
        }
        await using (var db = database.Context(owner))
        {
            var retained = await db.FundingPayments.SingleAsync();
            Assert.Equal("BTC", retained.ContractId); Assert.Equal(native, retained.VenueContractId);
            Assert.Equal(reader.Amount, retained.Amount);
            var expected = native is null ? At : reader.Observation;
            Assert.Equal(expected, (await db.Snapshots.SingleAsync()).ObservedAtUtc);
            Assert.Equal(expected, (await db.FundingStates.SingleAsync()).CoveredToUtc);
        }
    }

    [PostgresFact]
    public async Task Funding_rate_limit_keeps_public_refresh_but_defers_orders_and_other_accounts_on_the_venue()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var reader = new Reader { FundingRetryAtUtc = At.AddMinutes(10) };
        var orders = new AccountHistoryOrders();
        var venues = TestVenues.With(reader, orders);
        await using var db = database.Context(owner);
        var id = await Account(db, owner);
        var other = new Account(Guid.NewGuid(), owner, "hyperliquid", "Other synthetic account");
        other.Configure(null, "0x2222222222222222222222222222222222222222", null);
        db.Accounts.Add(other); await db.SaveChangesAsync();
        var clock = new Clock();
        await MonitorTestPaths.RefreshAsync(db, owner, venues, id, time: clock);
        Assert.Equal(At, (await db.Snapshots.SingleAsync()).ObservedAtUtc);
        Assert.False((await db.FundingStates.SingleAsync()).LastReadComplete);
        Assert.Empty(await db.FundingPayments.ToListAsync());
        Assert.Equal(reader.FundingRetryAtUtc,
            await MonitorTestPaths.Store(db, TestUnitOfWork.For(db)).VenueRetryAtAsync("hyperliquid", default));
        Assert.Equal(0, orders.Reads);
        await Assert.ThrowsAsync<MonitorAdmissionException>(() =>
            MonitorTestPaths.RefreshAsync(db, owner, venues, other.Id, time: clock));
        Assert.Equal(1, reader.FundingReads);
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => At; }

    [PostgresFact]
    public async Task Funding_failure_preserves_history_and_valid_snapshot_refresh_without_exposing_provider_errors()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = new Reader(); Guid id;
        await using (var db = database.Context(owner))
        {
            id = await Account(db, owner); await Refresh(db, owner, reader, id);
            reader.FailFunding = true; reader.Observation = At.AddHours(1);
            await Refresh(db, owner, reader, id);
        }
        await using (var db = database.Context(owner))
        {
            Assert.Single(await db.FundingPayments.ToListAsync());
            var state = await db.FundingStates.SingleAsync();
            Assert.False(state.LastReadComplete); Assert.Equal(At, state.CoveredToUtc);
            Assert.DoesNotContain("secret", state.Notice, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(At.AddHours(1), (await db.Snapshots.SingleAsync()).ObservedAtUtc);
        }
    }

    [PostgresFact]
    public async Task Coverage_merges_overlaps_but_does_not_bridge_an_unread_gap_and_keeps_settlements()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = new Reader(); Guid id;
        await using (var db = database.Context(owner))
        {
            id = await Account(db, owner); await Refresh(db, owner, reader, id);
            reader.Observation = At.AddDays(1);
            await Refresh(db, owner, reader, id);
            Assert.Equal(At.AddDays(-30), (await db.FundingStates.SingleAsync()).CoveredFromUtc);
            Assert.Equal(At.AddDays(1), (await db.FundingStates.SingleAsync()).CoveredToUtc);
            reader.Observation = At.AddDays(40);
            reader.Funding = new([], At.AddDays(10), At.AddDays(40), true, "Later verified empty window.", FundingFacts.Excluded);
            await Refresh(db, owner, reader, id);
        }
        await using (var db = database.Context(owner))
        {
            var state = await db.FundingStates.SingleAsync();
            Assert.Equal(At.AddDays(10), state.CoveredFromUtc); Assert.Equal(At.AddDays(40), state.CoveredToUtc);
            Assert.Single(await db.FundingPayments.ToListAsync());
        }
    }

    [PostgresFact]
    public async Task Native_nanoseconds_round_trip_separately_from_the_postgres_microsecond_timestamp()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = new Reader(); Guid id;
        const long ns = 1792144800001234567;
        var timestamp = DateTimeOffset.UnixEpoch.AddTicks(ns / 1000 * 10);
        reader.Funding = new([new VenueFundingPayment("ns-payment", "BTC", "1", 2, "USDC", timestamp, "{}")
        { OccurredAtUnixNanoseconds = ns }], At.AddDays(-30), At, true, "Exact native timestamp.");
        await using (var db = database.Context(owner))
        {
            id = await Account(db, owner); await Refresh(db, owner, reader, id);
        }
        await using (var db = database.Context(owner))
        {
            var payment = await db.FundingPayments.SingleAsync();
            Assert.Equal(ns, payment.OccurredAtUnixNanoseconds); Assert.Equal(timestamp, payment.OccurredAtUtc);
            await Refresh(db, owner, reader, id);
            Assert.Single(await db.FundingPayments.ToListAsync());
        }
    }

    [PostgresFact]
    public async Task Cancelled_funding_never_saves_the_refresh_and_disabled_accounts_never_read()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = new Reader { CancelFunding = true }; Guid id;
        using var cancellation = new CancellationTokenSource();
        await using (var db = database.Context(owner))
        {
            id = await Account(db, owner);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Refresh(db, owner, reader, id, cancellation.Token));
        }
        await using (var db = database.Context(owner))
        {
            Assert.Empty(await db.FundingPayments.ToListAsync()); Assert.Empty(await db.Snapshots.ToListAsync());
            var account = await db.Accounts.SingleAsync(); account.UpdateSettings(account.Name, null, false); await db.SaveChangesAsync();
            await Assert.ThrowsAsync<ProblemException>(() => Refresh(db, owner, reader, id));
            Assert.Equal(0, reader.FundingReads);
        }
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Funding_reads_outside_the_unit_and_changed_settings_discard_the_captured_refresh(bool disable)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = new Reader();
        await using var db = database.Context(owner);
        var id = await Account(db, owner);
        var inUnit = true;
        var settingsChanged = false;
        reader.BeforeFunding = async ct =>
        {
            inUnit = TestUnitOfWork.For(db).IsActive || db.Database.CurrentTransaction is not null;
            await using var changed = database.Context(owner);
            var account = await changed.Accounts.SingleAsync(ct);
            account.UpdateSettings("Changed during funding read", null, !disable);
            await changed.SaveChangesAsync(ct);
            settingsChanged = true;
        };
        var error = await Assert.ThrowsAsync<ProblemException>(() => Refresh(db, owner, reader, id));
        Assert.Equal(502, error.StatusCode);
        Assert.False(inUnit);
        Assert.True(settingsChanged);
        Assert.Equal(1, reader.FundingReads);
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.FundingPayments.ToListAsync());
        Assert.Empty(await verify.FundingStates.ToListAsync());
        Assert.Empty(await verify.Snapshots.ToListAsync());
    }

    [PostgresFact]
    public async Task Cancellation_during_funding_read_does_not_commit_snapshot_or_settlements()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = new Reader();
        using var cancellation = new CancellationTokenSource();
        reader.BeforeFunding = _ => { cancellation.Cancel(); return Task.CompletedTask; };
        await using (var db = database.Context(owner))
        {
            var id = await Account(db, owner);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Refresh(db, owner, reader, id, cancellation.Token));
            Assert.Equal(1, reader.FundingReads);
        }
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.FundingPayments.ToListAsync());
        Assert.Empty(await verify.FundingStates.ToListAsync());
        Assert.Empty(await verify.Snapshots.ToListAsync());
    }

    [PostgresFact]
    public async Task Retrospective_preview_uses_the_same_funding_aware_native_partition_as_activity()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); Guid id; Guid openingId;
        await using (var db = database.Context(owner))
        {
            id = await Account(db, owner);
            var account = await db.Accounts.SingleAsync();
            var legacy = DiaryPostgresTests.Imported(account, "legacy", "legacy", "buy", "open", 1, 100, 0);
            var open = DiaryPostgresTests.Imported(account, "native-entry", "native-entry", "buy", "open", 1, 100, 1);
            var close = DiaryPostgresTests.Imported(account, "native-exit", "native-exit", "sell", "close", 1, 110, 2, 10);
            open.VenueContractId = "1"; close.VenueContractId = "1"; openingId = open.Id;
            db.Fills.AddRange(legacy, open, close);
            db.Orders.AddRange(DiaryPostgresTests.Order(account, "native-entry", "buy", 1),
                DiaryPostgresTests.Order(account, "native-exit", "sell", 1));
            // A second native market known only through funding makes the old unqualified execution ambiguous.
            db.FundingPayments.Add(new() { Id = Guid.NewGuid(), OwnerId = owner, AccountId = id, ContractId = "BTC",
                VenueContractId = "2", SourceId = "retired-market-payment", Amount = 1, Asset = "USDC",
                OccurredAtUtc = open.OccurredAtUtc.AddMilliseconds(500) });
            db.Snapshots.Add(new() { OwnerId = owner, AccountId = id, ObservedAtUtc = close.OccurredAtUtc.AddSeconds(1), ValueScope = "fixture" });
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(owner))
        {
            var activity = await ActivityTestSupport.Activity(db).PageAsync(id);
            var operation = Assert.Single(activity.Operations, o => o.Status == "closed");
            Assert.StartsWith(openingId.ToString(), operation.Id);
            var preview = await DiaryPostgresTests.Service(db).PreviewAsync(id, operation.Id, default);
            Assert.Equal(operation.Id, preview.Operation.Id);
            Assert.Equal(2, preview.Operation.Fills.Count);
            Assert.DoesNotContain(preview.Operation.Fills, f => f.Fill.SourceFillId == "legacy");
        }
    }
}
