using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Application.MarketData;
using Vessel.Application.Overview;
using Vessel.Application.Ownership;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Review;
using Vessel.Application.Risk;
using Vessel.Application.Sizing;
using Vessel.Application.Workspace;
using Vessel.Domain.Plays;
using Vessel.Domain.Risk;
using Vessel.Domain.Sizing;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>The five screens of the golden journal, built over one set of stores.</summary>
internal sealed record GoldenJournalScreens(ActivityService Activity, OverviewSummaryService Overview, ReviewService Review,
    SizingService Sizing, OpenRiskService OpenRisk)
{
    public static GoldenJournalScreens Create(IActivityStore activityStore, IWorkspaceStore workspaceStore, IReviewStore reviewStore,
        ISizingStore sizingStore, IOpenRiskStore riskStore, IUnitOfWork unitOfWork)
    {
        var clock = new GoldenClock();
        var venues = GoldenJournal.Venues();
        var owner = new CoreOwner(GoldenJournal.Owner);
        var activity = new ActivityService(activityStore, workspaceStore, clock, venues, marks: new MarketContextCache(clock));
        var workspace = new WorkspaceService(workspaceStore, owner, venues, unitOfWork);
        return new(activity, new OverviewSummaryService(workspaceStore, workspace, activity, sizingStore, clock),
            new ReviewService(reviewStore, clock, venues, new ActivityReviewPositions(activity, sizingStore)),
            new SizingService(sizingStore, owner, clock, unitOfWork, activity, venues),
            new OpenRiskService(riskStore, workspaceStore, workspace, venues, owner, clock, unitOfWork));
    }

    /// <summary>The screens over in-memory stores that read the dataset by the same rules as the database stores.</summary>
    public static GoldenJournalScreens InMemory(GoldenJournal journal)
    {
        var workspace = new MemoryWorkspaceStore(GoldenJournal.Owner);
        workspace.Portfolios.Add(journal.Swing);
        workspace.Accounts.AddRange(journal.Accounts);
        workspace.Snapshots.AddRange(journal.Snapshots);
        workspace.Fills.AddRange(journal.Fills);
        var stores = new GoldenJournalStores(journal);
        return Create(stores.Activity, workspace, stores.Review, stores.Sizing, stores.Risk, TestUnitOfWork.None);
    }
}

/// <summary>
/// In-memory stores for the golden journal. Links reach fills through their order by the shared <see cref="OrderKey"/> rule,
/// and a linked order is incomplete by <see cref="ExecutionIntegrity.MissingExecutions(ImportedOrder, IEnumerable{ImportedFill})"/>,
/// as the database stores decide.
/// </summary>
internal sealed class GoldenJournalStores(GoldenJournal journal)
{
    public IActivityStore Activity => new ActivityReader(journal);
    public IReviewStore Review => new ReviewReader(journal);
    public ISizingStore Sizing => new SizingReader(journal);
    public IOpenRiskStore Risk => new RiskReader(journal);

    private static ImportedOrder OrderOf(GoldenJournal journal, PlayOrderLink link) => journal.Orders.Single(o => o.Id == link.ImportedOrderId);

    private static List<ClosedPlayFacts> Closed(GoldenJournal journal) => journal.Plays
        .Where(p => p.Status == PlayStatus.Closed && journal.IsEnabled(p.AccountId))
        .Select(p =>
        {
            var links = journal.Links.Where(l => l.PlayId == p.Id && l.State == OrderLinkState.Linked).ToList();
            return new ClosedPlayFacts(p, journal.Revisions.Where(r => r.PlayId == p.Id).ToList(),
                links.SelectMany(l => journal.FillsOf(OrderOf(journal, l)).Select(f => new LinkedFill(l.Role, f))).ToList(),
                links.Select(l => OrderOf(journal, l)).Where(o => ExecutionIntegrity.MissingExecutions(o, journal.FillsOf(o)))
                    .Select(o => o.ExternalOrderId).Distinct(StringComparer.Ordinal).ToList());
        }).ToList();

