using Vessel.Application.Activity;
using Vessel.Application.Sizing;
using Vessel.Domain.Common;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Plays.Execution;

/// <summary>
/// Phase 2, item 9b: settled funding beside a Play, only with proven sole exposure over its holding window. Funding still
/// belongs to the native position interval (<see cref="OperationFunding"/>); a Play shows it only when that interval is the
/// Play's own and the facts prove nobody else was in it. Any condition that cannot be shown reads <c>shared</c> or
/// <c>incomplete</c>, never a guessed sum. The amount is shown beside the results and never added to either.
/// </summary>
/// <remarks>
/// The holding window starts at the Play's first linked fill. Once its own fills net to zero (a Closed Play's always do)
/// it ends at the last one; otherwise it ends at the latest covered evidence, as an open Activity operation's funding does:
/// the later of its last fill and the account's latest snapshot, in whole milliseconds (<see cref="OperationFunding.End"/>).
/// </remarks>
internal static class PlayFunding
{
    public const string Sole = "sole", Shared = "shared", Incomplete = "incomplete", Unavailable = "unavailable";

    /// <summary>The venue offers no funding read (a capability, never a venue name).</summary>
    public static readonly PlayFundingDto NotRead = Not(Unavailable, "no-funding-read");
    /// <summary>A disabled account's facts stay hidden, as in Activity.</summary>
    public static readonly PlayFundingDto Disabled = Not(Unavailable, "account-disabled");

    /// <summary>A real venue Play holds a position once a linked entry fill exists; before that, and for manual instruments, nothing shows.</summary>
    public static bool Applies(Play play, PlayPlanDocument plan, IEnumerable<LinkedFill> linked) =>
        play.TradingMode == TradingMode.Real && play.InstrumentSource == InstrumentSource.Venue && play.ContractId is not null &&
        linked.Any(f => f.Role == OrderLinkRole.Entry && !ExecutionIntegrity.Contradicts(f.Role, ExecutionIntegrity.EntrySide(plan), f.Fill));

