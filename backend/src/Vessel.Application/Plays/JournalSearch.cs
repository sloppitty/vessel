using System.Globalization;
using System.Text;
using Vessel.Application.Common;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Application.Plays.Execution;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;

namespace Vessel.Application.Plays;

/// <summary>
/// Journal-wide search (Phase 2, item 13): the fields a Play's search projection holds, their normalization, the query rules
/// and excerpts, and the ended Play's outcome the Plays list shows from the same projection (F-07). Persistence writes the
/// projection; these rules are shared, so a query is folded exactly as the stored text was.
/// </summary>
public static class JournalSearch
{
    public const int MinQueryLength = 2, MaxQueryLength = 100, ExcerptLength = 160, PageSize = 50, MaxOffset = 1_000_000;

    /// <summary>
    /// The projection rules. Raise it when what a projection holds or how it is computed changes; rows of an older version read
    /// as not indexed until <c>play-search-rebuild --apply</c> rewrites them.
    /// </summary>
    public const int Version = 1;

    /// <summary>Searchable fields in the order a first match is chosen. Log entries and image notes are one row each.</summary>
    public static readonly string[] Fields =
        ["title", "thesis", "invalidation", "strategy", "evidence", "review", "lesson", "mistakeTag", "nextAction", "log", "image"];

    /// <summary>Positions order rows by field, then by item; items of one field stay below the next field's.</summary>
    public const int FieldStride = 1_000_000;

    public static int Position(string field, int item)
    {
        var index = Array.IndexOf(Fields, field);
        if (index < 0 || item is < 0 or >= FieldStride) throw new ArgumentOutOfRangeException(nameof(field));
        return index * FieldStride + item;
    }

    public static string FieldOf(int position) => Fields[position / FieldStride];

    public const string NotIndexedReason =
        "Not indexed yet. The journal index is filled once after an upgrade with the play-search-rebuild command.";
    public const string CancelledReason = "A cancelled Play never filled, so it has no result.";

