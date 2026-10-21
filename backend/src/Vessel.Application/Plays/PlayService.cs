using System.Text.RegularExpressions;
using System.Text.Json;
using Vessel.Application.Common;
using Vessel.Application.Evidence;
using Vessel.Application.Ownership;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Application.Paper;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;

namespace Vessel.Application.Plays;

/// <summary>
/// Saved Plays and their lifecycle. Open and Closed come from linked venue fills, so this service
/// only performs the owner's transitions: plan, pause, resume and cancel. Nothing is sent to a venue.
/// </summary>
public sealed partial class PlayService(IPlayStore store, IEvidenceObjectStore objects, TimeProvider time, IUnitOfWork unitOfWork,
    IPaperTradingService? paper = null)
{
    public const int MaxUnpagedPlays = 200, MaxSearchLength = 100;

    /// <summary>One view of saved Plays. Drafts and In progress return one page of at most 200 rows (or an explicit size); <c>TotalCount</c> stays exact.</summary>
    public async Task<PlayListDto> ListAsync(string? view, string? search, string? scope, string? page, string? pageSize, CancellationToken ct,
        string? tradingMode = "real")
    {
        var listView = View(view);
        var (requested, size) = ListPaging.Parse(page, pageSize);
        var filter = await Filter(search, scope, Mode(tradingMode), ct);
        // Unpaged views are one page: the whole view up to 200 rows, or an explicit smaller size such as Overview's 5.
        if (listView is PlayListView.Drafts or PlayListView.Progress) (requested, size) = (1, string.IsNullOrEmpty(pageSize) ? MaxUnpagedPlays : size);
        var result = await store.PageAsync(listView, filter, requested, size, ct);
        return new(result.Rows.Select(Summary).ToList(), ViewName(listView), result.Page, size, result.TotalCount);
    }

    public async Task<PlayCountsDto> CountsAsync(string? search, string? scope, CancellationToken ct, string? tradingMode = "real")
    {
        var rows = await store.CountsAsync(await Filter(search, scope, Mode(tradingMode), ct), ct);
        int Count(Func<PlayCountRow, bool> where) => rows.Where(where).Sum(r => r.Count);
        static bool Ended(PlayCountRow r) => r.Status is PlayStatus.Closed or PlayStatus.Cancelled;
        return new(Count(r => r.Status == PlayStatus.Draft), Count(r => r.Status is PlayStatus.Planned or PlayStatus.Paused or PlayStatus.Open),
            Count(r => Ended(r) && !r.Reviewed), Count(r => Ended(r) && r.Reviewed), Count(r => r.Status == PlayStatus.Closed),
            Count(r => r.Status == PlayStatus.Cancelled), Count(_ => true));
    }

    private async Task<PlayListFilter> Filter(string? search, string? scope, string tradingMode, CancellationToken ct)
    {
        search = search?.Trim();
        if (search?.Length > MaxSearchLength) throw new ProblemException(400, $"Search is limited to {MaxSearchLength} characters.");
        search = string.IsNullOrEmpty(search) ? null : search;
        // Without a scope the Plays page lists every saved Play; Overview scopes follow enabled accounts only.
        if (scope is null) return new(search, null, tradingMode);
        var listScope = ListScope.Parse(scope);
        if (!await store.ScopeExistsAsync(listScope, tradingMode, ct))
            throw new ProblemException(404, listScope.Kind == "portfolio" ? "Portfolio not found." : "Account not found.");
        return new(search, listScope, tradingMode);
    }

    private static PlayListView View(string? view) => view switch
    {
        null or "all" => PlayListView.All,
        "drafts" => PlayListView.Drafts,
        "progress" => PlayListView.Progress,
        "review" => PlayListView.Review,
        "reviewed" => PlayListView.Reviewed,
        "closed" => PlayListView.Closed,
        "cancelled" => PlayListView.Cancelled,
        _ => throw new ProblemException(400, "View must be drafts, progress, review, reviewed, closed, cancelled or all.")
    };
    private static string ViewName(PlayListView view) => view.ToString().ToLowerInvariant();

    public async Task<PlayDto> GetAsync(Guid id, CancellationToken ct) => ToDto(await Find(id, ct));

    public Task<PlayDto> CreateAsync(CreatePlayRequest request, CancellationToken ct, string? tradingMode = "real") => unitOfWork.ExecuteAsync(async () =>
    {
        var account = await EnabledAccount(request.AccountId, ct);
        if (account.TradingMode != Mode(tradingMode)) throw new ProblemException(404, "Account not found.");
        var (instrument, source) = Instrument(account, request.Instrument, request.InstrumentSource);
        var plan = PlayDocuments.Serialize(PlayDocuments.Normalize(request.Plan));
        var now = time.GetUtcNow();
        var play = new Play(Guid.NewGuid(), account, instrument, source, plan, now);
        var review = play.Annotate(request.Title ?? "", PlayDocuments.NormalizeDrawings(request.Drawings),
            request.Review ?? "", now, request.StructuredReview);
        store.Add(play);
        if (review is not null) store.Add(review);
        return ToDto(play);
    }, ct);

    /// <summary>Holds the Play while it checks entry fills and saves, so no order links to an entry this revision changes in between.</summary>
    public Task<PlayDto> UpdateAsync(Guid id, UpdatePlayRequest request, CancellationToken ct) => unitOfWork.ExecuteAsync(() => store.WithPlayLockAsync(id, async () =>
    {
        var play = await Find(id, ct);
        RequireVersion(play, request.ExpectedVersion);
        if (request.Plan is null) throw new ProblemException(400, "A plan compatibility document is required.");
        // Retrospectives keep an empty compatibility document, not a manufactured plan. Number text unchanged from
        // the stored plan still saves, even where it predates the plain-decimal rule. Stops of filled entries may sit
        // at or beyond the entry; the fills are read under the Play lock, so none arrives in between.
        var plan = play.Origin == PlayOrigin.Retrospective ? PlayDocuments.Serialize(request.Plan)
            : PlayDocuments.Serialize(PlayDocuments.Normalize(request.Plan, PlayDocuments.Read(play.Plan),
                play.TradingMode == "paper" ? (await RequirePaper().GetExecutionProofAsync(play.Id, ct)).ExecutedEntryIds
                    : await ExecutedEntriesAsync(store, play, ct)));
        var drawings = PlayDocuments.NormalizeDrawings(request.Drawings);
        var now = time.GetUtcNow();
        if (request.AccountId != play.AccountId || request.Instrument != play.ContractId ||
            request.InstrumentSource != SourceName(play.InstrumentSource))
        {
            var account = play.Status == PlayStatus.Draft ? await EnabledAccount(request.AccountId, ct)
                : throw new ProblemException(409, "The account and instrument are fixed once a Play is planned.");
            var (instrument, source) = Instrument(account, request.Instrument, request.InstrumentSource);
            play.MoveTo(account, instrument, source, now);
        }
        if (play.Status != PlayStatus.Draft && plan != play.Plan) await RequireFilledEntriesKept(play, request.Plan, ct);
        var revision = play.ChangePlan(plan, request.RevisionReason, now);
        var review = play.Annotate(request.Title ?? "", drawings, request.Review ?? "", now, request.StructuredReview);
        if (revision is not null) store.Add(revision);
        if (revision is not null && play.TradingMode == "paper")
            await RequirePaper().ActivateRevisionAsync(play, ct);
        if (review is not null) store.Add(review);
        return ToDto(play);
    }, ct, destinationAccountId: request.AccountId), ct);

    public Task<PlayDto> ChangeStatusAsync(Guid id, ChangePlayStatusRequest request, CancellationToken ct) =>
        unitOfWork.ExecuteAsync(() => store.WithPlayLockAsync(id, async () =>
    {
        var play = await Find(id, ct);
        RequireVersion(play, request.ExpectedVersion);
        if (request.Preflight is not null && (request.Status, play.Status) is not ("planned", PlayStatus.Draft))
            throw new ProblemException(400, "A pre-flight is recorded only when a Draft is planned.");
        var now = time.GetUtcNow();
        switch (request.Status, play.Status)
        {
            case ("planned", PlayStatus.Draft):
                await EnabledAccount(play.AccountId, ct);
                PlayDocuments.RequirePlannable(PlayDocuments.Read(play.Plan));
                var (change, revision) = play.MarkPlanned(now, PlayPreflight.Serialize(PlayPreflight.Normalize(request.Preflight)));
                store.Add(change);
                store.Add(revision);
                break;
            case ("planned", _):
                store.Add(play.Resume(now));
                break;
            case ("paused", _):
                store.Add(play.Pause(now));
                break;
            case ("cancelled", _):
                var reason = request.Reason switch
                {
                    "invalidated" => CancelReason.Invalidated,
                    "missed" => CancelReason.Missed,
                    "changed-mind" => CancelReason.ChangedMind,
                    "expired" => CancelReason.Expired,
                    "mistake" => CancelReason.Mistake,
                    "other" => CancelReason.Other,
                    _ => throw new ProblemException(400, "Cancel reasons are invalidated, missed, changed-mind, expired, mistake or other.")
                };
                store.Add(play.Cancel(reason, request.Note, now));
                break;
            default:
                throw new ProblemException(400, "Status must be planned, paused or cancelled.");
        }
        if (play.TradingMode == "paper")
        {
            if (play.Status == PlayStatus.Planned) await RequirePaper().ArmAsync(play, ct);
            else if (play.Status is PlayStatus.Paused or PlayStatus.Cancelled) await RequirePaper().SuspendAsync(play, ct);
        }
        return ToDto(play);
    }, ct), ct);

    /// <summary>At most this many log entries are kept per Play.</summary>
    public const int MaxNotes = 500;

    /// <summary>Complete review: the review of an ended Play is complete, whether or not its text changed (decision 3).</summary>
    public Task<PlayDto> CompleteReviewAsync(Guid id, ReviewCompletionRequest request, CancellationToken ct) =>
        ReviewActionAsync(id, request, play => Task.FromResult(play.CompleteReview(time.GetUtcNow())), ct);

    /// <summary>Review again: the completion is cleared and the Play needs review; nothing it holds is lost.</summary>
    public Task<PlayDto> ReopenReviewAsync(Guid id, ReviewCompletionRequest request, CancellationToken ct) =>
        ReviewActionAsync(id, request, play => Task.FromResult(play.ReopenReview(time.GetUtcNow())), ct);

    /// <summary>No review needed, for a Cancelled Play with no linked fill.</summary>
    public Task<PlayDto> NoReviewNeededAsync(Guid id, ReviewCompletionRequest request, CancellationToken ct) =>
        ReviewActionAsync(id, request, async play =>
        {
            if (await store.HasLinkedFillsAsync(play.Id, ct))
                throw new ProblemException(409, "This Play has linked fills, so its review is needed.");
            return play.MarkNoReviewNeeded(time.GetUtcNow());
        }, ct);

    private Task<PlayDto> ReviewActionAsync(Guid id, ReviewCompletionRequest request, Func<Play, Task<PlayReviewRevision>> action,
        CancellationToken ct) => unitOfWork.ExecuteAsync(() => store.WithPlayLockAsync(id, async () =>
    {
        var play = await Find(id, ct);
        RequireVersion(play, request.ExpectedVersion);
        store.Add(await action(play));
        return ToDto(play);
    }, ct), ct);

    public async Task<IReadOnlyList<PlayNoteDto>> NotesAsync(Guid id, CancellationToken ct)
    {
        var play = await Find(id, ct);
        return (await store.NotesAsync(play.Id, ct)).Select(NoteDto).ToList();
    }

    /// <summary>
    /// Appends one log entry to a saved Play. It does not take the Play's version: an entry changes nothing the Play holds,
    /// so it never conflicts with plan or review edits. A retry with the same request ID returns the log unchanged.
    /// </summary>
    public Task<IReadOnlyList<PlayNoteDto>> AddNoteAsync(Guid id, AddPlayNoteRequest request, CancellationToken ct) =>
        unitOfWork.ExecuteAsync(() => store.WithNoteRequestLockAsync(request.RequestId, () => store.WithPlayLockAsync(id, async () =>
    {
        var play = await Find(id, ct);
        if (await store.NoteAsync(request.RequestId, ct) is { } earlier)
        {
            if (earlier.PlayId != play.Id || earlier.Text != (request.Text ?? "").Trim() || earlier.State != request.State)
                throw new ProblemException(409, "This request ID was already used for another log entry.");
        }
        else
        {
            if ((await store.NotesAsync(play.Id, ct)).Count >= MaxNotes) throw new ProblemException(409, $"A Play keeps at most {MaxNotes} log entries.");
            store.Add(new PlayNote(play, request.RequestId, request.Text ?? "", request.State, time.GetUtcNow()));
            await store.SaveAsync(ct);
        }
        return (IReadOnlyList<PlayNoteDto>)(await store.NotesAsync(play.Id, ct)).Select(NoteDto).ToList();
    }, ct), ct), ct);

    private static PlayNoteDto NoteDto(PlayNote note) => new(note.Id, note.Text, note.State, note.CreatedAtUtc);

    /// <summary>At most this many recent next actions are offered at planning (phase 1, 1.5).</summary>
    public const int MaxNextActions = 10;

    public Task<List<NextActionDto>> NextActionsAsync(CancellationToken ct) => store.NextActionsAsync(MaxNextActions, ct);

    public async Task<PlayHistoryDto> HistoryAsync(Guid id, CancellationToken ct)
    {
        var play = await Find(id, ct);
        var revisions = await store.RevisionsAsync(play.Id, ct);
        var changes = await store.StatusChangesAsync(play.Id, ct);
        var reviews = await store.ReviewsAsync(play.Id, ct);
        return new(revisions.Select(r => new PlanRevisionDto(r.Number, StatusName(r.Status), r.Reason, r.CreatedAtUtc, PlayDocuments.Read(r.Plan))
            { Preflight = PlayPreflight.Read(r.Preflight) }).ToList(),
            changes.Select(c => new StatusChangeDto(StatusName(c.From), StatusName(c.To), c.Reason is { } reason ? ReasonName(reason) : null,
                c.Note, c.OccurredAtUtc, c.Source.ToString().ToLowerInvariant())).ToList(),
            reviews.Select(r => new ReviewRevisionDto(r.Number, r.OccurredAtUtc, r.Review,
                JsonSerializer.Deserialize<ReviewAnswers>(r.StructuredReview)!) { Action = r.Action }).ToList());
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        // Evidence images are deleted once the Play removal commits, so this must be the outermost unit.
        unitOfWork.RequireOutermost("Play deletion");
        var keys = await unitOfWork.ExecuteAsync(async () =>
        {
            var play = await Find(id, ct);
            if (play.Status != PlayStatus.Draft)
                throw new ProblemException(409, "Only Drafts can be deleted. Cancel a planned Play instead, so its history is kept.");
            return await store.DeleteAsync(play, ct);
        }, ct);
        foreach (var key in keys)
        {
            try { await objects.DeleteAsync(key, CancellationToken.None); }
            catch (Exception) { /* Unreferenced objects are harmless; cleanup is a later maintenance task. */ }
        }
    }

    /// <summary>
    /// A filled entry already happened: its price and share cannot be revised, nor the entry removed. The direction
    /// decides which side closes that exposure, so it stays too; the opposite idea is a new Play.
    /// </summary>
    private async Task RequireFilledEntriesKept(Play play, PlayPlanDocument next, CancellationToken ct)
    {
        var filled = play.TradingMode == "paper" ? (await RequirePaper().GetExecutionProofAsync(play.Id, ct)).ExecutedEntryIds
            : await store.FilledEntryIdsAsync(play.Id, ct);
        var current = PlayDocuments.Read(play.Plan);
        var linked = await store.LinkedPlanReferencesAsync(play.Id, ct);
        if ((filled.Count > 0 || linked.Count > 0) && next.Direction != current.Direction)
            throw new ProblemException(409, $"This play has entry fills or linked orders, so it stays {current.Direction}. Start a new play for the opposite idea.");
        foreach (var (link, externalOrderId) in linked.Where(l => l.Link.Role is OrderLinkRole.Stop or OrderLinkRole.Target))
        {
            var oldEntry = current.Entries.FirstOrDefault(e => e.Id == link.EntryId);
            var nextEntry = next.Entries.FirstOrDefault(e => e.Id == link.EntryId);
            var oldLevel = (link.Role == OrderLinkRole.Stop ? oldEntry?.Stops : oldEntry?.Targets)?.FirstOrDefault(l => l.Id == link.LevelId);
            var nextLevel = (link.Role == OrderLinkRole.Stop ? nextEntry?.Stops : nextEntry?.Targets)?.FirstOrDefault(l => l.Id == link.LevelId);
            if (nextEntry is null || nextLevel is null || nextLevel != oldLevel || nextEntry.Price != oldEntry?.Price ||
                nextEntry.Share != oldEntry?.Share ||
                next.Leverage != current.Leverage && oldLevel?.Unit == "percent")
                throw new ProblemException(409, $"Order {externalOrderId} still references this plan level. Unlink it before changing or removing the level.");
        }
        var before = current.Entries.ToDictionary(e => e.Id);
        foreach (var id in filled)
        {
            if (!before.TryGetValue(id, out var old)) continue;
            var now = next.Entries.FirstOrDefault(e => e.Id == id);
            if (now is null || now.Price != old.Price || now.Share != old.Share)
                throw new ProblemException(409, $"{old.Name} has fills or linked orders, so its price and share stay as planned. Unlinked stops and targets can still change.");
        }
    }

    private static readonly IReadOnlySet<string> NoEntries = new HashSet<string>();

    /// <summary>
    /// Entries that have filled: a fill of a linked entry order that does not contradict the entry side, or a manual
    /// entry execution. Their stops may sit at or beyond the entry. A Draft has none.
    /// </summary>
    internal static async Task<IReadOnlySet<string>> ExecutedEntriesAsync(IPlayStore store, Play play, CancellationToken ct)
    {
        if (play.Status == PlayStatus.Draft || play.Origin == PlayOrigin.Retrospective) return NoEntries;
        // Fills keep the direction fixed, so the stored plan's entry side is the one they were checked against.
        var side = ExecutionIntegrity.EntrySide(PlayDocuments.Read(play.Plan));
        return (await store.EntryExecutionsAsync(play.Id, ct))
            .Where(e => e.Fill is null || !ExecutionIntegrity.Contradicts(OrderLinkRole.Entry, side, e.Fill))
            .Select(e => e.EntryId).ToHashSet(StringComparer.Ordinal);
    }

    private async Task<Play> Find(Guid id, CancellationToken ct) =>
        await store.FindAsync(id, ct) ?? throw new ProblemException(404, "Play not found.");

    private async Task<Account> EnabledAccount(Guid id, CancellationToken ct)
    {
        var account = await store.AccountAsync(id, ct) ?? throw new ProblemException(404, "Account not found.");
        return account.IsEnabled ? account : throw new ProblemException(409, "Enable the account before using it for a Play.");
    }

    private static void RequireVersion(Play play, long expected)
    {
        if (play.Version != expected)
            throw new ProblemException(409, "This Play changed elsewhere. Reload it before saving again.");
    }

    private static (PerpetualInstrument?, InstrumentSource) Instrument(Account account, string? instrument, string? source)
    {
        if (account.TradingMode == "paper" && source != "venue")
            throw new ProblemException(400, "Paper Plays use the venue's perpetual catalogue.");
        var kind = source switch
        {
            "venue" when account.VenueId != VenueDescriptor.ManualId => InstrumentSource.Venue,
            "venue" => throw new ProblemException(400, "Manual accounts use manual instrument labels."),
            "manual" => InstrumentSource.Manual,
            _ => throw new ProblemException(400, "Instrument source must be venue or manual.")
        };
        if (string.IsNullOrEmpty(instrument)) return (null, kind);
        var valid = kind == InstrumentSource.Venue ? ContractPattern().IsMatch(instrument)
            : instrument.Length <= 64 && instrument == instrument.Trim() && !instrument.Any(char.IsControl);
        return valid ? (new PerpetualInstrument(account.VenueId, instrument), kind)
            : throw new ProblemException(400, kind == InstrumentSource.Venue ? "Venue instruments use their contract ID."
                : "Manual instrument labels have at most 64 characters without surrounding spaces.");
    }

    public static string StatusName(PlayStatus status) => status.ToString().ToLowerInvariant();
    private static string SourceName(InstrumentSource source) => source == InstrumentSource.Venue ? "venue" : "manual";
    private static string ReasonName(CancelReason reason) => reason == CancelReason.ChangedMind ? "changed-mind" : reason.ToString().ToLowerInvariant();

    public static PlaySummaryDto Summary(Play play) => Summary(play, PlayDocuments.Read(play.Plan).Direction);

    private static PlaySummaryDto Summary(Play play, string direction) => new(play.Id, play.Title, StatusName(play.Status), play.AccountId, play.VenueId,
        play.ContractId, SourceName(play.InstrumentSource), direction, play.PlanRevision, play.Version,
        play.CancelReason is { } reason ? ReasonName(reason) : null, play.CreatedAtUtc, play.UpdatedAtUtc, play.PlannedAtUtc, play.EndedAtUtc,
        play.HasReview, play.Origin == PlayOrigin.Retrospective ? "retrospective" : "planned", play.ReviewedAtUtc, play.TradingMode)
    {
        ReviewCompletion = CompletionName(play.ReviewCompletion), ReviewCompletedAtUtc = play.ReviewCompletedAtUtc,
    };

    private static PlaySummaryDto Summary(PlayListRow play) => new(play.Id, play.Title, StatusName(play.Status), play.AccountId, play.VenueId,
        play.ContractId, SourceName(play.InstrumentSource), play.Direction, play.PlanRevision, play.Version,
        play.CancelReason is { } reason ? ReasonName(reason) : null, play.CreatedAtUtc, play.UpdatedAtUtc, play.PlannedAtUtc, play.EndedAtUtc,
        play.HasReview, play.Origin == PlayOrigin.Retrospective ? "retrospective" : "planned", play.ReviewedAtUtc, play.TradingMode)
    {
        ReviewCompletion = CompletionName(play.ReviewCompletion), ReviewCompletedAtUtc = play.ReviewCompletedAtUtc,
    };

    private IPaperTradingService RequirePaper() => paper ?? throw new ProblemException(503, "Paper execution is unavailable.");
    private static string Mode(string? mode) => mode switch
    {
        null or "real" => "real", "paper" => "paper",
        _ => throw new ProblemException(400, "Trading mode must be real or paper.")
    };

    public static string? CompletionName(ReviewCompletionKind? kind) => kind switch
    {
        ReviewCompletionKind.Completed => "completed", ReviewCompletionKind.NotNeeded => "not-needed", ReviewCompletionKind.Legacy => "legacy",
        _ => null,
    };

    public static PlayDto ToDto(Play play)
    {
        var plan = PlayDocuments.Read(play.Plan);
        return new(Summary(play, plan.Direction), plan, PlayDocuments.ReadDrawings(play.Drawings), play.Review, play.Answers);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex ContractPattern();
}
