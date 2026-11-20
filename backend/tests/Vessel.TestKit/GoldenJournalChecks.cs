using Vessel.Application.Activity;
using Vessel.Application.Review;
using Vessel.Domain.Common;

namespace Vessel.Tests;

/// <summary>
/// The golden journal's hand-computed figures (docs/architecture/golden-journal.md), checked through each screen. The
/// in-memory run and the database run call the same checks, so both meet the document line by line. Figures a known open
/// defect makes wrong are not here: their tests are skipped with the issue named.
/// </summary>
internal static class GoldenJournalChecks
{
    /// <summary>Document section 2: every operation on the enabled accounts, and nothing from the disabled one.</summary>
    public static async Task ActivityAsync(GoldenJournal j, GoldenJournalScreens s)
    {
        var page = await s.Activity.PageAsync(new(PageSize: "100"), default);
        Assert.Equal((13, 24), (page.ScopeCount, page.RetainedFillCount));
        var rows = page.Operations.ToDictionary(o => o.Id);
        void Operation(string first, string direction, string status, string entered, string exited, string open, string? entry,
            string? exit, string pnl, string fees, string? net, string? gross, string? play)
        {
            var o = rows[j.Operation(first, direction)];
            Assert.Equal((status, entered, exited, open, entry, exit, pnl, fees, net, gross),
                (o.Status, o.TotalEnteredQuantity, o.TotalExitedQuantity, o.OpenQuantity, o.AverageEntryPrice, o.AverageExitPrice,
                    o.ReportedClosedPnlUsd, o.StablecoinFeesNominalUsd, o.NetResultUsd, o.GrossClosedPnlUsd));
            Assert.Equal(play is null ? ("unassigned", (Guid?)null) : ("linked", j.Play[play].Id), (o.PlayAssociation, o.PlayId));
        }
        Operation("a1", "short", "closed", "2", "2", "0", "50", "52", "-4", "0.1", "-4.1", "-4", "P2");
        Operation("a3", "long", "closed", "1", "1", "0", "100", "110", "10", "0.2", "9.8", "10", "P1");
        Operation("a5", "long", "closed", "1", "1", "0", "100", "100.2", "0.2", "0.2", "0", "0.2", "P3");
        Operation("a7", "long", "closed", "2", "2", "0", "100", "103", "6", "0.4", "5.6", "6", "P4");
        Operation("a9", "long", "closed", "1", "1", "0", "60", "63", "3", "0", "3", "3", "P6");
        Operation("a11", "long", "closed", "100", "100", "0", "1", "1.02", "2", "0.1", null, "2", null);
        Operation("a13", "long", "closed", "1", "1", "0", "100", "100", "0", "0.2", "-0.2", "0", "P5");
        Operation("a15", "long", "open", "15", "5", "10", "106.6666666666666666666666666667", "110", "50", "0", "50", "50", null);
        Operation("a18", "short", "open", "2", "0", "2", "51", null, "0", "0.05", "-0.05", "0", "P8");
        Operation("b1", "long", "closed", "1", "1", "0", "200", "190", "-10.2", "0.2", "-10.2", "-10", "P7");
        Operation("b3", "long", "closed", "2", "2", "0", "10", "12", "3.94", "0.06", "3.94", "4", null);
        Operation("b4", "short", "closed", "3", "3", "0", "12", "11", "2.97", "0.03", "2.97", "3", null);
        Operation("b6", "short", "open", "4", "0", "4", "0.5", null, "-0.002", "0.002", "-0.002", "0", null);
        // The HYPE fee is listed apart and leaves X1's net unknown; the disabled account's win never appears.
        Assert.Equal([new OperationFeeDto("HYPE", "0.01")], rows[j.Operation("a11", "long")].OtherFees);
        Assert.DoesNotContain(j.Operation("c1", "long"), rows.Keys);
        // The flip closes the long and opens the short at the same instant; the short closes exactly on the 7-day boundary.
        Assert.Equal((GoldenJournal.D(8), GoldenJournal.D(8), GoldenJournal.D(7)), (rows[j.Operation("b3", "long")].ClosedAtUtc,
            rows[j.Operation("b4", "short")].OpenedAtUtc, rows[j.Operation("b4", "short")].ClosedAtUtc));

        // Unrealized P&L (section 2.3). P8 is marked at 52: −(2 × (52 − 51)). XRP has no mark, so the snapshot's own figure
        // counts. AVAX is marked at 130 against the remaining cost 110 (#90): 10 × (130 − 110).
        Assert.Equal(("200", "mark"), (rows[j.Operation("a15", "long")].UnrealizedPnlUsd, rows[j.Operation("a15", "long")].UnrealizedPnlSource));
        var eth = rows[j.Operation("a18", "short")];
        Assert.Equal(("-2", "mark", GoldenJournal.EthMark), (eth.UnrealizedPnlUsd, eth.UnrealizedPnlSource, eth.UnrealizedPnlMarkPrice));
        var xrp = rows[j.Operation("b6", "short")];
        Assert.Equal(("-0.2", "snapshot", GoldenJournal.Now), (xrp.UnrealizedPnlUsd, xrp.UnrealizedPnlSource, xrp.UnrealizedPnlAsOfUtc));
        Assert.All(page.Operations.Where(o => o.Status == "closed"), o => Assert.Null(o.UnrealizedPnlUsd));
    }

