using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Monitoring;
using Vessel.Application.Ownership;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Vessel.Tests")]

namespace Vessel.Worker;

/// <summary>Coalesces command wake-ups into at most one early scheduling pass.</summary>
public sealed class MonitorWakeup : IMonitorWakeup
{
    private readonly Channel<bool> signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite });
    private int pendingWaiters;
    public void Wake() => signals.Writer.TryWrite(true);

    /// <summary>Waiters created by <see cref="WaitAsync"/> that have not completed yet (diagnostics and tests).</summary>
    internal int PendingWaiters => Volatile.Read(ref pendingWaiters);

    /// <summary>
    /// A task that completes once a wake is available (or <paramref name="ct"/> is cancelled). Each call registers a
    /// channel waiter that stays until a wake or cancellation, so callers keep one across timer ticks.
    /// </summary>
    internal Task<bool> WaitAsync(CancellationToken ct)
    {
        var wait = signals.Reader.WaitToReadAsync(ct);
        if (wait.IsCompleted) return wait.AsTask();
        Interlocked.Increment(ref pendingWaiters);
        var task = wait.AsTask();
        _ = task.ContinueWith(static (_, state) => ((MonitorWakeup)state!).WaiterCompleted(), this,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    internal void Consume() => signals.Reader.TryRead(out _);
    private void WaiterCompleted() => Interlocked.Decrement(ref pendingWaiters);
}

/// <summary>
/// The scheduler's loop: one pass at start, then one per timer tick, and one early pass when a wake arrives
/// (a wake during a pass causes exactly one more pass). The wake waiter is kept across timer ticks and replaced only
/// once it has completed, so timer-only ticks never accumulate channel waiters.
/// </summary>
internal static class MonitorSchedulerLoop
{
    public static async Task RunAsync(TimeProvider time, TimeSpan period, MonitorWakeup wakeup,
        Func<CancellationToken, Task> pass, Action<Exception> failed, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(period, time);
        var tick = Task.FromResult(true);
        Task<bool>? wake = null;
        try
        {
            while (true)
            {
                if (tick.IsCompleted)
                {
                    if (!await tick) break;
                    tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                }
                wakeup.Consume();
                try { await pass(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) { failed(error); }
                // A waiter that completed before this pass's Consume() is stale: replace it. A fresh waiter completes
                // at once if a wake arrived during the pass, otherwise it waits (across timer ticks) for the next wake.
                if (wake is { IsCompleted: true }) wake = null;
                wake ??= wakeup.WaitAsync(stoppingToken);
                await Task.WhenAny(tick, wake);
                stoppingToken.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

/// <summary>
/// Every <see cref="MonitorOptions.SchedulerTick"/>, or early when a command arrives: discover accounts, claim due
/// rows and run up to the configured process concurrency. The heartbeat row and file advance only after such a productive pass.
/// TimeProvider and registry are normal DI inputs, including in isolated integration hosts.
/// </summary>
public sealed class MonitorScheduler(IOwnerScopeFactory scopes, IJournalOwnerDirectory owners, TimeProvider time, MonitorOptions options,
    MonitorWakeup wakeup, ILogger<MonitorScheduler> logger, ISystemMonitorStore candidates, IServerStateStore server,
    WorkerSettings worker) : BackgroundService
{
    private readonly string workerId = Guid.NewGuid().ToString("N");
    private readonly List<Task> running = [];
    // One scan cycle walks the work due by its start in key order, across ticks. Skipped accounts (locked, capped,
    // failed) are revisited by the next cycle; work rescheduled during a cycle lands after its bound and waits too.
    // Candidate pages hide no row, so a tick may stop after a bounded number of pages and continue next tick.
    private const int CandidatePageSize = 100;
    internal const int CandidatePagesPerTick = 10;
    private DueMonitorKey? continuation;
    private DateTimeOffset? cycleBound;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await MonitorSchedulerLoop.RunAsync(time, MonitorOptions.SchedulerTick, wakeup, TickAsync,
                error => logger.LogWarning("Monitor scheduling failed with {ErrorType}. It will retry on the next tick.",
                    error.GetType().Name), stoppingToken);
        }
        finally { await Task.WhenAll(running); }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        running.RemoveAll(t => t.IsCompleted);
        var statuses = new Dictionary<Guid, JournalOwnerStatus>();
        var failed = new HashSet<Guid>();
        var productive = new HashSet<Guid>();
        var sawActiveOwner = false;
        // Phase 2, item 9c: venues whose funding the slow schedule reads, by capability (a funding reader), never by name.
        string[]? fundingVenues = null;
        Guid? after = null;
        while (true)
        {
            var page = await owners.ActiveOwnersAsync(after, 100, ct);
            if (page.Count == 0) break;
            foreach (var owner in page)
            {
                after = owner;
                try
                {
                    if (await StatusAsync(owner) != JournalOwnerStatus.Active) continue;
                    sawActiveOwner = true;
                    await using var scope = scopes.Create(owner);
                    var venues = scope.Services.GetRequiredService<IVenueRegistry>();
                    fundingVenues ??= venues.Descriptors.Where(v => v.Capabilities.Sync && venues.Funding(v.Id) is not null)
                        .Select(v => v.Id).ToArray();
                    await scope.Services.GetRequiredService<MonitorCoordinator>().DiscoverAsync(venues.Descriptors
                        .Where(v => v.Capabilities is { Sync: true, Orders: true }).Select(v => v.Id).ToArray(), time.GetUtcNow(), ct);
                    productive.Add(owner);
                }
                catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    productive.Remove(owner);
                    failed.Add(owner);
                    Warn(owner, error);
                }
            }
        }
        var capped = new HashSet<Guid>();
        var cappedVenues = new HashSet<string>(StringComparer.Ordinal);
        var wrapped = false;
        // Phase 2, item 6 (Sol round 1): protection work is claimed first. Due accounts with only kept history behind them
        // wait for the rest of the tick, and leave one of this process's worker slots free when it has more than one.
        var keptHistory = new List<DueMonitorCandidate>();
        for (var pages = 0; running.Count < options.WorkerConcurrency && pages < CandidatePagesPerTick; pages++)
        {
            cycleBound ??= time.GetUtcNow();
            var page = await candidates.DueCandidatesAsync(continuation, cycleBound.Value, CandidatePageSize, ct);
            if (page.Count == 0)
            {
                // The cycle is exhausted. Start a fresh one at once (at most once per tick) unless it had just begun.
                var fresh = continuation is null;
                (continuation, cycleBound) = (null, null);
                if (fresh || wrapped) break;
                wrapped = true;
                continue;
            }
            foreach (var candidate in page)
            {
                continuation = candidate.Key;
                var owner = candidate.OwnerId;
                try
                {
                    if (cappedVenues.Contains(candidate.VenueId) || failed.Contains(owner) || capped.Contains(owner) || await StatusAsync(owner) != JournalOwnerStatus.Active) continue;
                    await using var scope = scopes.Create(owner);
                    var store = scope.Services.GetRequiredService<IMonitorStore>();
                    if (await store.OwnerAtCapacityAsync(time.GetUtcNow(), ct)) { capped.Add(owner); continue; }
                    if (await store.VenueAtCapacityAsync(candidate.VenueId, time.GetUtcNow(), ct))
                    { cappedVenues.Add(candidate.VenueId); continue; }
                    // Claim rechecks status, deadlines and both budgets, locking just this account and venue.
                    if (await store.ClaimAsync(candidate.AccountId, null, workerId, time.GetUtcNow(), ct, protectionOnly: true) is { } lease)
                        running.Add(RunAsync(owner, lease, ct));
                    else if (keptHistory.Count < CandidatePagesPerTick * CandidatePageSize) keptHistory.Add(candidate);
                }
                catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    productive.Remove(owner);
                    failed.Add(owner);
                    Warn(owner, error);
                }
                if (running.Count >= options.WorkerConcurrency) break;
            }
        }
        var keptSlots = options.WorkerConcurrency - (options.WorkerConcurrency > 1 ? 1 : 0);
        foreach (var candidate in keptHistory)
        {
            if (running.Count >= keptSlots) break;
            await ClaimBehindAsync(candidate, (store, now) => store.ClaimAsync(candidate.AccountId, null, workerId, now, ct));
        }
        // Phase 2, item 6b: owner-started backfills read last, in runs of their own, leaving the same slot free.
        if (running.Count < keptSlots)
            foreach (var candidate in await candidates.DueBackfillsAsync(CandidatePageSize, ct))
            {
                if (running.Count >= keptSlots) break;
                await ClaimBehindAsync(candidate, (store, now) => store.ClaimBackfillAsync(candidate.AccountId, workerId, now, ct));
            }
        // Phase 2, item 9c: settled funding on its slow schedule comes after everything else, leaving the same slot free.
        if (running.Count < keptSlots && fundingVenues is { Length: > 0 })
            foreach (var candidate in await candidates.DueFundingAsync(fundingVenues, CandidatePageSize, ct))
            {
                if (running.Count >= keptSlots) break;
                await ClaimBehindAsync(candidate, (store, now) => store.ClaimFundingAsync(candidate.AccountId, workerId, now, ct));
            }
        // An empty/all-suspended directory or no due accounts is healthy: the pass still did real discovery.
        // If every active owner's work failed, do not turn mere scheduler liveness into a healthy monitor.
        if (productive.Count > 0 || !sawActiveOwner && failed.Count == 0) await HeartbeatAsync(ct);

        async Task ClaimBehindAsync(DueMonitorCandidate candidate, Func<IMonitorStore, DateTimeOffset, Task<MonitorLease?>> claim)
        {
            var owner = candidate.OwnerId;
            try
            {
                if (cappedVenues.Contains(candidate.VenueId) || failed.Contains(owner) || capped.Contains(owner) || await StatusAsync(owner) != JournalOwnerStatus.Active) return;
                await using var scope = scopes.Create(owner);
                if (await claim(scope.Services.GetRequiredService<IMonitorStore>(), time.GetUtcNow()) is { } lease)
                    running.Add(RunAsync(owner, lease, ct));
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                productive.Remove(owner);
                failed.Add(owner);
                Warn(owner, error);
            }
        }

        async Task<JournalOwnerStatus> StatusAsync(Guid owner)
        {
            if (!statuses.TryGetValue(owner, out var status)) statuses.Add(owner, status = await owners.StatusAsync(owner, ct));
            return status;
        }
    }

    private void Warn(Guid owner, Exception error) =>
        logger.LogWarning("Monitor scheduling for owner {OwnerId} failed with {ErrorType}. Later owners will still be checked.",
            owner, error.GetType().Name);

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        var file = options.HeartbeatFile;
        var now = time.GetUtcNow();
        try
        {
            // "<unix seconds> <timeout seconds>": health needs no configuration or journal information.
            var temporary = file + "." + workerId + ".new";
            await File.WriteAllTextAsync(temporary, string.Create(CultureInfo.InvariantCulture,
                $"{now.ToUnixTimeSeconds()} {(long)options.HeartbeatTimeout.TotalSeconds}"), ct);
            File.Move(temporary, file, overwrite: true);
            await server.RecordHeartbeatAsync(worker.Deployment!, now, ct);
        }
        catch
        {
            try { File.Delete(file); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private async Task RunAsync(Guid owner, MonitorLease lease, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.Create(owner);
            await scope.Services.GetRequiredService<MonitorRunExecutor>().RunAsync(lease, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogWarning("Monitor run {RequestId} for account {AccountId} failed with {ErrorType}. Lease recovery will retry it.",
                lease.RequestId, lease.AccountId, error.GetType().Name);
        }
    }
}
