using System.Text.Json;
using System.Text.Json.Serialization;
using Vessel.Application.Common;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Plays;

public sealed record CreatePlayRequest(
    [property: JsonRequired] Guid AccountId,
    string? Instrument,
    [property: JsonRequired] string InstrumentSource,
    [property: JsonRequired] string Title,
    [property: JsonRequired] PlayPlanDocument Plan,
    JsonElement? Drawings = null,
    string? Review = null,
    ReviewAnswers? StructuredReview = null);

public sealed record UpdatePlayRequest(
    [property: JsonRequired] long ExpectedVersion,
    [property: JsonRequired] Guid AccountId,
    string? Instrument,
    [property: JsonRequired] string InstrumentSource,
    [property: JsonRequired] string Title,
    [property: JsonRequired] PlayPlanDocument Plan,
    [property: JsonRequired] JsonElement Drawings,
    [property: JsonRequired] string Review,
    string? RevisionReason = null,
    ReviewAnswers? StructuredReview = null);

/// <summary>Status is planned, paused (from planned), planned again to resume, or cancelled with a reason.</summary>
public sealed record ChangePlayStatusRequest(
    [property: JsonRequired] long ExpectedVersion,
    [property: JsonRequired] string Status,
    string? Reason = null,
    string? Note = null,
    IReadOnlyList<PreflightCheckDto>? Preflight = null);

public sealed record PlaySummaryDto(Guid Id, string Title, string Status, Guid AccountId, string VenueId, string? Instrument,
    string InstrumentSource, string Direction, int PlanRevision, long Version, string? CancelReason,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? PlannedAtUtc, DateTimeOffset? EndedAtUtc,
    bool HasReview, string Origin = "planned", DateTimeOffset? ReviewedAtUtc = null);

public sealed record PlayListDto(IReadOnlyList<PlaySummaryDto> Plays, string View, int Page, int PageSize, int TotalCount);
public sealed record PlayCountsDto(int Drafts, int Progress, int Review, int Reviewed, int Closed, int Cancelled, int All);

/// <summary>
/// List views. Drafts and In progress are unpaged (capped); the ended views and All are paged. "Reviewed" means
/// a Closed or Cancelled Play whose review has text or answers, or that was ever reviewed (<c>ReviewedAtUtc</c>).
/// </summary>
public enum PlayListView { Drafts, Progress, Review, Reviewed, Closed, Cancelled, All }

public sealed record PlayListPage(int TotalCount, int Page, List<PlayListRow> Rows);

/// <summary>
/// Search is a trimmed, case-insensitive substring of title or instrument. A scope keeps its enabled accounts only;
/// a null scope is the Plays page: every owner Play, including those on disabled accounts.
/// </summary>
public sealed record PlayListFilter(string? Search, ListScope? Scope);

