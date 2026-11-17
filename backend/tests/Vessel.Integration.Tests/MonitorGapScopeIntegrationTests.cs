using Microsoft.EntityFrameworkCore;
using Vessel.Application.Credentials;
using Vessel.Application.Events;
using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// #94: a check passes the latest end of the account's open gaps to the evaluation, so a Play that started after it keeps
/// its full checks while one that started before it does not.
/// </summary>
public sealed class MonitorGapScopeIntegrationTests
{
    // The fixture's Play placed its entry order 25 minutes before the fixture's start and filled it 5 minutes later.
    [PostgresTheory]
    [InlineData(-90, "short")]
    [InlineData(-5, "history-gap")]
    public async Task A_check_scopes_the_history_gap_to_Plays_started_no_later_than_its_end(int legacyGapMinutes, string reason)
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture();
        f.Account.Configure(null, "0x" + Guid.NewGuid().ToString("N") + "00000000", null);
        f.Stop(o => o with { RemainingSize = .5m });
        var start = f.Clock.Now;
        await database.Seed(f);
        await using var scope = database.Scope(f);
        var probe = new Probe(scope.Service);
        var (monitor, coordinator) = Create(scope, f, probe);
        Assert.True(await coordinator.RunAsync(await coordinator.AdmitRefreshAsync(f.Account.Id, "owner", default), default));
        await coordinator.DiscoverAsync(["hyperliquid"], f.Clock.Now, default);
        // One gap with a known end, and one from before the ledger, which ended before it was recorded.
        scope.Db.HistoryGaps.Add(new AccountHistoryGap { Id = Guid.CreateVersion7(), OwnerId = f.Account.OwnerId, AccountId = f.Account.Id,
            FromUtc = start.AddHours(-3), ToUtc = start.AddHours(-2), OpenedAtUtc = start.AddHours(-2) });
        scope.Db.HistoryGaps.Add(new AccountHistoryGap { Id = Guid.CreateVersion7(), OwnerId = f.Account.OwnerId, AccountId = f.Account.Id,
            OpenedAtUtc = start.AddMinutes(legacyGapMinutes) });
        (await scope.Db.Set<MonitorAccount>().SingleAsync()).HasHistoryGap = true;
        await scope.Db.SaveChangesAsync();
        f.Advance(660);
        var lease = Assert.Single(await monitor.ClaimDueAsync("scheduler", f.Clock.Now, 2, default));
        Assert.True(await coordinator.RunAsync(lease, default));

        Assert.Equal((true, (DateTimeOffset?)start.AddMinutes(legacyGapMinutes)), (probe.Last!.HistoryGap, probe.Last.HistoryGapEndUtc));
        scope.Db.ChangeTracker.Clear();
        Assert.Equal(reason, (await scope.Db.Set<MonitorCoverage>().SingleAsync()).Reason);
        // The gap still degrades the account whatever it reaches.
        var row = await scope.Db.Set<MonitorAccount>().SingleAsync();
        Assert.True(row.HasHistoryGap);
    }

    private sealed class Probe(IMonitorEvaluator inner) : IMonitorEvaluator
    {
        public MonitorEvaluation? Last;
        public Task<MonitorEvaluationResult> EvaluateAsync(MonitorEvaluation observation, CancellationToken ct)
        { Last = observation; return inner.EvaluateAsync(observation, ct); }
        public Task<IReadOnlyList<MonitorDiscrepancyDto>> ReadAsync(Guid account, Guid? play, CancellationToken ct) => inner.ReadAsync(account, play, ct);
        public Task<MonitorDiscrepancyList> ReadListAsync(Guid account, Guid? play, CancellationToken ct) => inner.ReadListAsync(account, play, ct);
        public Task SettleAsync(Account account, Guid run, string trigger, bool interrupted, CancellationToken ct) =>
            inner.SettleAsync(account, run, trigger, interrupted, ct);
    }
    private static (MonitorStore, MonitorCoordinator) Create(DiscrepancyDatabaseScope scope, DiscrepancyFixture f, IMonitorEvaluator evaluation)
    {
        var monitor = MonitorTestPaths.Store(scope.Db, scope.Work);
        return (monitor, new(monitor, new WorkspaceStore(scope.Db, scope.Work), new NoImporter(),
            TestVenues.With(new Reader(f), new Orders(f)), new NoCredentials(), scope.Work, scope.Events, new NoCommands(),
            new CoreOwner(f.Account.OwnerId), new MonitorOptions(), f.Clock, evaluation));
    }
    /// <summary>A bounded read that reaches no cap: it neither finds nor closes a gap.</summary>
    private sealed class Reader(DiscrepancyFixture f) : IPerpetualVenueReader
    {
        public string VenueId => "hyperliquid";
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) => Task.FromResult(f.Observation().Read.Sync.Instruments);
        public Task<PerpetualVenueReadResult> ReadAsync(string source, CancellationToken ct) =>
            Task.FromResult(f.Observation().Read.Sync with { FillHistoryTruncated = false });
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
