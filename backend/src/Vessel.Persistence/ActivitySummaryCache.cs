using System.Reflection;
using Microsoft.Extensions.Logging;
using Vessel.Application.Activity;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence;

/// <summary>
/// One cached aggregate. <paramref name="Revision"/> is the owner's execution revision (issue #107): equal values hold equal
/// retained history, so an entry is rebased onto newer observations rather than read again.
/// </summary>
public readonly record struct ActivitySummaryKey(Guid OwnerId, long Revision, Guid? AccountId, string Accounts, string TradingMode = "real");

/// <summary>Process-local, size-bounded LRU. Failed and abandoned computations never enter the cache.</summary>
public sealed class ActivitySummaryCache(long capacity = 256 * 1024 * 1024, int entryLimit = 64, ILogger<ActivitySummaryCache>? logger = null)
{
    private readonly object gate = new();
    private readonly Dictionary<ActivitySummaryKey, Flight> flights = [];
    private readonly Dictionary<ActivitySummaryKey, LinkedListNode<Entry>> entries = [];
    private readonly LinkedList<Entry> lru = [];
    private long size;
    private long warnedAt;
    private static readonly Dictionary<Type, PropertyInfo[]> StringProperties = new[]
    {
        typeof(Account), typeof(ImportedFill), typeof(AccountSnapshot), typeof(AccountPosition), typeof(AccountStablecoin),
        typeof(FundingPayment), typeof(AccountFundingState), typeof(LeverageObservation), typeof(ActivityOmission), typeof(ForcedEvent),
        typeof(ActivityOperationDto), typeof(OperationFeeDto), typeof(ActivityCoverageDto)
    }.ToDictionary(type => type, type => type.GetProperties().Where(p => p.PropertyType == typeof(string)).ToArray());

