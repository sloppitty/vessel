using Vessel.Application.Common;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>Execution facts of one account, kept in memory for owner linking and lifecycle checks.</summary>
internal sealed class MemoryExecutionStore(MemoryPlayStore plays) : IPlayExecutionStore
{
    public List<ImportedOrder> Orders { get; } = [];
    public List<ImportedFill> Fills { get; } = [];
    public List<PlayOrderLink> Links { get; } = [];
    public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => plays.AccountAsync(id, ct);
    public Task<List<Play>> ActivePlaysAsync(Guid accountId, CancellationToken ct, bool settling = false) =>
        Task.FromResult(plays.Plays.Where(p => p.AccountId == accountId && (ExecutionMatcher.IsActive(p) || settling && p.Status == PlayStatus.Unresolved)).ToList());
    public Task<List<ImportedOrder>> OrdersAsync(Guid accountId, CancellationToken ct) => Task.FromResult(Orders.Where(o => o.AccountId == accountId).ToList());
    public Task UpsertOrdersAsync(Account account, IReadOnlyList<VenueOrder> orders, DateTimeOffset observedAt, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<List<PlayOrderLink>> LinksAsync(Guid accountId, CancellationToken ct) => Task.FromResult(Links.Where(l => l.AccountId == accountId).ToList());
    public Task<List<ImportedFill>> FillsAsync(Guid accountId, IReadOnlyCollection<OrderKey> orders, CancellationToken ct) =>
        Task.FromResult(Fills.Where(f => f.AccountId == accountId && orders.Contains(OrderKey.Of(f))).OrderBy(f => f.OccurredAtUtc).ToList());
    /// <summary>The retained order with this venue ID, for building links in tests.</summary>
    public ImportedOrder Order(string externalOrderId) => Orders.Single(o => o.ExternalOrderId == externalOrderId);
    public void Add(PlayOrderLink link) => Links.Add(link);
    public void Add(PlayStatusChange change) => plays.Add(change);
    public Task ForgetWatchesAsync(Guid playId, CancellationToken ct) => Task.CompletedTask;
    public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<T> WithAccountLockAsync<T>(Guid accountId, Func<Task<T>> action, CancellationToken ct) => action();
}
