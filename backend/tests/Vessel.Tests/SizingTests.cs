using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Sizing;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Sizing;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Application.Venues;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class SizingRuleTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");

    /// <summary>Outcomes one day apart; positive is a win of that return on a 1,000 notional, negative a loss.</summary>
    private static List<PlayOutcome> Outcomes(params decimal[] returns) => returns
        .Select((r, i) => new PlayOutcome(Guid.NewGuid(), Start.AddDays(i), r * 10, 1000, 50, true)).ToList();

    [Fact]
    public void Three_losses_halve_and_two_more_quarter_the_size()
    {
        Assert.Equal(ExposureLevel.Full, Exposure.From(Outcomes(-1, -1)).Level);
        var half = Exposure.From(Outcomes(-1, -1, -1));
        Assert.Equal((ExposureLevel.Half, 0.5m, 0), (half.Level, half.Multiplier, half.LossStreak));
        Assert.Equal("Half size after 3 losses in a row.", half.Reason);
        Assert.Equal(ExposureLevel.Half, Exposure.From(Outcomes(-1, -1, -1, -1)).Level);
        var quarter = Exposure.From(Outcomes(-1, -1, -1, -1, -1));
        Assert.Equal((ExposureLevel.Quarter, 0.25m), (quarter.Level, quarter.Multiplier));
        Assert.Equal(ExposureLevel.Quarter, Exposure.From(Outcomes(-1, -1, -1, -1, -1, -1, -1, -1)).Level);
    }

    [Fact]
    public void A_win_breaks_the_streak_and_scratches_are_ignored()
    {
        Assert.Equal(ExposureLevel.Full, Exposure.From(Outcomes(-1, -1, 2, -1, -1)).Level);
        Assert.Equal(ExposureLevel.Half, Exposure.From(Outcomes(-1, 0, -1, 0, -1)).Level);
        Assert.Equal(2, Exposure.From(Outcomes(-1, -1, 0)).LossStreak);
    }

    [Fact]
    public void Two_wins_with_a_positive_result_step_back_up_one_level()
    {
        var waiting = Exposure.From(Outcomes(-1, -1, -1, 2));
        Assert.Equal((ExposureLevel.Half, 1), (waiting.Level, waiting.WinsSinceStepDown));
        Assert.Contains("1 of 2 wins", waiting.Reason);
        var full = Exposure.From(Outcomes(-1, -1, -1, 2, 2));
        Assert.Equal((ExposureLevel.Full, "Full size again after 2 wins at half size."), (full.Level, full.Reason));
        Assert.Equal(ExposureLevel.Half, Exposure.From(Outcomes(-1, -1, -1, -1, -1, 2, 2)).Level);
        Assert.Equal(ExposureLevel.Full, Exposure.From(Outcomes(-1, -1, -1, -1, -1, 2, 2, 2, 2)).Level);
    }

    [Fact]
    public void Wins_after_a_step_down_must_recover_the_losses_since()
    {
        // Two small wins after a larger loss at half size are not yet a positive result.
        var stuck = Exposure.From(Outcomes(-1, -1, -1, -5, 1, 1));
        Assert.Equal((ExposureLevel.Half, 2), (stuck.Level, stuck.WinsSinceStepDown));
        Assert.Contains("steps up once the result since stepping down is positive", stuck.Reason);
        Assert.Equal(ExposureLevel.Full, Exposure.From(Outcomes(-1, -1, -1, -5, 1, 1, 4)).Level);
    }

    [Fact]
    public void Record_reports_batting_average_and_win_loss_ratio_over_the_latest_window()
    {
        var record = TrackRecord.From(Outcomes(12, -4, 0, 8, -6, -5));
        Assert.Equal((6, 5, 2, 3, 1), (record.ClosedPlays, record.DecidedPlays, record.Wins, record.Losses, record.Scratches));
        Assert.Equal(0.4m, record.BattingAverage);
        Assert.Equal(10m, record.AverageGainPercent);
        Assert.Equal(5m, record.AverageLossPercent);
        Assert.Equal(2m, record.WinLossRatio);
        Assert.Equal(1.5m, record.BreakEvenRewardRisk);
        Assert.Equal(2m, record.AverageWinR);
        Assert.Equal(-1m, record.AverageLossR);

        var windowed = TrackRecord.From(Outcomes(-1, -1, 3, 3), window: 2);
        Assert.Equal((2, 1m), (windowed.Wins, windowed.BattingAverage));
        Assert.Null(windowed.WinLossRatio);
        var empty = TrackRecord.From([]);
        Assert.Null(empty.BattingAverage);
        Assert.Null(TrackRecord.From(Outcomes(-1)).BreakEvenRewardRisk);
    }

    [Fact]
    public void Limits_default_to_ten_percent_and_two_to_one_until_the_record_says_more()
    {
        var full = Exposure.From([]);
        var none = SizingLimits.From(1.25m, TrackRecord.From([]), full);
        Assert.Equal((1.25m, 10m, LimitSource.Default, 2m, LimitSource.Default),
            (none.EffectiveRiskPercent, none.MaxStopPercent, none.MaxStopSource, none.MinRewardRisk, none.MinRewardRiskSource));

        // Five wins averaging 8% cap the stop at 4%; 25% winners over ten plays need 3:1.
        var record = TrackRecord.From(Outcomes(8, 8, 8, 8, 8, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2));
        var limits = SizingLimits.From(2m, record, Exposure.From([]));
        Assert.Equal((4m, LimitSource.AverageGain, 3m, LimitSource.BattingAverage),
            (limits.MaxStopPercent, limits.MaxStopSource, limits.MinRewardRisk, limits.MinRewardRiskSource));
        Assert.Equal(1m, SizingLimits.From(2m, record, Exposure.From(Outcomes(-1, -1, -1))).EffectiveRiskPercent);

        // A large average gain never loosens the 10% stop.
        Assert.Equal(10m, SizingLimits.From(1, TrackRecord.From(Outcomes(40, 40, 40, 40, 40)), full).MaxStopPercent);
    }

    [Theory]
    [InlineData("0.09")]
    [InlineData("5.01")]
    [InlineData("1.255")]
    [InlineData("-1")]
    public void Settings_reject_risk_outside_the_range(string risk) =>
        Assert.Throws<DomainValidationException>(() => new SizingSettings(Guid.NewGuid(), decimal.Parse(risk, System.Globalization.CultureInfo.InvariantCulture), Start));
}

