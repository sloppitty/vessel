using System.Reflection;
using Vessel.Application.Activity;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence;

public readonly record struct ActivitySummaryKey(Guid OwnerId, long Revision, Guid? AccountId, string Accounts, string TradingMode = "real");

/// <summary>Process-local, size-bounded LRU. Failed and abandoned computations never enter the cache.</summary>
public sealed class ActivitySummaryCache(long capacity = 256 * 1024 * 1024, int entryLimit = 64)
{
    private readonly object gate = new();
    private readonly Dictionary<ActivitySummaryKey, Flight> flights = [];
    private readonly Dictionary<ActivitySummaryKey, LinkedListNode<Entry>> entries = [];
    private readonly LinkedList<Entry> lru = [];
    private long size;
    private static readonly Dictionary<Type, PropertyInfo[]> StringProperties = new[]
    {
        typeof(Account), typeof(ImportedFill), typeof(AccountSnapshot), typeof(AccountPosition), typeof(AccountStablecoin),
        typeof(FundingPayment), typeof(AccountFundingState), typeof(LeverageObservation),
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

    private static long Charge(ActivitySummaryKey key, ActivitySummaries value)
    {
        // Conservative retained-graph charge: entities, allocation rows, list/dictionary slots and every
        // variable-length string. Lazy constituent DTOs are not materialized just to measure the cache.
        static long Row(object row)
        {
            var bytes = 768L;
            foreach (var property in StringProperties[row.GetType()])
                if (property.GetValue(row) is string text) bytes += 2L * text.Length;
            return bytes;
        }
        var facts = value.Facts;
        return 4096L + 2L * key.Accounts.Length + facts.Accounts.Sum(Row) + facts.Fills.Sum(Row)
            + facts.Snapshots.Sum(s => Row(s) + s.Positions.Sum(Row) + s.Stablecoins.Sum(Row))
            + facts.FundingPayments.Sum(Row) + facts.FundingStates.Sum(Row) + 128L * facts.FillPlayIds.Count
            + facts.LeverageObservations.Sum(Row)
            // Each fill allocation can also retain a timestamp/leverage pair (at most one pair per allocation).
            + value.Operations.Sum(o => Row(o) + 288L * o.FillCount + o.OtherFees.Sum(Row)
                + o.Coverage.Sum(s => 64L + 2L * s.Length) + 16L * o.FundingPaymentCount)
            + value.Coverage.Sum(Row) + 16L * value.UnassignedFunding.Count;
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