    /// <summary>Document section 3; its realized total is in <see cref="OverviewRealizedAsync"/>.</summary>
    public static async Task OverviewAsync(GoldenJournal j, GoldenJournalScreens s)
    {
        var all = await s.Overview.GetAsync("portfolio", "all", "all", TradingMode.Real, default);
        var t = all.Totals;
        Assert.Equal((2, "1500", "1000", 3, "197.8", 24),
            (t.AccountCount, t.Balance.AmountUsd, t.Available.AmountUsd, t.OpenPositionCount, t.UnrealizedPnl.AmountUsd, t.ImportedFillCount));
        Assert.Equal([$"portfolio:{j.Swing.Id}", "unassigned"], all.Rows.Select(r => r.Id));
        Assert.Equal([("1000", "198"), ("500", "-0.2")], all.Rows.Select(r => (r.Totals.Balance.AmountUsd, r.Totals.UnrealizedPnl.AmountUsd)));
        Assert.Equal(new Vessel.Application.Overview.BalanceShare("1000", "1500"), all.Rows[0].ShareOfBalance);
        // Unrealized is the venue's figure at the last refresh, never a live mark: both snapshots were taken at Now.
        Assert.Equal(GoldenJournal.Now, t.UnrealizedPnlAsOfUtc);
        // X1's net is unknown, so it is excluded and said so. The unknown is never zero.
        Assert.Contains(new Vessel.Application.Overview.SummaryReason("Closed operation net result or exit time unavailable.", 1),
            t.RealizedResult.Coverage.Reasons);
        // The Net account holds no operation with a known missing execution: −10.2 + 3.94 + 2.97.
        var net = all.Rows[1].Totals.RealizedResult;
        Assert.Equal(("-3.29", 1, 1, 3, 0), (net.AmountUsd, net.Coverage.ContributingAccountCount, net.Coverage.TotalAccountCount,
            net.Coverage.ContributingOperationCount, net.Coverage.ExcludedOperationCount));
        Assert.Empty(net.Coverage.Reasons);
    }

