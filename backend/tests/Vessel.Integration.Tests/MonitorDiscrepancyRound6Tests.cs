using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vessel.Application.Monitoring;
using Vessel.Application.Credentials;
using Vessel.Application.Events;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>Sixth review round for PR #46: every lease-owned run outcome settles discrepancy housekeeping (Sol r5 repros).</summary>
public sealed class MonitorDiscrepancyRound6Tests : MonitorDiscrepancyRound6TestsSupport
{
    [PostgresFact]
    public async Task Timestamp_fenced_checks_should_still_expire_outstanding_work()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = Untracked(); await database.Seed(f);
        await using var scope = database.Scope(f);
        await scope.Evaluate(f.Observation());
        var reader = new Reader(f);
        var (monitor, coordinator) = Create(scope, f, reader);
        Assert.True(await coordinator.RunAsync(await coordinator.AdmitRefreshAsync(f.Account.Id, "owner", default), default));
        // A refresh never schedules checks; discovery does, for the discrepancy work alone.
        await coordinator.DiscoverAsync(["hyperliquid"], f.Clock.Now, default);
        reader.Frozen = f.Observation().Read.Sync;
        // Hyperliquid stamps snapshots with the upstream clearinghouseState.time, not local receive time.
        f.Advance((int)TimeSpan.FromDays(7).TotalSeconds + 60);
        // The fenced check rejects the stale data but still settles: the outstanding work expires and releases the account.
        Assert.False(await coordinator.RunAsync(Assert.Single(await monitor.ClaimDueAsync("scheduler", f.Clock.Now, 2, default)), default));
        var row = await scope.Db.Set<PlayDiscrepancy>().SingleAsync();
        Assert.NotNull(row.UnverifiableAtUtc);
        Assert.Equal("candidate", row.State);
        Assert.False(await monitor.EligibleAsync(f.Account.Id, f.Clock.Now, default));
        Assert.Null((await monitor.FindAsync(f.Account.Id, default))!.NextRunAtUtc);
        f.Advance(660);
        Assert.Empty(await monitor.ClaimDueAsync("scheduler", f.Clock.Now, 2, default));
    }
    [PostgresFact]
    public async Task Failed_confirmation_check_should_interrupt_pending_confirmation()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = Untracked(); await database.Seed(f);
        await using var scope = database.Scope(f);
        await scope.Evaluate(f.Observation());
        var reader = new Reader(f);
        var (monitor, coordinator) = Create(scope, f, reader);
        Assert.True(await coordinator.RunAsync(await coordinator.AdmitRefreshAsync(f.Account.Id, "owner", default), default));
        // A refresh never schedules checks; discovery does, for the discrepancy work alone.
        await coordinator.DiscoverAsync(["hyperliquid"], f.Clock.Now, default);
        reader.Failing = true; f.Advance(660);
        Assert.False(await coordinator.RunAsync(Assert.Single(await monitor.ClaimDueAsync("scheduler", f.Clock.Now, 2, default)), default));
        reader.Failing = false; f.Advance(60);
        Assert.True(await coordinator.RunAsync(Assert.Single(await monitor.ClaimDueAsync("scheduler", f.Clock.Now, 2, default)), default));
        // The first fresh observation after the failed check must start, not complete, a new sequence.
        Assert.Equal("candidate", (await scope.Db.Set<PlayDiscrepancy>().SingleAsync()).State);
    }
    [PostgresFact]
    public async Task Overdue_scheduler_is_serialized_with_refresh_and_claimed_only_once()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = Untracked(); await database.Seed(f);
        await using var scope = database.Scope(f);
        await scope.Evaluate(f.Observation());
        var reader = new Reader(f);
        var (monitor, coordinator) = Create(scope, f, reader);
        Assert.True(await coordinator.RunAsync(await coordinator.AdmitRefreshAsync(f.Account.Id, "owner", default), default));
        // A refresh never schedules checks; discovery does, for the discrepancy work alone.
        await coordinator.DiscoverAsync(["hyperliquid"], f.Clock.Now, default);
        f.Advance(660);
        var refresh = await coordinator.AdmitRefreshAsync(f.Account.Id, "owner", default);
        await using var other = database.Scope(f);
        var otherMonitor = MonitorTestPaths.Store(other.Db, other.Work);
        Assert.Empty(await otherMonitor.ClaimDueAsync("other-worker", f.Clock.Now, 2, default));
        Assert.True(await coordinator.RunAsync(refresh, default));
        f.Advance(1); // Give the next source snapshot an independent observation timestamp.
        var scheduled = Assert.Single(await otherMonitor.ClaimDueAsync("other-worker", f.Clock.Now, 2, default));
        Assert.Empty(await monitor.ClaimDueAsync("scheduler", f.Clock.Now, 2, default));
        var otherCoordinator = Create(other, f, reader).Item2;
        Assert.True(await otherCoordinator.RunAsync(scheduled, default));
        Assert.Empty(await monitor.ClaimDueAsync("scheduler", f.Clock.Now, 2, default));
        var calls = reader.Calls;
        Assert.False(await coordinator.RunAsync(refresh, default));
        Assert.False(await otherCoordinator.RunAsync(scheduled, default));
        Assert.Equal(calls, reader.Calls);
    }
    [PostgresFact]
    public async Task Migration_backfills_confirmed_records_without_revealing_hidden_history()
    {
        await using var database = await DiscrepancyDatabase.Create("20261018100000_ServerEventSubscriptions");
        var f = Untracked();
        await using var scope = database.Scope(f);
        // This pinned historical schema predates paper account columns. Seed the same account facts explicitly,
        // rather than asking the latest EF Account mapping to write columns the migration has not introduced.
        var account = f.Account;
        await scope.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO accounts ("Id","OwnerId","VenueId","Name","PortfolioId","Address","SourceId",
              "ManualAccountValueUsd","LastSyncedAtUtc","SyncStatus","LastSyncError","HistoryNotice","IsEnabled","SettingsRevision")
            VALUES ({account.Id},{account.OwnerId},{account.VenueId},{account.Name},{account.PortfolioId},{account.Address},{account.SourceId},
              {account.ManualAccountValueUsd},{account.LastSyncedAtUtc},{account.SyncStatus},{account.LastSyncError},
              {account.HistoryNotice},{account.IsEnabled},{account.SettingsRevision})
            """);
        var shapes = new[] { ("open", true), ("resolved", true), ("dismissed", true), ("candidate", false), ("resolved", false) };
        for (var i = 0; i < shapes.Length; i++)
            scope.Db.Add(new PlayDiscrepancy { OwnerId = f.Account.OwnerId, AccountId = f.Account.Id,
                Kind = "position.excess", SubjectKey = "subject-" + i, ContractId = "BTC", NativeContractId = "BTC",
                State = shapes[i].Item1, Severity = "warning", Fingerprint = "A", Revision = 7,
                OpenedAtUtc = f.Clock.Now, LastObservedAtUtc = f.Clock.Now,
                ConfirmedAtUtc = shapes[i].Item2 ? f.Clock.Now : null });
        await scope.Db.SaveChangesAsync();
        await scope.Db.GetService<IMigrator>().MigrateAsync("20261016110300_PlayDiscrepancies");
        await scope.Db.GetService<IMigrator>().MigrateAsync("20261016110400_PlayDiscrepancyUnverifiable");
        scope.Db.ChangeTracker.Clear();
        var rows = await scope.Db.Set<PlayDiscrepancy>().OrderBy(x => x.SubjectKey).ToArrayAsync();
        Assert.Equal(new string?[] { "opened", "resolved", "dismissed", null, null }, rows.Select(x => x.PublishedTransition));
        Assert.Equal(new long?[] { 7, 7, 7, null, null }, rows.Select(x => x.PublishedRevision));
        Assert.Equal("resolved", DiscrepancyLifecycle.Apply(rows[0], DiscrepancyInput.Absent(f.Clock.Now, f.Clock.Now, "Z", "{}", [])).Publish);
        Assert.Equal(8, rows[0].Revision);
    }
    private static (MonitorStore, MonitorCoordinator) Create(DiscrepancyDatabaseScope scope, DiscrepancyFixture f, Reader reader)
    {
        var monitor = MonitorTestPaths.Store(scope.Db, scope.Work);
        return (monitor, new(monitor, new WorkspaceStore(scope.Db, scope.Work), new NoImporter(),
            TestVenues.With(reader, new Orders(f)), new NoCredentials(), scope.Work, scope.Events, new NoCommands(),
            new CoreOwner(f.Account.OwnerId), new MonitorOptions(), f.Clock, scope.Service));
    }
    private sealed class Reader(DiscrepancyFixture f) : IPerpetualVenueReader
    {
        public bool Failing;
        public int Calls;
        public PerpetualVenueReadResult? Frozen;
        public string VenueId => "hyperliquid";
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) => Task.FromResult(f.Observation().Read.Sync.Instruments);
        public Task<PerpetualVenueReadResult> ReadAsync(string source, CancellationToken ct)
        {
            Calls++;
            if (Failing) throw new HttpRequestException("synthetic failure");
            return Task.FromResult((Frozen ?? f.Observation().Read.Sync) with { FillHistoryComplete = true });
        }
    }
    private sealed class Orders(DiscrepancyFixture f) : IVenueOrderReader
    {
        public string VenueId => "hyperliquid";
        public Task<VenueOrderReadResult> ReadOrdersAsync(string source, CancellationToken ct) => Task.FromResult(f.Observation().Read.Orders!);
    }
    private sealed class NoImporter : IMonitorImporter
    {
        public Task RetainAsync(Account a, VenueOrderReadResult o, CancellationToken ct) => Task.CompletedTask;
        public Task ReconcileAsync(Account a, VenueOrderReadResult o, IReadOnlyList<VenueInstrument> i, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class NoCredentials : IAccountCredentialReader
    {
        public Task<string?> ReadAsync(Guid id, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<CredentialAttempt?> ReadAttemptAsync(Guid id, CancellationToken ct) => Task.FromResult<CredentialAttempt?>(null);
        public Task MarkRefusedAsync(Guid id, string revision, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class NoCommands : IMonitorCommandSink
    {
        public Task EnqueueAsync(MonitorAccountCommand c, CancellationToken ct) => Task.CompletedTask;
    }
}
