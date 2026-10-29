using Vessel.Application.Activity;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public partial class ActivityOperationsTestsSupport : ActivityOperationsTestsValues
{

    internal static Account Account(Guid id, bool enabled = true, Guid? portfolio = null)
    {
        var account = new Account(id, Owner, "manual", "Fixture");
        account.UpdateSettings(account.Name, portfolio, enabled);
        return account;
    }
    internal sealed class Store(ActivityFacts facts, IReadOnlyDictionary<Guid, string>? titles = null) : IActivityStore
    {
        public ActivityFacts Facts => facts;
        public List<Guid> RequestedTitles { get; } = [];
        public Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null) => Task.FromResult(facts);
        public Task<IReadOnlyDictionary<Guid, string>> PlayTitlesAsync(IReadOnlyCollection<Guid> playIds, CancellationToken ct)
        {
            RequestedTitles.AddRange(playIds);
            return Task.FromResult<IReadOnlyDictionary<Guid, string>>((titles ?? new Dictionary<Guid, string>())
                .Where(t => playIds.Contains(t.Key)).ToDictionary());
        }
    }
    internal static MemoryWorkspaceStore Workspace(ActivityFacts facts, params Portfolio[] portfolios)
    {
        var workspace = new MemoryWorkspaceStore(Owner);
        workspace.Accounts.AddRange(facts.Accounts); workspace.Portfolios.AddRange(portfolios);
        return workspace;
    }
}