    /// <summary>
    /// Document section 3's realized figures and identity 7.2 (#93): Overview leaves out P6, whose entry order misses an
    /// execution, by the rule Review and Sizing apply, and says so.
    /// </summary>
    public static async Task OverviewRealizedAsync(GoldenJournal j, GoldenJournalScreens s)
    {
        var activity = (await s.Activity.PageAsync(new(PageSize: "100"), default)).Operations.Where(o => o.Status == "closed").ToList();
        var missing = activity.Where(o => o.PlayId == j.Play["P6"].Id).ToList();
        var overview = await s.Overview.GetAsync("portfolio", "all", "all", TradingMode.Real, default);
        var realized = overview.Totals.RealizedResult;
        Assert.Contains(new Vessel.Application.Overview.SummaryReason(Vessel.Application.Overview.OverviewSummaryService.MissingExecutionsReason, 1),
            realized.Coverage.Reasons);
        Assert.Equal(("7.81", 8, 2), (realized.AmountUsd, realized.Coverage.ContributingOperationCount, realized.Coverage.ExcludedOperationCount));
        Assert.Equal([("Swing", "11.1"), ("Unassigned", "-3.29")], overview.Rows.Select(r => (r.Name, r.Totals.RealizedResult.AmountUsd)));
        Assert.Equal(ExactSum(activity.Select(o => o.NetResultUsd)) - ExactSum(missing.Select(o => o.NetResultUsd)),
            ExactSum([realized.AmountUsd]));
        // With the same rule, Review with positions equals Overview realized: both leave out P6 and X1, and nothing else.
        var review = await s.Review.GetAsync(new(Period: "all", Positions: "include"), default);
        Assert.Equal(realized.AmountUsd, review.Metrics.Net);
        foreach (var scope in new[] { $"portfolio:{j.Swing.Id}", $"account:{j.Gross.Id}" })
        {
            var scoped = await s.Overview.GetAsync("portfolio", "all", scope, TradingMode.Real, default);
            Assert.Equal(ExactSum(activity.Where(o => o.AccountId == j.Gross.Id && o.PlayId != j.Play["P6"].Id)
                .Select(o => o.NetResultUsd)), ExactSum([scoped.Totals.RealizedResult.AmountUsd]));
        }
    }

    /// <summary>Document section 4.1: Plays only.</summary>
    public static async Task ReviewPlaysAsync(GoldenJournal j, GoldenJournalScreens s)
    {
        var report = await s.Review.GetAsync(new(Period: "all", Positions: "exclude"), default);
        var m = report.Metrics;
        Assert.Equal((6, 2, 3, 1, 0, "0.9"), (m.Count, m.Wins, m.Losses, m.Scratches, m.Positions, m.Net));
        Assert.Equal(("0.4", "6.3", "3.1333333333333333333333333333", "2.0106382978723404255319148936"),
            (m.Batting, m.AverageGain, m.AverageLoss, m.Payoff));
        Assert.Equal(("1.0620689655172413793103448276", "7.7", "4.8333333333333333333333333333", "0.15"),
            (m.ProfitFactor, m.AverageWin, m.AverageLossUsd, m.Expectancy));
        // Section 4.1.2: AverageR over the six Plays with a complete initial stop, the short P2 and the scratch P3 included.
        Assert.Equal(("0.1066666666666666666666666667", 6), (m.AverageR, m.RiskCount));
        // Section 4.1.3: L W S W L L. The scratch does not break the two wins; P5 is the loss made only of fees.
        Assert.Equal(("10.4", 2, 2, 1, 0), (m.Drawdown, m.MaxWins, m.MaxLosses, m.FeeOnlyLosses, m.UnknownGrossLosses));
        Assert.Equal((7, 1, 0, 1), (report.Coverage.ClosedCandidates, report.Coverage.IncompleteExecutions, report.Coverage.IncompleteFees,
            report.Coverage.OpenPlays));
        // Newest first; P5 and P7 share their exit time and keep the Play-ID order.
        Assert.Equal([("P5", "-0.2", "-0.2", "-0.04"), ("P7", "-10.2", "-5.1", "-1.02"), ("P4", "5.6", "2.8", "0.56"),
                ("P3", "0", "0", "0"), ("P1", "9.8", "9.8", "1.96"), ("P2", "-4.1", "-4.1", "-0.82")],
            report.Plays.Select(p => (Name(j, p.Id), p.Net, p.ReturnPercent, p.RMultiple)));
        Assert.Equal(GoldenJournal.D(15), report.Plays.Single(p => p.Id == j.Play["P2"].Id).ClosedAtUtc);
        Assert.Equal("short", report.Plays.Single(p => p.Id == j.Play["P2"].Id).Direction);
        Assert.Equal([("long", "5", 5, "0.292"), ("short", "-4.1", 1, "-0.82")],
            report.Groups.Directions.Select(g => (g.Id, g.Metrics.Net, g.Metrics.Count, g.Metrics.AverageR)));
        Assert.Equal([("Swing", "11.1"), ("Unassigned", "-10.2")], report.Groups.Portfolios.Select(g => (g.Name, g.Metrics.Net)));
        Assert.Equal([("BTC", "15.2"), ("ETH", "-14.3")], report.Groups.Assets.Select(g => (g.Name, g.Metrics.Net)));
    }

