using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

internal sealed class MemoryPlayStore(Guid owner) : IPlayStore
{
    public List<Account> Accounts { get; } = [];
    public List<Play> Plays { get; } = [];
    public List<PlayPlanRevision> Revisions { get; } = [];
    public List<PlayStatusChange> Changes { get; } = [];
    public List<PlayReviewRevision> Reviews { get; } = [];
    public List<PlayOrderLink> Links { get; } = [];
    public List<string> EvidenceKeys { get; } = [];
    public int Saves { get; private set; }
    // A plain test double for service tests; SQL view, search and scope semantics are covered against PostgreSQL.
    public Task<bool> ScopeExistsAsync(ListScope scope, CancellationToken ct) => Task.FromResult(scope.Kind switch
    {
        "account" => Accounts.Any(a => a.Id == scope.Id && a.OwnerId == owner),
        "portfolio" => Accounts.Any(a => a.PortfolioId == scope.Id && a.OwnerId == owner),
        _ => true
    });
    private IEnumerable<Play> Listed(PlayListView view, PlayListFilter filter) => Plays.Where(p => p.OwnerId == owner &&
        (filter.Scope is not { } scope || Accounts.Any(a => a.Id == p.AccountId && scope.Includes(a.Id, a.PortfolioId, a.IsEnabled))) &&
        (filter.Search is null || p.Title.Contains(filter.Search, StringComparison.OrdinalIgnoreCase) ||
            p.ContractId?.Contains(filter.Search, StringComparison.OrdinalIgnoreCase) == true) && view switch
        {
            PlayListView.Drafts => p.Status == PlayStatus.Draft,
            PlayListView.Progress => p.Status is PlayStatus.Planned or PlayStatus.Paused or PlayStatus.Open,
            PlayListView.Review => p.Status is PlayStatus.Closed or PlayStatus.Cancelled && p.ReviewCompletion is null,
            PlayListView.Reviewed => p.Status is PlayStatus.Closed or PlayStatus.Cancelled && p.ReviewCompletion is not null,
            PlayListView.Closed => p.Status == PlayStatus.Closed,
            PlayListView.Cancelled => p.Status == PlayStatus.Cancelled,
            _ => true
        });
    public Task<PlayListPage> PageAsync(PlayListView view, PlayListFilter filter, int page, int pageSize, CancellationToken ct)
    {
        var total = Listed(view, filter).Count();
        page = ListPaging.Clamp(page, pageSize, total);
        return Task.FromResult(new PlayListPage(total, page, Rows(view, filter, ListPaging.Skip(page, pageSize), pageSize)));
    }
    private List<PlayListRow> Rows(PlayListView view, PlayListFilter filter, int skip, int take) =>
        Listed(view, filter).OrderByDescending(p => p.UpdatedAtUtc).ThenBy(p => p.Id).Skip(skip).Take(take).Select(p => new PlayListRow
        {
            Id = p.Id, Title = p.Title, Status = p.Status, AccountId = p.AccountId, VenueId = p.VenueId, ContractId = p.ContractId,
            InstrumentSource = p.InstrumentSource, Direction = p.Direction, PlanRevision = p.PlanRevision, Version = p.Version,
            CancelReason = p.CancelReason, CreatedAtUtc = p.CreatedAtUtc, UpdatedAtUtc = p.UpdatedAtUtc, PlannedAtUtc = p.PlannedAtUtc,
            EndedAtUtc = p.EndedAtUtc, HasReview = p.HasReview, Origin = p.Origin, ReviewedAtUtc = p.ReviewedAtUtc,
            ReviewCompletion = p.ReviewCompletion, ReviewCompletedAtUtc = p.ReviewCompletedAtUtc,
        }).ToList();
    public Task<List<PlayCountRow>> CountsAsync(PlayListFilter filter, CancellationToken ct) => Task.FromResult(Listed(PlayListView.All, filter)
        .GroupBy(p => (p.Status, Reviewed: p.ReviewCompletion is not null)).Select(g => new PlayCountRow(g.Key.Status, g.Key.Reviewed, g.Count())).ToList());
    public Task<Play?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Plays.SingleOrDefault(p => p.Id == id && p.OwnerId == owner));
    public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => Task.FromResult(Accounts.SingleOrDefault(a => a.Id == id && a.OwnerId == owner));
    public Task<List<PlayPlanRevision>> RevisionsAsync(Guid playId, CancellationToken ct) => Task.FromResult(Revisions.Where(r => r.PlayId == playId).ToList());
    public Task<List<PlayStatusChange>> StatusChangesAsync(Guid playId, CancellationToken ct) => Task.FromResult(Changes.Where(c => c.PlayId == playId).ToList());
    public Task<List<PlayReviewRevision>> ReviewsAsync(Guid playId, CancellationToken ct) => Task.FromResult(Reviews.Where(c => c.PlayId == playId).ToList());
    public List<PlayNote> Notes { get; } = [];
    public Task<List<PlayNote>> NotesAsync(Guid playId, CancellationToken ct) =>
        Task.FromResult(Notes.Where(n => n.PlayId == playId).OrderBy(n => n.CreatedAtUtc).ToList());
    public Task<PlayNote?> NoteAsync(Guid requestId, CancellationToken ct) => Task.FromResult(Notes.SingleOrDefault(n => n.RequestId == requestId));
    public void Add(PlayNote note) => Notes.Add(note);
    public bool LinkedFills { get; set; }
    public Task<bool> HasLinkedFillsAsync(Guid playId, CancellationToken ct) => Task.FromResult(LinkedFills);
    public Task<List<NextActionDto>> NextActionsAsync(int limit, string tradingMode, CancellationToken ct) => Task.FromResult(Plays
        .Where(p => p.OwnerId == owner && p.TradingMode == tradingMode && p.Status is PlayStatus.Closed or PlayStatus.Cancelled && !string.IsNullOrWhiteSpace(p.ReviewNextAction))
        .OrderByDescending(p => p.ReviewedAtUtc).ThenByDescending(p => p.UpdatedAtUtc).ThenBy(p => p.Id).Take(limit)
        .Select(p => new NextActionDto(p.Id, p.Title, p.ReviewNextAction, p.ReviewedAtUtc)).ToList());
    /// <summary>Venue order IDs of linked orders, by internal ID; a link without one shows its internal ID.</summary>
    public Dictionary<Guid, string> ExternalOrderIds { get; } = [];
    public Task<List<LinkedPlanReference>> LinkedPlanReferencesAsync(Guid playId, CancellationToken ct) => Task.FromResult(Links
        .Where(l => l.PlayId == playId && l.State == OrderLinkState.Linked)
        .Select(l => new LinkedPlanReference(l, ExternalOrderIds.GetValueOrDefault(l.ImportedOrderId) ?? l.ImportedOrderId.ToString())).ToList());
    public void Add(Play play) => Plays.Add(play);
    public void Add(PlayPlanRevision revision) => Revisions.Add(revision);
    public void Add(PlayStatusChange change) => Changes.Add(change);
    public void Add(PlayReviewRevision revision) => Reviews.Add(revision);
    public HashSet<string> FilledEntries { get; } = [];
    public Task<IReadOnlySet<string>> FilledEntryIdsAsync(Guid playId, CancellationToken ct) => Task.FromResult<IReadOnlySet<string>>(FilledEntries);
    /// <summary>Entry fills and manual entry executions, for every Play of this store.</summary>
    public List<EntryExecution> EntryExecutions { get; } = [];
    public Task<List<EntryExecution>> EntryExecutionsAsync(Guid playId, CancellationToken ct) => Task.FromResult(EntryExecutions.ToList());
    public Task SaveAsync(CancellationToken ct) { Saves++; return Task.CompletedTask; }
    public Task<T> WithPlayLockAsync<T>(Guid id, Func<Task<T>> action, CancellationToken ct, Guid? destinationAccountId = null) => action();
    public Task<T> WithNoteRequestLockAsync<T>(Guid requestId, Func<Task<T>> action, CancellationToken ct) => action();
    public Task<IReadOnlyList<string>> DeleteAsync(Play play, CancellationToken ct)
    {
        Plays.Remove(play);
        return Task.FromResult<IReadOnlyList<string>>(EvidenceKeys);
    }
}