/// <summary>Summary columns only. Plan, drawings and review text are never read for a list.</summary>
public sealed record PlayListRow
{
    public Guid Id { get; init; }
    public string Title { get; init; } = "";
    public PlayStatus Status { get; init; }
    public Guid AccountId { get; init; }
    public string VenueId { get; init; } = "";
    public string? ContractId { get; init; }
    public InstrumentSource InstrumentSource { get; init; }
    public string Direction { get; init; } = "";
    public int PlanRevision { get; init; }
    public long Version { get; init; }
    public CancelReason? CancelReason { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? PlannedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public bool HasReview { get; init; }
    public PlayOrigin Origin { get; init; }
    public DateTimeOffset? ReviewedAtUtc { get; init; }
}

/// <summary>Row counts by status and whether the Play counts as reviewed, from one grouped query.</summary>
public sealed record PlayCountRow(PlayStatus Status, bool Reviewed, int Count);

public sealed record PlayDto(PlaySummaryDto Summary, PlayPlanDocument Plan, JsonElement Drawings, string Review,
    ReviewAnswers? StructuredReview = null);

public sealed record PlanRevisionDto(int Number, string Status, string Reason, DateTimeOffset CreatedAtUtc, PlayPlanDocument Plan)
{
    /// <summary>The pre-flight recorded at Plan it, on revision 1 only; null for older plans and later revisions.</summary>
    public IReadOnlyList<PreflightCheckDto>? Preflight { get; init; }
}

/// <summary>One of the owner's recent next actions, with the Play it was written for (phase 1, 1.5).</summary>
public sealed record NextActionDto(Guid PlayId, string Title, string NextAction, DateTimeOffset? ReviewedAtUtc);
public sealed record StatusChangeDto(string From, string To, string? Reason, string? Note, DateTimeOffset OccurredAtUtc, string Source = "owner");
public sealed record ReviewRevisionDto(int Number, DateTimeOffset OccurredAtUtc, string Review, ReviewAnswers StructuredReview);
public sealed record PlayHistoryDto(IReadOnlyList<PlanRevisionDto> Revisions, IReadOnlyList<StatusChangeDto> StatusChanges,
    IReadOnlyList<ReviewRevisionDto>? Reviews = null);

// Application owns this bounded port; EF details stay in Persistence. Reads are owner-filtered.
public interface IPlayStore
{
    /// <summary>Whether the scope's portfolio or account exists for this owner; <c>all</c> and <c>unassigned</c> always do.</summary>
    Task<bool> ScopeExistsAsync(ListScope scope, CancellationToken ct);
    /// <summary>
    /// One consistent page of a view: the exact total, the requested page clamped to it and its ordered summary rows
    /// (updated desc for Drafts, In progress and All; ended desc, updated desc otherwise; then ID), read from one snapshot.
    /// </summary>
    Task<PlayListPage> PageAsync(PlayListView view, PlayListFilter filter, int page, int pageSize, CancellationToken ct);
    Task<List<PlayCountRow>> CountsAsync(PlayListFilter filter, CancellationToken ct);
    Task<Play?> FindAsync(Guid id, CancellationToken ct);
    Task<Account?> AccountAsync(Guid id, CancellationToken ct);
    Task<List<PlayPlanRevision>> RevisionsAsync(Guid playId, CancellationToken ct);
    Task<List<PlayStatusChange>> StatusChangesAsync(Guid playId, CancellationToken ct);
    Task<List<PlayReviewRevision>> ReviewsAsync(Guid playId, CancellationToken ct);
    /// <summary>
    /// The newest non-blank next actions of the owner's Closed or Cancelled Plays, by review time, at most
    /// <paramref name="limit"/>; only the ID, title, next action and review time are read.
    /// </summary>
    Task<List<NextActionDto>> NextActionsAsync(int limit, CancellationToken ct);
    /// <summary>Entries referenced by linked orders or manual facts; their price, share and existence are fixed.</summary>
    Task<IReadOnlySet<string>> FilledEntryIdsAsync(Guid playId, CancellationToken ct);
    /// <summary>
    /// The executions of the Play's entries: each fill of a linked entry order, through the shared order-key rule, and
    /// each manual entry execution (with no fill). Facts only; the caller decides which count.
    /// </summary>
    Task<List<EntryExecution>> EntryExecutionsAsync(Guid playId, CancellationToken ct);
    /// <summary>Linked orders of the Play with the venue's order ID, for the owner's messages.</summary>
    Task<List<LinkedPlanReference>> LinkedPlanReferencesAsync(Guid playId, CancellationToken ct);
    void Add(Play play);
    void Add(PlayPlanRevision revision);
    void Add(PlayStatusChange change);
    void Add(PlayReviewRevision revision);
    /// <summary>Persists pending changes through the active unit of work without committing; a concurrent Play write is a 409.</summary>
    Task SaveAsync(CancellationToken ct);
    /// <summary>
    /// Requires an active unit of work and holds account, execution advisory, then Play locks until its completion.
    /// Inside it the Play is read with its committed values.
    /// Cross-account moves first hold the owner management lock and reload the locked destination account.
    /// </summary>
    Task<T> WithPlayLockAsync<T>(Guid id, Func<Task<T>> action, CancellationToken ct, Guid? destinationAccountId = null);
    /// <summary>Deletes the Play with its evidence records and returns the evidence object keys to remove.</summary>
    Task<IReadOnlyList<string>> DeleteAsync(Play play, CancellationToken ct);
}

/// <summary>A linked order that fixes a plan level, with its venue order ID.</summary>
public sealed record LinkedPlanReference(PlayOrderLink Link, string ExternalOrderId);

/// <summary>One execution of a plan entry: a fill of a linked entry order, or a manual entry execution (<paramref name="Fill"/> null).</summary>
public sealed record EntryExecution(string EntryId, ImportedFill? Fill);
