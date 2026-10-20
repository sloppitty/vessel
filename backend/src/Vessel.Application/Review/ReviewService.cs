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
public sealed class ReviewService(IReviewStore store, TimeProvider time, IVenueRegistry? venues = null)
{
    public const int PageSize = 50;
    public static readonly string[] Periods = ["day", "week", "month", "year", "all"];
    private const string Notice = "Saved closed Plays only, from linked imported executions or explicit owner-entered manual facts. " +
        "Manual facts are estimates, not venue balance reconciliation or imported Activity. Retrospective Plays have no contemporaneous " +
        "plan or initial-risk R and are excluded from the sizing ladder. Return percent is net/executed entry notional, not return on margin. " +
        "Unassigned imported fills are not Plays. Retained venue history can be incomplete; All is not lifetime history. " +
        "Results are nominal USD and include reported execution fees once. " + NominalStablecoins.Caveat +
        " Venues with different quote stablecoins are pooled nominally. Separate funding cashflows are not added; " +
        "whether RISEx reported realized P&L includes funding remains unverified. Balanced retained fills are not proof of full venue history. " +
        "Accounts use current portfolio membership; disabled accounts are excluded. Open-play counts are current, not a historical cohort. " +
        "Unresolved Plays, declared by the owner because their executions cannot settle them, have no result and are excluded.";

    public static DateTimeOffset? From(string period, DateTimeOffset now) => period switch
    {
        "day" => now.AddDays(-1), "week" => now.AddDays(-7), "month" => now.AddDays(-30),
        "year" => now.AddDays(-365), "all" => null,
        _ => throw new ProblemException(400, "Choose day, week, month, year or all."),
    };

    /// <summary>The filters every Review read validates the same way, with the portfolio ID normalized.</summary>
    private static ReviewQuery Validated(ReviewQuery query)
    {
        From(query.Period, DateTimeOffset.UnixEpoch);
        if (query.Portfolio is not ("all" or "unassigned") &&
            (!Guid.TryParse(query.Portfolio, out var portfolio) || portfolio == Guid.Empty))
            throw new ProblemException(400, "Choose a valid portfolio.");
        if (Guid.TryParse(query.Portfolio, out var normalized)) query = query with { Portfolio = normalized.ToString() };
        if (query.AccountId == Guid.Empty || query.Offset is < 0 or > 1_000_000 ||
            query.Instrument is { } instrument && (string.IsNullOrWhiteSpace(instrument) || instrument.Length > 128 || instrument != instrument.Trim()))
            throw new ProblemException(400, "Invalid review filters.");
        return query;
    }

