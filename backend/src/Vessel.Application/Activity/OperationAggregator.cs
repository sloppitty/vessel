using System.Collections;
using System.Numerics;
using Vessel.Application.Workspace;
using Vessel.Domain.Numerics;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Activity;

/// <summary>Position round trips from normalized facts, never Play intent or venue direction text.</summary>
public static class OperationAggregator
{
    public static IReadOnlyList<ActivityOperationDto> Aggregate(IEnumerable<ImportedFill> fills,
        IReadOnlyDictionary<Guid, Guid>? fillPlayIds = null, IReadOnlyList<AccountSnapshot>? snapshots = null,
        IReadOnlyList<FundingPayment>? funding = null, IReadOnlyList<ActivityOmission>? omissions = null)
    {
        var retained = fills.ToList();
        return AggregateSummaries(retained, fillPlayIds, new(retained, snapshots ?? [], funding, omissions))
            .Select(o => o with { Fills = o.Fills.ToList() }).ToList();
    }

    /// <summary>
    /// Every operation of one read's facts, from the same inputs Activity's partitions, anchors and walk use, omission markers
    /// included (Phase 2, item 7b, Sol round 1), so a preview or confirmation never rebuilds an operation across a gap that
    /// Activity reports.
    /// </summary>
    public static IReadOnlyList<ActivityOperationDto> Aggregate(ActivityFacts facts) =>
        Aggregate(facts.Fills, facts.FillPlayIds, facts.Snapshots, facts.FundingPayments, facts.Omissions);