internal sealed class MemorySizingStore : ISizingStore
{
    public SizingSettings? Settings { get; set; }
    public List<ClosedPlayFacts> Closed { get; } = [];
    public int Saves { get; private set; }
    public Task<SizingSettings?> SettingsAsync(CancellationToken ct) => Task.FromResult(Settings);
    public Task<List<ClosedPlayFacts>> ClosedPlaysAsync(CancellationToken ct) => Task.FromResult(Closed.ToList());
    public List<ImportedOrder> Orders { get; } = [];
    public Task<List<ImportedOrder>> OrdersAsync(IReadOnlyCollection<OrderKey> orders, CancellationToken ct) =>
        Task.FromResult(Orders.Where(o => orders.Contains(OrderKey.Of(o))).ToList());
    public void Add(SizingSettings settings) => Settings = settings;
    public Task SaveAsync(CancellationToken ct) { Saves++; return Task.CompletedTask; }
}

public sealed class SizingServiceTests
{
    private static readonly DateTimeOffset Planned = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
    private readonly Guid owner = Guid.NewGuid();
    private readonly MemorySizingStore store = new();
    private SizingService Service => new(store, new CoreOwner(owner), TimeProvider.System, TestUnitOfWork.None);

    private static PlayPlanDocument Plan(string leverage = "3", params PlanEntry[] entries) => new("long", "margin", "250", leverage, null,
        entries.Length > 0 ? entries : [Entry("entry-1", "100", "100", new PlanExit("stop-1", "price", "95", "100"))],
        new PlanNotes("", "", "", ""));

