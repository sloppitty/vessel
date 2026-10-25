using Vessel.Application.Common;
using Vessel.Application.Credentials;
using Vessel.Application.Events;
using Vessel.Application.Monitoring;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// The production account Refresh and Play Check paths over a test context: monitor admission, the lease, the
/// fenced venue reads and the guarded apply, with a real <see cref="PlayExecutionService"/> as the importer.
/// </summary>
internal static class MonitorTestPaths
{
    public const string RefreshFailed = "The venue refresh failed or was superseded by changed account settings. Try again later.";

    public static MonitorStore Store(VesselDbContext db, UnitOfWork work, MonitorOptions? options = null) =>
        new(db, work, MonitorServerState.For(db), new ConfiguredOwnerDirectory(db.CurrentOwnerId), options);

    public static MonitorOptions Options() => new() { ManualCooldown = TimeSpan.Zero };

    public static PlayExecutionService Execution(VesselDbContext db, IVenueRegistry venues,
        IAccountCredentialReader? credentials = null, TimeProvider? time = null) =>
        new(new PlayStore(db, TestUnitOfWork.For(db)), new ExecutionStore(db, TestUnitOfWork.For(db)), venues,
            time ?? TimeProvider.System, TestUnitOfWork.For(db), credentials);

    public static MonitorCoordinator Coordinator(VesselDbContext db, Guid owner, IVenueRegistry venues,
        IAccountCredentialReader? credentials = null, TimeProvider? time = null)
    {
        var work = TestUnitOfWork.For(db);
        var options = Options();
        time ??= TimeProvider.System;
        return new(new MonitorWorkerOnline(MonitorTestPaths.Store(db, work, options), time), new WorkspaceStore(db, work), Execution(db, venues, credentials, time), venues,
            credentials ?? new NoCredentials(), work, new NoEvents(), new NoCommands(), new CoreOwner(owner), options, time);
    }

    /// <summary>POST /api/accounts/{id}/sync: admit, run on the caller's lease, then read the account.</summary>
    public static async Task<AccountDto> RefreshAsync(VesselDbContext db, Guid owner, IVenueRegistry venues, Guid accountId,
        IAccountCredentialReader? credentials = null, CancellationToken ct = default, TimeProvider? time = null)
    {
        var coordinator = Coordinator(db, owner, venues, credentials, time);
        if (!await coordinator.RunAsync(await coordinator.AdmitRefreshAsync(accountId, "test-" + Guid.NewGuid().ToString("N"), ct), ct))
            throw new ProblemException(502, RefreshFailed);
        return await new WorkspaceService(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), venues,
            TestUnitOfWork.For(db)).AccountAsync(accountId, ct);
    }

    /// <summary>
    /// POST /plays/{id}/execution/check, then the worker's claim and run of that request. A run that does not complete
    /// (failed or superseded) throws unless <paramref name="expectCompleted"/> is false, so a fenced check never passes silently.
    /// </summary>
    public static async Task<PlayExecutionDto> CheckAsync(VesselDbContext db, Guid owner, IVenueRegistry venues, Guid playId,
        IAccountCredentialReader? credentials = null, bool expectCompleted = true, CancellationToken ct = default, TimeProvider? time = null)
    {
        var work = TestUnitOfWork.For(db);
        time ??= TimeProvider.System;
        var coordinator = Coordinator(db, owner, venues, credentials, time);
        var view = new MonitorExecutionView(new PlayStore(db, work), new WorkspaceStore(db, work), venues, coordinator);
        var accepted = await view.RequestAsync(playId, ct);
        var accountId = accepted.Monitor.AccountId;
        var lease = await MonitorTestPaths.Store(db, work, Options()).ClaimAsync(accountId, accepted.RequestId, "test-worker",
            time.GetUtcNow(), ct) ?? throw new InvalidOperationException("The accepted check could not be claimed.");
        if (!await coordinator.RunAsync(lease, ct) && expectCompleted)
            throw new InvalidOperationException("The monitor check failed or was superseded.");
        return await Execution(db, venues, credentials, time).GetAsync(playId, ct);
    }

    /// <summary>
    /// Each read observed strictly later than any before it, as successive real venue reads are. Checks discard
    /// a read that is not newer than the retained observation.
    /// </summary>
    public static IPerpetualVenueReader Advancing(IPerpetualVenueReader reader) => new AdvancingReader(reader);

    private static long lastObservation;

    private static DateTimeOffset NextObservation(DateTimeOffset after)
    {
        // Millisecond steps survive PostgreSQL's microsecond timestamps.
        var floor = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), after.ToUnixTimeMilliseconds() + 1);
        long previous, next;
        do
        {
            previous = Interlocked.Read(ref lastObservation);
            next = Math.Max(floor, previous + 1);
        } while (Interlocked.CompareExchange(ref lastObservation, next, previous) != previous);
        return DateTimeOffset.FromUnixTimeMilliseconds(next);
    }

    private sealed class AdvancingReader(IPerpetualVenueReader inner) : IPerpetualVenueReader
    {
        public string VenueId => inner.VenueId;
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) => inner.ReadInstrumentsAsync(ct);
        public async Task<PerpetualVenueReadResult> ReadAsync(string publicAddress, CancellationToken ct) =>
            Advance(await inner.ReadAsync(publicAddress, ct));
        public async Task<PerpetualVenueReadResult> ReadAsync(Vessel.Domain.Accounts.Account account, CancellationToken ct) =>
            Advance(await inner.ReadAsync(account, ct));
        private static PerpetualVenueReadResult Advance(PerpetualVenueReadResult result) =>
            result with { Snapshot = result.Snapshot with { ObservedAtUtc = NextObservation(result.Snapshot.ObservedAtUtc) } };
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
