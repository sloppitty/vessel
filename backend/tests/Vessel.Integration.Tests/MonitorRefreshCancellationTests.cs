using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Credentials;
using Vessel.Application.Events;
using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Monitoring;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// An owner Refresh interrupted by the caller's token (request abort, shutdown or lost lease renewal) is not a venue
/// sync failure: the lease is released with the refresh cooldown, but the account's sync status is left alone.
/// Venue deadlines arrive as exceptions without the caller's token cancelled and still record a failure.
/// </summary>
public sealed class MonitorRefreshCancellationTests
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    [PostgresFact]
    public async Task Cancelling_while_the_venue_read_is_blocked_records_no_sync_failure()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var clock = new Clock();
        var reader = new ControlledReader();
        var id = await SyncedAccountAsync(database, owner, reader, clock);

        reader.BlockRead = true;
        using var cts = new CancellationTokenSource();
        await using (var db = database.Context(owner))
        {
            var run = RefreshAsync(db, owner, reader, id, clock, cts.Token);
            await reader.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        await using var verify = database.Context(owner);
        var account = await verify.Accounts.AsNoTracking().SingleAsync();
        Assert.Equal("synced", account.SyncStatus);
        Assert.Null(account.LastSyncError);
        await AssertReleasedWithCooldownAsync(verify, clock);
    }

    [PostgresFact]
    public async Task Cancelling_a_first_refresh_leaves_the_account_not_synced()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var clock = new Clock();
        var reader = new ControlledReader { BlockRead = true };
        Guid id;
        await using (var db = database.Context(owner)) id = await CreateVenueAccount(db, owner, reader);
        using var cts = new CancellationTokenSource();
        await using (var db = database.Context(owner))
        {
            var run = RefreshAsync(db, owner, reader, id, clock, cts.Token);
            await reader.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        await using var verify = database.Context(owner);
        var account = await verify.Accounts.AsNoTracking().SingleAsync();
        Assert.Equal(("not-synced", null), (account.SyncStatus, account.LastSyncError));
        await AssertReleasedWithCooldownAsync(verify, clock);
    }

    [PostgresFact]
    public async Task Cancelling_after_the_snapshot_was_applied_records_the_order_coverage_notice()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var clock = new Clock();
        var reader = new ControlledReader();
        var id = await SyncedAccountAsync(database, owner, reader, clock);

        reader.BlockOrders = true;
        using var cts = new CancellationTokenSource();
        await using (var db = database.Context(owner))
        {
            var run = RefreshAsync(db, owner, reader, id, clock, cts.Token);
            await reader.OrdersStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        await using var verify = database.Context(owner);
        var account = await verify.Accounts.AsNoTracking().SingleAsync();
        Assert.Equal("synced", account.SyncStatus);
        Assert.Equal(reader.Result.Snapshot.ObservedAtUtc, account.LastSyncedAtUtc);
        Assert.Contains("order coverage is unavailable because the order check did not complete", account.HistoryNotice);
        await AssertReleasedWithCooldownAsync(verify, clock);
    }

    [PostgresFact]
    public async Task A_venue_timeout_without_caller_cancellation_is_still_a_sync_failure()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var clock = new Clock();
        var reader = new ControlledReader();
        var id = await SyncedAccountAsync(database, owner, reader, clock);

        // HttpClient.Timeout surfaces as TaskCanceledException while the caller's token is not cancelled.
        reader.ReadFailure = new TaskCanceledException("The request timed out.");
        await using (var db = database.Context(owner))
            Assert.Equal(502, (await Assert.ThrowsAsync<ProblemException>(() => RefreshAsync(db, owner, reader, id, clock, default))).StatusCode);
        await using (var verify = database.Context(owner))
        {
            var account = await verify.Accounts.AsNoTracking().SingleAsync();
            Assert.Equal(("error", "The venue refresh failed. Try again later."), (account.SyncStatus, account.LastSyncError));
            await AssertReleasedWithCooldownAsync(verify, clock);
        }

        clock.Now += Cooldown + TimeSpan.FromSeconds(1);
        reader.ReadFailure = new VenueReadException("Venue deadline exceeded.");
        await using (var db = database.Context(owner))
            Assert.Equal(502, (await Assert.ThrowsAsync<ProblemException>(() => RefreshAsync(db, owner, reader, id, clock, default))).StatusCode);
        await using (var verify = database.Context(owner))
            Assert.Equal("error", (await verify.Accounts.AsNoTracking().SingleAsync()).SyncStatus);
    }

    private static async Task<Guid> SyncedAccountAsync(CoreDatabase database, Guid owner, ControlledReader reader, Clock clock)
    {
        Guid id;
        await using (var db = database.Context(owner)) id = await CreateVenueAccount(db, owner, reader);
        await using (var db = database.Context(owner))
            Assert.Equal("synced", (await RefreshAsync(db, owner, reader, id, clock, default)).SyncStatus);
        clock.Now += Cooldown + TimeSpan.FromSeconds(1);
        reader.Advance();
        return id;
    }

    private static async Task AssertReleasedWithCooldownAsync(VesselDbContext db, Clock clock)
    {
        var row = await db.Set<MonitorAccount>().AsNoTracking().SingleAsync();
        Assert.Null(row.LeaseHolder);
        Assert.Null(row.LeaseUntilUtc);
        Assert.Equal(clock.Now + Cooldown, row.NextManualAllowedAtUtc);
    }

    private static async Task<Guid> CreateVenueAccount(VesselDbContext db, Guid owner, ControlledReader reader)
    {
        var service = new WorkspaceService(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner),
            TestVenues.With(reader), TestUnitOfWork.For(db));
        var portfolio = await service.CreatePortfolioAsync(new("Trading"), default);
        return (await service.CreateAccountAsync(new(portfolio.Id, "Hyperliquid", "hyperliquid",
            "0x" + Guid.NewGuid().ToString("N") + "00000000"), default)).Id;
    }

    /// <summary>POST /api/accounts/{id}/sync with a real refresh cooldown and a controllable clock.</summary>
    private static async Task<AccountDto> RefreshAsync(VesselDbContext db, Guid owner, ControlledReader reader, Guid id,
        Clock clock, CancellationToken ct)
    {
        var venues = TestVenues.With(reader);
        var work = TestUnitOfWork.For(db);
        var options = new MonitorOptions { ManualCooldown = Cooldown };
        var coordinator = new MonitorCoordinator(MonitorTestPaths.Store(db, work, options), new WorkspaceStore(db, work),
            MonitorTestPaths.Execution(db, venues, time: clock), venues, new NoCredentials(), work, new NoEvents(),
            new NoCommands(), new CoreOwner(owner), options, clock);
        if (!await coordinator.RunAsync(await coordinator.AdmitRefreshAsync(id, "test-" + Guid.NewGuid().ToString("N"), ct), ct))
            throw new ProblemException(502, MonitorTestPaths.RefreshFailed);
        return await new WorkspaceService(new WorkspaceStore(db, work), new CoreOwner(owner), venues, work).AccountAsync(id, ct);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ControlledReader : IPerpetualVenueReader, IVenueOrderReader
    {
        private readonly FixtureReader fixture = new();
        public string VenueId => "hyperliquid";
        public bool BlockRead { get; set; }
        public bool BlockOrders { get; set; }
        public Exception? ReadFailure { get; set; }
        public TaskCompletionSource ReadStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OrdersStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PerpetualVenueReadResult Result => fixture.Result;

        /// <summary>Each refresh must observe later than the retained one; the started signals are re-armed.</summary>
        public void Advance()
        {
            fixture.Result = fixture.Result with
            {
                Snapshot = fixture.Result.Snapshot with { ObservedAtUtc = fixture.Result.Snapshot.ObservedAtUtc.AddMinutes(1) }
            };
            ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            OrdersStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) => fixture.ReadInstrumentsAsync(ct);

        public async Task<PerpetualVenueReadResult> ReadAsync(string publicAddress, CancellationToken ct)
        {
            ReadStarted.TrySetResult();
            if (ReadFailure is not null) throw ReadFailure;
            if (BlockRead) await Task.Delay(Timeout.Infinite, ct);
            return fixture.Result;
        }

        public async Task<VenueOrderReadResult> ReadOrdersAsync(string publicAddress, CancellationToken ct)
        {
            OrdersStarted.TrySetResult();
            if (BlockOrders) await Task.Delay(Timeout.Infinite, ct);
            return new([], fixture.Result.Snapshot.ObservedAtUtc, "Open orders only.");
        }
    }

    private sealed class NoCredentials : IAccountCredentialReader
    {
        public Task<string?> ReadAsync(Guid accountId, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<CredentialAttempt?> ReadAttemptAsync(Guid accountId, CancellationToken ct) => Task.FromResult<CredentialAttempt?>(null);
        public Task MarkRefusedAsync(Guid accountId, string attemptedRevision, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoCommands : IMonitorCommandSink
    {
        public Task EnqueueAsync(MonitorAccountCommand command, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoEvents : IVesselEventSink
    {
        public Task AppendAsync(VesselEventDraft draft, CancellationToken ct) => Task.CompletedTask;
        public Task SetRunAsync(Guid runId, string trigger, CancellationToken ct) => Task.CompletedTask;
        public Task EnsureMonitoringBaselineAsync(Guid accountId, CancellationToken ct) => Task.CompletedTask;
    }
}
