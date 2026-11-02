using System.Text.Json.Serialization;
using Vessel.Application.Common;
using Vessel.Application.Sizing;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Plays.Execution;

/// <summary>
/// A current allocation with lines (Phase 2, item 11), its original fill, and the fill's retained order with whether that
/// order is known to miss executions (<see cref="ExecutionIntegrity.MissingExecutions(ImportedOrder, IEnumerable{ImportedFill})"/>).
/// </summary>
public sealed record AllocatedFill(FillAllocation Allocation, ImportedFill Fill, ImportedOrder? Order, bool OrderMissing)
{
    /// <summary>
    /// How many retained fills of the same order have no current allocation (Sol round 1 on item 11). On a split order, one
    /// whose allocated fill opens a Play (an entry line), they count for no Play until the owner allocates them.
    /// </summary>
    public int UnallocatedOrderFills { get; init; }
    /// <summary>The order opened a Play through an allocation: its other fills count only through allocations.</summary>
    public bool Splits => Allocation.Lines.Any(l => l.Role == AllocationRole.Entry);
}

/// <summary>
/// One fill as a Play counts it: its role, the plan entry and level it belongs to, and the fill. <paramref name="Line"/> is
/// set when the fill is allocated, and <paramref name="Fill"/> is then the Play's portion; <paramref name="Link"/> is the
/// Play's link to the fill's order, when it has one.
/// </summary>
public sealed record PlayFill(OrderLinkRole Role, string? EntryId, string? LevelId, ImportedFill Fill, PlayOrderLink? Link, FillAllocationLine? Line)
{
    public bool Allocated => Line is not null;
    public LinkedFill Linked => new(Role, Fill) { Allocated = Allocated };
}

/// <summary>
/// The read rule of owner-confirmed allocations (Phase 2, item 11): an allocated fill counts for a Play only through that
/// Play's own line, whatever the order links say; every other fill counts through the Play's linked orders, as before.
/// </summary>
public static class FillAllocations
{
    /// <summary>
    /// The fills of one Play: each fill of its linked orders (<paramref name="linked"/>, link and fill), with an allocated one
    /// replaced by the Play's line or left out when the Play has none, plus the lines that reach it without a link.
    /// </summary>
    public static List<PlayFill> For(Guid playId, IEnumerable<(PlayOrderLink Link, ImportedFill Fill)> linked,
        IReadOnlyCollection<AllocatedFill> allocations)
    {
        var byFill = allocations.Where(a => a.Allocation.Lines.Count > 0).ToDictionary(a => a.Fill.Id);
        // Sol round 1 on item 11: an order that opened a Play through an allocation (a flip's entry line) is split, and its
        // other fills count for no Play, not even through a link, until the owner allocates them.
        var split = byFill.Values.Where(a => a.Splits).Select(a => OrderKey.Of(a.Fill)).ToHashSet();
        var result = new List<PlayFill>();
        var reached = new HashSet<Guid>();
        var links = new Dictionary<Guid, PlayOrderLink>();
        foreach (var (link, fill) in linked)
        {
            links.TryAdd(fill.Id, link);
            if (!byFill.ContainsKey(fill.Id) && !split.Contains(OrderKey.Of(fill))) result.Add(new(link.Role, link.EntryId, link.LevelId, fill, link, null));
        }
        foreach (var allocated in byFill.Values.OrderBy(a => a.Fill.OccurredAtUtc).ThenBy(a => a.Fill.Id))
        {
            if (allocated.Allocation.Lines.FirstOrDefault(l => l.PlayId == playId) is not { } line || !reached.Add(allocated.Fill.Id)) continue;
            var link = links.GetValueOrDefault(allocated.Fill.Id);
            var portion = FillAllocation.Portion(allocated.Fill, line);
            // An exit line keeps the level of the Play's own exit link to the order, so a linked stop is still that stop.
            result.Add(line.Role == AllocationRole.Entry
                ? new(OrderLinkRole.Entry, line.EntryId, null, portion, link, line)
                : link is { Role: not OrderLinkRole.Entry }
                    ? new(link.Role, link.EntryId, link.LevelId, portion, link, line)
                    : new(OrderLinkRole.Exit, null, null, portion, link, line));
        }
        return result;
    }

    /// <summary>The venue order IDs a Play's lines rest on that are known to miss executions: such a Play is incomplete.</summary>
    public static IEnumerable<string> Incomplete(Guid playId, IEnumerable<AllocatedFill> allocations) =>
        allocations.Where(a => a.OrderMissing && a.Order is not null && a.Allocation.Lines.Any(l => l.PlayId == playId))
            .Select(a => a.Order!.ExternalOrderId);

