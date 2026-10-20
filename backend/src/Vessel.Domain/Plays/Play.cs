using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Markets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vessel.Domain.Plays;

// Open and Closed are reached from linked venue fills, which arrive with order linking.
public enum PlayStatus { Draft, Planned, Paused, Open, Closed, Cancelled }

public enum InstrumentSource { Venue, Manual }
public enum PlayOrigin { Planned, Retrospective }
/// <summary>
/// How a review was completed (phase 1, 1.2): by the owner's Complete review or a save of changed answers, by No review
/// needed on a Cancelled Play, or by the migration that kept Plays already listed as Reviewed.
/// </summary>
public enum ReviewCompletionKind { Completed, NotNeeded, Legacy }

public sealed record ReviewAnswers(string? Adherence = null, string? MistakeTag = null, string Lesson = "", string NextAction = "")
{
    [JsonIgnore]
    public bool IsAnswered => Adherence is not null || !string.IsNullOrWhiteSpace(MistakeTag) ||
        !string.IsNullOrWhiteSpace(Lesson) || !string.IsNullOrWhiteSpace(NextAction);
}

public enum CancelReason { Invalidated, Missed, ChangedMind, Expired, Mistake, Other }

/// <summary>Who changed the status: the owner, or linked venue fills.</summary>
public enum StatusChangeSource { Owner, Venue }

// One trading idea. The plan is an application-validated JSON document; after planning, every plan
// change is an append-only revision. It contains neither inferred executions nor an invented thesis.
public sealed class Play : IOwned
{
    public const int MaxTitleLength = 200;
    public const int MaxReviewLength = 20000;
    public const int MaxCancelNoteLength = 2000;

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid AccountId { get; private set; }
    public string VenueId { get; private set; } = null!;
    /// <summary>Venue contract ID or manual label; null while a Draft has no instrument.</summary>
    public string? ContractId { get; private set; }
    public InstrumentSource InstrumentSource { get; private set; }
    public PerpetualInstrument? Instrument => ContractId is null ? null : new(VenueId, ContractId);
    public PlayStatus Status { get; private set; }
    public string Title { get; private set; } = "";
    public string Plan { get; private set; } = null!;
    /// <summary>The plan's direction, kept in step with <see cref="Plan"/> so lists never parse the plan; empty if it has none.</summary>
    public string Direction { get; private set; } = "";
    public string Drawings { get; private set; } = "{}";
    public string Review { get; private set; } = "";
    public PlayOrigin Origin { get; private set; }
    public string? ReviewAdherence { get; private set; }
    public string? ReviewMistakeTag { get; private set; }
    public string ReviewLesson { get; private set; } = "";
    public string ReviewNextAction { get; private set; } = "";
    public int ReviewRevision { get; private set; }
    /// <summary>When the review text or answers last changed; it never completes the review.</summary>
    public DateTimeOffset? ReviewedAtUtc { get; private set; }
    /// <summary>The review's completion, only ever set explicitly; null means it still needs review once the Play ends.</summary>
    public ReviewCompletionKind? ReviewCompletion { get; private set; }
    public DateTimeOffset? ReviewCompletedAtUtc { get; private set; }
    public ReviewAnswers Answers => new(ReviewAdherence, ReviewMistakeTag, ReviewLesson, ReviewNextAction);
    public bool HasReview => !string.IsNullOrWhiteSpace(Review) || Answers.IsAnswered;
    /// <summary>A Play whose review can be completed: it has ended.</summary>
    public bool IsReviewable => Status is PlayStatus.Closed or PlayStatus.Cancelled;
    /// <summary>The latest plan revision number; zero while the Play is a Draft.</summary>
    public int PlanRevision { get; private set; }
    /// <summary>Increases on every change, for optimistic concurrency.</summary>
    public long Version { get; private set; }
    public CancelReason? CancelReason { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? PlannedAtUtc { get; private set; }
    public DateTimeOffset? EndedAtUtc { get; private set; }
    private Play() { }

    public Play(Guid id, Account account, PerpetualInstrument? instrument, InstrumentSource source, string plan, DateTimeOffset now)
    {
        if (id == Guid.Empty) throw new ArgumentException("Play ID must be nonempty.");
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan);
        Id = id;
        OwnerId = account.OwnerId;
        AccountId = account.Id;
        VenueId = account.VenueId;
        SetInstrument(instrument, source);
        Status = PlayStatus.Draft;
        SetPlan(plan);
        Version = 1;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public bool IsEditable => Status is PlayStatus.Draft or PlayStatus.Planned or PlayStatus.Paused or PlayStatus.Open;

    /// <summary>Account and instrument are part of the idea until it is planned.</summary>
    public void MoveTo(Account account, PerpetualInstrument? instrument, InstrumentSource source, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.OwnerId != OwnerId) throw new ArgumentException("Account belongs to another owner.");
        if (account.Id == AccountId && instrument == Instrument && source == InstrumentSource) return;
        if (Status != PlayStatus.Draft) throw new DomainRuleException("The account and instrument are fixed once a Play is planned.");
        AccountId = account.Id;
        VenueId = account.VenueId;
        SetInstrument(instrument, source);
        Touch(now);
    }