    private static PlanEntry Entry(string id, string share, string price, params PlanExit[] stops) =>
        new(id, id, "#b9c9e4", share, price, stops, [new PlanExit(id + "-target", "price", "120", "100")]);

    private static ImportedFill Fill(decimal price, decimal quantity, decimal pnl = 0, decimal fee = 0.1m, string token = "USDC", int minutes = 0,
        string pnlBasis = ExecutionFacts.PnlGross, string side = ExecutionFacts.Buy, string effect = ExecutionFacts.Unknown) => new()
    {
        Id = Guid.NewGuid(), Price = price, Quantity = quantity, ClosedPnlUsd = pnl, Fee = fee, FeeToken = token, PnlBasis = pnlBasis,
        OccurredAtUtc = Planned.AddMinutes(30 + minutes), ExternalOrderId = "o", ContractId = "BTC", SourceFillId = "f", Side = side, PositionEffect = effect,
        Direction = "", TransactionHash = "",
    };

    private ClosedPlayFacts Closed(PlayPlanDocument plan, IEnumerable<LinkedFill> fills, PlayPlanDocument? revised = null, int closedDay = 2)
    {
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Venue");
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(plan), Planned);
        List<PlayPlanRevision> revisions = [play.MarkPlanned(Planned).Revision];
        if (revised is not null) revisions.Add(play.ChangePlan(PlayDocuments.Serialize(revised), "Moved stop", Planned.AddHours(2))!);
        play.MarkOpen("Filled.", Planned.AddHours(3));
        play.MarkClosed("Exited.", Planned.AddDays(closedDay));
        return new ClosedPlayFacts(play, revisions, fills.ToList());
    }

    [Fact]
    public void Outcome_is_closed_pnl_less_usdc_fees_with_risk_from_the_plan_at_the_first_entry_fill()
    {
        // Revised two hours after planning; the entry filled 30 minutes after planning, so revision 1 (stop 95) applies.
        var facts = Closed(Plan(), [new(OrderLinkRole.Entry, Fill(100, 2)), new(OrderLinkRole.Target, Fill(110, 2, pnl: 20))],
            revised: Plan(entries: Entry("entry-1", "100", "100", new PlanExit("stop-1", "price", "90", "100"))));
        var outcome = PlayOutcomeCalculator.Outcome(facts)!;
        Assert.Equal((19.8m, 200m, 10m, PlayOutcomeKind.Win, true), (outcome.NetResult, outcome.EntryNotional, outcome.PlannedRisk, outcome.Kind, outcome.FeesComplete));
        Assert.Equal(9.9m, outcome.ReturnPercent);
        Assert.Equal(1.98m, outcome.RMultiple);
    }

    [Fact]
    public void Net_of_fee_closed_pnl_is_not_reduced_by_its_fee_again()
    {
        // RISEx-style: every fill's closed PnL already includes its fee (an opening fill reports -fee).
        var outcome = PlayOutcomeCalculator.Outcome(Closed(Plan(), [
            new(OrderLinkRole.Entry, Fill(100, 2, pnl: -0.1m, fee: 0.1m, pnlBasis: ExecutionFacts.PnlNetOfFee)),
            new(OrderLinkRole.Target, Fill(110, 2, pnl: 19.9m, fee: 0.1m, pnlBasis: ExecutionFacts.PnlNetOfFee))]))!;
        Assert.Equal((19.8m, true, PlayOutcomeKind.Win), (outcome.NetResult, outcome.FeesComplete, outcome.Kind));
    }

    [Fact]
    public void A_net_of_fee_fill_paid_in_another_token_still_counts_as_complete()
    {
        var outcome = PlayOutcomeCalculator.Outcome(Closed(Plan(), [
            new(OrderLinkRole.Entry, Fill(100, 1, pnl: -0.5m, fee: 0.5m, token: "HYPE", pnlBasis: ExecutionFacts.PnlNetOfFee)),
            new(OrderLinkRole.Stop, Fill(95, 1, pnl: -5.1m, pnlBasis: ExecutionFacts.PnlNetOfFee))]))!;
        Assert.Equal((-5.6m, true), (outcome.NetResult, outcome.FeesComplete));
    }

    [Fact]
    public void Fees_in_another_token_are_left_out_and_flagged()
    {
        var outcome = PlayOutcomeCalculator.Outcome(Closed(Plan(), [new(OrderLinkRole.Entry, Fill(100, 1, fee: 0.5m, token: "HYPE")),
            new(OrderLinkRole.Stop, Fill(95, 1, pnl: -5))]))!;
        Assert.Equal((-5.1m, false, PlayOutcomeKind.Loss), (outcome.NetResult, outcome.FeesComplete, outcome.Kind));
        Assert.Equal(-1.02m, outcome.RMultiple);
    }

    [Theory]
    [InlineData("USDG", "USDG")]
    [InlineData("USDC", "USDG")]
    [InlineData("USDT0", "USDe")]
    public void Fees_in_listed_stablecoins_are_deducted_nominally_even_when_mixed(string entryToken, string exitToken)
    {
        var outcome = PlayOutcomeCalculator.Outcome(Closed(Plan(), [new(OrderLinkRole.Entry, Fill(100, 1, fee: 0.5m, token: entryToken)),
            new(OrderLinkRole.Stop, Fill(95, 1, pnl: -5, fee: 0.25m, token: exitToken))]))!;
        Assert.Equal((-5.75m, true, PlayOutcomeKind.Loss), (outcome.NetResult, outcome.FeesComplete, outcome.Kind));
    }

    [Theory]
    [InlineData("usdg")]
    [InlineData("PYUSD")]
    [InlineData("")]
    public void Fees_in_tokens_outside_the_list_are_never_converted(string token)
    {
        var outcome = PlayOutcomeCalculator.Outcome(Closed(Plan(), [new(OrderLinkRole.Entry, Fill(100, 1, fee: 0.5m, token: token)),
            new(OrderLinkRole.Stop, Fill(95, 1, pnl: -5, fee: 0.25m, token: "USDG"))]))!;
        Assert.Equal((-5.25m, false), (outcome.NetResult, outcome.FeesComplete));
    }

    [Fact]
    public void Stop_distance_weights_entries_and_stops_and_resolves_percent_at_leverage()
    {
        // Entry 1: 60% at 100, stops 95 (50%) and 90 (50%) → 7.5 per unit. Entry 2: 40% at 90, a 20% stop at 4× → 5% → 4.5.
        var plan = Plan("4", Entry("a", "60", "100", new PlanExit("s1", "price", "95", "50"), new PlanExit("s2", "price", "90", "50")),
            Entry("b", "40", "90", new PlanExit("s3", "percent", "20", "100")));
        Assert.Equal((0.6m * 7.5m + 0.4m * 4.5m) / (0.6m * 100 + 0.4m * 90), PlayOutcomeCalculator.StopDistance(plan));
        Assert.Null(PlayOutcomeCalculator.StopDistance(Plan(entries: Entry("a", "100", "100"))));
        Assert.Null(PlayOutcomeCalculator.StopDistance(Plan(entries: Entry("a", "100", "", new PlanExit("s", "price", "95", "100")))));
    }

    [Fact]
    public void A_play_without_entry_fills_has_no_outcome()
    {
        Assert.Null(PlayOutcomeCalculator.Outcome(Closed(Plan(), [new(OrderLinkRole.Exit, Fill(100, 1, pnl: 3))])));
    }

    [Fact]
    public async Task Get_uses_the_default_risk_and_reports_the_record_newest_first()
    {
        // A long's stop and target sell. The outcomes close at their exits (an hour apart), not at detection.
        store.Closed.Add(Closed(Plan(), [new(OrderLinkRole.Entry, Fill(100, 1, fee: 0)),
            new(OrderLinkRole.Stop, Fill(95, 1, pnl: -5, fee: 0, minutes: 60, side: ExecutionFacts.Sell))], closedDay: 2));
        store.Closed.Add(Closed(Plan(), [new(OrderLinkRole.Entry, Fill(100, 1, fee: 0)),
            new(OrderLinkRole.Target, Fill(110, 1, pnl: 10, fee: 0, minutes: 120, side: ExecutionFacts.Sell))], closedDay: 3));
        var sizing = await Service.GetAsync(default);
        Assert.Equal("1.25", sizing.Settings.RiskPercent);
        Assert.Equal(("full", "1", "1.25", "10", "2"), (sizing.Exposure.Level, sizing.Exposure.Multiplier, sizing.Limits.EffectiveRiskPercent,
            sizing.Limits.MaxStopPercent, sizing.Limits.MinRewardRisk));
        Assert.Equal(("0.5", "2", "1"), (sizing.Record.BattingAverage, sizing.Record.WinLossRatio, sizing.Record.BreakEvenRewardRisk));
        Assert.Equal(["win", "loss"], sizing.Record.Recent.Select(r => r.Outcome));
        Assert.Equal(("10", "10", "5", "2"), (sizing.Record.Recent[0].NetResultUsd, sizing.Record.Recent[0].ReturnPercent,
            sizing.Record.Recent[0].RiskUsd, sizing.Record.Recent[0].RMultiple));
    }

    [Fact]
    public async Task Closed_records_their_fills_cannot_settle_are_not_scored()
    {
        LinkedFill In(int minutes = 0) => new(OrderLinkRole.Entry, Fill(100, 1, fee: 0, minutes: minutes, effect: ExecutionFacts.Open));
        LinkedFill Out(int minutes = 5, decimal quantity = 1, string side = ExecutionFacts.Sell, string effect = ExecutionFacts.Close) =>
            new(OrderLinkRole.Target, Fill(110, quantity, pnl: 10, fee: 0, minutes: minutes, side: side, effect: effect));
        store.Closed.Add(Closed(Plan(), [In(), Out()]));
        store.Closed.Add(Closed(Plan(), [In(minutes: 10), Out()]));
        store.Closed.Add(Closed(Plan(), [In(), Out(side: ExecutionFacts.Buy, effect: ExecutionFacts.Open)]));
        store.Closed.Add(Closed(Plan(), [In(), Out(effect: ExecutionFacts.Flip)]));
        store.Closed.Add(Closed(Plan(), [In(), Out(quantity: 2)]));
        // The entry order filled 2 at the venue, but only this one execution is retained.
        store.Closed.Add(Closed(Plan(), [In(), Out()]) with { IncompleteOrders = ["o"] });
        var sizing = await Service.GetAsync(default);
        Assert.Equal((1, 1), (sizing.Record.ClosedPlays, sizing.Record.Wins));
    }

    [Fact]
    public async Task Update_saves_the_owners_risk_and_rejects_bad_values()
    {
        var sizing = await Service.UpdateSettingsAsync(new("2"), default);
        Assert.Equal(("2", owner, 1), (sizing.Settings.RiskPercent, store.Settings!.OwnerId, store.Saves));
        Assert.Equal("1.5", (await Service.UpdateSettingsAsync(new("1.50"), default)).Settings.RiskPercent);
        foreach (var bad in new[] { "", "abc", "1e2", "-1", " 1" })
            Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => Service.UpdateSettingsAsync(new(bad), default))).StatusCode);
        foreach (var outside in new[] { "0.05", "6", "1.333" })
            await Assert.ThrowsAsync<DomainValidationException>(() => Service.UpdateSettingsAsync(new(outside), default));
        Assert.Equal(1.5m, store.Settings.RiskPercent);
    }
}

