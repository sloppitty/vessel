using Vessel.Application.Common;
using Vessel.Application.Monitoring;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;
internal sealed class DiscrepancyMemoryStore : IMonitorDiscrepancyStore
{
    public Account Account = null!;
    public List<Play> Plays = [];
    public List<PlayOrderLink> Links = [];
    public List<ImportedOrder> Orders = [];
    public List<ImportedFill> Fills = [];
    public List<PlayDiscrepancy> Rows = [];
    public bool Complete = true, AssignmentLinksComplete = true;
    public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => Task.FromResult(id == Account.Id ? Account : null);
    public Task<Play?> PlayAsync(Guid id, CancellationToken ct) => Task.FromResult(Plays.SingleOrDefault(x => x.Id == id));
    public HashSet<string> TruncatedFillContracts = [];
    public DateTimeOffset? RecentSince;
    public Task<MonitorDiscrepancyFacts> LoadAsync(Guid id, DateTimeOffset recentSince, IReadOnlyCollection<OrderKey> resting, CancellationToken ct)
    {
        RecentSince = recentSince;
        return Task.FromResult(new MonitorDiscrepancyFacts(Plays, Links, Orders, Fills, Complete, Links, AssignmentLinksComplete, TruncatedFillContracts));
    }
    public Task<IReadOnlyList<PlayDiscrepancy>> RecordsAsync(Guid id, CancellationToken ct) => Task.FromResult<IReadOnlyList<PlayDiscrepancy>>(Rows.Where(x => x.AccountId == id).ToArray());
    public void Add(PlayDiscrepancy row) => Rows.Add(row);
    public Task LockAsync(Guid accountId, CancellationToken ct) => Task.CompletedTask;
}