    /// <summary>
    /// Plain text for matching and excerpts: compatibility-composed (NFKC), every run of white space or control characters one
    /// space, invisible format characters dropped, trimmed. An unpaired surrogate becomes U+FFFD.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var composed = Paired(text);
        composed = composed.IsNormalized(NormalizationForm.FormKC) ? composed : composed.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(composed.Length);
        var space = false;
        foreach (var c in composed)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = builder.Length > 0; continue; }
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            if (space) { builder.Append(' '); space = false; }
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>
    /// Case-folded normalized text with the same length, so a position in it is the same position in the text. Invariant
    /// lowering maps each character alone; the per-character fallback keeps the guarantee if a mapping ever changed length.
    /// </summary>
    public static string Fold(string normalized)
    {
        var folded = normalized.ToLowerInvariant();
        return folded.Length == normalized.Length ? folded : string.Create(normalized.Length, normalized, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++) span[i] = char.ToLowerInvariant(source[i]);
        });
    }

    /// <summary>
    /// The owner's query as shown and as matched: trimmed, normalized and folded like the stored text, 2 to 100 characters.
    /// </summary>
    public static (string Query, string Folded) Query(string? q)
    {
        var trimmed = q?.Trim() ?? "";
        var query = trimmed.Length > MaxQueryLength ? "" : Normalize(trimmed);
        if (query.Length is < MinQueryLength or > MaxQueryLength)
            throw new ProblemException(400, $"Search the journal with {MinQueryLength} to {MaxQueryLength} characters.");
        return (query, Fold(query));
    }

    /// <summary>A LIKE pattern for a substring, with <c>\</c>, <c>%</c> and <c>_</c> escaped by <c>\</c>.</summary>
    public static string LikePattern(string folded) =>
        "%" + folded.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";

    /// <summary>
    /// About <see cref="ExcerptLength"/> characters around the first match, with an ellipsis where text was cut, and where the
    /// match starts in the excerpt. A surrogate pair is never split.
    /// </summary>
    public static (string Excerpt, int Start, int Length) Excerpt(string text, string folded, string query)
    {
        var at = folded.Length == text.Length ? folded.IndexOf(query, StringComparison.Ordinal) : -1;
        var length = at < 0 ? 0 : query.Length;
        var window = Math.Max(ExcerptLength, length);
        var start = at < 0 ? 0 : Math.Max(0, at - (window - length) / 2);
        var end = Math.Min(text.Length, start + window);
        start = Math.Max(0, Math.Min(start, end - window));
        if (start > 0 && char.IsLowSurrogate(text[start])) start--;
        if (end < text.Length && char.IsLowSurrogate(text[end])) end++;
        var prefix = start > 0 ? "…" : "";
        var excerpt = prefix + text[start..end] + (end < text.Length ? "…" : "");
        return (excerpt, at < 0 ? 0 : at - start + prefix.Length, length);
    }

    /// <summary>
    /// The texts a Play's projection holds, as (field, item, raw text): its title, the current plan's notes, the review, its
    /// answers' lesson, mistake tag and next action, each log entry and each image note, oldest first. Blank texts are left
    /// out after normalization.
    /// </summary>
    public static IEnumerable<(string Field, int Item, string Text)> Texts(Play play, IEnumerable<string> log, IEnumerable<string> images)
    {
        yield return ("title", 0, play.Title);
        if (Notes(play.Plan) is { } notes)
        {
            yield return ("thesis", 0, notes.Thesis);
            yield return ("invalidation", 0, notes.Invalidation);
            yield return ("strategy", 0, notes.Strategy);
            yield return ("evidence", 0, notes.Evidence);
        }
        yield return ("review", 0, play.Review);
        yield return ("lesson", 0, play.ReviewLesson);
        yield return ("mistakeTag", 0, play.ReviewMistakeTag ?? "");
        yield return ("nextAction", 0, play.ReviewNextAction);
        var item = 0;
        foreach (var entry in log) yield return ("log", item++, entry);
        item = 0;
        foreach (var note in images) yield return ("image", item++, note);
    }

    // A stored plan that cannot be read has no notes to search; the Play itself stays searchable by its other fields.
    private static PlanNotes? Notes(string plan)
    {
        try { return PlayDocuments.Read(plan).Notes; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // Normalization refuses unpaired surrogates, which JSON can carry; they are not text anyone can search for.
    private static string Paired(string text)
    {
        char[]? chars = null;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
            if (char.IsSurrogate(text[i])) (chars ??= text.ToCharArray())[i] = '�';
        }
        return chars is null ? text : new string(chars);
    }

    /// <summary>
    /// An ended Play's outcome by Review's own rules (F-07). A Closed Play gets its latest actual exit execution and, when
    /// Review scores it (<see cref="ReviewService.Score"/>), its venue result and estimated R; an excluded one keeps the reason
    /// code and no figures. Cancelled and Unresolved Plays have no result; Draft, Planned, Paused and Open are active. Nothing
    /// unknown is zero. <paramref name="facts"/> are the facts Review and sizing read, needed for a Closed Play only.
    /// </summary>
    public static PlayOutcomeFacts Outcome(Play play, ClosedPlayFacts? facts, DateTimeOffset now)
    {
        switch (play.Status)
        {
            case PlayStatus.Cancelled: return new(PlaySearchStates.Cancelled, null, null, null, null);
            case PlayStatus.Unresolved: return new(PlaySearchStates.Unresolved, null, null, null, null);
            case not PlayStatus.Closed: return new(PlaySearchStates.Active, null, null, null, null);
        }
        ArgumentNullException.ThrowIfNull(facts);
        var exit = facts.Paper is { } paper
            ? paper.Executions.Where(e => e.Role != "entry").Select(e => (DateTimeOffset?)e.OccurredAtUtc).Max()
            : PlayExitTime.LatestExit(facts.Fills);
        var score = ReviewService.Score(facts, now, facts.IncompleteOrders is { Count: > 0 });
        if (score.Result is not { } result || score.Exclusion is not null)
            return new(score.Exclusion == "fees" ? PlaySearchStates.Fees : PlaySearchStates.Executions, exit, null, null, null);
        string? reason = null;
        if (result.RMultiple is null)
        {
            var entries = facts.Fills.Where(f => f.Role == OrderLinkRole.Entry).Select(f => f.Fill.OccurredAtUtc).ToList();
            reason = play.Origin == PlayOrigin.Retrospective ? "retrospective"
                : facts.Paper is null && entries.Count > 0 && !facts.Revisions.Any(r => r.CreatedAtUtc <= entries.Min()) ? "plan-after-fill"
                : "no-stop";
        }
        return new(PlaySearchStates.Scored, exit, result.NetResult, result.RMultiple, reason);
    }

    /// <summary>The list's outcome of a row: null facts mean the Play is not indexed yet, which is never read as no result.</summary>
    public static PlayOutcomeSummaryDto Dto(PlayOutcomeFacts? facts) => facts is null
        ? new("not-indexed", null, null, null, NotIndexedReason)
        : new(facts.State, facts.ExitAtUtc, ExactDecimal.Format(facts.ResultUsd), ExactDecimal.Format(facts.EstimatedR), facts.State switch
        {
            PlaySearchStates.Executions => PlayReceipts.ExecutionsReason,
            PlaySearchStates.Fees => PlayReceipts.FeesReason,
            PlaySearchStates.Unresolved => PlayReceipts.UnresolvedNote,
            PlaySearchStates.Cancelled => CancelledReason,
            PlaySearchStates.Scored => facts.RReason switch
            {
                "retrospective" => PlayReceipts.RetrospectiveReason,
                "plan-after-fill" => PlayReceipts.PlanAfterFillReason,
                "no-stop" => PlayReceipts.NoStopReason,
                _ => null,
            },
            _ => null,
        });
}

/// <summary>
/// An ended Play's outcome as its search projection stores it (F-07): the state (<see cref="PlaySearchStates"/>), the latest
/// actual exit, the scored result and estimated R, and why a scored Play has no R.
/// </summary>
public sealed record PlayOutcomeFacts(string State, DateTimeOffset? ExitAtUtc, decimal? ResultUsd, decimal? EstimatedR, string? RReason);
