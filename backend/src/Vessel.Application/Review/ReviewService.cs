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
using Vessel.Domain.Common;
using Vessel.Domain.Reporting;

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
    /// <summary>The <see cref="ReviewQuery.Week"/> value for the week holding today in the reporting zone.</summary>
    public const string CurrentWeek = "current";
    private const string PaperNotice = "Simulated paper Plays only, separate from real trading. Touched allocations execute at their defined price, " +
        "with zero fees and zero slippage. Unresolved or incomplete simulations are excluded, never guessed. " +
        "Starting capital is not profit. Return is simulated P&L over entry notional, not margin. " +
        "Accounts use current paper portfolio membership; disabled accounts are excluded.";
    private const string Notice = "Saved closed Plays only, from linked imported executions or explicit owner-entered manual facts. " +
        "Manual facts are estimates, not venue balance reconciliation or imported Activity. Retrospective Plays have no contemporaneous " +
        "plan or initial-risk R and are excluded from the sizing ladder. Return percent is net/executed entry notional, not return on margin. " +
        "Unassigned imported fills are not Plays. Retained venue history can be incomplete; All is not lifetime history. " +
        "Results are nominal USD and include reported execution fees once. " + NominalStablecoins.Caveat +
        " Venues with different quote stablecoins are pooled nominally. Separate funding cashflows are not added; " +
        "whether RISEx reported realized P&L includes funding remains unverified. Balanced retained fills are not proof of full venue history. " +
        "Accounts use current portfolio membership; disabled accounts are excluded. Open-play counts are current, not a historical cohort. " +
        "Unresolved Plays, declared by the owner because their executions cannot settle them, have no result and are excluded. " +
        "Cancelled Plays had no fill, so they have no result and are listed with the exclusions.";

    public static DateTimeOffset? From(string period, DateTimeOffset now) => period switch
    {
        "day" => now.AddDays(-1), "week" => now.AddDays(-7), "month" => now.AddDays(-30),
        "year" => now.AddDays(-365), "all" => null,
        _ => throw new ProblemException(400, "Choose day, week, month, year or all."),
    };

    /// <summary>
    /// The read's window: the rolling period back from now, or, with <see cref="ReviewQuery.Week"/>, the reporting week holding
    /// that local date under the owner's reporting zone and week start (Phase 2, item 2). Weeks after the current one are refused.
    /// </summary>
    private async Task<ReviewWindow> WindowAsync(ReviewQuery query, DateTimeOffset now, CancellationToken ct)
    {
        if (query.Week is null) return new(From(query.Period, now), now);
        // "current" is the week holding today in the reporting zone, which the browser may not know.
        var date = DateOnly.MinValue;
        if (query.Week != CurrentWeek &&
            (!DateOnly.TryParseExact(query.Week, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) || date.Year < 2000))
            throw new ProblemException(400, "Choose a week by one of its dates, yyyy-MM-dd, from 2000 on, or current.");
        var settings = await store.ReportingAsync(ct);
        var zone = settings?.Zone() ?? TimeZoneInfo.Utc;
        var weekStart = settings?.WeekStart ?? ReportingSettings.Monday;
        var today = ReportingCalendar.Day(now, zone);
        // No later date can be in the current week, and the calendar's end would overflow (Sol round 1 on item 2).
        if (query.Week != CurrentWeek && date > today.AddDays(6)) throw new ProblemException(400, "Choose this week or an earlier one.");
        var week = ReportingCalendar.Week(query.Week == CurrentWeek ? today : date, zone, weekStart);
        if (week.FromUtc > now) throw new ProblemException(400, "Choose this week or an earlier one.");
        return new(week.FromUtc, week.ToUtc, week, zone, weekStart);
    }

    /// <summary>The filters every Review read validates the same way, with the portfolio ID normalized.</summary>
    private static ReviewQuery Validated(ReviewQuery query)
    {
        query = query with { TradingMode = TradingMode.Validate(query.TradingMode ?? TradingMode.Real) };
        From(query.Period, DateTimeOffset.UnixEpoch);
        if (query.Portfolio is not ("all" or "unassigned") &&
            (!Guid.TryParse(query.Portfolio, out var portfolio) || portfolio == Guid.Empty))
            throw new ProblemException(400, "Choose a valid portfolio.");
        if (Guid.TryParse(query.Portfolio, out var normalized)) query = query with { Portfolio = normalized.ToString() };
        if (query.AccountId == Guid.Empty || query.Offset is < 0 or > 1_000_000 ||
            query.Instrument is { } instrument && (string.IsNullOrWhiteSpace(instrument) || instrument.Length > 128 || instrument != instrument.Trim()))
            throw new ProblemException(400, "Invalid review filters.");
        if (!PositionModes.Contains(query.Positions) || query.MinPlays is < 0 or > ReviewQuery.MaxMinPlays)
            throw new ProblemException(400, "Choose exclude, include or auto positions and a Play threshold from 0 to 1000.");
        // Imported positions are actual Activity facts, never a source or fallback for paper Review.
        if (query.TradingMode == TradingMode.Paper) query = query with { Positions = "exclude" };
        return query;
    }

    public async Task<ReviewDto> GetAsync(ReviewQuery query, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        query = Validated(query);
        var window = await WindowAsync(query, now, ct);
        var from = window.From;

        var selection = await SelectAsync(query, now, window, ct);
        var (facts, accounts, scoped, evaluated, candidates, accepted, current) =
            (selection.Facts, selection.Accounts, selection.Scoped, selection.Evaluated, selection.Candidates, selection.Accepted, selection.Current);
        var (read, positions, playCount, include) = (selection.Read, selection.Positions, selection.PlayCount, selection.Include);
        var unresolved = Unresolved(facts, scoped, query.Instrument, query.TradingMode!).Count(u => window.Contains(u.EndedAt));
        var cancelled = Cancelled(facts, scoped, query.Instrument, query.TradingMode!).Count(c => window.Contains(CancelledAt(c.Play)));
        var portfolios = facts.Portfolios.Where(p => p.TradingMode == query.TradingMode).ToList();
        var portfolioNames = portfolios.ToDictionary(p => p.Id.ToString(), p => p.Name);
        var options = new ReviewOptionsDto(
            portfolios.OrderBy(p => p.Name).Select(p => new ReviewPortfolioDto(p.Id.ToString(), p.Name)).ToList(),
            accounts.Values.OrderBy(a => a.Name).Select(a => new ReviewAccountDto(a.Id, a.Name, a.PortfolioId, a.VenueId,
                a.LastSyncedAtUtc, a.SyncStatus, a.HistoryNotice)).ToList(),
            facts.ClosedPlays.Select(f => f.Play).Concat(facts.UnresolvedPlays.Select(u => u.Play)).Concat(facts.CancelledPlays.Select(c => c.Play))
                .Where(p => p.TradingMode == query.TradingMode && accounts.ContainsKey(p.AccountId))
                .Select(p => p.ContractId).Concat(read.Where(p => p.Outcome is not null).Select(p => p.ContractId))
                .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
        var series = Series(current, window, now);
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
                string.Join(", ", items.Select(e => e.VenueId).Distinct().Order())))
        {
            // Fixed order rather than by result (1.4b). Imported positions have no plan and form their own origin.
            Origins = Fixed(current, e => e.Facts is { } play ? OriginName(play.Play) : "position",
                [("planned", "Planned"), ("retrospective", "Retrospective"), ("position", "Imported position")]),
            Directions = Fixed(current, e => e.Facts?.Play.Direction ?? e.Position!.Direction ?? "", [("long", "Long"), ("short", "Short")]),
        };
        var page = current.OrderByDescending(e => e.ClosedAt).ThenBy(e => e.Facts?.Play.Id ?? Guid.Empty)
            .ThenBy(e => e.Position?.OperationId, StringComparer.Ordinal).Skip(query.Offset).Take(PageSize)
            .Select(e => e.Facts is { } play
                ? new ReviewPlayDto(play.Play.Id, play.Play.Title, play.Play.AccountId,
                    accounts[play.Play.AccountId].Name, play.Play.VenueId, play.Play.ContractId!, e.ClosedAt,
                    ExactDecimal.Format(e.Result!.NetResult), ExactDecimal.Format(e.Result.ReturnPercent), ExactDecimal.Format(e.Result.RMultiple),
                    play.Play.ReviewCompletion is not null, play.Play.Origin == PlayOrigin.Retrospective ? "retrospective" : "planned",
                    play.Paper is not null ? "paper" : play.ManualSource ? "manual" : "imported")
                {
                    Local = e.Local!.ToDto(e.Result.NetResult), PlannedAfterExecution = PlayReceipts.PlannedAfterExecution(play),
                    Adherence = play.Play.ReviewAdherence, MistakeTag = play.Play.ReviewMistakeTag, Direction = play.Play.Direction,
                    ReviewCompletion = PlayService.CompletionName(play.Play.ReviewCompletion),
                }
                : new ReviewPlayDto(null, $"{e.ContractId} {(e.Position!.Direction == "short" ? "Short" : "Long")}", e.AccountId,
                    accounts[e.AccountId].Name, e.VenueId, e.ContractId, e.ClosedAt, ExactDecimal.Format(e.Result!.NetResult),
                    ExactDecimal.Format(e.Result.ReturnPercent), null, false, "position", "imported")
                {
                    OperationId = e.Position.OperationId, Direction = e.Position.Direction ?? "",
                })
            .ToList();
        var cases = ProcessCases(evaluated, facts, scoped, query.Instrument, query.TradingMode!, window);
        var omitted = positions.Count(e => e.Result is null && window.Contains(e.ClosedAt));
        return new ReviewDto(now, from ?? current.FirstOrDefault()?.ClosedAt, query.Period, "USD",
            options, Periods.Select(period => {
                var selected = accepted.Where(e => new ReviewWindow(From(period, now), now).Contains(e.ClosedAt)).ToList();
                return new ReviewPeriodDto(period, selected.Count,
                    selected.Count == 0 ? null : ExactDecimal.Format(Sum(selected.Select(e => e.Result!.NetResult))))
                {
                    Positions = selected.Count(e => e.Position is not null)
                };
            }).ToList(),
            Metrics(current), series.Points, series.BucketDays, groups,
            new ReviewCoverageDto(candidates.Count, candidates.Count(e => e.Exclusion == "fees"),
                candidates.Count(e => e.Exclusion == "executions"), facts.OpenPlays, facts.UnassignedFills, facts.FirstFillAtUtc,
                query.TradingMode == TradingMode.Paper ? PaperNotice : include ? Notice + PositionNotice : Notice)
            { Unresolved = unresolved, Cancelled = cancelled },
            page, query.Offset, query.Offset + page.Count < current.Count ? query.Offset + page.Count : null, query.TradingMode!)
        {
            Preflight = Preflight(cases),
            Process = Process(cases),
            Rule = Rule(cases),
            Positions = new ReviewPositionsDto(query.Positions, include, query.MinPlays, playCount,
                include ? current.Count(e => e.Position is not null) : null,
                include ? omitted : null, PlayWeight.ToString(CultureInfo.InvariantCulture)),
            Week = window.Week is { } week ? new ReviewWeekDto(week.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), week.FromUtc,
                week.ToUtc, window.Zone!.Id, window.WeekStart!,
                current.Count > 0 || cases.Count > 0 || candidates.Count > 0 || include && omitted > 0 ? "some"
                    : Earliest(facts, scoped, query) is { } earliest && earliest < week.ToUtc ? "empty" : "none") : null,
        };
    }

    /// <summary>
    /// The estimated R (planned-stop model) of real Plays closed in each half-open range, pooled across the owner's enabled real
    /// accounts, from one snapshot (Phase 2, item 10b). A closed Play without R, unscorable or without a complete initial stop,
    /// is counted apart and never as zero. Positions have no R and are left out.
    /// </summary>
    public async Task<IReadOnlyList<ClosedRSum>> ClosedRAsync(IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> ranges, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var query = Validated(new ReviewQuery(Period: "all", Positions: "exclude", TradingMode: TradingMode.Real));
        var selection = await SelectAsync(query, now, new ReviewWindow(null, now), ct);
        return ranges.Select(range =>
        {
            var window = new ReviewWindow(range.From, range.To) { HalfOpen = true };
            var closed = selection.Evaluated.Where(e => window.Contains(e.ClosedAt)).ToList();
            var known = closed.Where(e => e.Result?.RMultiple is not null).ToList();
            return new ClosedRSum(Sum(known.Select(e => e.Result!.RMultiple!.Value)), known.Count, closed.Count - known.Count);
        }).ToList();
    }

    /// <summary>
    /// The earliest fact Vessel holds in scope: the first retained execution or the first Play created in any status, so a
    /// week before it reads as without data rather than without trades (Phase 2, item 2; active Plays since Sol round 1).
    /// </summary>
    private static DateTimeOffset? Earliest(ReviewFacts facts, HashSet<Guid> scoped, ReviewQuery query) =>
        facts.ClosedPlays.Select(f => f.Play).Concat(facts.UnresolvedPlays.Select(u => u.Play)).Concat(facts.CancelledPlays.Select(c => c.Play))
            .Where(p => p.TradingMode == query.TradingMode && scoped.Contains(p.AccountId) && (query.Instrument is null || p.ContractId == query.Instrument))
            .Select(p => (DateTimeOffset?)p.CreatedAtUtc).Append(facts.FirstFillAtUtc).Append(facts.FirstPlayAtUtc).Min();

    /// <summary>
    /// The report's selection from one snapshot: the scoped enabled accounts, every evaluated closed Play, imported
    /// positions when they are included, and the period's scored rows. The report and its CSV export share it.
    /// </summary>
    private sealed record Selection(ReviewFacts Facts, Dictionary<Guid, Account> Accounts, HashSet<Guid> Scoped, List<Evaluated> Evaluated,
        List<Evaluated> Candidates, List<Evaluated> Accepted, List<Evaluated> Current, List<ReviewPosition> Read, List<Evaluated> Positions,
        int PlayCount, bool Include);

    private async Task<Selection> SelectAsync(ReviewQuery query, DateTimeOffset now, ReviewWindow window, CancellationToken ct)
    {
        ReviewFacts facts;
        Dictionary<Guid, Account> accounts;
        List<Evaluated> accepted, candidates;
        List<ReviewPosition> read;
        int playCount;
        bool include;
        HashSet<Guid> scoped;
        List<Evaluated> evaluated;
        for (var attempt = 1; ; attempt++)
        {
            facts = await store.ReadAsync(query, now, window, ct);
            accounts = facts.Accounts.Where(a => a.IsEnabled && a.TradingMode == query.TradingMode).ToDictionary(a => a.Id);
            scoped = accounts.Values.Where(a =>
                (query.AccountId is null || a.Id == query.AccountId) &&
                (query.Portfolio == "all" || query.Portfolio == "unassigned" && a.PortfolioId is null ||
                    a.PortfolioId?.ToString() == query.Portfolio)).Select(a => a.Id).ToHashSet();
            evaluated = facts.ClosedPlays.Where(f => f.Play.TradingMode == query.TradingMode && scoped.Contains(f.Play.AccountId) &&
                (query.Instrument is null || f.Play.ContractId == query.Instrument))
                .Select(f => Evaluate(f, now, facts.IncompleteOrderPlayIds?.Contains(f.Play.Id) == true || f.IncompleteOrders is { Count: > 0 })).ToList();
            accepted = evaluated.Where(e => e.Result is not null).ToList();
            candidates = evaluated.Where(e => window.Contains(e.ClosedAt)).ToList();
            // Auto counts the scored Plays of the selected scope and period, and the decision applies to every period total.
            playCount = accepted.Count(e => window.Contains(e.ClosedAt));
            include = positionSource is not null &&
                (query.Positions == "include" || query.Positions == "auto" && playCount < query.MinPlays);
            read = [];
            if (!include) break;
            read = (await positionSource!.ReadAsync(scoped, ct)).Where(p => accounts.ContainsKey(p.AccountId)).ToList();
            // Plays and position claims come from separate reads. A Play created or linked in between (a retrospective
            // conversion) would hide its result or count it twice, so the Play snapshot must be unchanged around them.
            if (Claims(facts) == Claims(await store.ReadAsync(query, now, window, ct))) break;
            if (attempt == 3) throw new ProblemException(409, "Plays changed while Review read imported positions. Reload Review.");
        }
        var positions = read.Where(p => query.Instrument is null || p.ContractId == query.Instrument)
            .Select(p => new Evaluated(null, p.ClosedAtUtc, p.Outcome, p.Outcome is null ? "position" : null)
            {
                Position = p, PositionVenue = accounts[p.AccountId].VenueId
            }).ToList();
        accepted = accepted.Concat(positions.Where(p => p.Result is not null))
            .OrderBy(e => e.ClosedAt).ThenBy(e => e.Facts?.Play.Id ?? Guid.Empty).ThenBy(e => e.Position?.OperationId, StringComparer.Ordinal).ToList();
        var current = accepted.Where(e => window.Contains(e.ClosedAt)).ToList();
        return new(facts, accounts, scoped, evaluated, candidates, accepted, current, read, positions, playCount, include);
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
        bool PostHoc, string? Lane, string? Cell, PlanRuleSnapshotDto? Rule = null)
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
        var first = revisions.SingleOrDefault(r => r.Number == 1);
        return new(play, at, result, PlayPreflight.Read(first?.Preflight), postHoc, lane, cell, PlayRules.Read(first?.Rule));
    }

    /// <summary>The process population: the period's Closed, Cancelled and Unresolved Plays in scope, each by its own time.</summary>
    private static List<ProcessCase> ProcessCases(IEnumerable<Evaluated> closed, ReviewFacts facts, HashSet<Guid> scope, string? instrument,
        string mode, ReviewWindow window) =>
        closed.Select(e => Case(e.Facts!.Play, e.ClosedAt, e.Result, e.Facts.Revisions, PlayReceipts.PlannedAfterExecution(e.Facts)))
            .Concat(Cancelled(facts, scope, instrument, mode).Select(c => Case(c.Play, CancelledAt(c.Play), null, c.Revisions, false)))
            .Concat(Unresolved(facts, scope, instrument, mode).Select(u => Case(u.Play, u.EndedAt, null, u.Revisions,
                u.Play is { Origin: PlayOrigin.Planned, PlannedAtUtc: { } planned } && u.FirstExecutionAtUtc is { } first && planned > first)))
            .Where(c => window.Contains(c.At)).ToList();

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

    /// <summary>The if-then rule's answers (Phase 2, item 3), over the cases planned under a rule; each count equals its drill-down.</summary>
    private static ReviewRuleDto Rule(IReadOnlyList<ProcessCase> cases)
    {
        var carried = cases.Where(c => c.Rule is not null).ToList();
        ReviewRuleRowDto Row(string? answer)
        {
            var rows = carried.Where(c => c.Play.ReviewRule == answer).ToList();
            return new(rows.Count(c => c.Outcome == "win"), rows.Count(c => c.Outcome == "loss"), rows.Count(c => c.Outcome == "scratch"),
                rows.Count(c => c.Outcome == "unscored"));
        }
        return new(carried.Count, carried.Count(c => c.Rule!.Answer == PlayRules.Acknowledged), carried.Count(c => c.Rule!.Answer == PlayRules.Exception),
            carried.Count(c => c.Rule!.Answer is null), Row("followed"), Row("deviated"), Row("not-applicable"), Row(null));
    }

    /// <summary>The rule selectors: a review answer, unanswered, or the answer at Plan it.</summary>
    public static readonly string[] RuleSelectors = ["followed", "deviated", "not-applicable", "unanswered", "acknowledged", "exception", "not-acknowledged"];

    private static bool RuleMatches(ProcessCase c, string selector) => c.Rule is { } rule && selector switch
    {
        "unanswered" => c.Play.ReviewRule is null,
        "acknowledged" => rule.Answer == PlayRules.Acknowledged,
        "exception" => rule.Answer == PlayRules.Exception,
        "not-acknowledged" => rule.Answer is null,
        _ => c.Play.ReviewRule == selector,
    };

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
        var window = await WindowAsync(query, now, ct);
        var selectors = new[] { request.Cell, request.Lane, request.MistakeTag, request.SkippedCheck, request.Rule }.Count(s => s is not null);
        if (selectors != 1) throw new ProblemException(400, "Choose one cell, lane, mistake tag, skipped check or rule answer.");
        if (request.Cell is { } cell && !ProcessCells.Contains(cell) || request.Lane is { } lane && !ProcessLanes.Contains(lane) ||
            request.MistakeTag is { } tag && (string.IsNullOrWhiteSpace(tag) || tag.Length > 100) ||
            request.SkippedCheck is { } check && check != "not-recorded" && !PlayPreflight.Codes.Contains(check) ||
            request.Rule is { } ruleSelector && !RuleSelectors.Contains(ruleSelector))
            throw new ProblemException(400, "Invalid process selection.");
        var facts = await store.ReadAsync(query, now, window, ct);
        var accounts = facts.Accounts.Where(a => a.IsEnabled && a.TradingMode == query.TradingMode).ToDictionary(a => a.Id);
        var scoped = accounts.Values.Where(a => (query.AccountId is null || a.Id == query.AccountId) &&
            (query.Portfolio == "all" || query.Portfolio == "unassigned" && a.PortfolioId is null ||
                a.PortfolioId?.ToString() == query.Portfolio)).Select(a => a.Id).ToHashSet();
        var evaluated = facts.ClosedPlays.Where(f => f.Play.TradingMode == query.TradingMode && scoped.Contains(f.Play.AccountId) &&
                (query.Instrument is null || f.Play.ContractId == query.Instrument))
            .Select(f => Evaluate(f, now, facts.IncompleteOrderPlayIds?.Contains(f.Play.Id) == true || f.IncompleteOrders is { Count: > 0 }));
        var cases = ProcessCases(evaluated, facts, scoped, query.Instrument, query.TradingMode!, window);
        var selected = (request.Cell is { } c1 ? cases.Where(c => c.Cell == c1)
            : request.Lane is { } l1 ? cases.Where(c => c.Lane == l1)
            : request.MistakeTag is { } t1 ? Tags(cases).Where(t => t.Key == TagKey(t1)).SelectMany(t => t.Cases)
            : request.Rule is { } r1 ? cases.Where(c => RuleMatches(c, r1))
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
        query = Validated(query);
        var window = await WindowAsync(query, now, ct);
        var facts = await store.ReadAsync(query, now, window, ct);
        var accounts = facts.Accounts.Where(a => a.IsEnabled && a.TradingMode == query.TradingMode && (query.AccountId is null || a.Id == query.AccountId) &&
            (query.Portfolio == "all" || query.Portfolio == "unassigned" && a.PortfolioId is null ||
                a.PortfolioId?.ToString() == query.Portfolio)).ToDictionary(a => a.Id);
        var all = Excluded(facts, accounts, query, window, now);
        var items = all.Skip(query.Offset).Take(PageSize).Select(x => x.Item()).ToList();
        return new(items, all.Count, query.Offset, query.Offset + items.Count < all.Count ? query.Offset + items.Count : null,
            query.TradingMode == TradingMode.Paper ? PaperNotice : Notice);
    }

    /// <summary>
    /// The period's excluded Plays in the scoped <paramref name="accounts"/>, newest first: closed Plays Review cannot score,
    /// Unresolved ones by their declaration and Cancelled ones by their cancellation.
    /// </summary>
    private List<(DateTimeOffset At, Play Play, string Status, Func<ReviewExclusionDto> Item)> Excluded(ReviewFacts facts,
        Dictionary<Guid, Account> accounts, ReviewQuery query, ReviewWindow window, DateTimeOffset now)
    {
        var excluded = facts.ClosedPlays.Where(f => f.Play.TradingMode == query.TradingMode && accounts.ContainsKey(f.Play.AccountId) &&
            (query.Instrument is null || f.Play.ContractId == query.Instrument))
            .Select(f => Evaluate(f, now, facts.IncompleteOrderPlayIds?.Contains(f.Play.Id) == true || f.IncompleteOrders is { Count: > 0 }))
            .Where(e => e.Exclusion is not null && window.Contains(e.ClosedAt))
            .OrderByDescending(e => e.ClosedAt).ThenBy(e => e.Facts!.Play.Id).ToList();
        // Unresolved Plays join the drill-down by their declaration time, with its reason, note and execution facts; Cancelled
        // Plays by their cancellation time, so an unscored Play without answers is never missing from every list (1.4b).
        return excluded.Select(e => (At: e.ClosedAt, e.Facts!.Play, Status: "closed", Item: (Func<ReviewExclusionDto>)(() => new ReviewExclusionDto(e.Facts.Play.Id,
                e.Facts.Play.Title, e.Facts.Play.AccountId, accounts[e.Facts.Play.AccountId].Name,
                e.Facts.Play.ContractId ?? "", ExclusionReasons(e.Facts, now, e.Exclusion!)))))
            .Concat(Unresolved(facts, accounts.Keys.ToHashSet(), query.Instrument, query.TradingMode!).Where(u => window.Contains(u.EndedAt))
                .Select(u => (At: u.EndedAt, u.Play, Status: "unresolved", Item: (Func<ReviewExclusionDto>)(() => new ReviewExclusionDto(u.Play.Id, u.Play.Title,
                    u.Play.AccountId, accounts[u.Play.AccountId].Name, u.Play.ContractId ?? "", [UnresolvedReason(u.Declaration)])
                    { Unresolved = u.Declaration }))))
            .Concat(Cancelled(facts, accounts.Keys.ToHashSet(), query.Instrument, query.TradingMode!).Where(c => window.Contains(CancelledAt(c.Play)))
                .Select(c => (At: CancelledAt(c.Play), c.Play, Status: "cancelled", Item: (Func<ReviewExclusionDto>)(() => new ReviewExclusionDto(c.Play.Id,
                    c.Play.Title, c.Play.AccountId, accounts[c.Play.AccountId].Name, c.Play.ContractId ?? "", [CancelledReason(c.Play)])))))
            .OrderByDescending(x => x.At).ThenBy(x => x.Play.Id).ToList();
    }

    public static readonly IReadOnlyList<string> ExportColumns = ["kind", "play_id", "operation_id", "title", "account", "venue", "instrument",
        "direction", "origin", "execution_source", "status", "at_utc", "net_usd", "return_percent", "play_local_net_usd", "play_local_basis",
        "play_local_reason", "sign_differs", "fees", "estimated_r", "estimated_r_reason", "plan_written_after_first_execution", "adherence",
        "mistake_tag", "review_completion", "exclusion_reasons"];

    public const string ExportNotice = "Not a tax report. Review's own rows in the selection: scored Plays and imported positions, then exclusions. " +
        "Amounts are exact decimal strings in nominal USD; an empty cell is unknown, never zero. Net is the scored venue result; the Play-local " +
        "result is shown beside it and never scored. Separate funding is not added.";

    /// <summary>
    /// Review's selection as CSV (Phase 2, item 5a): the scored rows of the report, newest first, then its exclusions, from one
    /// snapshot. Fees are by token; estimated R carries its reason. Text cells are formula-neutralized.
    /// </summary>
    public async Task<ReviewExportDto> ExportAsync(ReviewQuery query, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        query = Validated(query) with { Offset = 0 };
        var window = await WindowAsync(query, now, ct);
        var selection = await SelectAsync(query, now, window, ct);
        // A reporting week names its first day, zone and week start; a rolling period its name.
        var span = window.Week is { } week
            ? $"Week of {week.Start:yyyy-MM-dd} ({window.Zone!.Id}, weeks start {window.WeekStart})"
            : $"Period {query.Period}";
        var accounts = selection.Accounts;
        var scoped = accounts.Where(a => selection.Scoped.Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value);
        var csv = new Csv(ExportColumns);
        void Row(string kind, Guid? playId, string? operationId, string title, Guid accountId, string venue, string instrument, string? direction,
            string origin, string source, string status, DateTimeOffset at, string? net, string? returnPercent, PlayLocalResultDto? local,
            string? fees, string? r, string? rReason, bool? postHoc, Play? play, string? reasons)
        {
            csv.Text(kind).Text(playId?.ToString()).Text(operationId).Text(title).Text(accounts[accountId].Name).Text(venue).Text(instrument)
                .Text(direction).Text(origin).Text(source).Text(status).Time(at).Number(net).Number(returnPercent).Number(local?.NetUsd)
                .Text(local?.Basis).Text(local?.Reason).Flag(local is null ? null : local.SignDiffers).Text(fees).Number(r).Text(rReason)
                .Flag(postHoc).Text(play?.ReviewAdherence).Text(play?.ReviewMistakeTag).Text(PlayService.CompletionName(play?.ReviewCompletion))
                .Text(reasons).End();
        }
        csv.Text("notice");
        for (var i = 1; i < ExportColumns.Count; i++) csv.Text(i == 3 ? $"{ExportNotice} {span}, {query.TradingMode} mode, as of " +
            now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "." : null);
        csv.End();
        foreach (var e in selection.Current.OrderByDescending(e => e.ClosedAt).ThenBy(e => e.Facts?.Play.Id ?? Guid.Empty)
                     .ThenBy(e => e.Position?.OperationId, StringComparer.Ordinal))
        {
            if (e.Facts is { } facts)
            {
                var play = facts.Play;
                var paper = facts.Paper is not null;
                // Paper executions are simulated without fees (the paper contract): a known zero, never unknown.
                var fees = paper ? "0 (simulated, no fees)" : string.Join("; ", facts.Fills.GroupBy(f => f.Fill.FeeToken, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => $"{g.Key}={g.Aggregate(default(ExactAmount), (sum, f) => sum + ExactAmount.From(f.Fill.Fee))}"));
                Row("scored", play.Id, null, play.Title, play.AccountId, play.VenueId, play.ContractId!, play.Direction, OriginName(play),
                    paper ? "paper" : facts.ManualSource ? "manual" : "imported", "closed", e.ClosedAt, ExactDecimal.Format(e.Result!.NetResult),
                    ExactDecimal.Format(e.Result.ReturnPercent), e.Local!.ToDto(e.Result.NetResult), fees, ExactDecimal.Format(e.Result.RMultiple),
                    e.Result.RMultiple is null && !paper ? PlayReceipts.Build(facts, now).EstimatedRReason : null,
                    PlayReceipts.PlannedAfterExecution(facts), play, null);
            }
            else
                Row("position", null, e.Position!.OperationId, $"{e.ContractId} {(e.Position.Direction == "short" ? "Short" : "Long")}", e.AccountId,
                    e.VenueId, e.ContractId, e.Position.Direction, "position", "imported", "closed", e.ClosedAt,
                    ExactDecimal.Format(e.Result!.NetResult), ExactDecimal.Format(e.Result.ReturnPercent), null, e.Position.Fees, null, null, null, null, null);
        }
        foreach (var x in Excluded(selection.Facts, scoped, query, window, now))
        {
            var item = x.Item();
            Row("excluded", x.Play.Id, null, x.Play.Title, x.Play.AccountId, x.Play.VenueId, x.Play.ContractId ?? "", x.Play.Direction,
                OriginName(x.Play), x.Play.TradingMode == TradingMode.Paper ? "paper" : "", x.Status, x.At, null, null, null, null, null, null,
                null, x.Play, string.Join("; ", item.Reasons));
        }
        // Sol round 1 on #38: closed positions Review cannot score are listed too, by their close time, with no amounts.
        foreach (var e in selection.Positions.Where(p => p.Result is null && window.Contains(p.ClosedAt))
                     .OrderByDescending(p => p.ClosedAt).ThenBy(p => p.Position!.OperationId, StringComparer.Ordinal))
            Row("excluded", null, e.Position!.OperationId, e.Position.Direction is null ? $"{e.ContractId} position"
                    : $"{e.ContractId} {(e.Position.Direction == "short" ? "Short" : "Long")}", e.AccountId,
                e.VenueId, e.ContractId, e.Position.Direction, "position", "imported", "closed", e.ClosedAt, null, null, null, null, null, null,
                null, null, e.Position.Omission ?? "Review cannot score this closed position.");
        var mode = query.TradingMode == TradingMode.Paper ? "paper-" : "";
        return new(window.Week is { } named ? $"vessel-review-{mode}week-{named.Start:yyyyMMdd}.csv"
            : $"vessel-review-{mode}{query.Period}-{now.UtcDateTime:yyyyMMdd}.csv", csv.ToString());
    }

    /// <summary>Unresolved Plays in the scope's mode, accounts and instrument; they are excluded with the reason "unresolved".</summary>
    private static IEnumerable<UnresolvedPlayFacts> Unresolved(ReviewFacts facts, HashSet<Guid> accounts, string? instrument, string mode) =>
        facts.UnresolvedPlays.Where(u => u.Play.TradingMode == mode && accounts.Contains(u.Play.AccountId) &&
            (instrument is null || u.Play.ContractId == instrument));

    /// <summary>Cancelled Plays in the scope's mode, accounts and instrument; they have no fills, so Review never scores them.</summary>
    private static IEnumerable<EndedPlayFacts> Cancelled(ReviewFacts facts, HashSet<Guid> accounts, string? instrument, string mode) =>
        facts.CancelledPlays.Where(c => c.Play.TradingMode == mode && accounts.Contains(c.Play.AccountId) &&
            (instrument is null || c.Play.ContractId == instrument));

    private static DateTimeOffset CancelledAt(Play play) => play.EndedAtUtc ?? play.UpdatedAtUtc;

    private static string CancelledReason(Play play) => play.CancelReason switch
    {
        CancelReason.Invalidated => "Cancelled by the owner as invalidated, before any fill: it has no result.",
        CancelReason.Missed => "Cancelled by the owner as missed, before any fill: it has no result.",
        CancelReason.ChangedMind => "Cancelled by the owner after a change of mind, before any fill: it has no result.",
        CancelReason.Expired => "Cancelled by the owner as expired, before any fill: it has no result.",
        CancelReason.Mistake => "Cancelled by the owner as a mistake, before any fill: it has no result.",
        _ => "Cancelled by the owner before any fill: it has no result.",
    };

    private static string UnresolvedReason(UnresolvedDeclarationDto? declaration) =>
        declaration is null || PlayUnresolved.ParseReason(declaration.Reason) is not { } reason
            ? "Declared unresolved by the owner: its executions cannot settle it, so it has no result."
            : $"Declared unresolved by the owner because of {PlayUnresolved.ReasonLabel(reason)}: its executions cannot settle it, so it has no result.";

    private static IReadOnlyList<string> ExclusionReasons(ClosedPlayFacts facts, DateTimeOffset now, string exclusion)
    {
        if (facts.Paper is { } paper)
        {
            paper.Outcome(now, out var problem);
            return DiaryService.BoundedReasons(paper.Issues.Concat(problem is null ? [] : new[] { problem }));
        }
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
            Local = score.Exclusion is not null ? null : facts.Play.TradingMode == TradingMode.Paper
                ? PaperPlayFacts.Local(score.Result!)
                : PlayLocalResult.From(facts, venues?.Descriptor(facts.Play.VenueId)?.QuoteAsset),
        };
    }

    /// <summary>
    /// Review's verdict on one Play, shared with the Play's own receipt: its scored outcome, whose R is known only with a
    /// complete initial stop, or why it is excluded (<c>executions</c> or <c>fees</c>). <paramref name="incompleteOrders"/>
    /// says a linked order is known to miss executions. A paper Play is scored from its simulated executions only.
    /// </summary>
    public static PlayScore Score(ClosedPlayFacts facts, DateTimeOffset now, bool incompleteOrders)
    {
        if (facts.Play.TradingMode == TradingMode.Paper)
        {
            var paper = facts.Paper;
            var outcome = paper?.Outcome(now, out _);
            if (outcome is not null && !HasCompleteInitialRisk(facts, default, ""))
                outcome = outcome with { PlannedRisk = null };
            return new PlayScore(paper?.ClosedAt ?? facts.Play.EndedAtUtc ?? facts.Play.UpdatedAtUtc, outcome,
                outcome is null ? "executions" : null);
        }
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
        if (facts.Play.Origin == PlayOrigin.Retrospective || facts.Paper is null && revision is null) return false;
        var plan = facts.Paper?.InitialPlan ?? (revision is null ? null : facts.ParsedPlan(revision.Plan));
        if (plan is null || facts.Paper is null && (plan.Direction == "long" ? ExecutionFacts.Buy : ExecutionFacts.Sell) != side) return false;
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
            played.Count(p => p.Facts!.Play.ReviewCompletion is not null), plays.Count == 0 ? null : ExactDecimal.Format(drawdown),
            maxWins, maxLosses)
        {
            SignDiffers = played.Count(p => p.Local!.SignDiffers(p.Result!.NetResult)),
            LocalUnknown = played.Count(p => p.Local!.Net is null),
            FeeOnlyLosses = outcomes.Count(o => o.Outcome.FeeOnlyLoss),
            UnknownGrossLosses = losses.Count(o => o.Outcome.GrossResult is null),
            Positions = plays.Count - played.Count,
        };
    }

    private static (List<ReviewPointDto> Points, int BucketDays) Series(List<Evaluated> plays, ReviewWindow window, DateTimeOffset now)
    {
        if (plays.Count == 0) return ([], 1);
        if (window.Week is { } week) return (WeekSeries(plays, week, window.Zone!, now), 1);
        var from = window.From;
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

    /// <summary>
    /// A reporting week's daily points, by local day in the reporting zone, up to today for the current week. Each point is
    /// dated by its local day (midnight UTC of that date, as the rolling series dates its UTC days).
    /// </summary>
    private static List<ReviewPointDto> WeekSeries(List<Evaluated> plays, ReportingWeek week, TimeZoneInfo zone, DateTimeOffset now)
    {
        var today = ReportingCalendar.Day(now, zone);
        var last = today < week.Start.AddDays(6) ? today : week.Start.AddDays(6);
        var byDay = plays.ToLookup(p => ReportingCalendar.Day(p.ClosedAt, zone));
        List<ReviewPointDto> points = [];
        ExactAmount cumulative = default, peak = default;
        for (var day = week.Start; day <= last; day = day.AddDays(1))
        {
            var items = byDay[day].ToList();
            var net = Sum(items.Select(p => p.Result!.NetResult));
            cumulative += net;
            peak = Max(peak, cumulative);
            points.Add(new(new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), ExactDecimal.Format(net),
                ExactDecimal.Format(cumulative), ExactDecimal.Format(cumulative - peak), items.Count));
        }
        return points;
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