    private sealed class ActivityReader(GoldenJournal journal) : IActivityStore
    {
        public Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null)
        {
            var accounts = journal.Accounts.Where(a => a.IsEnabled && (accountId is null || a.Id == accountId) &&
                (accountIds is null || accountIds.Contains(a.Id))).ToList();
            var ids = accounts.Select(a => a.Id).ToHashSet();
            var plays = journal.Links.Where(l => l.State == OrderLinkState.Linked)
                .SelectMany(l => journal.FillsOf(OrderOf(journal, l)).Select(f => (f.Id, l.PlayId)))
                .Distinct().ToDictionary(x => x.Id, x => x.PlayId);
            return Task.FromResult(new ActivityFacts(accounts, journal.Fills.Where(f => ids.Contains(f.AccountId)).ToList(),
                journal.Snapshots.Where(s => ids.Contains(s.AccountId)).ToList(), plays));
        }

        public Task<IReadOnlyDictionary<Guid, string>> PlayTitlesAsync(IReadOnlyCollection<Guid> playIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(journal.Plays.Where(p => playIds.Contains(p.Id)).ToDictionary(p => p.Id, p => p.Title));
    }

    private sealed class ReviewReader(GoldenJournal journal) : IReviewStoreDefaults
    {
        public Task<ReviewFacts> ReadAsync(ReviewQuery query, DateTimeOffset asOf, CancellationToken ct) =>
            Task.FromResult(new ReviewFacts([journal.Swing], journal.Accounts.ToList(), Closed(journal),
                journal.Plays.Count(p => p.Status == PlayStatus.Open && journal.IsEnabled(p.AccountId)), 0,
                journal.Fills.Where(f => journal.IsEnabled(f.AccountId)).Min(f => f.OccurredAtUtc)));

        public Task<IReadOnlyList<string>> MistakeTagsAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class SizingReader(GoldenJournal journal) : ISizingStore
    {
        private SizingSettings? settings;
        public Task<SizingSettings?> SettingsAsync(CancellationToken ct) => Task.FromResult(settings);
        public Task<List<ClosedPlayFacts>> ClosedPlaysAsync(CancellationToken ct) => Task.FromResult(Closed(journal));
        public Task<int> UnresolvedPlaysAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<List<ImportedOrder>> OrdersAsync(IReadOnlyCollection<OrderKey> orders, CancellationToken ct) =>
            Task.FromResult(journal.Orders.Where(o => orders.Contains(OrderKey.Of(o))).ToList());
        public void Add(SizingSettings value) => settings = value;
        public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RiskReader(GoldenJournal journal) : IOpenRiskStore
    {
        private static readonly PlayStatus[] Active = [PlayStatus.Planned, PlayStatus.Paused, PlayStatus.Open, PlayStatus.Unresolved];

        public Task<List<OpenRiskFacts>> ActivePlaysAsync(IReadOnlyCollection<Guid>? playIds, CancellationToken ct) =>
            Task.FromResult(journal.Plays.Where(p => playIds?.Contains(p.Id) ?? Active.Contains(p.Status) && journal.IsEnabled(p.AccountId))
                .Select(p =>
                {
                    var links = journal.Links.Where(l => l.PlayId == p.Id && l.State == OrderLinkState.Linked).ToList();
                    var executions = links.SelectMany(l => journal.FillsOf(OrderOf(journal, l)).Select(f =>
                        new RiskExecution(l.Role, l.Role == OrderLinkRole.Exit ? null : l.EntryId, l.LevelId, f.Quantity, f.Price) { Fill = f }))
                        .ToList();
                    var incomplete = links.Any(l => ExecutionIntegrity.MissingExecutions(OrderOf(journal, l), journal.FillsOf(OrderOf(journal, l))));
                    var stopOrders = links.Where(l => l is { Role: OrderLinkRole.Stop, EntryId: not null, LevelId: not null }).Select(l =>
                    {
                        var order = OrderOf(journal, l);
                        return new RiskOrder(l.EntryId!, l.LevelId!, order.Status, order.OriginalSize, order.RemainingSize, order.IsPositionTpsl,
                            journal.FillsOf(order).Sum(f => f.Quantity));
                    }).ToList();
                    return new OpenRiskFacts(p, executions, null, false, incomplete ? OpenRiskCalculator.MissingExecutions : null, stopOrders);
                }).ToList());

        public Task<Play?> PlayAsync(Guid id, CancellationToken ct) => Task.FromResult(journal.Plays.SingleOrDefault(p => p.Id == id));
        public Task<RiskSettings?> SettingsAsync(CancellationToken ct) => Task.FromResult<RiskSettings?>(null);
        public Task<RiskSettings> SettingsForUpdateAsync(RiskSettings initial, CancellationToken ct) => Task.FromResult(initial);
        public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
