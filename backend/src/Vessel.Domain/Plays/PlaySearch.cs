using Vessel.Domain.Common;

namespace Vessel.Domain.Plays;

/// <summary>
/// The journal search projection of one Play (Phase 2, item 13): a derived read model, never a fact. It is rewritten from
/// the Play's facts in the unit of work of every change to them, and carries the ended Play's real exit time, scored
/// result and estimated R for the Plays list (F-07). Unknown stays null.
/// </summary>
public sealed class PlaySearchEntry : IOwned
{
    public Guid OwnerId { get; set; }
    public Guid PlayId { get; set; }
    /// <summary>The projection rules that wrote it; an older version reads as not indexed until it is rebuilt.</summary>
    public int Version { get; set; }
    public DateTimeOffset ProjectedAtUtc { get; set; }
    /// <summary>active, scored, executions, fees, unresolved, cancelled or not-allocated (<see cref="PlaySearchStates"/>).</summary>
    public string State { get; set; } = PlaySearchStates.Active;
    /// <summary>The latest actual exit execution of a Closed Play; null without one.</summary>
    public DateTimeOffset? ExitAtUtc { get; set; }
    /// <summary>Review's scored result; null unless the state is scored.</summary>
    public decimal? ResultUsd { get; set; }
    /// <summary>Estimated R (planned-stop model); null unless scored with a complete initial stop.</summary>
    public decimal? EstimatedR { get; set; }
    /// <summary>Why a scored Play has no R: retrospective, plan-after-fill or no-stop; null otherwise.</summary>
    public string? RReason { get; set; }
    /// <summary>
    /// When an exclusion that a fact dated after the projection caused expires: its latest execution time. From then the
    /// stored outcome is stale; readers compute it again by the same rules until a write reprojects it. Null otherwise.
    /// </summary>
    public DateTimeOffset? RecheckAtUtc { get; set; }
}

/// <summary>One searchable text of a Play: a field, or one log entry or image note, normalized and case-folded.</summary>
public sealed class PlaySearchText : IOwned
{
    public Guid OwnerId { get; set; }
    public Guid PlayId { get; set; }
    /// <summary>The field's order among the fields times a million, plus the item's order: the first match is the lowest.</summary>
    public int Position { get; set; }
    public string Field { get; set; } = "";
    /// <summary>Normalized plain text, shown in excerpts.</summary>
    public string Text { get; set; } = "";
    /// <summary><see cref="Text"/> case-folded, character for character, so a match position in it is one in the text.</summary>
    public string Folded { get; set; } = "";
}

public static class PlaySearchStates
{
    public const string Active = "active", Scored = "scored", Executions = "executions", Fees = "fees",
        Unresolved = "unresolved", Cancelled = "cancelled",
        /// <summary>A fill of the Play is allocated between Plays, so its venue result is not allocated (Phase 2, item 11).</summary>
        NotAllocated = "not-allocated";
}
