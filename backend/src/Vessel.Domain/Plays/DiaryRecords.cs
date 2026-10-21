using System.Text.Json;
using Vessel.Domain.Common;

namespace Vessel.Domain.Plays;

public sealed class PlayReviewRevision : IOwned
{
    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid PlayId { get; private set; }
    public int Number { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public string Review { get; private set; } = "";
    public string StructuredReview { get; private set; } = "{}";
    /// <summary>What happened to the review: edited, completed, reopened or not-needed (<see cref="ReviewActions"/>).</summary>
    public string Action { get; private set; } = ReviewActions.Edited;
    private PlayReviewRevision() { }
    internal PlayReviewRevision(Play play, int number, DateTimeOffset now, string action = ReviewActions.Edited)
    {
        Id = Guid.NewGuid(); OwnerId = play.OwnerId; PlayId = play.Id; Number = number;
        OccurredAtUtc = now; Review = play.Review;
        StructuredReview = JsonSerializer.Serialize(play.Answers);
        Action = action;
    }
}

/// <summary>Review history actions. Completion, reopening and no-review-needed are kept with the text they applied to.</summary>
public static class ReviewActions
{
    public const string Edited = "edited", Completed = "completed", Reopened = "reopened", NotNeeded = "not-needed";
    /// <summary>The completion was cleared because the Play left a final state (phase 1, 1.3a).</summary>
    public const string Cleared = "cleared";
}

/// <summary>
/// An append-only in-trade log entry (phase 1, 1.2): timestamped text with an optional state. It is never edited or
/// deleted, never counts as a review and never feeds Review metrics; a correction is a new entry.
/// </summary>
public sealed class PlayNote : IOwned
{
    public const int MaxTextLength = 2000;
    public static readonly IReadOnlyList<string> States = ["calm", "rushed", "fomo", "tired", "after-loss"];

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid PlayId { get; private set; }
    /// <summary>The client's request identity; a retry with the same one returns the entry already written.</summary>
    public Guid RequestId { get; private set; }
    public string Text { get; private set; } = "";
    public string? State { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    private PlayNote() { }

    public PlayNote(Play play, Guid requestId, string text, string? state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(play);
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (requestId == Guid.Empty || text.Length is 0 or > MaxTextLength || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new DomainValidationException($"A log entry is 1 to {MaxTextLength} characters of plain text with a request identity.");
        if (state is not null && !States.Contains(state))
            throw new DomainValidationException("A log entry's state is calm, rushed, fomo, tired or after-loss.");
        Id = Guid.NewGuid(); OwnerId = play.OwnerId; PlayId = play.Id; RequestId = requestId;
        Text = text; State = state; CreatedAtUtc = now;
    }
}

// These are owner-entered facts. They are never inserted into imported_orders or imported_fills.
public sealed class ManualExecution : IOwned
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid PlayId { get; set; }
    public string Role { get; set; } = null!;
    public string? EntryId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string Side { get; set; } = null!;
    public string Quantity { get; set; } = null!;
    public string Price { get; set; } = null!;
    public string? Fee { get; set; }
    public string? FeeCurrency { get; set; }
    public string Source { get; set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ManualExecutionHistory : IOwned
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid PlayId { get; set; }
    public Guid ExecutionId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string? Before { get; set; }
    public string After { get; set; } = null!;
    /// <summary>Shared by every row a correction batch changed together (phase 1, 1.3b); null for single-row writes.</summary>
    public Guid? BatchId { get; set; }
}

// The response snapshot makes a retry return the original result, even after later mutations.
public sealed class DiaryRequest : IOwned
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string PayloadHash { get; set; } = null!;
    public string Response { get; set; } = null!;
}
