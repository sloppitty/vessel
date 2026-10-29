using Vessel.Application.Activity;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public partial class PaperReportingTestsSupport : PaperReportingTestsValues
{

    internal static ActivityFacts Facts(params (Account Account, Portfolio Portfolio, ClosedPlayFacts Facts)[] papers) =>
        new(papers.Select(p => p.Account).ToList(), [], [], new Dictionary<Guid, Guid>())
        { PaperPlays = papers.Select(p => p.Facts.Paper!).ToList() };

    internal sealed class ActivityRows(ActivityFacts facts) : IActivityStore
    {
        public Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null) => Task.FromResult(facts);
        public Task<IReadOnlyDictionary<Guid, string>> PlayTitlesAsync(IReadOnlyCollection<Guid> playIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    }
    internal sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => At.AddDays(1); }
}
