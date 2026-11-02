using Microsoft.EntityFrameworkCore;
using Vessel.Application.Monitoring;
using Vessel.Application.Credentials;
using Vessel.Application.Events;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>Seventh review round for PR #46: every lease-owned run outcome, including deferral, settles exactly once (Sol r6 repros).</summary>
public sealed class MonitorDiscrepancyRound7Tests
{
    private static DiscrepancyFixture Untracked()
    {
        var f = new DiscrepancyFixture { Position = 1 };
        f.Account.Configure(null, "0x" + Guid.NewGuid().ToString("N") + "00000000", null);
        f.Store.Plays.Clear(); f.Store.Links.Clear(); f.Store.Orders.Clear(); f.Store.Fills.Clear(); f.VenueOrders.Clear();
        return f;
    }
    [DiscrepancyPostgresTheory]
    [InlineData("check-success")]
    [InlineData("check-deferred")]
    [InlineData("refresh-deferred")]
    [InlineData("check-fail")]
    [InlineData("check-discard")]
    [InlineData("check-cancel")]
    [InlineData("check-rate-limit")]
    [InlineData("check-credential-fail")]
    [InlineData("check-settings-change")]
    [InlineData("refresh-discard")]
    [InlineData("refresh-final-discard")]
    [InlineData("refresh-success")]
    [InlineData("refresh-fail")]
    [InlineData("refresh-order-fail")]
    [InlineData("refresh-order-cancel")]
    public async Task Terminal_paths_settle_once(string mode)
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = Untracked(); await database.Seed(f);
        await using var scope = database.Scope(f);
        await scope.Evaluate(f.Observation());
        var reader = new Reader(f);
        var (_, initial) = Create(scope, f, reader);
        Assert.True(await initial.RunAsync(await initial.AdmitRefreshAsync(f.Account.Id, "owner", default), default));
        // A refresh never schedules checks; discovery does, for the discrepancy work alone.
        await initial.DiscoverAsync(["hyperliquid"], f.Clock.Now, default);
        var frozen = f.Observation().Read.Sync;
        f.Advance(660);
        var probe = new Probe(scope.Service);
        var orders = new Orders(f);
        var (monitor, coordinator) = Create(scope, f, reader, probe, orders);
        using var cancel = new CancellationTokenSource();
        var refresh = mode.StartsWith("refresh");
        var lease = refresh ? await coordinator.AdmitRefreshAsync(f.Account.Id, "owner", default) :
            Assert.Single(await monitor.ClaimDueAsync("scheduler", f.Clock.Now, 2, default));
        if (mode.EndsWith("-fail"))
        {
            if (mode == "refresh-order-fail") orders.BeforeRead = () => throw new HttpRequestException();
            else reader.Failing = true;
        }
        if (mode == "check-discard") reader.Frozen = frozen;
        if (mode == "refresh-discard") reader.Frozen = frozen with { Snapshot = frozen.Snapshot with { ObservedAtUtc = frozen.Snapshot.ObservedAtUtc.AddSeconds(-1) } };
        if (mode == "check-rate-limit") reader.BeforeRead = () => throw new VenueRateLimitException(f.Clock.Now.AddMinutes(10));
        if (mode == "check-credential-fail") reader.BeforeRead = () => throw new VenueCredentialRejectedException();
        Action change = () => scope.Db.Accounts.Where(a => a.Id == f.Account.Id).ExecuteUpdate(x => x.SetProperty(a => a.SettingsRevision, a => a.SettingsRevision + 1));
        if (mode == "check-settings-change") reader.BeforeRead = change;
        if (mode == "refresh-final-discard") orders.BeforeRead = change;
        var deferred = mode.EndsWith("-deferred");
        if (deferred) await monitor.DeferVenueAsync("hyperliquid", f.Clock.Now.AddMinutes(10), f.Clock.Now, default);
        if (mode == "refresh-deferred") await Assert.ThrowsAsync<MonitorAdmissionException>(() => coordinator.RunAsync(lease, default));
        else if (mode.EndsWith("-cancel"))
        {
            Action interrupt = () => { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); };
            if (refresh) orders.BeforeRead = interrupt; else reader.BeforeRead = interrupt;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunAsync(lease, cancel.Token));
        }
        else await coordinator.RunAsync(lease, default);
        Assert.Equal(mode == "check-success" ? 1 : 0, probe.Evaluations);
        Assert.Equal(mode == "check-success" ? 0 : 1, probe.Settlements.Count);
        // Only an attempted, non-refresh check interrupts pending confirmations; a deferral observed nothing.
        if (probe.Settlements.Count > 0) Assert.Equal(!refresh && !deferred, probe.Settlements.Single());
        if (deferred) Assert.Equal(1, reader.Calls);
        var count = probe.Evaluations + probe.Settlements.Count;
        Assert.False(await coordinator.RunAsync(lease, default));
        Assert.Equal(count, probe.Evaluations + probe.Settlements.Count);
    }
    [PostgresFact]
    public async Task Deferred_owned_attempt_should_age_expired_candidate()
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
        f.Advance(7 * 24 * 60 * 60 + 60);
        var lease = Assert.Single(await monitor.ClaimDueAsync("scheduler", f.Clock.Now, 2, default));
        await monitor.DeferVenueAsync("hyperliquid", f.Clock.Now.AddMinutes(10), f.Clock.Now, default);
        Assert.False(await coordinator.RunAsync(lease, default));
        Assert.Equal(1, reader.Calls); // Only the initial owner refresh performed a read.
        var row = await scope.Db.Set<PlayDiscrepancy>().SingleAsync();
        Assert.NotNull(row.UnverifiableAtUtc);
        Assert.Equal("candidate", row.State);
        // Eligibility is recomputed: with no work left, the deferred check is not kept pending.
        Assert.False(await monitor.EligibleAsync(f.Account.Id, f.Clock.Now, default));
        var state = (await monitor.FindAsync(f.Account.Id, default))!;
        Assert.Equal(("idle", null, null), (state.ManualState, state.LeaseHolder, state.NextRunAtUtc));
        Assert.Single((await EventReads.ReadAsync(scope.Db, 0, 100, default)).Events, x => x.Type == "discrepancy.unverifiable");
    }

    [PostgresFact]
    public async Task Deferred_attempt_keeps_remaining_work_pending_and_its_confirmation_sequence()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = Untracked(); await database.Seed(f);
        await using var scope = database.Scope(f);
        await scope.Evaluate(f.Observation());
        var since = (await scope.Db.Set<PlayDiscrepancy>().SingleAsync()).PendingSinceUtc;
        var reader = new Reader(f);
        var (monitor, coordinator) = Create(scope, f, reader);
        Assert.True(await coordinator.RunAsync(await coordinator.AdmitRefreshAsync(f.Account.Id, "owner", default), default));
        // A refresh never schedules checks; discovery does, for the discrepancy work alone.
        await coordinator.DiscoverAsync(["hyperliquid"], f.Clock.Now, default);
        f.Advance(660);
        var lease = Assert.Single(await monitor.ClaimDueAsync("scheduler", f.Clock.Now, 2, default));
        await monitor.DeferVenueAsync("hyperliquid", f.Clock.Now.AddMinutes(10), f.Clock.Now, default);
        Assert.False(await coordinator.RunAsync(lease, default));
        var pending = (await monitor.FindAsync(f.Account.Id, default))!;
        Assert.Equal(("pending", null, lease.RequestId), (pending.ManualState, pending.LeaseHolder, pending.RequestId));
        Assert.True(await monitor.EligibleAsync(f.Account.Id, f.Clock.Now, default));
        var row = await scope.Db.Set<PlayDiscrepancy>().SingleAsync();
        Assert.Null(row.UnverifiableAtUtc);
        Assert.Equal(since, row.PendingSinceUtc); // Not interrupted: nothing was attempted.
    }
    private sealed class DiscrepancyPostgresTheoryAttribute : TheoryAttribute
    {
        public DiscrepancyPostgresTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Vessel_TEST_POSTGRES")))
                Skip = "Set Vessel_TEST_POSTGRES to a real PostgreSQL connection with CREATE SCHEMA permission.";
        }
    }
    private sealed class Probe(IMonitorEvaluator inner) : IMonitorEvaluator
    {
        public int Evaluations;
        public List<bool> Settlements = [];
        public Task<MonitorEvaluationResult> EvaluateAsync(MonitorEvaluation observation, CancellationToken ct)
        { Evaluations++; return inner.EvaluateAsync(observation, ct); }
        public Task<IReadOnlyList<MonitorDiscrepancyDto>> ReadAsync(Guid account, Guid? play, CancellationToken ct) => inner.ReadAsync(account, play, ct);
        public Task<MonitorDiscrepancyList> ReadListAsync(Guid account, Guid? play, CancellationToken ct) => inner.ReadListAsync(account, play, ct);
        public Task SettleAsync(Account account, Guid run, string trigger, bool interrupted, CancellationToken ct)
        { Settlements.Add(interrupted); return inner.SettleAsync(account, run, trigger, interrupted, ct); }
    }
    private static (MonitorStore, MonitorCoordinator) Create(DiscrepancyDatabaseScope scope, DiscrepancyFixture f, Reader reader, IMonitorEvaluator? evaluation = null, Orders? orders = null)
    {
        var monitor = MonitorTestPaths.Store(scope.Db, scope.Work);
        return (monitor, new(monitor, new WorkspaceStore(scope.Db, scope.Work), new NoImporter(),
            TestVenues.With(reader, orders ?? new Orders(f)), new NoCredentials(), scope.Work, scope.Events, new NoCommands(),
            new CoreOwner(f.Account.OwnerId), new MonitorOptions(), f.Clock, evaluation ?? scope.Service));
    }
    private sealed class Reader(DiscrepancyFixture f) : IPerpetualVenueReader
    {
        public bool Failing;
        public Action? BeforeRead;
        public int Calls;
        public PerpetualVenueReadResult? Frozen;
        public string VenueId => "hyperliquid";
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) => Task.FromResult(f.Observation().Read.Sync.Instruments);
        public Task<PerpetualVenueReadResult> ReadAsync(string source, CancellationToken ct)
        {
            Calls++; BeforeRead?.Invoke();
            if (Failing) throw new HttpRequestException("synthetic failure");
            return Task.FromResult((Frozen ?? f.Observation().Read.Sync) with { FillHistoryComplete = true });
        }
    }
    private sealed class Orders(DiscrepancyFixture f) : IVenueOrderReader
    {
        public string VenueId => "hyperliquid";
        public Action? BeforeRead;
        public Task<VenueOrderReadResult> ReadOrdersAsync(string source, CancellationToken ct) { BeforeRead?.Invoke(); return Task.FromResult(f.Observation().Read.Orders!); }
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