    public async Task<ActivitySummaries> ReadAsync(ActivitySummaryKey key,
        Func<CancellationToken, Task<ActivitySummaries>> compute, Func<ValueTask> disposeUnused, CancellationToken ct)
    {
        Flight? flight = null;
        ActivitySummaries? hit = null;
        var producer = false;
        lock (gate)
        {
            if (entries.TryGetValue(key, out var node))
            {
                lru.Remove(node);
                lru.AddFirst(node);
                hit = node.Value.Value;
            }
            else
            {
                if (!flights.TryGetValue(key, out flight))
                {
                    flight = new();
                    flights.Add(key, flight);
                    producer = true;
                }
                flight.Waiters++;
            }
        }
        if (producer) _ = RunAsync(key, flight!, compute);
        try
        {
            if (!producer) await disposeUnused();
            if (hit is not null) { ct.ThrowIfCancellationRequested(); return hit; }
            return await flight!.Completion.Task.WaitAsync(ct);
        }
        finally
        {
            var cancel = false;
            lock (gate)
            {
                if (flight is not null && --flight.Waiters == 0 && !flight.Completion.Task.IsCompleted)
                {
                    // Remove before cancelling so a new caller cannot join an abandoned flight.
                    if (flights.GetValueOrDefault(key) == flight) flights.Remove(key);
                    flight.CancellationPending = cancel = true;
                }
            }
            if (cancel)
            {
                // Npgsql cancellation can synchronously contact PostgreSQL. Never hold the shared gate here.
                try { flight!.Cancellation.Cancel(); }
                catch (AggregateException) { } // A callback failure must not replace the waiter's cancellation.
                finally
                {
                    lock (gate)
                    {
                        flight!.CancellationPending = false;
                        if (flight.Finished) flight.Cancellation.Dispose();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Issue #107: a cached aggregate over several accounts, at this execution revision, that holds
    /// <paramref name="accountId"/>; an account's read narrows it instead of reading the account's history again.
    /// </summary>
    public ActivitySummaries? FindContaining(Guid ownerId, long revision, string tradingMode, Guid accountId)
    {
        lock (gate)
        {
            foreach (var entry in lru)
                if (entry.Key.OwnerId == ownerId && entry.Key.Revision == revision && entry.Key.TradingMode == tradingMode &&
                    entry.Key.AccountId is null && entry.Value.Facts.Accounts.Any(a => a.Id == accountId))
                    return entry.Value;
        }
        return null;
    }

    /// <summary>
    /// Issue #107: keeps an entry's aggregate rebased onto newer observations, so later reads compare with the latest. Only
    /// replaces the value it was rebased from, and never with an older revision. A rebase that shares the operations keeps
    /// the charge: execution facts, which dominate it, are shared, and observations are one row per account. A rebase that
    /// walked the history again is measured again, since newer snapshots can split it into more operations.
    /// </summary>
    public void Replace(ActivitySummaryKey key, ActivitySummaries from, ActivitySummaries to)
    {
        long? charge = ReferenceEquals(from.Operations, to.Operations) ? null : Charge(key, to);
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var node) || !ReferenceEquals(node.Value.Value, from) ||
                (to.Facts.Revision ?? 0) <= (from.Facts.Revision ?? 0))
                return;
            if (charge is not { } measured)
            {
                node.Value = node.Value with { Value = to };
                return;
            }
            size -= node.Value.Size;
            if (measured > capacity)
            {
                entries.Remove(key);
                lru.Remove(node);
            }
            else
            {
                node.Value = node.Value with { Value = to, Size = measured };
                size += measured;
                // The entry was just read: it is the most recent, and eviction never reaches it before the others.
                lru.Remove(node);
                lru.AddFirst(node);
                while (size > capacity && lru.Last is { } last && last != node)
                {
                    entries.Remove(last.Value.Key);
                    size -= last.Value.Size;
                    lru.RemoveLast();
                }
            }
        }
        if (charge > capacity) WarnOversized(to, charge.Value);
    }

    // An aggregate larger than the whole cache is computed again on every read; say so, at most hourly.
    private void WarnOversized(ActivitySummaries value, long charge)
    {
        if (Environment.TickCount64 is var now &&
            (Interlocked.Read(ref warnedAt) is var warned && (warned == 0 || now - warned >= 3_600_000)) &&
            Interlocked.CompareExchange(ref warnedAt, now, warned) == warned)
            logger?.LogWarning("An Activity aggregate of {FillCount} fills and {OperationCount} operations (estimated {ChargeBytes} bytes) " +
                "exceeds the Activity cache capacity of {CapacityBytes} bytes and is not cached; every read recomputes it. " +
                "Raise Vessel:Activity:CacheCapacityBytes if the host has memory to spare.",
                value.Facts.Fills.Count, value.Operations.Count, charge, capacity);
    }

    private async Task RunAsync(ActivitySummaryKey key, Flight flight, Func<CancellationToken, Task<ActivitySummaries>> compute)
    {
        try
        {
            var value = await compute(flight.Cancellation.Token);
            var charge = Charge(key, value);
            lock (gate)
            {
                if (!flight.Cancellation.IsCancellationRequested && flight.Waiters > 0 && charge <= capacity)
                {
                    while (lru.Last is { } last && (size + charge > capacity || entries.Count >= entryLimit))
                    {
                        entries.Remove(last.Value.Key);
                        size -= last.Value.Size;
                        lru.RemoveLast();
                    }
                    if (entryLimit > 0)
                    {
                        var node = lru.AddFirst(new Entry(key, value, charge));
                        entries.Add(key, node);
                        size += charge;
                    }
                }
                if (flights.GetValueOrDefault(key) == flight) flights.Remove(key);
                flight.Completion.TrySetResult(value);
            }
            if (charge > capacity) WarnOversized(value, charge);
        }
        catch (OperationCanceledException)
        {
            lock (gate)
            {
                if (flights.GetValueOrDefault(key) == flight) flights.Remove(key);
                flight.Completion.TrySetCanceled();
            }
        }
        catch (Exception error)
        {
            lock (gate)
            {
                if (flights.GetValueOrDefault(key) == flight) flights.Remove(key);
                flight.Completion.TrySetException(error);
                // Observe even when every waiter has already cancelled.
                _ = flight.Completion.Task.Exception;
            }
        }
        finally
        {
            lock (gate)
            {
                flight.Finished = true;
                if (!flight.CancellationPending) flight.Cancellation.Dispose();
            }
        }
    }

    // Issue #107: the strings ActivityStore.Share keeps one copy of, charged once per distinct value.
    private static readonly HashSet<(Type, string)> SharedStrings =
    [
        .. new[] { "ContractId", "VenueContractId", "Side", "Direction", "PositionEffect", "FeeToken", "FeeBasis", "PnlBasis" }
            .Select(name => (typeof(ImportedFill), name)),
        (typeof(FundingPayment), "ContractId"), (typeof(FundingPayment), "VenueContractId"), (typeof(FundingPayment), "Asset"),
        (typeof(LeverageObservation), "ContractId"), (typeof(LeverageObservation), "VenueContractId")
    ];

    private static long Charge(ActivitySummaryKey key, ActivitySummaries value)
    {
        // Conservative retained-graph charge: entities, allocation rows, list/dictionary slots and every
        // variable-length string. Lazy constituent DTOs are not materialized just to measure the cache.
        var shared = new HashSet<string>(StringComparer.Ordinal);
        long Row(object row)
        {
            var bytes = 768L;
            foreach (var property in StringProperties[row.GetType()])
                if (property.GetValue(row) is string text && (!SharedStrings.Contains((row.GetType(), property.Name)) || shared.Add(text)))
                    bytes += 2L * text.Length;
            return bytes;
        }
        var facts = value.Facts;
        return 4096L + 2L * key.Accounts.Length + facts.Accounts.Sum(Row) + facts.Fills.Sum(Row)
            + facts.Snapshots.Sum(s => Row(s) + s.Positions.Sum(Row) + s.Stablecoins.Sum(Row))
            + facts.FundingPayments.Sum(Row) + facts.FundingStates.Sum(Row) + 128L * facts.FillPlayIds.Count
            + facts.LeverageObservations.Sum(Row) + facts.Omissions.Sum(Row) + facts.ForcedEvents.Sum(Row)
            + 64L * (facts.ForcedEventCounts?.Count ?? 0)
            // Each fill allocation can also retain a timestamp/leverage pair (at most one pair per allocation).
            + value.Operations.Sum(o => Row(o) + 288L * o.FillCount + o.OtherFees.Sum(Row)
                + o.Coverage.Sum(s => 64L + 2L * s.Length) + 16L * o.FundingPaymentCount)
            + value.Coverage.Sum(Row) + 16L * value.UnassignedFunding.Count
            // Phase 2, item 9b's proof index, built on first use: fills by partition, order and Play, with prefix sums.
            + 160L * facts.Fills.Count;
    }

    private sealed class Flight
    {
        public int Waiters;
        public bool CancellationPending, Finished;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<ActivitySummaries> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record Entry(ActivitySummaryKey Key, ActivitySummaries Value, long Size);
}