    public PlayReviewRevision? Annotate(string title, string drawings, string review, DateTimeOffset now, ReviewAnswers? answers = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentException.ThrowIfNullOrWhiteSpace(drawings);
        title = title.Trim();
        if (title.Length > MaxTitleLength) throw new DomainValidationException($"Titles are limited to {MaxTitleLength} characters.");
        if (review.Length > MaxReviewLength) throw new DomainValidationException($"Reviews are limited to {MaxReviewLength} characters.");
        answers ??= Answers;
        if (answers.Lesson is null || answers.NextAction is null ||
            answers.Adherence is not (null or "followed" or "deviated" or "not-applicable") ||
            answers.MistakeTag?.Length > 100 || answers.Lesson.Length > 4000 || answers.NextAction.Length > 4000 ||
            answers.MistakeTag?.Any(char.IsControl) == true ||
            new[] { answers.Lesson, answers.NextAction }.Any(s => s.Any(c => char.IsControl(c) && c is not ('\r' or '\n'))))
            throw new DomainValidationException("Review answers must be bounded plain text with a valid adherence.");
        if (answers.IsAnswered && Status is not (PlayStatus.Closed or PlayStatus.Cancelled))
            throw new DomainRuleException("Structured review answers are only available for closed or cancelled Plays.");
        if (Origin == PlayOrigin.Retrospective && answers.Adherence is not (null or "not-applicable"))
            throw new DomainRuleException("A retrospective Play has no contemporaneous plan to assess adherence against.");
        var previousAnswers = Answers;
        var changedReview = review != Review || answers != previousAnswers;
        var sameDrawings = SameDocument(drawings, Drawings);
        if (title == Title && sameDrawings && !changedReview) return null;
        if (!IsEditable && (title != Title || !sameDrawings))
            throw new DomainRuleException("Closed and cancelled Plays keep their plan; only the review can change.");
        Title = title;
        Drawings = drawings;
        Review = review;
        ReviewAdherence = answers.Adherence;
        ReviewMistakeTag = answers.MistakeTag;
        ReviewLesson = answers.Lesson;
        ReviewNextAction = answers.NextAction;
        var completes = answers != previousAnswers && answers.IsAnswered && IsReviewable && ReviewCompletion is null;
        Touch(now);
        if (!changedReview) return null;
        ReviewedAtUtc = HasReview ? now : null;
        // Saving changed answers on an ended Play completes its review (decision 3); text alone never does.
        if (completes) Complete(ReviewCompletionKind.Completed, now);
        return new PlayReviewRevision(this, checked(++ReviewRevision), now, completes ? ReviewActions.Completed : ReviewActions.Edited);
    }

    /// <summary>The owner completes the review of an ended Play, whether or not its text changed.</summary>
    public PlayReviewRevision CompleteReview(DateTimeOffset now)
    {
        if (!IsReviewable) throw new DomainRuleException("A review is completed once the Play is closed or cancelled.");
        if (ReviewCompletion is not null) throw new DomainRuleException("This review is already complete.");
        Complete(ReviewCompletionKind.Completed, now);
        Touch(now);
        return new PlayReviewRevision(this, checked(++ReviewRevision), now, ReviewActions.Completed);
    }

    /// <summary>Review again: the completion is cleared and the Play needs review; text, answers and history stay.</summary>
    public PlayReviewRevision ReopenReview(DateTimeOffset now)
    {
        if (ReviewCompletion is null) throw new DomainRuleException("This review is not complete.");
        ReviewCompletion = null;
        ReviewCompletedAtUtc = null;
        Touch(now);
        return new PlayReviewRevision(this, checked(++ReviewRevision), now, ReviewActions.Reopened);
    }