    public async Task<ReviewDto> GetAsync(ReviewQuery query, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var from = From(query.Period, now);
        query = Validated(query);

        var facts = await store.ReadAsync(query, now, ct);
        var accounts = facts.Accounts.Where(a => a.IsEnabled).ToDictionary(a => a.Id);
        var scoped = accounts.Values.Where(a =>
            (query.AccountId is null || a.Id == query.AccountId) &&
            (query.Portfolio == "all" || query.Portfolio == "unassigned" && a.PortfolioId is null ||
                a.PortfolioId?.ToString() == query.Portfolio)).Select(a => a.Id).ToHashSet();
        var evaluated = facts.ClosedPlays.Where(f => scoped.Contains(f.Play.AccountId) &&
            (query.Instrument is null || f.Play.ContractId == query.Instrument))
            .Select(f => Evaluate(f, now, facts.IncompleteOrderPlayIds?.Contains(f.Play.Id) == true || f.IncompleteOrders is { Count: > 0 })).ToList();
        var accepted = evaluated.Where(e => e.Result is not null)
            .OrderBy(e => e.ClosedAt).ThenBy(e => e.Facts.Play.Id).ToList();
        var current = accepted.Where(e => InPeriod(e.ClosedAt, from, now)).ToList();
        var candidates = evaluated.Where(e => InPeriod(e.ClosedAt, from, now)).ToList();
        var unresolved = Unresolved(facts, scoped, query.Instrument).Count(u => InPeriod(u.EndedAt, from, now));
        var portfolioNames = facts.Portfolios.ToDictionary(p => p.Id.ToString(), p => p.Name);
        var options = new ReviewOptionsDto(
            facts.Portfolios.OrderBy(p => p.Name).Select(p => new ReviewPortfolioDto(p.Id.ToString(), p.Name)).ToList(),
            accounts.Values.OrderBy(a => a.Name).Select(a => new ReviewAccountDto(a.Id, a.Name, a.PortfolioId, a.VenueId,
                a.LastSyncedAtUtc, a.SyncStatus, a.HistoryNotice)).ToList(),
            facts.ClosedPlays.Select(f => f.Play).Concat(facts.UnresolvedPlays.Select(u => u.Play)).Concat(facts.CancelledPlays.Select(c => c.Play))
                .Where(p => accounts.ContainsKey(p.AccountId))
                .Select(p => p.ContractId).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
        var series = Series(current, from, now);
        List<ReviewGroupDto> Groups(Func<Evaluated, string> key, Func<string, string> name, Func<string, List<Evaluated>, string> detail) =>
            current.GroupBy(key).Select(group =>
            {
                var items = group.ToList();
                return (Total: Sum(items.Select(x => x.Result!.NetResult)),
                    Dto: new ReviewGroupDto(group.Key, name(group.Key), detail(group.Key, items), Metrics(items)));
            }).OrderByDescending(x => x.Total).ThenBy(x => x.Dto.Name, StringComparer.Ordinal).Select(x => x.Dto).ToList();

        var groups = new ReviewGroupsDto(
            Groups(e => accounts[e.Facts.Play.AccountId].PortfolioId?.ToString() ?? "unassigned",
                id => portfolioNames.GetValueOrDefault(id) ?? "Unassigned", (_, items) =>
                {
                    var count = items.Select(x => x.Facts.Play.AccountId).Distinct().Count();
                    return $"{count} account{(count == 1 ? "" : "s")}";
                }),
            Groups(e => e.Facts.Play.AccountId.ToString(), id => accounts[Guid.Parse(id)].Name,
                (id, _) => accounts[Guid.Parse(id)].VenueId),
            Groups(e => e.Facts.Play.ContractId!, id => id, (_, items) =>
                string.Join(", ", items.Select(e => e.Facts.Play.VenueId).Distinct().Order())))
        {
            // Fixed order rather than by result (1.4b).
            Origins = Fixed(current, e => OriginName(e.Facts.Play), [("planned", "Planned"), ("retrospective", "Retrospective")]),
            Directions = Fixed(current, e => e.Facts.Play.Direction, [("long", "Long"), ("short", "Short")]),
        };
        var page = current.OrderByDescending(e => e.ClosedAt).ThenBy(e => e.Facts.Play.Id).Skip(query.Offset).Take(PageSize)
            .Select(e => new ReviewPlayDto(e.Facts.Play.Id, e.Facts.Play.Title, e.Facts.Play.AccountId,
                accounts[e.Facts.Play.AccountId].Name, e.Facts.Play.VenueId, e.Facts.Play.ContractId!, e.ClosedAt,
                ExactDecimal.Format(e.Result!.NetResult), ExactDecimal.Format(e.Result.ReturnPercent), ExactDecimal.Format(e.Result.RMultiple),
                e.Facts.Play.ReviewCompletion is not null, e.Facts.Play.Origin == PlayOrigin.Retrospective ? "retrospective" : "planned",
                e.Facts.ManualSource ? "manual" : "imported")
            {
                Local = e.Local!.ToDto(e.Result.NetResult), PlannedAfterExecution = PlayReceipts.PlannedAfterExecution(e.Facts),
                Adherence = e.Facts.Play.ReviewAdherence, MistakeTag = e.Facts.Play.ReviewMistakeTag, Direction = e.Facts.Play.Direction,
                ReviewCompletion = PlayService.CompletionName(e.Facts.Play.ReviewCompletion),
            }).ToList();
        var cases = ProcessCases(evaluated, facts, scoped, query.Instrument, from, now);
        return new ReviewDto(now, from ?? current.FirstOrDefault()?.ClosedAt, query.Period, "USD",
            options, Periods.Select(period => {
                var selected = accepted.Where(e => InPeriod(e.ClosedAt, From(period, now), now)).ToList();
                return new ReviewPeriodDto(period, selected.Count,
                    selected.Count == 0 ? null : ExactDecimal.Format(Sum(selected.Select(e => e.Result!.NetResult))));
            }).ToList(),
            Metrics(current), series.Points, series.BucketDays, groups,
            new ReviewCoverageDto(candidates.Count, candidates.Count(e => e.Exclusion == "fees"),
                candidates.Count(e => e.Exclusion == "executions"), facts.OpenPlays, facts.UnassignedFills, facts.FirstFillAtUtc, Notice)
            { Unresolved = unresolved },
            page, query.Offset, query.Offset + page.Count < current.Count ? query.Offset + page.Count : null)
        {
            Preflight = Preflight(cases),
            Process = Process(cases),
        };
    }

    /// <summary>
    /// Pre-flights over the process population's planned Plays that were planned (a Draft cancelled before planning had no
    /// Plan it); a retrospective Play has no plan and is left out. Each count equals its drill-down.
    /// </summary>
    private static ReviewPreflightDto Preflight(IReadOnlyList<ProcessCase> cases)
    {
        var planned = cases.Where(PreflightApplies).Select(c => c.Preflight).ToList();
        var recorded = planned.OfType<IReadOnlyList<PreflightCheckDto>>().ToList();
        return new(recorded.Count, planned.Count - recorded.Count, PlayPreflight.Codes
            .Select(code => new ReviewPreflightCountDto(code, recorded.Count(checks => checks.Any(c => c.Code == code && c.Outcome == "skipped"))))
            .ToList());
    }

    private static bool PreflightApplies(ProcessCase c) => c.Play.Origin == PlayOrigin.Planned && c.Play.PlannedAtUtc is not null;

    /// <summary>The mistake-tag key: trimmed and case-insensitive, so spellings of one tag count together.</summary>
    public static string TagKey(string tag) => tag.Trim().ToUpperInvariant();

    private static string OriginName(Play play) => play.Origin == PlayOrigin.Retrospective ? "retrospective" : "planned";

    /// <summary>Groups in a fixed order, each with the existing metrics; a group without Plays is left out.</summary>
    private static List<ReviewGroupDto> Fixed(List<Evaluated> current, Func<Evaluated, string> key, (string Id, string Name)[] order) =>
        order.Select(group => (group, Items: current.Where(e => key(e) == group.Id).ToList())).Where(x => x.Items.Count > 0)
            .Select(x => new ReviewGroupDto(x.group.Id, x.group.Name, $"{x.Items.Count} scored", Metrics(x.Items))).ToList();

    /// <summary>
    /// One Play of the process view (phase 1, 1.4b): Closed, Cancelled or Unresolved, in scope and period, with Review's verdict.
    /// <see cref="Lane"/> is the first that applies: retrospective, unscored (with answers), unanswered, not-applicable, or cells;
    /// null for an unscored planned Play with no answers, which only the exclusion list shows.
    /// </summary>
    private sealed record ProcessCase(Play Play, DateTimeOffset At, PlayOutcome? Result, IReadOnlyList<PreflightCheckDto>? Preflight,
        bool PostHoc, string? Lane, string? Cell)
    {
        public string Outcome => Result is null ? "unscored" : Result.NetResult > 0 ? "win" : Result.NetResult < 0 ? "loss" : "scratch";
    }

    private static ProcessCase Case(Play play, DateTimeOffset at, PlayOutcome? result, IReadOnlyList<PlayPlanRevision> revisions, bool postHoc)
    {
        // The answers as stored: a completed review is not required.
        var answers = play.Answers;
        var outcome = result is null ? null : result.NetResult > 0 ? "win" : result.NetResult < 0 ? "loss" : "scratch";
        var (lane, cell) = play.Origin == PlayOrigin.Retrospective ? ("retrospective", (string?)null)
            : result is null ? (answers.IsAnswered ? "unscored" : null, null)
            : answers.Adherence is null ? ("unanswered", null)
            : answers.Adherence == "not-applicable" ? ("not-applicable", null)
            : ("cells", $"{answers.Adherence}-{outcome}");
        return new(play, at, result, PlayPreflight.Read(revisions.SingleOrDefault(r => r.Number == 1)?.Preflight), postHoc, lane, cell);
    }

    /// <summary>The process population: the period's Closed, Cancelled and Unresolved Plays in scope, each by its own time.</summary>
    private static List<ProcessCase> ProcessCases(IEnumerable<Evaluated> closed, ReviewFacts facts, HashSet<Guid> scope, string? instrument,
        DateTimeOffset? from, DateTimeOffset now) =>
        closed.Select(e => Case(e.Facts.Play, e.ClosedAt, e.Result, e.Facts.Revisions, PlayReceipts.PlannedAfterExecution(e.Facts)))
            .Concat(facts.CancelledPlays.Where(c => scope.Contains(c.Play.AccountId) && (instrument is null || c.Play.ContractId == instrument))
                .Select(c => Case(c.Play, c.Play.EndedAtUtc ?? c.Play.UpdatedAtUtc, null, c.Revisions, false)))
            .Concat(Unresolved(facts, scope, instrument).Select(u => Case(u.Play, u.EndedAt, null, u.Revisions,
                u.Play is { Origin: PlayOrigin.Planned, PlannedAtUtc: { } planned } && u.FirstExecutionAtUtc is { } first && planned > first)))
            .Where(c => InPeriod(c.At, from, now)).ToList();

    private const int MaxMistakeTags = 200;

    private static ReviewProcessDto Process(IReadOnlyList<ProcessCase> cases)
    {
        ReviewProcessRowDto Row(string adherence) => new(cases.Count(c => c.Cell == $"{adherence}-win"),
            cases.Count(c => c.Cell == $"{adherence}-loss"), cases.Count(c => c.Cell == $"{adherence}-scratch"));
        var tags = Tags(cases).ToList();
        return new(Row("followed"), Row("deviated"), cases.Count(c => c.Lane == "retrospective"), cases.Count(c => c.Lane == "unscored"),
            cases.Count(c => c.Lane == "unanswered"), cases.Count(c => c.Lane == "not-applicable"),
            tags.OrderBy(t => t.Key, StringComparer.Ordinal).Take(MaxMistakeTags).Select(t => new ReviewMistakeTagDto(t.Spelling,
                t.Cases.Count(c => c.Outcome == "win"), t.Cases.Count(c => c.Outcome == "loss"), t.Cases.Count(c => c.Outcome == "scratch"),
                t.Cases.Count(c => c.Outcome == "unscored"))).ToList(), tags.Count);
    }

    /// <summary>Process cases per mistake tag, keyed case-insensitively and shown in the latest spelling (newest review first).</summary>
    private static IEnumerable<(string Key, string Spelling, List<ProcessCase> Cases)> Tags(IReadOnlyList<ProcessCase> cases) =>
        cases.Where(c => c.Lane is not null && !string.IsNullOrWhiteSpace(c.Play.ReviewMistakeTag))
            .GroupBy(c => TagKey(c.Play.ReviewMistakeTag!))
            .Select(g => (g.Key, g.OrderByDescending(c => c.Play.ReviewedAtUtc).ThenByDescending(c => c.Play.UpdatedAtUtc).ThenBy(c => c.Play.Id)
                .First().Play.ReviewMistakeTag!.Trim(), g.ToList()));

    public static readonly string[] ProcessCells = ["followed-win", "followed-loss", "followed-scratch", "deviated-win", "deviated-loss", "deviated-scratch"];
    public static readonly string[] ProcessLanes = ["retrospective", "unscored", "unanswered", "not-applicable"];

    /// <summary>
    /// One process drill-down (phase 1, 1.4b), paged, newest first: exactly the Plays its count holds, from the same snapshot
    /// rules as <see cref="GetAsync"/>. Financial values are null for Plays Review does not score.
    /// </summary>
    public async Task<ReviewProcessPageDto> ProcessAsync(ReviewProcessQuery request, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var query = Validated(request.Filters);
        var from = From(query.Period, now);
        var selectors = new[] { request.Cell, request.Lane, request.MistakeTag, request.SkippedCheck }.Count(s => s is not null);
        if (selectors != 1) throw new ProblemException(400, "Choose one cell, lane, mistake tag or skipped check.");
        if (request.Cell is { } cell && !ProcessCells.Contains(cell) || request.Lane is { } lane && !ProcessLanes.Contains(lane) ||
            request.MistakeTag is { } tag && (string.IsNullOrWhiteSpace(tag) || tag.Length > 100) ||
            request.SkippedCheck is { } check && check != "not-recorded" && !PlayPreflight.Codes.Contains(check))
            throw new ProblemException(400, "Invalid process selection.");
        var facts = await store.ReadAsync(query, now, ct);
        var accounts = facts.Accounts.Where(a => a.IsEnabled).ToDictionary(a => a.Id);
        var scoped = accounts.Values.Where(a => (query.AccountId is null || a.Id == query.AccountId) &&
            (query.Portfolio == "all" || query.Portfolio == "unassigned" && a.PortfolioId is null ||
                a.PortfolioId?.ToString() == query.Portfolio)).Select(a => a.Id).ToHashSet();
        var evaluated = facts.ClosedPlays.Where(f => scoped.Contains(f.Play.AccountId) && (query.Instrument is null || f.Play.ContractId == query.Instrument))
            .Select(f => Evaluate(f, now, facts.IncompleteOrderPlayIds?.Contains(f.Play.Id) == true || f.IncompleteOrders is { Count: > 0 }));
        var cases = ProcessCases(evaluated, facts, scoped, query.Instrument, from, now);
        var selected = (request.Cell is { } c1 ? cases.Where(c => c.Cell == c1)
            : request.Lane is { } l1 ? cases.Where(c => c.Lane == l1)
            : request.MistakeTag is { } t1 ? Tags(cases).Where(t => t.Key == TagKey(t1)).SelectMany(t => t.Cases)
            : request.SkippedCheck == "not-recorded" ? cases.Where(c => PreflightApplies(c) && c.Preflight is null)
            : cases.Where(c => PreflightApplies(c) && c.Preflight?.Any(p => p.Code == request.SkippedCheck && p.Outcome == "skipped") == true))
            .OrderByDescending(c => c.At).ThenBy(c => c.Play.Id).ToList();
        var items = selected.Skip(query.Offset).Take(PageSize).Select(c => new ReviewProcessPlayDto(c.Play.Id, c.Play.Title, c.Play.AccountId,
            accounts[c.Play.AccountId].Name, c.Play.VenueId, c.Play.ContractId ?? "", PlayService.StatusName(c.Play.Status), OriginName(c.Play),
            c.Play.Direction, c.At, ExactDecimal.Format(c.Result?.NetResult), ExactDecimal.Format(c.Result?.ReturnPercent),
            ExactDecimal.Format(c.Result?.RMultiple), c.Play.ReviewAdherence, c.Play.ReviewMistakeTag, c.PostHoc,
            PlayService.CompletionName(c.Play.ReviewCompletion))).ToList();
        return new(items, selected.Count, query.Offset, query.Offset + items.Count < selected.Count ? query.Offset + items.Count : null);
    }

    /// <summary>The owner's earlier mistake tags for the review form's autocomplete, newest use first.</summary>
    public Task<IReadOnlyList<string>> MistakeTagsAsync(CancellationToken ct) => store.MistakeTagsAsync(100, ct);

    public async Task<ReviewExclusionsDto> ExclusionsAsync(ReviewQuery query, CancellationToken ct)
    {
        // Run the same filter validation as reporting, then evaluate one fresh snapshot with the same boundary.
        var now = time.GetUtcNow();
        var from = From(query.Period, now);
        query = Validated(query);
        var facts = await store.ReadAsync(query, now, ct);
        var accounts = facts.Accounts.Where(a => a.IsEnabled && (query.AccountId is null || a.Id == query.AccountId) &&
            (query.Portfolio == "all" || query.Portfolio == "unassigned" && a.PortfolioId is null ||
                a.PortfolioId?.ToString() == query.Portfolio)).ToDictionary(a => a.Id);
        var excluded = facts.ClosedPlays.Where(f => accounts.ContainsKey(f.Play.AccountId) &&
            (query.Instrument is null || f.Play.ContractId == query.Instrument))
            .Select(f => Evaluate(f, now, facts.IncompleteOrderPlayIds?.Contains(f.Play.Id) == true || f.IncompleteOrders is { Count: > 0 }))
            .Where(e => e.Exclusion is not null && InPeriod(e.ClosedAt, from, now))
            .OrderByDescending(e => e.ClosedAt).ThenBy(e => e.Facts.Play.Id).ToList();
        // Unresolved Plays join the drill-down by their declaration time, with its reason, note and execution facts.
        var all = excluded.Select(e => (At: e.ClosedAt, e.Facts.Play, Item: (Func<ReviewExclusionDto>)(() => new ReviewExclusionDto(e.Facts.Play.Id,
                e.Facts.Play.Title, e.Facts.Play.AccountId, accounts[e.Facts.Play.AccountId].Name,
                e.Facts.Play.ContractId ?? "", ExclusionReasons(e.Facts, now, e.Exclusion!)))))
            .Concat(Unresolved(facts, accounts.Keys.ToHashSet(), query.Instrument).Where(u => InPeriod(u.EndedAt, from, now))
                .Select(u => (At: u.EndedAt, u.Play, Item: (Func<ReviewExclusionDto>)(() => new ReviewExclusionDto(u.Play.Id, u.Play.Title,
                    u.Play.AccountId, accounts[u.Play.AccountId].Name, u.Play.ContractId ?? "", [UnresolvedReason(u.Declaration)])
                    { Unresolved = u.Declaration }))))
            .OrderByDescending(x => x.At).ThenBy(x => x.Play.Id).ToList();
        var items = all.Skip(query.Offset).Take(PageSize).Select(x => x.Item()).ToList();
        return new(items, all.Count, query.Offset, query.Offset + items.Count < all.Count ? query.Offset + items.Count : null, Notice);
    }

    /// <summary>Unresolved Plays in the scope's accounts and instrument; they are excluded with the reason "unresolved".</summary>
    private static IEnumerable<UnresolvedPlayFacts> Unresolved(ReviewFacts facts, HashSet<Guid> accounts, string? instrument) =>
        facts.UnresolvedPlays.Where(u => accounts.Contains(u.Play.AccountId) && (instrument is null || u.Play.ContractId == instrument));

    private static string UnresolvedReason(UnresolvedDeclarationDto? declaration) =>
        declaration is null || PlayUnresolved.ParseReason(declaration.Reason) is not { } reason
            ? "Declared unresolved by the owner: its executions cannot settle it, so it has no result."
            : $"Declared unresolved by the owner because of {PlayUnresolved.ReasonLabel(reason)}: its executions cannot settle it, so it has no result.";

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

    /// <summary>One closed Play; an accepted one also has its Play-local result beside the scored venue result.</summary>
    private sealed record Evaluated(ClosedPlayFacts Facts, DateTimeOffset ClosedAt, PlayOutcome? Result, string? Exclusion)
    {
        public PlayLocalResult? Local { get; init; }
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

    private static bool InPeriod(DateTimeOffset at, DateTimeOffset? from, DateTimeOffset now) =>
        at <= now && (from is null || at > from);

    private static ReviewMetricsDto Metrics(List<Evaluated> plays)
    {
        var outcomes = plays.Select(p => p.Result!).ToList();
        var wins = outcomes.Where(o => o.NetResult > 0).ToList();
        var losses = outcomes.Where(o => o.NetResult < 0).ToList();
        var gain = Average(wins.Select(o => o.ReturnPercent));
        var loss = Average(losses.Select(o => -o.ReturnPercent));
        var risks = outcomes.Where(o => o.RMultiple is not null).ToList();
        ExactAmount running = default, peak = default, drawdown = default;
        int streak = 0, maxWins = 0, maxLosses = 0;
        foreach (var play in plays.OrderBy(e => e.ClosedAt).ThenBy(e => e.Facts.Play.Id))
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
        return new ReviewMetricsDto(plays.Count, wins.Count, losses.Count, plays.Count - wins.Count - losses.Count,
            plays.Count == 0 ? null : ExactDecimal.Format(running),
            ExactDecimal.Format(Ratio(ExactAmount.From(wins.Count), ExactAmount.From(wins.Count + losses.Count))),
            ExactDecimal.Format(gain), ExactDecimal.Format(loss), ExactDecimal.Format(Ratio(gain, loss)),
            ExactDecimal.Format(Ratio(Sum(wins.Select(o => o.NetResult)), -Sum(losses.Select(o => o.NetResult)))),
            ExactDecimal.Format(Average(wins.Select(o => o.NetResult))), ExactDecimal.Format(Average(losses.Select(o => -o.NetResult))),
            ExactDecimal.Format(Ratio(running, ExactAmount.From(plays.Count))),
            ExactDecimal.Format(Average(risks.Select(o => o.RMultiple!.Value))), risks.Count,
            plays.Count(p => p.Facts.Play.ReviewCompletion is not null), plays.Count == 0 ? null : ExactDecimal.Format(drawdown),
            maxWins, maxLosses)
        {
            SignDiffers = plays.Count(p => p.Local!.SignDiffers(p.Result!.NetResult)),
            LocalUnknown = plays.Count(p => p.Local!.Net is null),
            FeeOnlyLosses = outcomes.Count(o => o.FeeOnlyLoss),
            UnknownGrossLosses = losses.Count(o => o.GrossResult is null),
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
    private static ExactAmount? Average(IEnumerable<decimal> values)
    {
        var items = values.ToList();
        return Ratio(Sum(items), ExactAmount.From(items.Count));
    }
    // Aggregate sums can exceed decimal's coefficient range; round only a derived division, to 28 places.
    private static ExactAmount? Ratio(ExactAmount? a, ExactAmount? b) =>
        a is { } numerator && b is { } denominator && denominator.Units.Sign > 0
            ? ExactAmount.Parse(ExactAmount.Average(numerator.Units * ExactAmount.From(1).Units, denominator)!) : null;
}