    /// <summary>Document section 4.2: Plays and imported positions.</summary>
    public static async Task ReviewPositionsAsync(GoldenJournal j, GoldenJournalScreens s)
    {
        var report = await s.Review.GetAsync(new(Period: "all", Positions: "include"), default);
        var m = report.Metrics;
        Assert.Equal((8, 4, 3, 1, 2, "7.81"), (m.Count, m.Wins, m.Losses, m.Scratches, m.Positions, m.Net));
        Assert.Equal(("0.5", "8.8583333333333333333333333333", "3.1333333333333333333333333333", "2.8271276595744680851063829787"),
            (m.Batting, m.AverageGain, m.AverageLoss, m.Payoff));
        Assert.Equal(("1.5386206896551724137931034483", "6.285", "4.8333333333333333333333333333", "0.6221428571428571428571428571"),
            (m.ProfitFactor, m.AverageWin, m.AverageLossUsd, m.Expectancy));
        Assert.Equal(("0.1066666666666666666666666667", 6, "10.4", 4, 2), (m.AverageR, m.RiskCount, m.Drawdown, m.MaxWins, m.MaxLosses));
        Assert.Equal((true, 6, 2, 1), (report.Positions!.Included, report.Positions.Plays, report.Positions.Positions, report.Positions.Omitted));
        var positions = report.Plays.Where(p => p.Origin == "position").ToList();
        Assert.Equal([(j.Operation("b4", "short"), "2.97", "8.25"), (j.Operation("b3", "long"), "3.94", "19.7")],
            positions.Select(p => (p.OperationId, p.Net, p.ReturnPercent)));
        Assert.All(positions, p => Assert.Null(p.RMultiple));
        Assert.Equal([("Swing", "11.1"), ("Unassigned", "-3.29")], report.Groups.Portfolios.Select(g => (g.Name, g.Metrics.Net)));
        Assert.Equal([("BTC", "15.2"), ("SOL", "6.91"), ("ETH", "-14.3")], report.Groups.Assets.Select(g => (g.Name, g.Metrics.Net)));
        Assert.Equal([("day", 0, (string?)null), ("week", 2, "-10.4"), ("month", 8, "7.81"), ("year", 8, "7.81"), ("all", 8, "7.81")],
            report.Periods.Select(p => (p.Id, p.Count, p.Net)));
    }

    /// <summary>Document section 5: the record comes from the Plays; P6 is not settled and P3 is a scratch.</summary>
    public static async Task SizingAsync(GoldenJournal j, GoldenJournalScreens s)
    {
        var sizing = await s.Sizing.GetAsync(TradingMode.Real, default);
        var r = sizing.Record;
        Assert.Equal(("plays", 6, 5, 20, 2, 3, 1), (r.Source, r.ClosedPlays, r.DecidedPlays, r.Window, r.Wins, r.Losses, r.Scratches));
        Assert.Equal(("0.4", "6.3", "3.13333333", "2.0106383", "1.5", "1.26", "-0.62666667"), (r.BattingAverage, r.AverageGainPercent,
            r.AverageLossPercent, r.WinLossRatio, r.BreakEvenRewardRisk, r.AverageWinR, r.AverageLossR));
        Assert.Equal((1, 0), (r.FeeOnlyLosses, r.UnknownGrossLosses));
        Assert.Equal(("full", "1", 2, 0, 2), (sizing.Exposure.Level, sizing.Exposure.Multiplier, sizing.Exposure.LossStreak,
            sizing.Exposure.WinsSinceStepDown, sizing.Exposure.TiedOutcomes));
        Assert.Equal("Full size: no losing streak. 2 results share an exit time; their order is not known.", sizing.Exposure.Reason);
        Assert.Equal(("1.25", "10", "2", "default", "default"), (sizing.Limits.EffectiveRiskPercent, sizing.Limits.MaxStopPercent,
            sizing.Limits.MinRewardRisk, sizing.Limits.MaxStopSource, sizing.Limits.MinRewardRiskSource));
        Assert.Equal([("P5", "-0.2", "5", "-0.04"), ("P7", "-10.2", "10", "-1.02"), ("P4", "5.6", "10", "0.56"), ("P3", "0", "5", "0"),
                ("P1", "9.8", "5", "1.96"), ("P2", "-4.1", "5", "-0.82")],
            r.Recent.Select(o => (Name(j, o.PlayId), o.NetResultUsd, o.RiskUsd, o.RMultiple)));
    }