public sealed class SizingPostgresTests
{
    private static VenueOrder Order(string id, string side, decimal price, string status, bool reduceOnly = false) => new(id, "BTC", side, "Limit",
        price, null, reduceOnly, false, 1m, status == "open" ? 1m : 0m, DateTimeOffset.UtcNow, HyperliquidPerpetualReader.Normalize(status), status,
        DateTimeOffset.UtcNow);

    private static VenueFill Fill(string id, string orderId, string dir, decimal price, decimal pnl) =>
        new(id, "BTC", dir.StartsWith("Open") ? ExecutionFacts.Buy : ExecutionFacts.Sell, dir, price, 1m, 0.01m, "USDC", pnl, DateTimeOffset.UtcNow, orderId, "hash-" + id, "{}",
            HyperliquidPerpetualReader.EffectOf(dir));

    [PostgresFact]
    public async Task Record_comes_from_linked_fills_of_closed_plays_on_enabled_accounts_of_the_owner()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var reader = new FixtureReader();
        reader.Result = reader.Result with { Fills = [] };
        var orders = new FakeOrderReader();
        var workspace = new WorkspaceService(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TestVenues.With(reader), TestUnitOfWork.For(db));
        var plays = new PlayService(new PlayStore(db, TestUnitOfWork.For(db)), new MemoryObjectStore(), TimeProvider.System, TestUnitOfWork.For(db));
        var sizing = new SizingService(new SizingStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TimeProvider.System, TestUnitOfWork.For(db));
        var account = await workspace.CreateAccountAsync(new(null, "Venue", "hyperliquid", "0x" + Guid.NewGuid().ToString("N") + "00000000"), default);

