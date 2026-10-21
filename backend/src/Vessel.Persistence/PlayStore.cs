using System.Data;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;

namespace Vessel.Persistence;

public sealed class PlayStore(VesselDbContext db, UnitOfWork unitOfWork) : IPlayStore
{
    public Task<bool> ScopeExistsAsync(ListScope scope, CancellationToken ct) => scope.Kind switch
    {
        "portfolio" => db.Portfolios.AnyAsync(p => p.Id == scope.Id, ct),
        "account" => db.Accounts.AnyAsync(a => a.Id == scope.Id, ct),
        _ => Task.FromResult(true)
    };

    // Count, clamp and rows share one snapshot, so a concurrent close or delete never returns a total its rows contradict.
    public async Task<PlayListPage> PageAsync(PlayListView view, PlayListFilter filter, int page, int pageSize, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var rows = View(Rows(filter), view);
        var total = await rows.CountAsync(ct);
        page = ListPaging.Clamp(page, pageSize, total);
        if (total == 0) return new(0, page, []);
        var ordered = view is PlayListView.Drafts or PlayListView.Progress or PlayListView.All
            ? rows.OrderByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Id)
            : rows.OrderByDescending(x => x.EndedAtUtc).ThenByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Id);
        return new(total, page, await ordered.Skip(ListPaging.Skip(page, pageSize)).Take(pageSize).ToListAsync(ct));
    }

    public async Task<List<PlayCountRow>> CountsAsync(PlayListFilter filter, CancellationToken ct) =>
        (await Rows(filter).GroupBy(x => new { x.Status, Reviewed = x.ReviewCompletion != null })
            .Select(g => new { g.Key.Status, g.Key.Reviewed, Count = g.Count() }).ToListAsync(ct))
        .Select(g => new PlayCountRow(g.Status, g.Reviewed, g.Count)).ToList();

    private static IQueryable<PlayListRow> View(IQueryable<PlayListRow> rows, PlayListView view) => view switch
    {
        PlayListView.Drafts => rows.Where(x => x.Status == PlayStatus.Draft),
        PlayListView.Progress => rows.Where(x => x.Status == PlayStatus.Planned || x.Status == PlayStatus.Paused || x.Status == PlayStatus.Open),
        // Completion is explicit (decision 3): an edit, a note or a timestamp never moves a Play out of Needs review.
        PlayListView.Review => rows.Where(x => (x.Status == PlayStatus.Closed || x.Status == PlayStatus.Cancelled) && x.ReviewCompletion == null),
        PlayListView.Reviewed => rows.Where(x => (x.Status == PlayStatus.Closed || x.Status == PlayStatus.Cancelled) && x.ReviewCompletion != null),
        PlayListView.Closed => rows.Where(x => x.Status == PlayStatus.Closed),
        PlayListView.Cancelled => rows.Where(x => x.Status == PlayStatus.Cancelled),
        _ => rows
    };

    // Every character char.IsWhiteSpace accepts, so SQL "has a review" matches Play.HasReview exactly.
    private static readonly char[] Space = "\t\n\v\f\r \u0085\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000".ToCharArray();
    // Space plus U+FEFF, which JavaScript's trim also removes: the browser rejects a blank next action, so none is sent.
    private static readonly char[] BrowserSpace = [.. Space, '\uFEFF'];

    // Owner-filtered Plays, projected to summary columns only. A scope keeps enabled accounts in it; no scope keeps every Play.
    private IQueryable<PlayListRow> Rows(PlayListFilter filter)
    {
        var plays = db.Plays.AsQueryable();
        if (filter.Scope is { } scope)
        {
            var accounts = db.Accounts.Where(a => a.IsEnabled);
            var id = scope.Id;
            accounts = scope.Kind switch
            {
                "unassigned" => accounts.Where(a => a.PortfolioId == null),
                "portfolio" => accounts.Where(a => a.PortfolioId == id),
                "account" => accounts.Where(a => a.Id == id),
                _ => accounts
            };
            plays = plays.Where(p => accounts.Any(a => a.Id == p.AccountId));
        }
        if (filter.Search is { } search)
        {
            var pattern = "%" + search.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
            plays = plays.Where(p => EF.Functions.ILike(p.Title, pattern, @"\") ||
                p.ContractId != null && EF.Functions.ILike(p.ContractId, pattern, @"\"));
        }
        return plays.Select(p => new PlayListRow
        {
            Id = p.Id, Title = p.Title, Status = p.Status, AccountId = p.AccountId, VenueId = p.VenueId, ContractId = p.ContractId,
            InstrumentSource = p.InstrumentSource, Direction = p.Direction, PlanRevision = p.PlanRevision, Version = p.Version,
            CancelReason = p.CancelReason, CreatedAtUtc = p.CreatedAtUtc, UpdatedAtUtc = p.UpdatedAtUtc, PlannedAtUtc = p.PlannedAtUtc,
            EndedAtUtc = p.EndedAtUtc, Origin = p.Origin, ReviewedAtUtc = p.ReviewedAtUtc,
            ReviewCompletion = p.ReviewCompletion, ReviewCompletedAtUtc = p.ReviewCompletedAtUtc,
            HasReview = p.Review.Trim(Space) != "" || p.ReviewAdherence != null ||
                p.ReviewMistakeTag != null && p.ReviewMistakeTag.Trim(Space) != "" ||
                p.ReviewLesson.Trim(Space) != "" || p.ReviewNextAction.Trim(Space) != ""
        });
    }

    public Task<Play?> FindAsync(Guid id, CancellationToken ct) => db.Plays.SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => db.Accounts.SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<List<PlayPlanRevision>> RevisionsAsync(Guid playId, CancellationToken ct) =>
        db.PlanRevisions.Where(x => x.PlayId == playId).OrderBy(x => x.Number).AsNoTracking().ToListAsync(ct);
    public Task<List<PlayStatusChange>> StatusChangesAsync(Guid playId, CancellationToken ct) =>
        db.StatusChanges.Where(x => x.PlayId == playId).OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Id).AsNoTracking().ToListAsync(ct);
    public Task<List<NextActionDto>> NextActionsAsync(int limit, CancellationToken ct) =>
        db.Plays.Where(p => (p.Status == PlayStatus.Closed || p.Status == PlayStatus.Cancelled) && p.ReviewNextAction != "" &&
                p.ReviewNextAction.Trim(BrowserSpace) != "")
            .OrderByDescending(p => p.ReviewedAtUtc).ThenByDescending(p => p.UpdatedAtUtc).ThenBy(p => p.Id).Take(limit)
            .Select(p => new NextActionDto(p.Id, p.Title, p.ReviewNextAction, p.ReviewedAtUtc)).ToListAsync(ct);

    public Task<List<PlayNote>> NotesAsync(Guid playId, CancellationToken ct) =>
        db.PlayNotes.Where(x => x.PlayId == playId).OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).AsNoTracking().ToListAsync(ct);
    public Task<PlayNote?> NoteAsync(Guid requestId, CancellationToken ct) =>
        db.PlayNotes.AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == requestId, ct);
    public void Add(PlayNote note) { unitOfWork.RequireActive(); db.PlayNotes.Add(note); }
    public async Task<bool> HasLinkedFillsAsync(Guid playId, CancellationToken ct)
    {
        var orders = db.Orders.Where(o => db.OrderLinks.Any(l => l.PlayId == playId && l.State == OrderLinkState.Linked &&
            l.AccountId == o.AccountId && l.ImportedOrderId == o.Id));
        return await db.FillsOf(orders).AnyAsync(ct) || await db.ManualExecutions.AnyAsync(r => r.PlayId == playId, ct);
    }

    public Task<List<PlayReviewRevision>> ReviewsAsync(Guid playId, CancellationToken ct) =>
        db.ReviewRevisions.Where(x => x.PlayId == playId).OrderBy(x => x.Number).AsNoTracking().ToListAsync(ct);
    public async Task<IReadOnlySet<string>> FilledEntryIdsAsync(Guid playId, CancellationToken ct)
    {
        // Linked unfilled entry orders can fill later. Keep their referenced plan too, not only already-filled rows.
        var ids = await db.OrderLinks.Where(l => l.PlayId == playId && l.State == OrderLinkState.Linked && l.Role == OrderLinkRole.Entry)
            .Select(l => l.EntryId!).Distinct().ToListAsync(ct);
        ids.AddRange(await db.ManualExecutions.Where(r => r.PlayId == playId && r.Role == "entry").Select(r => r.EntryId!).Distinct().ToListAsync(ct));
        return ids.ToHashSet(StringComparer.Ordinal);
    }
    public async Task<List<EntryExecution>> EntryExecutionsAsync(Guid playId, CancellationToken ct)
    {
        var entries = db.OrderLinks.Where(l => l.PlayId == playId && l.State == OrderLinkState.Linked && l.Role == OrderLinkRole.Entry);
        var linked = await db.LinkedFills(entries).AsNoTracking().Select(x => new { x.Link.EntryId, x.Fill }).ToListAsync(ct);
        var manual = await db.ManualExecutions.Where(r => r.PlayId == playId && r.Role == "entry" && r.EntryId != null)
            .Select(r => r.EntryId!).Distinct().ToListAsync(ct);
        return [.. linked.Select(x => new EntryExecution(x.EntryId!, x.Fill)), .. manual.Select(id => new EntryExecution(id, null))];
    }
    public void Add(Play play) { unitOfWork.RequireActive(); db.Plays.Add(play); }
    public async Task<List<LinkedPlanReference>> LinkedPlanReferencesAsync(Guid playId, CancellationToken ct) =>
        (await db.OrderLinks.Where(l => l.PlayId == playId && l.State == OrderLinkState.Linked)
            .Join(db.Orders, l => new { l.OwnerId, l.AccountId, Id = l.ImportedOrderId }, o => new { o.OwnerId, o.AccountId, o.Id },
                (l, o) => new { Link = l, o.ExternalOrderId })
            .AsNoTracking().ToListAsync(ct)).Select(x => new LinkedPlanReference(x.Link, x.ExternalOrderId)).ToList();
    public void Add(PlayPlanRevision revision) { unitOfWork.RequireActive(); db.PlanRevisions.Add(revision); }
    public void Add(PlayStatusChange change) { unitOfWork.RequireActive(); db.StatusChanges.Add(change); }
    public void Add(PlayReviewRevision revision) { unitOfWork.RequireActive(); db.ReviewRevisions.Add(revision); }

    public Task SaveAsync(CancellationToken ct) => unitOfWork.SaveChangesAsync(ct);

    public Task<T> WithPlayLockAsync<T>(Guid id, Func<Task<T>> action, CancellationToken ct, Guid? destinationAccountId = null) => unitOfWork.ParticipateAsync(async () =>
    {
        var accountId = await db.Plays.Where(p => p.Id == id).Select(p => (Guid?)p.AccountId).SingleOrDefaultAsync(ct)
            ?? throw new ProblemException(404, "Play not found.");
        if (destinationAccountId is { } destination && destination != accountId)
        {
            // ponytail: moves serialize per owner; use ordered account locks if move throughput requires it.
            // Reuse the management key before either account row to avoid destination FK key-share deadlocks.
            var key = "vessel-management:" + db.CurrentOwnerId;
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
            await new WorkspaceStore(db, unitOfWork).WithAccountLockAsync(destination, _ => Task.FromResult(true), ct);
        }
        // Lock order is account row -> execution advisory -> Play row everywhere.
        await new ExecutionStore(db, unitOfWork).WithAccountLockAsync(accountId, () => Task.FromResult(true), ct);
        // The same row lock ExecutionStore.ActivePlaysAsync takes; evidence and history inserts only need a key share.
        var plays = await db.Plays.FromSqlInterpolated(
            $"SELECT * FROM plays WHERE \"Id\" = {id} AND \"OwnerId\" = {db.CurrentOwnerId} FOR NO KEY UPDATE").ToListAsync(ct);
        // A scoped context could already track this Play before waiting on the lock.
        foreach (var play in plays) await db.Entry(play).ReloadAsync(ct);
        if (plays.SingleOrDefault()?.AccountId != accountId)
            throw new ProblemException(409, "The Play moved to another account. Reload before editing.");
        var result = await action();
        return result;
    }, ct);

    public Task<T> WithNoteRequestLockAsync<T>(Guid requestId, Func<Task<T>> action, CancellationToken ct) => unitOfWork.ParticipateAsync(async () =>
    {
        var key = $"vessel-play-note-request:{db.CurrentOwnerId}:{requestId}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
        return await action();
    }, ct);

    public Task<IReadOnlyList<string>> DeleteAsync(Play play, CancellationToken ct) => unitOfWork.ParticipateAsync<IReadOnlyList<string>>(async () =>
    {
        var evidence = await db.Evidence.Where(x => x.PlayId == play.Id).ToListAsync(ct);
        db.Evidence.RemoveRange(evidence);
        db.PlanRevisions.RemoveRange(await db.PlanRevisions.Where(x => x.PlayId == play.Id).ToListAsync(ct));
        db.StatusChanges.RemoveRange(await db.StatusChanges.Where(x => x.PlayId == play.Id).ToListAsync(ct));
        db.OrderLinks.RemoveRange(await db.OrderLinks.Where(x => x.PlayId == play.Id).ToListAsync(ct));
        await unitOfWork.SaveChangesAsync(ct);
        db.Plays.Remove(play);
        await SaveAsync(ct);
        return evidence.Select(x => x.ObjectKey).ToList();
    }, ct);
}
