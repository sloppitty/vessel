using Vessel.Application.Activity;
using Vessel.Application.Overview;

namespace Vessel.Tests;

public partial class OverviewSummaryTestsSupport
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    internal readonly Guid owner = Guid.NewGuid();
    internal readonly MemoryWorkspaceStore store;
    internal readonly OverviewSummaryService service;
    internal readonly SummaryActivityStore activityStore;
    public OverviewSummaryTestsSupport()
    {
        store = new(owner);
        activityStore = new(store, owner);
        service = new(store, new(store, new CoreOwner(owner), TestVenues.With(), TestUnitOfWork.None), new(activityStore, store, new Clock()), new Clock());
    }
    internal sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    internal sealed class SummaryActivityStore(MemoryWorkspaceStore store, Guid owner) : IActivityStore
    {
        public List<IReadOnlySet<Guid>?> Reads { get; } = [];
        public Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null)
        {
            Reads.Add(accountIds);
            var accounts = store.Accounts.Where(a => a.OwnerId == owner && a.IsEnabled && (accountId is null || a.Id == accountId) &&
                (accountIds is null || accountIds.Contains(a.Id))).ToList();
            var ids = accounts.Select(a => a.Id).ToHashSet();
            return Task.FromResult(new ActivityFacts(accounts, store.Fills.Where(f => f.OwnerId == owner && ids.Contains(f.AccountId)).ToList(),
                store.Snapshots.Where(s => s.OwnerId == owner && ids.Contains(s.AccountId)).ToList(), new Dictionary<Guid, Guid>()));
        }
        public Task<IReadOnlyDictionary<Guid, string>> PlayTitlesAsync(IReadOnlyCollection<Guid> playIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    }
}