    /// <summary>
    /// The proof, in order. <paramref name="activity"/> is the account's Activity aggregate (enabled account, real facts);
    /// <paramref name="ledger"/> its coverage ledger, or null when none is kept.
    /// </summary>
    public static PlayFundingDto Evaluate(Play play, PlayPlanDocument plan, IReadOnlyCollection<LinkedFill> linked,
        IReadOnlyCollection<string> incompleteOrders, ActivitySummaries activity, ExecutionLedger? ledger)
    {
        var facts = activity.Facts;
        // Activity's own partitions and omission markers, plus the venue-forced executions themselves.
        var positions = new ActivityPositions(facts.Fills, facts.Snapshots, facts.FundingPayments,
            [.. facts.Omissions, .. (ledger?.ForcedEvents ?? [])]);
        var own = linked.Select(f => f.Fill).DistinctBy(f => f.Id).ToList();
        var ids = own.Select(f => f.Id).ToHashSet();

        // 1. One resolved native partition, by Activity's identity rule. An ID-less fill or settlement of the contract left
        // in the unresolved canonical partition could be this market's, so it leaves the partition unproven.
        if (own.Select(positions.Key).Distinct().ToList() is not [var key] || positions.Quantity(key) is null)
            return Not(Incomplete, "identity");
        bool Unresolved(ActivityContractKey k) => !k.IsNative && k.Identity == play.ContractId && k != key;
        if (facts.Fills.Any(f => Unresolved(positions.Key(f))) || facts.FundingPayments.Any(p => Unresolved(positions.Key(p))))
            return Not(Incomplete, "identity");
        var partition = facts.Fills.Where(f => positions.Key(f) == key).ToList();

        // The holding window.
        var start = own.Min(f => f.OccurredAtUtc);
        var last = own.Max(f => f.OccurredAtUtc);
        var flat = Sum(own).Units.IsZero;
        var snapshot = positions.Snapshots.GetValueOrDefault(play.AccountId);
        var end = OperationFunding.End(flat ? last : null, last, snapshot);
        // Imports may keep only millisecond precision, so the first and last milliseconds belong to the window.
        var (from, to) = (Floor(start), Floor(end).AddTicks(TimeSpan.TicksPerMillisecond - 1));

        // 2. Sole exposure: every retained fill of the partition in the window is this Play's. Another Play's fills, or fills
        // linked to none, mean the funding is shared and not allocated.
        var others = partition.Where(f => !ids.Contains(f.Id) && f.OccurredAtUtc >= start && (!flat || f.OccurredAtUtc <= end) &&
            !(facts.FillPlayIds.TryGetValue(f.Id, out var linkedTo) && linkedTo == play.Id)).ToList();
        if (others.Count > 0) return Not(Shared, others.Any(f => facts.FillPlayIds.ContainsKey(f.Id)) ? "other-play" : "unlinked-fill");

        // 3. Its own executions settle it: none of its orders has known missing executions (the shared rule), nothing
        // contradicts or runs out of order, and an ended Play is Closed with its fills flat.
        if (incompleteOrders.Count > 0) return Not(Incomplete, "missing-executions");
        if (play.Status is PlayStatus.Unresolved or PlayStatus.Cancelled || play.Status == PlayStatus.Closed && !flat ||
            ExecutionIntegrity.Problems(ExecutionIntegrity.EntrySide(plan), linked, incompleteOrders).Count > 0)
            return Not(Incomplete, "not-settled");

        // 4. Nothing omitted and affirmative execution coverage: no omission marker or forced event could lie in the
        // partition and the window (a marker's read window counts, not just its time), no open history gap overlaps it, and
        // the coverage ledger proves it continuous.
        var breaks = positions.Breaks(key);
        bool Omitted(DateTimeOffset a, DateTimeOffset b) => breaks.Any(m => m.ResetAt <= b && m.AnchorAfter >= a);
        var gaps = ledger?.OpenGaps ?? [];
        bool Gap(DateTimeOffset a, DateTimeOffset b) =>
            gaps.Any(g => (g.FromUtc ?? DateTimeOffset.MinValue) <= b && (g.ToUtc ?? DateTimeOffset.MaxValue) >= a);
        var spans = Merge(ledger?.Covered ?? []);
        // One continuous span holds [a, b]; a null a asks for complete history from the account's start.
        bool Covered(DateTimeOffset? a, DateTimeOffset b) =>
            spans.Any(s => s.ToUtc >= b && (s.FromUtc is null || a is { } at && s.FromUtc <= at));
        if (Omitted(from, to)) return Not(Incomplete, "omitted-executions");
        if (Gap(from, to)) return Not(Incomplete, "history-gap");
        if (!Covered(start, end)) return Not(Incomplete, "history-unproven");

        // 5. Flat before the first entry, anchored to covered history or to a snapshot; Activity's unanchored zero start is
        // never proof. Another fill earlier in that first millisecond might follow it.
        if (partition.Any(f => !ids.Contains(f.Id) && f.OccurredAtUtc >= from && f.OccurredAtUtc < start))
            return Not(Incomplete, "flat-start-unproven");
        var proofs = new List<ExactAmount>();
        // Covered history: complete from the account's start, which was flat, with nothing omitted or missing since.
        if (Covered(null, end) && !Omitted(DateTimeOffset.MinValue, to) && !Gap(DateTimeOffset.MinValue, to))
            proofs.Add(Sum(partition.Where(f => f.OccurredAtUtc < start)));
        // The latest snapshot, walked back as Activity anchors it (observed at or after every fill of the partition) over a
        // covered span with nothing omitted.
        if (snapshot is not null && positions.Quantity(key) is { } observed &&
            partition.All(f => f.OccurredAtUtc <= snapshot.ObservedAtUtc && f.Quantity > 0) &&
            Covered(start, snapshot.ObservedAtUtc) && !Omitted(from, snapshot.ObservedAtUtc) && !Gap(from, snapshot.ObservedAtUtc))
            proofs.Add(observed - Sum(partition.Where(f => f.OccurredAtUtc >= start)));
        if (proofs.Count == 0 || proofs.Any(p => p.Units.IsZero) && !proofs.All(p => p.Units.IsZero))
            return Not(Incomplete, "flat-start-unproven");
        // A position proven open at the first entry is someone else's exposure.
        if (!proofs[0].Units.IsZero) return Not(Shared, "open-position");

        // 6. Flat after the last exit once its own fills net to zero: flat before, nothing else in the window, and nothing
        // else in the last exit's millisecond, where the order is unknown.
        if (flat && partition.Any(f => !ids.Contains(f.Id) && f.OccurredAtUtc > end && f.OccurredAtUtc <= to))
            return Not(Incomplete, "flat-end-unproven");

        // 7. Activity bounds the same position: one operation of exactly these fills, opened at the first and closed at the
        // last once flat, so its funding interval is the holding window.
        var touching = activity.Operations.Where(o => o.ContractKey == key && o.SourceFills.Any(f => ids.Contains(f.Id))).ToList();
        if (touching is not [var operation] || operation.PlayAssociation != "linked" || operation.PlayId != play.Id ||
            operation.OpenedAtUtc != start || operation.ClosedAtUtc != (flat ? end : (DateTimeOffset?)null) ||
            !operation.SourceFills.Select(f => f.Id).ToHashSet().SetEquals(ids))
            return Not(Incomplete, "operation-boundaries");

        // 8. Complete funding coverage over the window, no settlement in a boundary's precision bucket, nominal stablecoins,
        // and nothing attributed past the window's end (an open interval takes later settlements; they are not this window's).
        if (operation.FundingStatus != "complete")
            return Not(Incomplete, operation.FundingAmbiguous ? "funding-ambiguous" : operation.FundingUnsupported ? "funding-asset" : "funding-coverage");
        if (operation.FundingPayments.Any(p => p.OccurredAtUtc > end)) return Not(Incomplete, "funding-coverage");
        var assets = operation.FundingPayments.Select(p => p.Asset).Distinct(StringComparer.Ordinal).ToList();
        return new(Sole, null, operation.FundingNetUsd, assets is [var asset] ? asset : null, operation.FundingPaymentCount, start, end);
    }

    private static PlayFundingDto Not(string state, string reason) => new(state, reason, null, null, null, null, null);

    private static ExactAmount Sum(IEnumerable<ImportedFill> fills) => fills.Aggregate(default(ExactAmount), (sum, f) =>
        f.Side == ExecutionFacts.Buy ? sum + ExactAmount.From(f.Quantity) : sum - ExactAmount.From(f.Quantity));

    private static DateTimeOffset Floor(DateTimeOffset at) => new(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    /// <summary>Covered spans joined where one starts at or before the previous one's end; an unread interval is never bridged.</summary>
    private static List<LedgerSpan> Merge(IEnumerable<LedgerSpan> covered)
    {
        var merged = new List<LedgerSpan>();
        foreach (var span in covered.Where(s => s.ToUtc is not null).OrderBy(s => s.FromUtc ?? DateTimeOffset.MinValue))
        {
            if (merged.Count > 0 && (span.FromUtc ?? DateTimeOffset.MinValue) <= merged[^1].ToUtc)
            {
                if (span.ToUtc > merged[^1].ToUtc) merged[^1] = merged[^1] with { ToUtc = span.ToUtc };
            }
            else merged.Add(span);
        }
        return merged;
    }
}
