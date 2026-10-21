using System.Globalization;
using System.Text.Json;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class DiaryArithmeticRegressionTests
{
    private static readonly DateTimeOffset At = ReviewClock.Now.AddDays(-1);

    [Theory]
    [InlineData("long", "6", "2", "7", "8", "4")]
    [InlineData("short", "6", "2", "7", "8", "-4")]
    [InlineData("long", "100", "8", "100.1", "101", "8.2")]
    [InlineData("short", "100", "8", "100.1", "101", "-8.2")]
    [InlineData("long", "6", "2", "7", "100", "280")]
    [InlineData("short", "6", "2", "7", "100", "-280")]
    public async Task Repeating_average_partial_exit_keeps_exact_facts_and_consumes_remaining_cost(
        string direction, string firstPrice, string secondQuantity, string secondPrice, string exitPrice, string expected)
    {
        var f = new DiaryArithmeticFixture(direction: direction);
        await f.Record("entry", "1", firstPrice, 0);
        await f.Record("entry", secondQuantity, secondPrice, 1);
        var before = JsonSerializer.Serialize(f.Rows);
        var partial = await f.Record("exit", "1", exitPrice, 2);
        Assert.Equal("open", partial.Play.Summary.Status);
        Assert.Equal(secondQuantity, partial.Totals.OpenQuantity);
        Assert.Equal(before, JsonSerializer.Serialize(f.Rows.Take(2).ToList()));
        Assert.True(partial.Totals.FeesComplete);
        Assert.NotEqual("0", partial.Totals.GrossResultUsd);

        var closed = await f.Record("exit", secondQuantity, exitPrice, 3);
        Assert.Equal("closed", closed.Play.Summary.Status);
        Assert.Equal("0", closed.Totals.OpenQuantity);
        var ledger = ManualLedger.Calculate(f.Play, f.Rows);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), ledger.Gross);
        Assert.Equal(ledger.Gross, ledger.Projection.Sum(x => x.Fill.ClosedPnlUsd));
        Assert.Equal(ledger.Gross, ledger.Net);
        f.Closed.Add(new(f.Play, f.Plays.Revisions, ledger.Projection, ManualSource: true));
        Assert.Equal(ledger.Net, PlayOutcomeCalculator.Outcome(f.Closed.Single())!.NetResult);
        Assert.Equal(ManualLedger.Text(ledger.Gross), (await f.Review.GetAsync(new("all"), default)).Metrics.Net);
        Assert.Equal(1, (await f.Sizing.GetAsync(default)).Record.ClosedPlays);
    }

    [Fact]
    public async Task Tiny_representable_entry_and_supported_return_are_not_rejected_or_zeroed()
    {
        var f = new DiaryArithmeticFixture();
        await f.Record("entry", "1e-28", "1", 0);
        var closed = await f.Record("exit", "1e-28", "2", 1);
        Assert.Equal("closed", closed.Play.Summary.Status);
        Assert.Equal("0.0000000000000000000000000001", closed.Totals.NetResultUsd);
        var ledger = ManualLedger.Calculate(f.Play, f.Rows);
        var outcome = PlayOutcomeCalculator.Outcome(new(f.Play, f.Plays.Revisions, ledger.Projection, ManualSource: true));
        Assert.NotNull(outcome);
        Assert.Equal(100, outcome.ReturnPercent);
    }

    [Fact]
    public async Task Repeating_cost_carry_supports_an_entry_between_partial_exits_and_exact_fees()
    {
        var f = new DiaryArithmeticFixture();
        await f.Record("entry", "1e0", "6.00", 0, "0.1250");
        await f.Record("entry", "2", "7", 1);
        await f.Record("exit", "1", "8", 2);
        await f.Record("entry", "1", "7", 3);
        await f.Record("exit", "1", "8", 4);
        var closed = await f.Record("exit", "2", "8", 5, "0.0750");
        Assert.Equal("1e0", closed.Records[0].Quantity);
        Assert.Equal("6.00", closed.Records[0].Price);
        Assert.Equal("0.1250", closed.Records[0].Fee);
        Assert.Equal("0.0750", closed.Records[^1].Fee);
        Assert.Equal("5", closed.Totals.GrossResultUsd);
        Assert.Equal("4.8", closed.Totals.NetResultUsd);
        Assert.Equal(5, ManualLedger.Calculate(f.Play, f.Rows).Projection.Sum(x => x.Fill.ClosedPnlUsd));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overflowing_return_is_rejected_before_closure_or_correction_mutates_anything(bool correction)
    {
        var f = new DiaryArithmeticFixture();
        await f.Record("entry", "1e-28", "1", 0);
        if (correction) await f.Record("exit", "1e-28", "1", 1);
        var previous = correction ? f.Rows.Single(r => r.Role == "exit") : null;
        var before = f.Snapshot();
        var request = f.Request("exit", "1e-28", "1", 1, "1") with { Reason = correction ? "Correct fee" : null };
        var error = await Assert.ThrowsAsync<ProblemException>(() =>
            f.Service.RecordAsync(f.Play.Id, previous?.Id, request, default));
        Assert.Equal(400, error.StatusCode);
        Assert.Contains("supported decimal range", error.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(correction ? PlayStatus.Closed : PlayStatus.Open, f.Play.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_overflow_outcome_is_excluded_from_review_and_sizing_with_a_reason(bool riskOverflow)
    {
        var f = new DiaryArithmeticFixture(plan: riskOverflow
            ? TestPlays.Plan("1", "0.9999999999999999999999999999") : null);
        var quantity = riskOverflow ? "1" : "1e-28";
        var records = new[] { f.Row("entry", quantity, "1", 0), f.Row("exit", quantity, riskOverflow ? "11" : "1", 1,
            riskOverflow ? "0" : "1") };
        var ledger = ManualLedger.Calculate(f.Play, records);
        f.Plays.Add(f.Play.RecordManualOpen(At));
        f.Plays.Add(f.Play.RecordManualClose(At.AddSeconds(1), At.AddSeconds(1)));
        var facts = new ClosedPlayFacts(f.Play, f.Plays.Revisions, ledger.Projection, ManualSource: true);
        Assert.Equal(riskOverflow ? 10 : -1, ledger.Net);
        Assert.True(ExecutionIntegrity.Settled(facts));
        f.Closed.Add(facts);
        var before = f.Snapshot();
        Assert.Null(PlayOutcomeCalculator.Outcome(facts));
        var review = await f.Review.GetAsync(new("all"), default);
        Assert.Equal(0, review.Metrics.Count);
        Assert.Equal(1, review.Coverage.IncompleteExecutions);
        Assert.Empty(review.Plays);
        var excluded = await f.Review.ExclusionsAsync(new("all"), default);
        Assert.Equal(1, excluded.Total);
        Assert.Contains(excluded.Items.Single().Reasons, r => r.Contains("supported decimal range", StringComparison.Ordinal));
        Assert.Equal(0, (await f.Sizing.GetAsync(default)).Record.ClosedPlays);
        Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public async Task Review_uses_the_same_plan_direction_integrity_as_sizing(string direction)
    {
        var f = new DiaryArithmeticFixture("hyperliquid", direction);
        var openingSide = direction == "long" ? "sell" : "buy";
        var closingSide = openingSide == "sell" ? "buy" : "sell";
        var opening = f.Fill("in", openingSide, "open", 0, 100);
        var closing = f.Fill("out", closingSide, "close", 1, 90, 10);
        f.Play.MarkOpen("import", At);
        f.Play.MarkClosed("import", At.AddSeconds(1));
        var facts = new ClosedPlayFacts(f.Play, f.Plays.Revisions,
            [new(OrderLinkRole.Entry, opening), new(OrderLinkRole.Exit, closing)]);
        f.Closed.Add(facts);
        Assert.False(ExecutionIntegrity.Settled(facts));
        var before = f.Snapshot();
        Assert.Equal(0, (await f.Review.GetAsync(new("all"), default)).Metrics.Count);
        Assert.Equal(0, (await f.Sizing.GetAsync(default)).Record.ClosedPlays);
        var exclusions = await f.Review.ExclusionsAsync(new("all"), default);
        Assert.Equal(1, exclusions.Total);
        Assert.Contains(exclusions.Items.Single().Reasons, r => r.Contains("in", StringComparison.Ordinal) &&
            r.Contains($"this {direction} play", StringComparison.Ordinal));
        Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("long", false, true)]
    [InlineData("short", false, true)]
    [InlineData("long", true, false)]
    [InlineData("short", true, false)]
    public async Task Retrospective_preview_and_confirmation_reject_invalid_opening_order_flags(
        string direction, bool reduceOnly, bool positionTpsl)
    {
        var f = new DiaryArithmeticFixture("hyperliquid", direction);
        f.AddOperation(direction);
        var opening = f.Orders.Single(o => o.ExternalOrderId == "in");
        opening.ReduceOnly = reduceOnly;
        opening.IsPositionTpsl = positionTpsl;
        var operation = OperationAggregator.Aggregate(f.Fills).Single();
        var before = f.Snapshot();
        var preview = await f.Service.PreviewAsync(f.Account.Id, operation.Id, default);
        Assert.False(preview.Eligible);
        Assert.Contains(preview.Reasons, r => r.Contains("in", StringComparison.Ordinal) &&
            r.Contains("reduce-only or position TP/SL", StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<ProblemException>(() => f.Service.CreateRetrospectiveAsync(
            new(Guid.NewGuid(), f.Account.Id, operation.Id, preview.Fingerprint, "Retrospective"), default));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public async Task Retrospective_confirmation_rechecks_flags_after_an_eligible_preview()
    {
        var f = new DiaryArithmeticFixture("hyperliquid");
        f.AddOperation("long");
        var operation = OperationAggregator.Aggregate(f.Fills).Single();
        var preview = await f.Service.PreviewAsync(f.Account.Id, operation.Id, default);
        Assert.True(preview.Eligible);
        f.Orders.Single(o => o.ExternalOrderId == "in").IsPositionTpsl = true;
        var before = f.Snapshot();
        var error = await Assert.ThrowsAsync<ProblemException>(() => f.Service.CreateRetrospectiveAsync(
            new(Guid.NewGuid(), f.Account.Id, operation.Id, preview.Fingerprint, "Retrospective"), default));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(before, f.Snapshot());
        var current = await f.Service.PreviewAsync(f.Account.Id, operation.Id, default);
        Assert.False(current.Eligible);
        Assert.Contains(current.Reasons, r => r.Contains("position TP/SL", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public async Task Retrospective_accepts_position_tpsl_exits_without_changing_imported_facts(string direction)
    {
        var f = new DiaryArithmeticFixture("hyperliquid", direction);
        f.AddOperation(direction);
        var exit = f.Orders.Single(o => o.ExternalOrderId == "out");
        exit.IsPositionTpsl = true;
        exit.ReduceOnly = true;
        var before = JsonSerializer.Serialize(new { f.Fills, f.Orders });
        var operation = OperationAggregator.Aggregate(f.Fills).Single();
        var preview = await f.Service.PreviewAsync(f.Account.Id, operation.Id, default);
        Assert.True(preview.Eligible);
        var created = await f.Service.CreateRetrospectiveAsync(
            new(Guid.NewGuid(), f.Account.Id, operation.Id, preview.Fingerprint, "Retrospective"), default);
        Assert.Equal("retrospective", created.Summary.Origin);
        Assert.Equal("closed", created.Summary.Status);
        Assert.Equal(2, f.Plays.Links.Count);
        Assert.Equal(before, JsonSerializer.Serialize(new { f.Fills, f.Orders }));
    }

    [Fact]
    public async Task Review_sums_two_supported_huge_manual_outcomes_without_scalar_overflow()
    {
        var f = new DiaryArithmeticFixture();
        await f.CloseManual("1e28", "5e28", 0);
        await f.CloseManual("1e28", "5e28", 2);
        Assert.All(f.Closed, facts =>
        {
            var outcome = PlayOutcomeCalculator.Outcome(facts)!;
            Assert.Equal(4e28m, outcome.NetResult);
            Assert.Equal(400, outcome.ReturnPercent);
        });
        var before = f.Snapshot();
        var result = await f.Review.GetAsync(new("all"), default);
        var total = "8" + new string('0', 28);
        Assert.Equal(2, result.Metrics.Count);
        Assert.Equal(total, result.Metrics.Net);
        Assert.Equal("4" + new string('0', 28), result.Metrics.AverageWin);
        Assert.Equal("400", result.Metrics.AverageGain);
        Assert.Equal(total, result.Series.Single(point => point.Count == 2).Net);
        Assert.Equal(total, result.Series[^1].Cumulative);
        Assert.All(result.Periods, period => Assert.Equal(total, period.Net));
        foreach (var groups in new[] { result.Groups.Accounts, result.Groups.Portfolios, result.Groups.Assets })
            Assert.Equal(total, groups.Single().Metrics.Net);
        Assert.Empty((await f.Review.ExclusionsAsync(new("all"), default)).Items);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public async Task Review_keeps_huge_positive_and_negative_running_totals_drawdown_and_profit_factor()
    {
        var f = new DiaryArithmeticFixture();
        for (var index = 0; index < 5; index++)
            await f.CloseManual(index < 2 ? "1e28" : "5e28", index < 2 ? "5e28" : "1e28",
                (index - 4) * 86400);
        var result = await f.Review.GetAsync(new("all"), default);
        Assert.Equal(5, result.Metrics.Count);
        Assert.Equal("-4" + new string('0', 28), result.Metrics.Net);
        Assert.Equal("12" + new string('0', 28), result.Metrics.Drawdown);
        Assert.Equal("0.6666666666666666666666666667", result.Metrics.ProfitFactor);
        Assert.Equal("5", result.Metrics.Payoff);
        Assert.Equal("4" + new string('0', 28), result.Metrics.AverageLossUsd);
        Assert.Equal("-8" + new string('0', 27), result.Metrics.Expectancy);
        Assert.Contains(result.Series, point => point.Cumulative == "8" + new string('0', 28));
        Assert.Equal("-12" + new string('0', 28), result.Series[^1].Drawdown);
        Assert.Equal(2, result.Metrics.MaxWins);
        Assert.Equal(3, result.Metrics.MaxLosses);
        foreach (var groups in new[] { result.Groups.Accounts, result.Groups.Portfolios, result.Groups.Assets })
            Assert.Equal(result.Metrics, groups.Single().Metrics);
    }

    [Fact]
    public async Task Review_money_cancellation_and_profit_factor_keep_precision_beyond_scalar_decimal()
    {
        var f = new DiaryArithmeticFixture();
        await f.CloseManual("1e28", "5e28", -2 * 86400);
        await f.CloseManual("1e28", "5e28", -86400);
        await f.CloseManual("1", "1", 0, "1e-28");
        var result = await f.Review.GetAsync(new("all"), default);
        var total = "7" + new string('9', 28) + "." + new string('9', 28);
        Assert.Equal(total, result.Metrics.Net);
        Assert.Equal("8" + new string('0', 56), result.Metrics.ProfitFactor);
        Assert.Equal("0.0000000000000000000000000001", result.Metrics.Drawdown);
        Assert.Equal(total, result.Series[^1].Cumulative);
        Assert.Equal("-0.0000000000000000000000000001", result.Series[^1].Drawdown);
        foreach (var groups in new[] { result.Groups.Accounts, result.Groups.Portfolios, result.Groups.Assets })
        {
            Assert.Equal(total, groups.Single().Metrics.Net);
            Assert.Equal(result.Metrics.ProfitFactor, groups.Single().Metrics.ProfitFactor);
        }
    }

    [Fact]
    public async Task Oversized_retrospective_operation_returns_before_order_reads_or_quadratic_scans()
    {
        var f = new DiaryArithmeticFixture("hyperliquid");
        for (var index = 0; index < 1000; index++)
            f.Fills.Add(f.Fill($"in-{index}", "buy", "open", index, 100));
        var exit = f.Fill("out", "sell", "close", 1000, 110, 10000);
        exit.Quantity = 1000;
        f.Fills.Add(exit);
        var operation = OperationAggregator.Aggregate(f.Fills).Single();
        var counted = new CountingFills(f.Fills);
        f.ActivityFills = counted;
        var preview = await f.Service.PreviewAsync(f.Account.Id, operation.Id, default);
        Assert.False(preview.Eligible);
        Assert.Equal(200, preview.Operation.Fills.Count);
        Assert.Equal(801, preview.Operation.OmittedFillCount);
        Assert.Contains(preview.Reasons, r => r.Contains("200", StringComparison.Ordinal) && r.Contains("limit", StringComparison.Ordinal));
        Assert.Equal(0, f.OrderReads);
        Assert.Equal(0, f.LinkReads);
        Assert.Equal(counted.Count, counted.Enumerated);
    }

    [Fact]
    public async Task Unrelated_instrument_import_does_not_change_retrospective_fingerprint_or_confirmation()
    {
        var f = new DiaryArithmeticFixture("hyperliquid");
        f.AddOperation("long");
        var operation = OperationAggregator.Aggregate(f.Fills).Single();
        var preview = await f.Service.PreviewAsync(f.Account.Id, operation.Id, default);
        Assert.True(preview.Eligible);
        var unrelated = f.Fill("eth-in", "buy", "open", 2, 100);
        unrelated.ContractId = "ETH";
        f.Fills.Add(unrelated);
        var after = await f.Service.PreviewAsync(f.Account.Id, operation.Id, default);
        Assert.True(after.Eligible);
        Assert.Equal(preview.Fingerprint, after.Fingerprint);
        var originals = JsonSerializer.Serialize(f.Fills);
        var created = await f.Service.CreateRetrospectiveAsync(
            new(Guid.NewGuid(), f.Account.Id, operation.Id, preview.Fingerprint, "BTC retrospective"), default);
        Assert.Equal("closed", created.Summary.Status);
        // Links reference the operation's own BTC orders only.
        Assert.All(f.Plays.Links, link => Assert.Contains(f.Orders, o => o.Id == link.ImportedOrderId && o.ContractId == "BTC"));
        Assert.Equal(originals, JsonSerializer.Serialize(f.Fills));
    }

    [Fact]
    public async Task Same_order_fill_outside_selected_operation_changes_fingerprint_and_blocks_confirmation()
    {
        var f = new DiaryArithmeticFixture("hyperliquid");
        f.AddOperation("long");
        var operation = OperationAggregator.Aggregate(f.Fills).Single();
        var preview = await f.Service.PreviewAsync(f.Account.Id, operation.Id, default);
        Assert.True(preview.Eligible);
        f.Fills.Add(f.Fill("in", "buy", "open", 2, 100));
        var before = f.Snapshot();
        var after = await f.Service.PreviewAsync(f.Account.Id, operation.Id, default);
        Assert.False(after.Eligible);
        Assert.NotEqual(preview.Fingerprint, after.Fingerprint);
        Assert.Contains(after.Reasons, reason => reason.Contains("Order in crosses operation boundaries", StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<ProblemException>(() => f.Service.CreateRetrospectiveAsync(
            new(Guid.NewGuid(), f.Account.Id, operation.Id, preview.Fingerprint, "BTC retrospective"), default));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public async Task Bounded_retrospective_preview_indexes_account_fills_once()
    {
        var f = new DiaryArithmeticFixture("hyperliquid");
        f.AddOperation("long");
        for (var index = 0; index < 150; index++)
        {
            var unrelated = f.Fill($"eth-{index}", "buy", "open", index, 100);
            unrelated.ContractId = "ETH";
            f.Fills.Add(unrelated);
        }
        var operation = OperationAggregator.Aggregate(f.Fills).Single(o => o.ContractId == "BTC");
        var counted = new CountingFills(f.Fills);
        f.ActivityFills = counted;
        Assert.True((await f.Service.PreviewAsync(f.Account.Id, operation.Id, default)).Eligible);
        Assert.InRange(counted.Enumerated, counted.Count, counted.Count * 2);
    }

    private sealed class CountingFills(IReadOnlyList<ImportedFill> fills) : IReadOnlyList<ImportedFill>
    {
        public int Enumerated { get; private set; }
        public int Count => fills.Count;
        public ImportedFill this[int index] => fills[index];
        public IEnumerator<ImportedFill> GetEnumerator()
        {
            foreach (var fill in fills) { Enumerated++; yield return fill; }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class DiaryArithmeticFixture : IDiaryStore, IPlayExecutionStore, IReviewStore
    {
        public Account Account { get; }
        public MemoryPlayStore Plays { get; }
        public Play Play { get; }
        public List<ManualExecution> Rows { get; } = [];
        public List<ManualExecutionHistory> History { get; } = [];
        public List<DiaryRequest> Requests { get; } = [];
        public List<ImportedFill> Fills { get; } = [];
        public List<ImportedOrder> Orders { get; } = [];
        public IReadOnlyList<ImportedFill>? ActivityFills { get; set; }
        public int OrderReads { get; private set; }
        public int LinkReads { get; private set; }
        private readonly MemorySizingStore sizing = new();
        public List<ClosedPlayFacts> Closed => sizing.Closed;
        public DiaryService Service => new(Plays, this, this, new VenueRegistry([VenueDescriptor.Manual], [], [], [], [], [], [], []), new ReviewClock(), TestUnitOfWork.None);
        public ReviewService Review => new(this, new ReviewClock());
        public SizingService Sizing => new(sizing, new CoreOwner(Account.OwnerId), new ReviewClock(), TestUnitOfWork.None);

        public DiaryArithmeticFixture(string venue = "manual", string direction = "long", PlayPlanDocument? plan = null)
        {
            Account = new(Guid.NewGuid(), Guid.NewGuid(), venue, "Regression");
            Plays = new(Account.OwnerId);
            Plays.Accounts.Add(Account);
            Play = new(Guid.NewGuid(), Account, new(venue, "BTC"),
                venue == "manual" ? InstrumentSource.Manual : InstrumentSource.Venue,
                PlayDocuments.Serialize((plan ?? TestPlays.Plan()) with { Direction = direction }), At.AddSeconds(-1));
            Plays.Add(Play);
            Plays.Add(Play.MarkPlanned(At.AddSeconds(-1)).Revision);
        }

        public ManualExecutionRequest Request(string role, string quantity, string price, int seconds, string fee = "0") =>
            new(Guid.NewGuid(), Play.Version, role, role == "entry" ? "entry-1" : null, At.AddSeconds(seconds),
                role == "entry" ? ExecutionIntegrity.EntrySide(PlayDocuments.Read(Play.Plan)) :
                    ExecutionIntegrity.EntrySide(PlayDocuments.Read(Play.Plan)) == "buy" ? "sell" : "buy",
                quantity, price, fee, "USDC", "Paper broker");
        public Task<ManualExecutionsDto> Record(string role, string quantity, string price, int seconds, string fee = "0") =>
            Service.RecordAsync(Play.Id, null, Request(role, quantity, price, seconds, fee), default);
        public async Task CloseManual(string entryPrice, string exitPrice, int seconds, string exitFee = "0")
        {
            var at = At.AddSeconds(seconds);
            var play = new Play(Guid.NewGuid(), Account, new("manual", "BTC"), InstrumentSource.Manual,
                PlayDocuments.Serialize(TestPlays.Plan()), at.AddSeconds(-1));
            Plays.Add(play);
            var revision = play.MarkPlanned(at.AddSeconds(-1)).Revision;
            Plays.Add(revision);
            await Service.RecordAsync(play.Id, null, new(Guid.NewGuid(), play.Version, "entry", "entry-1", at,
                "buy", "1", entryPrice, "0", "USDC", "Paper broker"), default);
            await Service.RecordAsync(play.Id, null, new(Guid.NewGuid(), play.Version, "exit", null, at.AddSeconds(1),
                "sell", "1", exitPrice, exitFee, "USDC", "Paper broker"), default);
            var ledger = ManualLedger.Calculate(play, Rows.Where(row => row.PlayId == play.Id));
            Closed.Add(new(play, [revision], ledger.Projection, ManualSource: true));
        }
        public ManualExecution Row(string role, string quantity, string price, int seconds, string fee = "0")
        {
            var request = Request(role, quantity, price, seconds, fee);
            return new() { Id = Guid.NewGuid(), OwnerId = Account.OwnerId, PlayId = Play.Id, Role = role,
                EntryId = request.EntryId, Side = request.Side, Quantity = quantity, Price = price,
                Fee = fee, FeeCurrency = "USDC", Source = request.Source, OccurredAtUtc = request.OccurredAtUtc };
        }
        public ImportedFill Fill(string order, string side, string effect, int seconds, decimal price, decimal pnl = 0) =>
            new() { Id = Guid.NewGuid(), OwnerId = Account.OwnerId, AccountId = Account.Id, ContractId = "BTC",
                SourceFillId = order + "-fill", ExternalOrderId = order, Side = side, PositionEffect = effect, Quantity = 1,
                Price = price, ClosedPnlUsd = pnl, Fee = 0, FeeToken = "USDC", FeeBasis = ExecutionFacts.FeeReported,
                PnlBasis = ExecutionFacts.PnlGross, Direction = "", TransactionHash = "", OccurredAtUtc = At.AddSeconds(seconds) };
        public void AddOperation(string direction)
        {
            Fills.Add(Fill("in", direction == "long" ? "buy" : "sell", "open", 0, 100));
            Fills.Add(Fill("out", direction == "long" ? "sell" : "buy", "close", 1, direction == "long" ? 110 : 90, 10));
            foreach (var fill in Fills)
                Orders.Add(new() { Id = Guid.NewGuid(), OwnerId = Account.OwnerId, AccountId = Account.Id, ContractId = "BTC",
                    ExternalOrderId = fill.ExternalOrderId, Side = fill.Side, OriginalSize = 1, RemainingSize = 0, OrderType = "Limit",
                    LimitPrice = fill.Price, Status = "filled", VenueStatus = "filled", PlacedAtUtc = At,
                    StatusAtUtc = fill.OccurredAtUtc, ObservedAtUtc = At.AddSeconds(2) });
        }
        public string Snapshot() => JsonSerializer.Serialize(new { Rows, History, Requests, Fills, Orders,
            Plays.Plays, Plays.Revisions, Plays.Changes, Plays.Links, Plays.Saves, Closed });
        public Task<T> WithRequestLockAsync<T>(Guid id, Func<Task<T>> action, CancellationToken ct) => action();
        public Task<T> WithAccountLockAsync<T>(Guid id, Func<Task<T>> action, CancellationToken ct) => action();
        public Task<DiaryRequest?> RequestAsync(Guid id, CancellationToken ct) => Task.FromResult(Requests.SingleOrDefault(r => r.Id == id));
        public Task<List<ManualExecution>> ManualAsync(Guid id, CancellationToken ct) => Task.FromResult(Rows.Where(r => r.PlayId == id).ToList());
        public Task<List<ManualExecutionHistory>> ManualHistoryAsync(Guid id, CancellationToken ct) => Task.FromResult(History.Where(r => r.PlayId == id).ToList());
        public Task<ActivityFacts> ActivityAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(new ActivityFacts([Account], ActivityFills ?? Fills, [], new Dictionary<Guid, Guid>()));
        public Task<List<ImportedOrder>> OrdersAsync(Guid id, CancellationToken ct)
        {
            OrderReads++;
            return Task.FromResult(Orders.ToList());
        }
        public Task<List<PlayOrderLink>> LinksAsync(Guid id, CancellationToken ct)
        {
            LinkReads++;
            return Task.FromResult(Plays.Links.ToList());
        }
        public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => Plays.AccountAsync(id, ct);
        public Task<List<Play>> ActivePlaysAsync(Guid id, CancellationToken ct, bool settling = false) => Task.FromResult(Plays.Plays.Where(p => p.AccountId == id).ToList());
        public Task<List<ImportedFill>> FillsAsync(Guid id, IReadOnlyCollection<OrderKey> orders, CancellationToken ct) =>
            Task.FromResult(Fills.Where(f => f.AccountId == id && orders.Contains(OrderKey.Of(f))).ToList());
        public Task UpsertOrdersAsync(Account account, IReadOnlyList<VenueOrder> orders, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct) => Plays.SaveAsync(ct);
        public Task<ReviewFacts> ReadAsync(ReviewQuery query, DateTimeOffset asOf, CancellationToken ct) =>
            Task.FromResult(new ReviewFacts([], [Account], Closed, 0, 0, null));
        public void Add(ManualExecution row) => Rows.Add(row);
        public void Add(ManualExecutionHistory history) => History.Add(history);
        public void Add(DiaryRequest request) => Requests.Add(request);
        public void Add(PlayOrderLink link) => Plays.Links.Add(link);
        public void Add(PlayStatusChange change) => Plays.Add(change);
        public Task ForgetWatchesAsync(Guid playId, CancellationToken ct) => Task.CompletedTask;
    }
}