    internal static IReadOnlyList<ActivityOperationDto> AggregateSummaries(IReadOnlyList<ImportedFill> fills,
        IReadOnlyDictionary<Guid, Guid>? fillPlayIds, ActivityPositions positions)
    {
        var result = new List<ActivityOperationDto>();
        foreach (var group in fills.GroupBy(positions.Key))
        {
            var firstResult = result.Count;
            var times = group.GroupBy(f => f.OccurredAtUtc).OrderBy(g => g.Key).ToList();
            var snapshot = positions.Snapshots.GetValueOrDefault(group.Key.AccountId);
            var observed = positions.Quantity(group.Key);
            var anchorable = snapshot is not null && observed is not null && snapshot.ObservedAtUtc >= times[^1].Key && group.All(f => f.Quantity > 0);
            // Phase 2, item 7b: executions an import left out, from the first retained fill on, break the walk. The
            // snapshot cannot anchor fills before the last of them, and the position after each one is unknown.
            var breaks = positions.Breaks(group.Key).Where(b => b.AnchorAfter >= times[0].Key).ToList();
            var anchorAfter = breaks.Count == 0 ? (DateTimeOffset?)null : breaks.Max(b => b.AnchorAfter);
            var tail = anchorAfter is { } cut ? group.Where(f => f.OccurredAtUtc > cut).ToList() : [];
            var anchored = breaks.Count == 0 && anchorable;
            // Addition is order-independent. This is the backward walk's position before the first fill.
            var position = anchored ? observed!.Value
                - group.Aggregate(default(ExactAmount), (sum, f) => sum + Signed(f)) : default;
            // Markers whose time or window has begun by the first retained fill: the walk starts unknown and stays uncertain
            // to the end of their windows.
            var begun = breaks.Where(b => b.ResetAt <= times[0].Key).ToList();
            var known = anchored || begun.Count == 0 &&
                times[0].All(f => f.PositionEffect == ExecutionFacts.Open) && times[0].Select(f => f.Side).Distinct().Count() == 1;
            var pending = new Queue<ActivityBreak>(breaks.Where(b => b.ResetAt > times[0].Key).OrderBy(b => b.ResetAt));
            // Sol round 1 on 7b: an omitted execution can lie between any two fills of its window, so nothing inside the
            // window proves flatness or direction; only evidence strictly after its end can. An unknown end never passes.
            DateTimeOffset? uncertainUntil = begun.Count == 0 ? null : begun.Max(b => b.AnchorAfter);
            // Every unknown operation from an omission on carries its explanation, until flatness is proved or anchored again.
            var omitted = begun.Count > 0;
            var unknownDirection = 0;
            Builder? current = null;
            // An open operation at an omitted execution ends there with unknown boundaries ("direction unknown"), never a
            // phantom opposite operation; the walk after it needs a new proof of flatness or the snapshot anchor.
            void Break(ActivityBreak? at)
            {
                if (current is not null)
                {
                    current.UnknownBoundaries = true;
                    current.Note(OmittedNotice);
                    result.Add(current.Build(position));
                    current = null;
                }
                (known, unknownDirection, position, omitted) = (false, 0, default, true);
                if (at is { } window && (uncertainUntil is null || window.AnchorAfter > uncertainUntil)) uncertainUntil = window.AnchorAfter;
            }
            foreach (var time in times)
            {
                while (pending.Count > 0 && pending.Peek().ResetAt <= time.Key) Break(pending.Dequeue());
                // After the last omitted execution, the snapshot anchors the remaining fills again.
                if (!anchored && anchorAfter is { } lastOmission && time.Key > lastOmission && anchorable && snapshot!.ObservedAtUtc >= lastOmission && tail.Count > 0)
                {
                    Break(null);
                    position = observed!.Value - tail.Aggregate(default(ExactAmount), (sum, f) => sum + Signed(f));
                    (known, anchored, omitted) = (true, true, false);
                }
                var uncertain = uncertainUntil is { } until && time.Key <= until;
                var (ordered, ambiguous, peakAmbiguous) = OrderTimestamp(time, known ? position : null, current?.Peak ?? position.Abs());
                foreach (var fill in ordered)
                {
                    var quantity = ExactAmount.From(fill.Quantity);
                    var signed = Signed(fill);
                    // A close proves the unknown position's direction, not its size. Opening the
                    // opposite position proves flatness between those fills. Reopening the same
                    // direction could just be another increase and cannot resynchronize the walk.
                    if (!known && !ambiguous && !uncertain && fill.Quantity > 0 && fill.PositionEffect == ExecutionFacts.Open && unknownDirection == -signed.Units.Sign)
                    {
                        if (current is not null) result.Add(current.Build(default));
                        current = null;
                        position = default;
                        (known, omitted) = (true, false);
                    }
                    if (!known)
                    {
                        current ??= new Builder(fill, fill.PositionEffect == ExecutionFacts.Close ? -signed.Units.Sign : signed.Units.Sign);
                        current.UnknownBoundaries = true;
                        current.Note(ambiguous ? TimestampNotice : "Retained opening history or position effect is unknown; position boundaries are unavailable until flatness is proved.");
                        if (omitted) current.Note(OmittedNotice);
                        current.Add(fill, "unknown", quantity, true, fillPlayIds);
                        // Inside an omission's window a close proves nothing: the omitted execution may follow it.
                        unknownDirection = ambiguous || uncertain || fill.Quantity <= 0 ? 0 : fill.PositionEffect switch
                        {
                            ExecutionFacts.Close => -signed.Units.Sign,
                            ExecutionFacts.Open or ExecutionFacts.Flip => signed.Units.Sign,
                            _ => 0
                        };
                        continue;
                    }
                    var valid = EffectMatches(fill, position);
                    if (!anchored && (!valid || fill.PositionEffect == ExecutionFacts.Unknown || fill.Quantity <= 0 || fill.Price <= 0))
                    {
                        current ??= new Builder(fill, position.Units.IsZero ? signed.Units.Sign : position.Units.Sign);
                        current.UnknownBoundaries = true;
                        current.Note("Position effect contradicts the retained position or is unknown; position boundaries need a new proof of flatness.");
                        if (ambiguous) current.Note(TimestampNotice);
                        current.Add(fill, "unknown", quantity, true, fillPlayIds);
                        known = false;
                        unknownDirection = 0; // A contradictory label is not a proof of direction.
                        continue;
                    }
                    current ??= new Builder(fill, position.Units.IsZero ? signed.Units.Sign : position.Units.Sign)
                    {
                        MissingEntries = !position.Units.IsZero,
                        Peak = position.Abs()
                    };
                    if (current.MissingEntries) current.Note("Opening entries precede retained history and are missing; entry quantity, entry average and opening time are unknown.");
                    if (ambiguous) { current.UnknownBoundaries = true; current.Note(TimestampNotice); }
                    if (peakAmbiguous) { current.UnknownPeak = true; current.Note(PeakNotice); }
                    if (!valid) current.Note($"Position effect '{fill.PositionEffect}' contradicts snapshot-anchored quantities in this operation; boundaries use side and quantity.");
                    if (fill.Price <= 0) { current.UnknownBoundaries = true; current.Note("Nonpositive price makes weighted prices unavailable."); }
                    var after = position + signed;
                    var reducing = !position.Units.IsZero && position.Units.Sign != signed.Units.Sign;
                    var crosses = reducing && !after.Units.IsZero && after.Units.Sign != position.Units.Sign;
                    if (crosses)
                    {
                        current.Add(fill, "exit", position.Abs(), true, fillPlayIds, split: true);
                        current.ClosedAt = fill.OccurredAtUtc;
                        result.Add(current.Build(default));
                        current = new Builder(fill, signed.Units.Sign) { Peak = after.Abs(), UnknownBoundaries = ambiguous || fill.Price <= 0 };
                        if (ambiguous) current.Note(TimestampNotice);
                        if (peakAmbiguous) { current.UnknownPeak = true; current.Note(PeakNotice); }
                        if (fill.Price <= 0) current.Note("Nonpositive price makes weighted prices unavailable.");
                        if (!valid) current.Note($"Position effect '{fill.PositionEffect}' contradicts snapshot-anchored quantities in this operation; boundaries use side and quantity.");
                        current.Add(fill, "entry", after.Abs(), false, fillPlayIds, split: true);
                    }
                    else
                    {
                        current.Add(fill, reducing ? "exit" : "entry", quantity, true, fillPlayIds);
                        if (after.Abs().CompareTo(current.Peak) > 0) current.Peak = after.Abs();
                        if (after.Units.IsZero)
                        {
                            current.ClosedAt = fill.OccurredAtUtc;
                            result.Add(current.Build(after));
                            current = null;
                        }
                    }
                    position = after;
                }
            }
            // An omitted execution after the last retained fill leaves its open operation unknown too.
            if (pending.Count > 0) Break(null);
            if (current is not null) result.Add(current.Build(position));
            for (var i = firstResult; i < result.Count; i++)
                result[i] = result[i] with
                {
                    ContractKey = group.Key,
                    Coverage = observed is null
                        ? result[i].Coverage.Append(ActivityPositions.AmbiguousIdentityNotice).ToList()
                        : result[i].Coverage
                };
        }
        return result.OrderByDescending(o => o.LastOccurredAtUtc).ThenBy(o => o.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Phase 2, item 7b.</summary>
    public const string OmittedNotice = "Direction unknown: executions omitted. The venue reported executions Vessel left out here, so the position after them is unknown until it is proven flat or anchored again.";
    private const string TimestampNotice = "Fills share a timestamp with no unique effect-consistent operation boundary; only operations touching this timestamp are ambiguous.";
    private const string PeakNotice = "Effect-consistent orders at a shared timestamp disagree on peak position; maximum quantity is unknown, while operation boundaries and totals are retained.";
    private static ExactAmount Signed(ImportedFill fill) => fill.Side == ExecutionFacts.Buy ? ExactAmount.From(fill.Quantity) : -ExactAmount.From(fill.Quantity);
    private static bool EffectMatches(ImportedFill fill, ExactAmount before)
    {
        var signed = Signed(fill);
        var same = before.Units.IsZero || before.Units.Sign == signed.Units.Sign;
        return fill.Quantity > 0 && fill.PositionEffect switch
        {
            ExecutionFacts.Open => same,
            ExecutionFacts.Close => !same && signed.Abs().CompareTo(before.Abs()) <= 0,
            ExecutionFacts.Flip => !same && signed.Abs().CompareTo(before.Abs()) > 0,
            ExecutionFacts.Unknown => true,
            _ => false
        };
    }

    private static (IReadOnlyList<ImportedFill> Fills, bool Ambiguous, bool PeakAmbiguous) OrderTimestamp(
        IEnumerable<ImportedFill> time, ExactAmount? position, ExactAmount priorPeak)
    {
        var rows = time.OrderBy(f => f.PositionEffect == ExecutionFacts.Close ? 0 : f.PositionEffect == ExecutionFacts.Flip ? 1 : 2)
            .ThenBy(f => f.Id).ToArray();
        if (rows.Length == 1) return (rows, false, false);
        if (position is null)
        {
            // With unknown size, same-side closes before opens can prove a direction change.
            // Opposing-side ties cannot prove the size or which position their opens increased.
            return (rows, rows.Select(f => f.Side).Distinct().Count() > 1 || rows.Any(f => f.PositionEffect is ExecutionFacts.Flip or ExecutionFacts.Unknown), false);
        }
        if (rows.Select(f => f.Side).Distinct().Count() == 1 && rows.All(f => f.Quantity > 0))
        {
            var sameSide = position.Value.Units.IsZero || position.Value.Units.Sign == Signed(rows[0]).Units.Sign;
            var total = rows.Aggregate(default(ExactAmount), (sum, f) => sum + ExactAmount.From(f.Quantity));
            if (sameSide && rows.All(f => f.PositionEffect is ExecutionFacts.Open or ExecutionFacts.Unknown) ||
                !sameSide && total.CompareTo(position.Value.Abs()) <= 0 && rows.All(f => f.PositionEffect is ExecutionFacts.Close or ExecutionFacts.Unknown))
                return (rows, false, false); // No internal boundary, regardless of order or burst size.
            var closed = rows.Where(f => f.PositionEffect == ExecutionFacts.Close)
                .Aggregate(default(ExactAmount), (sum, f) => sum + ExactAmount.From(f.Quantity));
            if (!sameSide && closed == position.Value.Abs() && rows.All(f => f.PositionEffect is ExecutionFacts.Close or ExecutionFacts.Open))
                return (rows, false, false); // All closes must finish before this opposite-position open.
        }

        // ponytail: cap unusual timestamp bursts, conservatively flag their operations instead of
        // factorial work. Ordinary bursts without internal boundaries take the linear path.
        if (rows.Length > 12) return (rows, true, false);
        var used = new bool[rows.Length];
        var path = new List<ImportedFill>();
        ImportedFill[]? chosen = null;
        string? signature = null;
        string? peakSignature = null;
        var ambiguous = false;
        var peakAmbiguous = false;
        var visited = 0;
        var states = new HashSet<string>(StringComparer.Ordinal);
        void Search(ExactAmount before, int operation, List<string> allocations, List<ExactAmount> peaks)
        {
            if (ambiguous) return;
            var peakState = string.Join("|", peaks);
            if (!states.Add($"{string.Join("", used.Select(u => u ? '1' : '0'))}:{before}:{operation}:{peakState}:{string.Join("|", allocations.Order(StringComparer.Ordinal))}")) return;
            if (++visited > 4096) { ambiguous = true; return; }
            if (path.Count == rows.Length)
            {
                var candidate = string.Join("|", allocations.Order(StringComparer.Ordinal));
                if (chosen is null) { chosen = path.ToArray(); signature = candidate; peakSignature = peakState; }
                else if (signature != candidate) ambiguous = true;
                else if (peakSignature != peakState) peakAmbiguous = true;
                return;
            }
            for (var i = 0; i < rows.Length; i++)
            {
                var fill = rows[i];
                if (used[i] || !EffectMatches(fill, before)) continue;
                var after = before + Signed(fill);
                var reducing = !before.Units.IsZero && before.Units.Sign != Signed(fill).Units.Sign;
                var crosses = reducing && !after.Units.IsZero && before.Units.Sign != after.Units.Sign;
                var next = operation;
                var added = new List<string>(allocations);
                var nextPeaks = new List<ExactAmount>(peaks);
                if (crosses)
                {
                    added.Add($"{fill.Id}:{operation}:exit:{before.Abs()}");
                    added.Add($"{fill.Id}:{++next}:entry:{after.Abs()}");
                    nextPeaks.Add(after.Abs());
                }
                else
                {
                    added.Add($"{fill.Id}:{operation}:{(reducing ? "exit" : "entry")}:{ExactAmount.From(fill.Quantity)}");
                    if (after.Abs().CompareTo(nextPeaks[operation]) > 0) nextPeaks[operation] = after.Abs();
                    if (after.Units.IsZero) { next++; nextPeaks.Add(default); }
                }
                used[i] = true; path.Add(fill);
                Search(after, next, added, nextPeaks);
                path.RemoveAt(path.Count - 1); used[i] = false;
                if (ambiguous) break;
            }
        }
        Search(position.Value, 0, [], [priorPeak]);
        return (chosen ?? rows, ambiguous || chosen is null, peakAmbiguous);
    }

    private sealed class Builder(ImportedFill first, int sign)
    {
        public bool UnknownBoundaries, MissingEntries, UnknownPeak;
        public void Note(string notice) { if (!Coverage.Contains(notice)) Coverage.Add(notice); }
        public ExactAmount Peak;
        public DateTimeOffset? ClosedAt;
        public List<string> Coverage = [];
        private readonly List<Allocation> parts = [];
        private readonly HashSet<string> entries = [], exits = [];
        private readonly HashSet<string> bases = [];
        private readonly Dictionary<string, ExactAmount> fees = new(StringComparer.Ordinal);
        private readonly HashSet<Guid> plays = [];
        private readonly List<(DateTimeOffset, int)> leverages = [];
        private bool unassigned, unsupportedGrossFee, unsupportedNetFee;
        private ExactAmount entered, exited, pnl, grossFees, netFees;
        private BigInteger entryNotional, exitNotional;
        public void Add(ImportedFill fill, string role, ExactAmount quantity, bool monetary, IReadOnlyDictionary<Guid, Guid>? links,
            bool split = false)
        {
            // A flip's reported leverage cannot be assigned to either side of the split.
            if (fill.Leverage is { } leverage && !split) leverages.Add((fill.OccurredAtUtc, leverage));
            Guid? play = links?.TryGetValue(fill.Id, out var linked) == true ? linked : null;
            if (play is { } id) plays.Add(id); else unassigned = true;
            var fee = monetary ? ExactAmount.From(fill.Fee) : default;
            var closedPnl = monetary ? ExactAmount.From(fill.ClosedPnlUsd) : default;
            pnl += closedPnl;
            bases.Add(fill.PnlBasis);
            fees[fill.FeeToken] = fees.GetValueOrDefault(fill.FeeToken) + fee;
            if (fill.PnlBasis == ExecutionFacts.PnlGross)
            {
                if (NominalStablecoins.Contains(fill.FeeToken)) grossFees += fee;
                else if (!fee.Units.IsZero) unsupportedGrossFee = true;
            }
            else if (fill.PnlBasis == ExecutionFacts.PnlNetOfFee)
            {
                if (NominalStablecoins.Contains(fill.FeeToken)) netFees += fee;
                else if (!fee.Units.IsZero) unsupportedNetFee = true;
            }
            if (role == "entry") { entered += quantity; entryNotional += quantity.Units * ExactAmount.From(fill.Price).Units; if (!string.IsNullOrWhiteSpace(fill.ExternalOrderId)) entries.Add(fill.ExternalOrderId); }
            if (role == "exit") { exited += quantity; exitNotional += quantity.Units * ExactAmount.From(fill.Price).Units; if (!string.IsNullOrWhiteSpace(fill.ExternalOrderId)) exits.Add(fill.ExternalOrderId); }
            parts.Add(new(fill, role, quantity, fee, closedPnl, play));
        }
        public ActivityOperationDto Build(ExactAmount position)
        {
            if (unsupportedGrossFee) Coverage.Add("Net result is unknown: a gross fill has a fee in a token outside the nominal stablecoin list; no fee conversion is performed.");
            if (unsupportedNetFee) Coverage.Add("Before-fee P&L is unknown: a net-of-fee fill has a fee in a token outside the nominal stablecoin list; no fee conversion is performed.");
            if (parts.Any(p => string.IsNullOrWhiteSpace(p.Fill.ExternalOrderId))) Coverage.Add("Some order IDs are missing; distinct order counts include known IDs only.");
            var association = plays.Count == 0 ? "unassigned" : plays.Count == 1 && !unassigned ? "linked" : "mixed";
            var stableFees = fees.Where(f => NominalStablecoins.Contains(f.Key)).Aggregate(default(ExactAmount), (sum, f) => sum + f.Value);
            return new($"{first.Id}:{(sign > 0 ? "long" : "short")}", first.AccountId, first.ContractId,
                UnknownBoundaries ? null : sign > 0 ? "long" : "short", UnknownBoundaries || MissingEntries ? "incomplete" : ClosedAt is null ? "open" : "closed",
                UnknownBoundaries || MissingEntries ? null : first.OccurredAtUtc, UnknownBoundaries ? null : ClosedAt, parts[^1].Fill.OccurredAtUtc,
                UnknownBoundaries || MissingEntries || UnknownPeak ? null : Peak.ToString(), UnknownBoundaries || MissingEntries ? null : entered.ToString(), UnknownBoundaries ? null : exited.ToString(),
                UnknownBoundaries ? null : position.Abs().ToString(), UnknownBoundaries || MissingEntries ? null : ExactAmount.Average(entryNotional, entered),
                UnknownBoundaries ? null : ExactAmount.Average(exitNotional, exited), entries.Count, exits.Count, parts.Count,
                pnl.ToString(), stableFees.ToString(), fees.Where(f => !NominalStablecoins.Contains(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal)
                    .Select(f => new OperationFeeDto(f.Key, f.Value.ToString())).ToList(), unsupportedGrossFee ? null : (pnl - grossFees).ToString(),
                bases.Count == 1 ? bases.Single() : "mixed", association == "linked" ? plays.Single() : null, association, Coverage, new AllocationRows(parts))
            {
                GrossClosedPnlUsd = unsupportedNetFee ? null : (pnl + netFees).ToString(),
                FirstOccurredAtUtc = parts[0].Fill.OccurredAtUtc,
                FillLeverages = leverages,
                // The unrounded 56-place product; the API's weighted averages above keep their documented rounding.
                ExactEntryNotional = UnknownBoundaries || MissingEntries || entered.Units.IsZero ? null : entryNotional,
                SourceFills = parts.Select(p => p.Fill)
            };
        }
    }

    private readonly record struct Allocation(ImportedFill Fill, string Role, ExactAmount Quantity, ExactAmount Fee, ExactAmount ClosedPnl, Guid? PlayId);

    // Keep exact totals for all allocations, but create constituent DTOs only when a caller reads them.
    private sealed class AllocationRows(IReadOnlyList<Allocation> parts) : IReadOnlyList<OperationFillDto>
    {
        public int Count => parts.Count;
        public OperationFillDto this[int index]
        {
            get
            {
                var part = parts[index]; var fill = part.Fill;
                return new(new(fill.Id, fill.AccountId, fill.ContractId, fill.Side, fill.Direction,
                    ExactAmount.From(fill.Price).ToString(), ExactAmount.From(fill.Quantity).ToString(), ExactAmount.From(fill.Fee).ToString(),
                    fill.FeeToken, ExactAmount.From(fill.ClosedPnlUsd).ToString(), fill.OccurredAtUtc, fill.ExternalOrderId, fill.SourceFillId,
                    fill.TransactionHash, fill.PositionEffect, fill.FeeBasis, fill.PnlBasis, part.PlayId),
                    part.Role, part.Quantity.ToString(), part.Fee.ToString(), part.ClosedPnl.ToString());
            }
        }
        public IEnumerator<OperationFillDto> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return this[i];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