    /// <summary>
    /// Split orders a Play has a line on whose other fills are not allocated yet (Sol round 1 on item 11): they count for no
    /// Play, so the Play is not reconciled until the owner allocates them. By venue order ID, once each.
    /// </summary>
    public static IEnumerable<string> Unallocated(Guid playId, IEnumerable<AllocatedFill> allocations) =>
        allocations.Where(a => a.Allocation.Lines.Count > 0 && a.Splits && a.UnallocatedOrderFills > 0 && a.Allocation.Lines.Any(l => l.PlayId == playId))
            .Select(a => a.Fill.ExternalOrderId).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Whether an order that supplies this Play's allocated entry lines still rests at the venue (Sol round 1 on item 11): like a
    /// resting linked entry order, it keeps the Play from closing, since more of it may still open the Play.
    /// </summary>
    public static bool EntryResting(Guid playId, IEnumerable<AllocatedFill> allocations, string? entryId = null) =>
        allocations.Any(a => a.Order is { Status: "open" } && a.Allocation.Lines.Any(l => l.PlayId == playId && l.Role == AllocationRole.Entry &&
            (entryId is null || l.EntryId == entryId)));

    /// <summary>The problem a Play with unallocated executions of a split order shows; it keeps the Play from settling.</summary>
    public static string UnallocatedProblem(string orderId) =>
        $"Order {orderId} has an execution that is not allocated yet; allocate it between its plays. Until then it counts for no play.";

    public static string ReasonName(AllocationReason reason) => reason switch
    {
        AllocationReason.SharedClose => "shared-close",
        AllocationReason.Flip => "flip",
        _ => "other",
    };

    public static AllocationReason? ParseReason(string? reason) => reason switch
    {
        "shared-close" => AllocationReason.SharedClose,
        "flip" => AllocationReason.Flip,
        "other" => AllocationReason.Other,
        _ => null,
    };

    public static string RoleName(AllocationRole role) => role == AllocationRole.Entry ? "entry" : "exit";

    /// <summary>What Review and the execution view say of a Play with an allocated fill.</summary>
    public const string NotAllocatedReason = "A fill of this play is allocated between plays. The venue's average-cost P&L cannot be split, " +
        "so its venue result is not allocated and it is not scored.";
}

/// <summary>A Play on an allocation request with the version the owner saw.</summary>
public sealed record AllocationPlayVersion([property: JsonRequired] Guid PlayId, [property: JsonRequired] long ExpectedVersion);

/// <summary>One requested line: a Play, its role (<c>exit</c> or <c>entry</c>), the plan entry an entry line fills, and an exact quantity.</summary>
public sealed record AllocationLineInput([property: JsonRequired] Guid PlayId, [property: JsonRequired] string Role, string? EntryId,
    [property: JsonRequired] string Quantity);

/// <summary>
/// Allocates one imported fill between Plays, replaces its allocation, or removes it with empty <paramref name="Lines"/>
/// (Phase 2, item 11). Every Play on the current or new lines comes with its version; <paramref name="ExpectedRevision"/> is
/// the allocation's current revision, 0 when there is none.
/// </summary>
public sealed record AllocateFillRequest(
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] Guid FillId,
    [property: JsonRequired] int ExpectedRevision,
    [property: JsonRequired] string Reason,
    [property: JsonRequired] string Note,
    [property: JsonRequired] IReadOnlyList<AllocationPlayVersion> Plays,
    [property: JsonRequired] IReadOnlyList<AllocationLineInput> Lines);

/// <summary>
/// One Play's line: its role, the entry an entry line fills, the exact quantity and fee share (null when the fee is unknown),
/// and <paramref name="FeeRemainder"/>, the rounding remainder inside the share, on the one line that took it.
/// </summary>
public sealed record FillAllocationLineDto(Guid PlayId, string Title, string Role, string? EntryId, string Quantity, string? Fee,
    string? FeeRemainder);

/// <summary>
/// A fill and its allocation, as the Execution panel shows it: the venue's fill facts, then the current revision with its
/// reason, note and lines (none when the fill has no allocation yet, or it was removed: <paramref name="Revision"/> still
/// counts). <paramref name="Kind"/> is <c>shared-exit</c>, <c>flip</c>, or <c>entry</c> for a later execution of an order a flip
/// split, which only opens Plays. <paramref name="Open"/> gives each Play that may close part of the fill (of the direction it
/// closes and open or unresolved, or on its exit lines) its open quantity without this fill, so a flip's exit can close it exactly.
/// </summary>
public sealed record AllocatableFillDto(Guid FillId, string Kind, ExecutionFillDto Fill, string OrderId, int Revision,
    string? Reason, string? Note, DateTimeOffset? UpdatedAtUtc, IReadOnlyList<FillAllocationLineDto> Lines,
    IReadOnlyList<AllocationOpenDto> Open);

/// <summary>A Play's open quantity without one fill: what its other counted fills entered minus what they exited.</summary>
public sealed record AllocationOpenDto(Guid PlayId, string OpenQuantity);

/// <summary>
/// A Play a line may name, or one a current line names: its direction, status, the version a request expects and its plan
/// entries (an entry line names one).
/// </summary>
public sealed record AllocationPlayDto(Guid PlayId, string Title, string Direction, string Status, long Version,
    IReadOnlyList<string> EntryIds);

/// <summary>
/// A page of a Play's allocated fills (Sol round 1 on item 11): newest first from <paramref name="Offset"/>, the Plays their
/// lines name and may name, the total, and the next offset (null when none is left).
/// </summary>
public sealed record SharedFillsPageDto(IReadOnlyList<AllocatableFillDto> Items, IReadOnlyList<AllocationPlayDto> Plays, int Total, int Offset,
    int? NextOffset);