    /// <summary>A Cancelled Play that never filled leaves the queue in one step; the caller has checked it has no fills.</summary>
    public PlayReviewRevision MarkNoReviewNeeded(DateTimeOffset now)
    {
        if (Status != PlayStatus.Cancelled) throw new DomainRuleException("Only a cancelled Play can be marked as needing no review.");
        if (ReviewCompletion is not null) throw new DomainRuleException("This review is already complete.");
        Complete(ReviewCompletionKind.NotNeeded, now);
        Touch(now);
        return new PlayReviewRevision(this, checked(++ReviewRevision), now, ReviewActions.NotNeeded);
    }

    private void Complete(ReviewCompletionKind kind, DateTimeOffset now)
    {
        ReviewCompletion = kind;
        ReviewCompletedAtUtc = now;
    }

    public static Play Retrospective(Guid id, Account account, PerpetualInstrument instrument, string emptyPlan,
        DateTimeOffset closedAt, DateTimeOffset now, string title)
    {
        var play = new Play(id, account, instrument, InstrumentSource.Venue, emptyPlan, now);
        play.Origin = PlayOrigin.Retrospective;
        play.Status = PlayStatus.Closed;
        play.EndedAtUtc = closedAt;
        ArgumentNullException.ThrowIfNull(title);
        title = title.Trim();
        if (title.Length > MaxTitleLength) throw new DomainValidationException($"Titles are limited to {MaxTitleLength} characters.");
        play.Title = title;
        return play;
    }

    public PlayStatusChange RecordManualOpen(DateTimeOffset now) => Status == PlayStatus.Planned
        ? Transition(PlayStatus.Open, null, "Owner-recorded manual entry, not a venue observation.", now)
        : throw new DomainRuleException("Only a planned Play can open from manual facts.");

    public PlayStatusChange RecordManualClose(DateTimeOffset closedAt, DateTimeOffset now)
    {
        if (Status != PlayStatus.Open) throw new DomainRuleException("Only an open Play can close from manual facts.");
        var change = Transition(PlayStatus.Closed, null, "Owner-recorded manual ledger is flat.", now);
        EndedAtUtc = closedAt;
        return change;
    }

    public void RecordManualCorrection(DateTimeOffset? closedAt, DateTimeOffset now)
    {
        if (Status == PlayStatus.Closed) EndedAtUtc = closedAt ?? throw new DomainRuleException("A closed manual ledger must stay closed.");
        Touch(now);
    }