    /// <summary>Document section 6.</summary>
    public static async Task OpenRiskAsync(GoldenJournal j, GoldenJournalScreens s)
    {
        var risk = await s.OpenRisk.GetAsync("all", default);
        Assert.Equal(("7.5", 1, 0), (risk.Long.KnownUsd, risk.Long.KnownCount, risk.Long.UnknownCount));
        Assert.Equal(("18", 1, 0), (risk.Short.KnownUsd, risk.Short.KnownCount, risk.Short.UnknownCount));
        Assert.Equal([("P8", "short", "open", "18", "not-checked"), ("P9", "long", "planned", "7.5", (string?)null)],
            risk.Plays.Select(p => (Name(j, p.PlayId), p.Direction, p.Status, p.RiskUsd, p.Protection)));
        Assert.Equal(("1500", 2, 2), (risk.Balance.AmountUsd, risk.Balance.ContributingAccountCount, risk.Balance.TotalAccountCount));
        Assert.Equal((0, 1), (risk.Protection.NotCovered, risk.Protection.Unverified));
        Assert.Empty(risk.NotIncluded);
    }

    /// <summary>Section 7: the identities that hold without a known defect.</summary>
    public static async Task IdentitiesAsync(GoldenJournal j, GoldenJournalScreens s)
    {
        var activity = (await s.Activity.PageAsync(new(PageSize: "100"), default)).Operations;
        var closed = activity.Where(o => o.Status == "closed").ToList();
        var review = await s.Review.GetAsync(new(Period: "all", Positions: "include"), default);
        var plays = await s.Review.GetAsync(new(Period: "all", Positions: "exclude"), default);

        // 7.1: Review with positions = Activity's closed known nets − the operations Review leaves out with a known net. Review
        // excludes P6 (an order misses an execution) and X1 (net unknown, so not in the sum either).
        var excluded = closed.Where(o => o.PlayId == j.Play["P6"].Id).ToList();
        Assert.Equal(ExactSum(closed.Select(o => o.NetResultUsd)) - ExactSum(excluded.Select(o => o.NetResultUsd)),
            ExactSum([review.Metrics.Net]));
        Assert.Equal(review.Metrics.Count + review.Coverage.IncompleteExecutions + review.Positions!.Omitted, closed.Count);

        // 7.2: in a scope with no known missing execution, Overview realized = Activity's closed known nets.
        foreach (var (scope, account) in new[] { ("unassigned", j.Net.Id), ($"account:{j.Net.Id}", j.Net.Id) })
        {
            var overview = await s.Overview.GetAsync("portfolio", "all", scope, TradingMode.Real, default);
            Assert.Equal(ExactSum(closed.Where(o => o.AccountId == account).Select(o => o.NetResultUsd)),
                ExactSum([overview.Totals.RealizedResult.AmountUsd]));
        }

        // 7.3: group nets and amounts sum to their totals, on every grouping.
        foreach (var groupBy in new[] { "portfolio", "account" })
        {
            var overview = await s.Overview.GetAsync(groupBy, "all", "all", TradingMode.Real, default);
            Assert.Equal(ExactSum([overview.Totals.RealizedResult.AmountUsd]), ExactSum(overview.Rows.Select(r => r.Totals.RealizedResult.AmountUsd)));
            Assert.Equal(ExactSum([overview.Totals.UnrealizedPnl.AmountUsd]), ExactSum(overview.Rows.Select(r => r.Totals.UnrealizedPnl.AmountUsd)));
            Assert.Equal(ExactSum([overview.Totals.Balance.AmountUsd]), ExactSum(overview.Rows.Select(r => r.Totals.Balance.AmountUsd)));
        }
        foreach (var report in new[] { review, plays })
            foreach (var groups in new[] { report.Groups.Portfolios, report.Groups.Accounts, report.Groups.Assets, report.Groups.Directions,
                         report.Groups.Origins })
                Assert.Equal(ExactSum([report.Metrics.Net]), ExactSum(groups.Select(g => g.Metrics.Net)));

        // 7.4: 30 days have no close on their boundary, so every screen selects the same ten closed operations.
        var activity30 = await s.Activity.PageAsync(new(Period: "30", Status: "closed", PageSize: "100"), default);
        var overview30 = (await s.Overview.GetAsync("portfolio", "30", "all", TradingMode.Real, default)).Totals.RealizedResult.Coverage;
        var review30 = await s.Review.GetAsync(new(Period: "month", Positions: "include"), default);
        Assert.Equal([10, 10, 10], new[] { activity30.TotalCount, overview30.ContributingOperationCount + overview30.ExcludedOperationCount,
            review30.Metrics.Count + review30.Coverage.IncompleteExecutions + review30.Positions!.Omitted!.Value });
        // 7 days: what lies strictly inside is selected everywhere, and nothing at or before D(8). The close exactly on D(7)
        // is in a test skipped for #125.
        var activity7 = await s.Activity.PageAsync(new(Period: "7", Status: "closed", PageSize: "100"), default);
        var review7 = await s.Review.GetAsync(new(Period: "week", Positions: "include"), default);
        Assert.Superset(new HashSet<string> { j.Operation("a13", "long"), j.Operation("b1", "long") },
            activity7.Operations.Select(o => o.Id).ToHashSet());
        Assert.DoesNotContain(j.Operation("b3", "long"), activity7.Operations.Select(o => o.Id));
        Assert.Equal(["P5", "P7"], review7.Plays.Where(p => p.Id is not null).Select(p => Name(j, p.Id)).Order().ToArray());

        // 7.5: Sizing's record and Review's Plays count the same outcomes and the same batting average.
        var record = (await s.Sizing.GetAsync(TradingMode.Real, default)).Record;
        Assert.Equal((plays.Metrics.Wins, plays.Metrics.Losses, plays.Metrics.Scratches, plays.Metrics.Count),
            (record.Wins, record.Losses, record.Scratches, record.ClosedPlays));
        Assert.Equal(ExactSum([plays.Metrics.Batting]), ExactSum([record.BattingAverage]));

        // 7.6: the mark path and the venue's own snapshot figure agree (P8, ETH −2; AVAX, after an exit and an add, 200).
        foreach (var (operation, contract) in new[] { (j.Operation("a18", "short"), "ETH"), (j.Operation("a15", "long"), "AVAX") })
        {
            var marked = activity.Single(o => o.Id == operation);
            var reported = j.Snapshots.Single(x => x.AccountId == j.Gross.Id).Positions.Single(p => p.ContractId == contract).UnrealizedPnlUsd;
            Assert.Equal(ExactSum([marked.UnrealizedPnlUsd]), Vessel.Domain.Numerics.ExactAmount.From(reported));
        }
    }

    public static string Name(GoldenJournal j, Guid? playId) => j.Play.SingleOrDefault(p => p.Value.Id == playId).Key ?? "?";

    public static Vessel.Domain.Numerics.ExactAmount ExactSum(IEnumerable<string?> values) =>
        values.OfType<string>().Aggregate(default(Vessel.Domain.Numerics.ExactAmount),
            (sum, v) => sum + Vessel.Domain.Numerics.ExactAmount.Parse(v));
}
