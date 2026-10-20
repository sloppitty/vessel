using Vessel.Domain.Accounts;
using Vessel.Application.Workspace;
using System.Globalization;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Sizing;
using Vessel.Application.Venues;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;
using Vessel.Domain.Sizing;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Review;

/// <summary>
/// Read-only reporting. Never assigns fills, mutates lifecycle or refreshes a venue. The venue registry supplies each
/// venue's quote asset for the Play-local result; without it that result is unknown.
/// </summary>
public sealed class ReviewService(IReviewStore store, TimeProvider time, IVenueRegistry? venues = null,
    IReviewPositionSource? positionSource = null)
{
    public const int PageSize = 50;
    /// <summary>How many imported positions one Play counts as in rate metrics. Dollar totals are never weighted.</summary>
    public const int PlayWeight = 2;
    public static readonly string[] PositionModes = ["exclude", "include", "auto"];
    private const string PositionNotice = " Imported positions are included: closed Activity round trips whose fills belong to no Play, scored by " +
        "the same net and return rules. They have no plan, R or review and never become Plays. A Play weighs twice a position in batting " +
        "average, average gain and loss, payoff and expectancy; P&L, profit factor, drawdown, streaks and the chart are not weighted.";
    public static readonly string[] Periods = ["day", "week", "month", "year", "all"];
    private const string Notice = "Saved closed Plays only, from linked imported executions or explicit owner-entered manual facts. " +
        "Manual facts are estimates, not venue balance reconciliation or imported Activity. Retrospective Plays have no contemporaneous " +
        "plan or initial-risk R and are excluded from the sizing ladder. Return percent is net/executed entry notional, not return on margin. " +
        "Unassigned imported fills are not Plays. Retained venue history can be incomplete; All is not lifetime history. " +
        "Results are nominal USD and include reported execution fees once. " + NominalStablecoins.Caveat +
        " Venues with different quote stablecoins are pooled nominally. Separate funding cashflows are not added; " +
        "whether RISEx reported realized P&L includes funding remains unverified. Balanced retained fills are not proof of full venue history. " +
        "Accounts use current portfolio membership; disabled accounts are excluded. Open-play counts are current, not a historical cohort.";

    public static DateTimeOffset? From(string period, DateTimeOffset now) => period switch
    {
        "day" => now.AddDays(-1), "week" => now.AddDays(-7), "month" => now.AddDays(-30),
        "year" => now.AddDays(-365), "all" => null,
        _ => throw new ProblemException(400, "Choose day, week, month, year or all."),
    };

    public async Task<ReviewDto> GetAsync(ReviewQuery query, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var from = From(query.Period, now);
        if (query.Portfolio is not ("all" or "unassigned") &&
            (!Guid.TryParse(query.Portfolio, out var portfolio) || portfolio == Guid.Empty))
            throw new ProblemException(400, "Choose a valid portfolio.");
        if (Guid.TryParse(query.Portfolio, out var normalized)) query = query with { Portfolio = normalized.ToString() };
        if (query.AccountId == Guid.Empty || query.Offset is < 0 or > 1_000_000 ||
            query.Instrument is { } instrument && (string.IsNullOrWhiteSpace(instrument) || instrument.Length > 128 || instrument != instrument.Trim()))
            throw new ProblemException(400, "Invalid review filters.");
        if (!PositionModes.Contains(query.Positions) || query.MinPlays is < 0 or > ReviewQuery.MaxMinPlays)
            throw new ProblemException(400, "Choose exclude, include or auto positions and a Play threshold from 0 to 1000.");

        ReviewFacts facts;
        Dictionary<Guid, Account> accounts;
        List<Evaluated> accepted, candidates;
        List<ReviewPosition> read;
        int playCount;
        bool include;
        for (var attempt = 1; ; attempt++)
        {
            facts = await store.ReadAsync(query, now, ct);
            accounts = facts.Accounts.Where(a => a.IsEnabled).ToDictionary(a => a.Id);
            var scoped = accounts.Values.Where(a =>
                (query.AccountId is null || a.Id == query.AccountId) &&
                (query.Portfolio == "all" || query.Portfolio == "unassigned" && a.PortfolioId is null ||
                    a.PortfolioId?.ToString() == query.Portfolio)).Select(a => a.Id).ToHashSet();
            var evaluated = facts.ClosedPlays.Where(f => scoped.Contains(f.Play.AccountId) &&
                (query.Instrument is null || f.Play.ContractId == query.Instrument))
                .Select(f => Evaluate(f, now, facts.IncompleteOrderPlayIds?.Contains(f.Play.Id) == true || f.IncompleteOrders is { Count: > 0 })).ToList();
            accepted = evaluated.Where(e => e.Result is not null).ToList();
            candidates = evaluated.Where(e => InPeriod(e.ClosedAt, from, now)).ToList();
            // Auto counts the scored Plays of the selected scope and period, and the decision applies to every period total.
            playCount = accepted.Count(e => InPeriod(e.ClosedAt, from, now));
            include = positionSource is not null &&
                (query.Positions == "include" || query.Positions == "auto" && playCount < query.MinPlays);
            read = [];
            if (!include) break;
            read = (await positionSource!.ReadAsync(scoped, ct)).Where(p => accounts.ContainsKey(p.AccountId)).ToList();
            // Plays and position claims come from separate reads. A Play created or linked in between (a retrospective
            // conversion) would hide its result or count it twice, so the Play snapshot must be unchanged around them.
            if (Claims(facts) == Claims(await store.ReadAsync(query, now, ct))) break;
            if (attempt == 3) throw new ProblemException(409, "Plays changed while Review read imported positions. Reload Review.");
        }
        var positions = read.Where(p => query.Instrument is null || p.ContractId == query.Instrument)
            .Select(p => new Evaluated(null, p.ClosedAtUtc, p.Outcome, p.Outcome is null ? "position" : null)
            {
                Position = p, PositionVenue = accounts[p.AccountId].VenueId
            }).ToList();
        accepted = accepted.Concat(positions.Where(p => p.Result is not null))
            .OrderBy(e => e.ClosedAt).ThenBy(e => e.Facts?.Play.Id ?? Guid.Empty).ThenBy(e => e.Position?.OperationId, StringComparer.Ordinal).ToList();
        var current = accepted.Where(e => InPeriod(e.ClosedAt, from, now)).ToList();
        var portfolioNames = facts.Portfolios.ToDictionary(p => p.Id.ToString(), p => p.Name);
        var options = new ReviewOptionsDto(
            facts.Portfolios.OrderBy(p => p.Name).Select(p => new ReviewPortfolioDto(p.Id.ToString(), p.Name)).ToList(),
            accounts.Values.OrderBy(a => a.Name).Select(a => new ReviewAccountDto(a.Id, a.Name, a.PortfolioId, a.VenueId,
                a.LastSyncedAtUtc, a.SyncStatus, a.HistoryNotice)).ToList(),
            facts.ClosedPlays.Where(f => accounts.ContainsKey(f.Play.AccountId)).Select(f => f.Play.ContractId)
                .Concat(read.Where(p => p.Outcome is not null).Select(p => p.ContractId))
                .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
        var series = Series(current, from, now);
        List<ReviewGroupDto> Groups(Func<Evaluated, string> key, Func<string, string> name, Func<string, List<Evaluated>, string> detail) =>
            current.GroupBy(key).Select(group =>
            {
                var items = group.ToList();
                return (Total: Sum(items.Select(x => x.Result!.NetResult)),
                    Dto: new ReviewGroupDto(group.Key, name(group.Key), detail(group.Key, items), Metrics(items)));
            }).OrderByDescending(x => x.Total).ThenBy(x => x.Dto.Name, StringComparer.Ordinal).Select(x => x.Dto).ToList();

        var groups = new ReviewGroupsDto(
            Groups(e => accounts[e.AccountId].PortfolioId?.ToString() ?? "unassigned",
                id => portfolioNames.GetValueOrDefault(id) ?? "Unassigned", (_, items) =>
                {
                    var count = items.Select(x => x.AccountId).Distinct().Count();
                    return $"{count} account{(count == 1 ? "" : "s")}";
                }),
            Groups(e => e.AccountId.ToString(), id => accounts[Guid.Parse(id)].Name,
                (id, _) => accounts[Guid.Parse(id)].VenueId),
            Groups(e => e.ContractId, id => id, (_, items) =>
                string.Join(", ", items.Select(e => e.VenueId).Distinct().Order())));
        var page = current.OrderByDescending(e => e.ClosedAt).ThenBy(e => e.Facts?.Play.Id ?? Guid.Empty)
            .ThenBy(e => e.Position?.OperationId, StringComparer.Ordinal).Skip(query.Offset).Take(PageSize)
            .Select(e => e.Facts is { } play
                ? new ReviewPlayDto(play.Play.Id, play.Play.Title, play.Play.AccountId,
                    accounts[play.Play.AccountId].Name, play.Play.VenueId, play.Play.ContractId!, e.ClosedAt,
                    ExactDecimal.Format(e.Result!.NetResult), ExactDecimal.Format(e.Result.ReturnPercent), ExactDecimal.Format(e.Result.RMultiple),
                    play.Play.HasReview, play.Play.Origin == PlayOrigin.Retrospective ? "retrospective" : "planned",
                    play.ManualSource ? "manual" : "imported")
                {
                    Local = e.Local!.ToDto(e.Result.NetResult), PlannedAfterExecution = PlayReceipts.PlannedAfterExecution(play),
                }
                : new ReviewPlayDto(null, $"{e.ContractId} {(e.Position!.Direction == "short" ? "Short" : "Long")}", e.AccountId,
                    accounts[e.AccountId].Name, e.VenueId, e.ContractId, e.ClosedAt, ExactDecimal.Format(e.Result!.NetResult),
                    ExactDecimal.Format(e.Result.ReturnPercent), null, false, "position", "imported") { OperationId = e.Position.OperationId })
            .ToList();
        return new ReviewDto(now, from ?? current.FirstOrDefault()?.ClosedAt, query.Period, "USD",
            options, Periods.Select(period => {
                var selected = accepted.Where(e => InPeriod(e.ClosedAt, From(period, now), now)).ToList();
                return new ReviewPeriodDto(period, selected.Count,
                    selected.Count == 0 ? null : ExactDecimal.Format(Sum(selected.Select(e => e.Result!.NetResult))))
                {
                    Positions = selected.Count(e => e.Position is not null)
                };
            }).ToList(),
            Metrics(current), series.Points, series.BucketDays, groups,
            new ReviewCoverageDto(candidates.Count, candidates.Count(e => e.Exclusion == "fees"),
                candidates.Count(e => e.Exclusion == "executions"), facts.OpenPlays, facts.UnassignedFills, facts.FirstFillAtUtc,
                include ? Notice + PositionNotice : Notice),
            page, query.Offset, query.Offset + page.Count < current.Count ? query.Offset + page.Count : null)
        {
            Positions = new ReviewPositionsDto(query.Positions, include, query.MinPlays, playCount,
                include ? current.Count(e => e.Position is not null) : null,
                include ? positions.Count(e => e.Result is null && InPeriod(e.ClosedAt, from, now)) : null, PlayWeight.ToString(CultureInfo.InvariantCulture))
        };
    }

    public async Task<ReviewExclusionsDto> ExclusionsAsync(ReviewQuery query, CancellationToken ct)
    {
        // Run the same filter validation as reporting, then evaluate one fresh snapshot with the same boundary.
        var now = time.GetUtcNow();
        var from = From(query.Period, now);
        if (query.Portfolio is not ("all" or "unassigned") &&
            (!Guid.TryParse(query.Portfolio, out var portfolio) || portfolio == Guid.Empty))
            throw new ProblemException(400, "Choose a valid portfolio.");
        if (Guid.TryParse(query.Portfolio, out var normalized)) query = query with { Portfolio = normalized.ToString() };
        if (query.AccountId == Guid.Empty || query.Offset is < 0 or > 1_000_000 ||
            query.Instrument is { } instrument && (string.IsNullOrWhiteSpace(instrument) || instrument.Length > 128 || instrument != instrument.Trim()))
            throw new ProblemException(400, "Invalid review filters.");
        var facts = await store.ReadAsync(query, now, ct);
        var accounts = facts.Accounts.Where(a => a.IsEnabled && (query.AccountId is null || a.Id == query.AccountId) &&
            (query.Portfolio == "all" || query.Portfolio == "unassigned" && a.PortfolioId is null ||
                a.PortfolioId?.ToString() == query.Portfolio)).ToDictionary(a => a.Id);
        var excluded = facts.ClosedPlays.Where(f => accounts.ContainsKey(f.Play.AccountId) &&
            (query.Instrument is null || f.Play.ContractId == query.Instrument))
            .Select(f => Evaluate(f, now, facts.IncompleteOrderPlayIds?.Contains(f.Play.Id) == true || f.IncompleteOrders is { Count: > 0 }))
            .Where(e => e.Exclusion is not null && InPeriod(e.ClosedAt, from, now))
            .OrderByDescending(e => e.ClosedAt).ThenBy(e => e.Facts!.Play.Id).ToList();
        var items = excluded.Skip(query.Offset).Take(PageSize).Select(e => new ReviewExclusionDto(e.Facts!.Play.Id,
            e.Facts.Play.Title, e.Facts.Play.AccountId, accounts[e.Facts.Play.AccountId].Name,
            e.Facts.Play.ContractId ?? "", ExclusionReasons(e.Facts, now, e.Exclusion!))).ToList();
        return new(items, excluded.Count, query.Offset, query.Offset + items.Count < excluded.Count ? query.Offset + items.Count : null, Notice);
    }

    private static IReadOnlyList<string> ExclusionReasons(ClosedPlayFacts facts, DateTimeOffset now, string exclusion)
    {
        var reasons = ExecutionIntegrity.Problems(ExecutionIntegrity.EntrySide(facts.CurrentPlan),
            facts.Fills.ToList(), facts.IncompleteOrders ?? []);
        PlayOutcomeCalculator.Outcome(facts, out var arithmeticProblem);
        if (arithmeticProblem is not null) reasons.Add(arithmeticProblem);
        if (exclusion == "fees" || facts.Fills.Any(f => !ExecutionFacts.IsFeeBasis(f.Fill.FeeBasis) ||
            string.IsNullOrWhiteSpace(f.Fill.FeeToken) || f.Fill.PnlBasis == ExecutionFacts.PnlGross &&
                f.Fill.Fee != 0 && !NominalStablecoins.Contains(f.Fill.FeeToken)))
        {
            reasons.AddRange(facts.Fills.Where(f => !ExecutionFacts.IsFeeBasis(f.Fill.FeeBasis) ||
                string.IsNullOrWhiteSpace(f.Fill.FeeToken) || f.Fill.PnlBasis == ExecutionFacts.PnlGross &&
                f.Fill.Fee != 0 && !NominalStablecoins.Contains(f.Fill.FeeToken)).Select(f =>
                    $"Fill {f.Fill.SourceFillId} of order {f.Fill.ExternalOrderId} has missing or unsupported fee inputs ({f.Fill.FeeToken}, {f.Fill.FeeBasis})."));
            if (!facts.ManualFeesComplete) reasons.Add("Manual costs are unknown or lack supported USD conversion; they are not treated as zero.");
        }
        if (exclusion == "executions")
        {
            reasons.AddRange(facts.Fills.Where(f => f.Fill.AccountId != facts.Play.AccountId || f.Fill.ContractId != facts.Play.ContractId ||
                f.Fill.PositionEffect is not (ExecutionFacts.Open or ExecutionFacts.Close) || f.Fill.Price <= 0 || f.Fill.Quantity <= 0 ||
                f.Fill.OccurredAtUtc > now).Select(f => $"Fill {f.Fill.SourceFillId} of order {f.Fill.ExternalOrderId} has invalid identity, effect, quantity, price or time."));
            if (facts.Fills.Select(f => f.Fill.Id).Distinct().Count() != facts.Fills.Count) reasons.Add("Repeated fill IDs prevent a unique execution result.");
            if (ExecutionIntegrity.Quantity(facts.Fills.Where(f => f.Role == OrderLinkRole.Entry).Select(f => f.Fill)) !=
                ExecutionIntegrity.Quantity(facts.Fills.Where(f => f.Role != OrderLinkRole.Entry).Select(f => f.Fill)))
                reasons.Add("Retained entry and exit quantities do not form a closed round trip.");
            if (reasons.Count == 0) reasons.Add("Complete opening and closing executions or executed entry notional are unavailable.");
        }
        return DiaryService.BoundedReasons(reasons);
    }

    /// <summary>Review's verdict on one Play: placed at <paramref name="ClosedAt"/>, scored, or excluded with a reason code.</summary>
    public sealed record PlayScore(DateTimeOffset ClosedAt, PlayOutcome? Result, string? Exclusion);

    /// <summary>
    /// One closed Play or imported position; an accepted Play also has its Play-local result beside the scored venue result.
    /// </summary>
    private sealed record Evaluated(ClosedPlayFacts? Facts, DateTimeOffset ClosedAt, PlayOutcome? Result, string? Exclusion)
    {
        public PlayLocalResult? Local { get; init; }
        public ReviewPosition? Position { get; init; }
        public string? PositionVenue { get; init; }
        public Guid AccountId => Facts?.Play.AccountId ?? Position!.AccountId;
        public string ContractId => Facts is { } facts ? facts.Play.ContractId! : Position!.ContractId;
        public string VenueId => Facts?.Play.VenueId ?? PositionVenue!;
        public int Weight => Facts is null ? 1 : PlayWeight;
    }

    private Evaluated Evaluate(ClosedPlayFacts facts, DateTimeOffset now, bool incompleteOrders)
    {
        var score = Score(facts, now, incompleteOrders);
        return new Evaluated(facts, score.ClosedAt, score.Result, score.Exclusion)
        {
            Local = score.Exclusion is null ? PlayLocalResult.From(facts, venues?.Descriptor(facts.Play.VenueId)?.QuoteAsset) : null,
        };
    }

    /// <summary>
    /// Review's verdict on one Play, shared with the Play's own receipt: its scored outcome, whose R is known only with a
    /// complete initial stop, or why it is excluded (<c>executions</c> or <c>fees</c>). <paramref name="incompleteOrders"/>
    /// says a linked order is known to miss executions.
    /// </summary>
    public static PlayScore Score(ClosedPlayFacts facts, DateTimeOffset now, bool incompleteOrders)
    {
        var fills = facts.Fills;
        var entries = fills.Where(f => f.Role == OrderLinkRole.Entry).ToList();
        var exits = fills.Where(f => f.Role != OrderLinkRole.Entry).ToList();
        // EndedAtUtc records when sync detected closure, not necessarily when it happened. Review places every Play, excluded
        // ones too, at its unvalidated latest exit without the detection clamp sizing applies (PlayExitTime.LatestValidExit).
        var close = PlayExitTime.LatestExit(fills) ?? facts.Play.EndedAtUtc ?? facts.Play.UpdatedAtUtc;
        var incomplete = new PlayScore(close, null, "executions");
        if (incompleteOrders || facts.Play.Status != PlayStatus.Closed || facts.Play.ContractId is null || entries.Count == 0 || exits.Count == 0 ||
            fills.Select(f => f.Fill.Id).Distinct().Count() != fills.Count ||
            fills.Any(f => f.Fill.AccountId != facts.Play.AccountId || f.Fill.ContractId != facts.Play.ContractId ||
                f.Fill.OccurredAtUtc > now || f.Fill.Quantity <= 0 || f.Fill.Price <= 0) ||
            entries.Any(f => f.Fill.PositionEffect != ExecutionFacts.Open) ||
            exits.Any(f => f.Fill.PositionEffect != ExecutionFacts.Close) ||
            !ExecutionIntegrity.Settled(facts))
            return incomplete;
        var side = ExecutionIntegrity.EntrySide(facts.CurrentPlan);
        var result = PlayOutcomeCalculator.Outcome(facts);
        if (result is not null && !HasCompleteInitialRisk(facts, entries.Min(f => f.Fill.OccurredAtUtc), side))
            result = result with { PlannedRisk = null };
        return result is null ? incomplete : !result.FeesComplete ? incomplete with { Exclusion = "fees" } : new PlayScore(close, result, null);
    }

    private static bool HasCompleteInitialRisk(ClosedPlayFacts facts, DateTimeOffset entryAt, string side)
    {
        // Review never treats a retrospective or partially specified plan as known initial risk.
        // The sizing assistant retains its existing fallback/partial-plan estimate.
        var revision = facts.Revisions.Where(r => r.CreatedAtUtc <= entryAt).OrderBy(r => r.Number).LastOrDefault();
        if (facts.Play.Origin == PlayOrigin.Retrospective || revision is null) return false;
        var plan = facts.ParsedPlan(revision.Plan);
        if ((plan.Direction == "long" ? ExecutionFacts.Buy : ExecutionFacts.Sell) != side) return false;
        static decimal Positive(string value) =>
            decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : 0;
        if (plan.Entries.Count == 0 || plan.Entries.Sum(e => Positive(e.Share)) != 100) return false;
        var levels = ExecutionMatcher.Levels(plan).Where(l => l.Role == OrderLinkRole.Stop).ToLookup(l => l.EntryId);
        return plan.Entries.All(e => Positive(e.Share) > 0 && Positive(e.Price) > 0 && e.Stops.Count > 0 &&
            e.Stops.Sum(s => Positive(s.Share)) == 100 && e.Stops.All(s => Positive(s.Share) > 0 &&
                levels[e.Id].FirstOrDefault(l => l.LevelId == s.Id)?.Price is > 0 and var stop &&
                (plan.Direction == "long" ? stop < Positive(e.Price) : stop > Positive(e.Price))));
    }

    /// <summary>The closed Plays, their linked fills and the enabled accounts: what decides which positions a Play claims.</summary>
    private static string Claims(ReviewFacts facts) => string.Join(';', facts.ClosedPlays.OrderBy(f => f.Play.Id)
        .Select(f => $"{f.Play.Id}:{f.Play.Status}:{string.Join(',', f.Fills.Select(x => x.Fill.Id).Order())}")
        .Concat(facts.Accounts.Where(a => a.IsEnabled).Select(a => a.Id.ToString()).Order()));

    private static bool InPeriod(DateTimeOffset at, DateTimeOffset? from, DateTimeOffset now) =>
        at <= now && (from is null || at > from);

    private static ReviewMetricsDto Metrics(List<Evaluated> plays)
    {
        // Rate metrics weigh each Play PlayWeight times a position; with only one kind they equal the unweighted figures.
        var outcomes = plays.Select(p => (Outcome: p.Result!, p.Weight)).ToList();
        var wins = outcomes.Where(o => o.Outcome.NetResult > 0).ToList();
        var losses = outcomes.Where(o => o.Outcome.NetResult < 0).ToList();
        var gain = Average(wins.Select(o => (o.Outcome.ReturnPercent, o.Weight)));
        var loss = Average(losses.Select(o => (-o.Outcome.ReturnPercent, o.Weight)));
        var risks = outcomes.Where(o => o.Outcome.RMultiple is not null).ToList();
        ExactAmount running = default, peak = default, drawdown = default;
        int streak = 0, maxWins = 0, maxLosses = 0;
        foreach (var play in plays.OrderBy(e => e.ClosedAt).ThenBy(e => e.Facts?.Play.Id ?? Guid.Empty)
            .ThenBy(e => e.Position?.OperationId, StringComparer.Ordinal))
        {
            var net = play.Result!.NetResult;
            running += ExactAmount.From(net);
            peak = Max(peak, running);
            drawdown = Max(drawdown, peak - running);
            if (net == 0) continue;
            streak = net > 0 ? Math.Max(0, streak) + 1 : Math.Min(0, streak) - 1;
            maxWins = Math.Max(maxWins, streak);
            maxLosses = Math.Max(maxLosses, -streak);
        }
        var played = plays.Where(p => p.Facts is not null).ToList();
        return new ReviewMetricsDto(plays.Count, wins.Count, losses.Count, plays.Count - wins.Count - losses.Count,
            plays.Count == 0 ? null : ExactDecimal.Format(running),
            ExactDecimal.Format(Ratio(ExactAmount.From(wins.Sum(o => o.Weight)), ExactAmount.From(wins.Concat(losses).Sum(o => o.Weight)))),
            ExactDecimal.Format(gain), ExactDecimal.Format(loss), ExactDecimal.Format(Ratio(gain, loss)),
            ExactDecimal.Format(Ratio(Sum(wins.Select(o => o.Outcome.NetResult)), -Sum(losses.Select(o => o.Outcome.NetResult)))),
            ExactDecimal.Format(Average(wins.Select(o => (o.Outcome.NetResult, o.Weight)))),
            ExactDecimal.Format(Average(losses.Select(o => (-o.Outcome.NetResult, o.Weight)))),
            ExactDecimal.Format(Average(outcomes.Select(o => (o.Outcome.NetResult, o.Weight)))),
            ExactDecimal.Format(Average(risks.Select(o => (o.Outcome.RMultiple!.Value, 1)))), risks.Count,
            played.Count(p => p.Facts!.Play.HasReview), plays.Count == 0 ? null : ExactDecimal.Format(drawdown),
            maxWins, maxLosses)
        {
            SignDiffers = played.Count(p => p.Local!.SignDiffers(p.Result!.NetResult)),
            LocalUnknown = played.Count(p => p.Local!.Net is null),
            FeeOnlyLosses = outcomes.Count(o => o.Outcome.FeeOnlyLoss),
            UnknownGrossLosses = losses.Count(o => o.Outcome.GrossResult is null),
            Positions = plays.Count - played.Count,
        };
    }

    private static (List<ReviewPointDto> Points, int BucketDays) Series(List<Evaluated> plays, DateTimeOffset? from, DateTimeOffset now)
    {
        if (plays.Count == 0) return ([], 1);
        var first = (from ?? plays.Min(p => p.ClosedAt)).UtcDateTime.Date;
        // Bound long-history charts without truncating any outcomes or aggregates.
        var days = (now.UtcDateTime.Date - first).Days + 1;
        var bucketDays = Math.Max(1, (int)Math.Ceiling(days / 366m));
        var buckets = plays.ToLookup(p => (p.ClosedAt.UtcDateTime.Date - first).Days / bucketDays);
        List<ReviewPointDto> points = [];
        ExactAmount cumulative = default, peak = default;
        for (var index = 0; index * bucketDays < days; index++)
        {
            var items = buckets[index].ToList();
            var net = Sum(items.Select(p => p.Result!.NetResult));
            cumulative += net;
            peak = Max(peak, cumulative);
            var end = first.AddDays(Math.Min((index + 1) * bucketDays, days) - 1);
            points.Add(new(new DateTimeOffset(end, TimeSpan.Zero), ExactDecimal.Format(net), ExactDecimal.Format(cumulative),
                ExactDecimal.Format(cumulative - peak), items.Count));
        }
        return (points, bucketDays);
    }

    private static ExactAmount Sum(IEnumerable<decimal> values) =>
        values.Aggregate(default(ExactAmount), (sum, value) => sum + ExactAmount.From(value));
    private static ExactAmount Max(ExactAmount a, ExactAmount b) => a.CompareTo(b) >= 0 ? a : b;
    private static ExactAmount? Average(IEnumerable<(decimal Value, int Weight)> values)
    {
        var items = values.ToList();
        return Ratio(items.Aggregate(default(ExactAmount), (sum, item) => sum + new ExactAmount(ExactAmount.From(item.Value).Units * item.Weight)),
            ExactAmount.From(items.Sum(item => item.Weight)));
    }
    // Aggregate sums can exceed decimal's coefficient range; round only a derived division, to 28 places.
    private static ExactAmount? Ratio(ExactAmount? a, ExactAmount? b) =>
        a is { } numerator && b is { } denominator && denominator.Units.Sign > 0
            ? ExactAmount.Parse(ExactAmount.Average(numerator.Units * ExactAmount.From(1).Units, denominator)!) : null;
}