        var play = await plays.CreateAsync(new CreatePlayRequest(account.Id, "BTC", "venue", "Breakout", TestPlays.Plan()), default);
        play = await plays.ChangeStatusAsync(play.Summary.Id, new(play.Summary.Version, "planned"), default);
        orders.Orders.AddRange([Order("21", "buy", 100m, "filled"), Order("22", "sell", 110m, "filled", reduceOnly: true)]);
        reader.Result = reader.Result with { Fills = [Fill("g1", "21", "Open Long", 100m, 0), Fill("g2", "22", "Close Long", 110m, 10m)] };
        Assert.Equal("closed", (await MonitorTestPaths.CheckAsync(db, owner, TestVenues.With(reader, orders), play.Summary.Id)).Status);

        var result = await sizing.GetAsync(default);
        var outcome = Assert.Single(result.Record.Recent);
        Assert.Equal(("win", "9.98", "5", "1.996"), (outcome.Outcome, outcome.NetResultUsd, outcome.RiskUsd, outcome.RMultiple));
        Assert.Equal("1", result.Record.BattingAverage);

        Assert.Equal("2.5", (await sizing.UpdateSettingsAsync(new("2.5"), default)).Settings.RiskPercent);
        await using (var again = database.Context(owner))
            Assert.Equal("2.5", (await new SizingService(new SizingStore(again, TestUnitOfWork.For(again)), new CoreOwner(owner), TimeProvider.System, TestUnitOfWork.For(again)).GetAsync(default)).Settings.RiskPercent);

        await using (var other = database.Context(Guid.NewGuid()))
        {
            var foreign = await new SizingService(new SizingStore(other, TestUnitOfWork.For(other)), new CoreOwner(Guid.NewGuid()), TimeProvider.System, TestUnitOfWork.For(other)).GetAsync(default);
            Assert.Equal((0, "1.25"), (foreign.Record.ClosedPlays, foreign.Settings.RiskPercent));
        }

        var stored = await db.Accounts.SingleAsync(a => a.Id == account.Id);
        await workspace.UpdateAccountAsync(account.Id, new(stored.Name, stored.PortfolioId, false, stored.SettingsRevision), default);
        Assert.Equal(0, (await sizing.GetAsync(default)).Record.ClosedPlays);
    }
}