    /// <summary>Changes a Draft's plan without a revision, or records the next revision of a planned one.</summary>
    public PlayPlanRevision? ChangePlan(string plan, string? reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plan);
        if (SameDocument(plan, Plan)) return null;
        if (!IsEditable) throw new DomainRuleException("Closed and cancelled Plays keep their plan.");
        if (Status == PlayStatus.Draft)
        {
            SetPlan(plan);
            Touch(now);
            return null;
        }
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainRuleException("Say why the plan changed. The reason is kept with the revision.");
        SetPlan(plan);
        Touch(now);
        return new PlayPlanRevision(this, checked(++PlanRevision), reason, now);
    }

    /// <summary>
    /// Plans a Draft as revision 1. <paramref name="preflight"/> is the checklist the owner saw at Plan it, as validated
    /// JSON; it never blocks planning (phase 1, 1.5).
    /// </summary>
    public (PlayStatusChange Change, PlayPlanRevision Revision) MarkPlanned(DateTimeOffset now, string? preflight = null)
    {
        if (Status != PlayStatus.Draft) throw new DomainRuleException("Only a Draft can be planned.");
        if (ContractId is null) throw new DomainRuleException("Choose an instrument before planning.");
        var change = Transition(PlayStatus.Planned, null, null, now);
        PlannedAtUtc = now;
        PlanRevision = 1;
        return (change, new PlayPlanRevision(this, 1, "Planned", now) { Preflight = preflight });
    }

    public PlayStatusChange Pause(DateTimeOffset now) => Status == PlayStatus.Planned
        ? Transition(PlayStatus.Paused, null, null, now)
        : throw new DomainRuleException("Only a planned Play without fills can be paused.");

    public PlayStatusChange Resume(DateTimeOffset now) => Status == PlayStatus.Paused
        ? Transition(PlayStatus.Planned, null, null, now)
        : throw new DomainRuleException("Only a paused Play can be resumed.");

    /// <summary>The first linked entry fill. A fill while paused still opens the Play; the note says so.</summary>
    public PlayStatusChange MarkOpen(string note, DateTimeOffset now) => Status is PlayStatus.Planned or PlayStatus.Paused
        ? Transition(PlayStatus.Open, null, Status == PlayStatus.Paused ? $"{note} Filled while paused." : note, now, StatusChangeSource.Venue)
        : throw new DomainRuleException("Only a planned or paused Play can open.");

    /// <summary>All linked entry quantity has exited and no linked entry order rests.</summary>
    public PlayStatusChange MarkClosed(string note, DateTimeOffset now)
    {
        if (Status != PlayStatus.Open) throw new DomainRuleException("Only an open Play can close.");
        var change = Transition(PlayStatus.Closed, null, note, now, StatusChangeSource.Venue);
        EndedAtUtc = now;
        return change;
    }

    public PlayStatusChange Cancel(CancelReason reason, string? note, DateTimeOffset now)
    {
        if (Status is not (PlayStatus.Draft or PlayStatus.Planned or PlayStatus.Paused))
            throw new DomainRuleException("Only a Play without fills can be cancelled.");
        if (!Enum.IsDefined(reason)) throw new DomainValidationException("Unknown cancel reason.");
        var change = Transition(PlayStatus.Cancelled, reason, note, now);
        CancelReason = reason;
        EndedAtUtc = now;
        return change;
    }

    private PlayStatusChange Transition(PlayStatus to, CancelReason? reason, string? note, DateTimeOffset now,
        StatusChangeSource source = StatusChangeSource.Owner)
    {
        var change = new PlayStatusChange(this, Status, to, reason, note, now, source);
        Status = to;
        Touch(now);
        return change;
    }

    private void SetPlan(string plan)
    {
        using var document = JsonDocument.Parse(plan);
        Plan = plan;
        Direction = document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("direction", out var direction) && direction.ValueKind == JsonValueKind.String
            ? direction.GetString()! : "";
    }

    private void SetInstrument(PerpetualInstrument? instrument, InstrumentSource source)
    {
        if (instrument is not null && instrument.VenueId != VenueId)
            throw new ArgumentException("Instrument must belong to the account venue.");
        if (!Enum.IsDefined(source)) throw new DomainValidationException("Unknown instrument source.");
        ContractId = instrument?.ContractId;
        InstrumentSource = source;
    }

    private void Touch(DateTimeOffset now)
    {
        Version = checked(Version + 1);
        UpdatedAtUtc = now;
    }

    private static bool SameDocument(string left, string right)
    {
        if (left == right) return true;
        using var a = JsonDocument.Parse(left);
        using var b = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(a.RootElement, b.RootElement);
    }
}

/// <summary>An append-only snapshot of a planned Play's plan and the reason it changed.</summary>
public sealed class PlayPlanRevision : IOwned
{
    public const int MaxReasonLength = 2000;

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid PlayId { get; private set; }
    public int Number { get; private set; }
    public PlayStatus Status { get; private set; }
    public string Plan { get; private set; } = null!;
    public string Reason { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    /// <summary>The pre-flight checklist recorded with revision 1 at Plan it, as JSON; null on later revisions and older plans.</summary>
    public string? Preflight { get; internal init; }
    private PlayPlanRevision() { }

    internal PlayPlanRevision(Play play, int number, string reason, DateTimeOffset now)
    {
        reason = reason.Trim();
        if (reason.Length > MaxReasonLength) throw new DomainValidationException($"Reasons are limited to {MaxReasonLength} characters.");
        Id = Guid.NewGuid();
        OwnerId = play.OwnerId;
        PlayId = play.Id;
        Number = number;
        Status = play.Status;
        Plan = play.Plan;
        Reason = reason;
        CreatedAtUtc = now;
    }
}

public sealed class PlayStatusChange : IOwned
{
    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid PlayId { get; private set; }
    public PlayStatus From { get; private set; }
    public PlayStatus To { get; private set; }
    public CancelReason? Reason { get; private set; }
    public string? Note { get; private set; }
    public StatusChangeSource Source { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    private PlayStatusChange() { }

    internal PlayStatusChange(Play play, PlayStatus from, PlayStatus to, CancelReason? reason, string? note, DateTimeOffset now,
        StatusChangeSource source)
    {
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note?.Length > Play.MaxCancelNoteLength) throw new DomainValidationException($"Notes are limited to {Play.MaxCancelNoteLength} characters.");
        Id = Guid.NewGuid();
        OwnerId = play.OwnerId;
        PlayId = play.Id;
        From = from;
        To = to;
        Reason = reason;
        Note = note;
        Source = source;
        OccurredAtUtc = now;
    }
}
