using System.Globalization;
using Vessel.Application.Plays;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>Phase 1.1a: each entry beside the plan in force at its first fill, and R by Review's own rule.</summary>
public sealed class PlayReceiptTests
{
    private static readonly DateTimeOffset Planned = DateTimeOffset.Parse("2026-10-01T10:00:00Z", CultureInfo.InvariantCulture);

    private static PlayPlanDocument Plan(string price = "100", string sizing = "margin", string size = "250", string leverage = "3", params PlanEntry[] more) =>
        new("long", sizing, size, leverage, null,
            [new PlanEntry("entry-1", "Entry 1", "#b9c9e4", more.Length == 0 ? "100" : "60", price, [new PlanExit("stop-1", "price", "95", "100")],
                [new PlanExit("target-1", "price", "110", "100")]), .. more], new PlanNotes("", "", "", ""));

    private static ImportedFill Fill(Play play, string side, string effect, decimal price, decimal quantity, DateTimeOffset at, decimal pnl = 0) => new()
    {
        Id = Guid.NewGuid(), OwnerId = play.OwnerId, AccountId = play.AccountId, ContractId = "BTC", Side = side, PositionEffect = effect,
        Direction = "", Price = price, Quantity = quantity, ClosedPnlUsd = pnl, Fee = 0, FeeToken = "USDC", FeeBasis = "reported",
        PnlBasis = ExecutionFacts.PnlGross, OccurredAtUtc = at, ExternalOrderId = "o-" + at.Ticks + side, SourceFillId = "f-" + Guid.NewGuid(),
        TransactionHash = "",
    };

    /// <summary>Planned at 10:00 with the entry at 100; revision 2 at 12:00 moves it to 105.</summary>
    private static (Play Play, List<PlayPlanRevision> Revisions) Revised()
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Venue");
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(Plan()), Planned);
        List<PlayPlanRevision> revisions = [play.MarkPlanned(Planned).Revision];
        revisions.Add(play.ChangePlan(PlayDocuments.Serialize(Plan("105")), "Moved entry", Planned.AddHours(2))!);
        return (play, revisions);
    }

    [Fact]
    public void Planned_values_come_from_the_revision_in_force_at_the_entry_first_fill()
    {
        var (play, revisions) = Revised();
        // Margin 250 at 3x over a 100 entry is 7.5 planned; at 105 it is 750 / 105.
        Assert.Equal(new PlannedEntry("100", "7.5", 1), PlayReceipts.Planned(play, revisions, "entry-1", Planned.AddHours(1)));
        Assert.Equal(new PlannedEntry("105", ExactDecimal.Format(750m / 105m), 2), PlayReceipts.Planned(play, revisions, "entry-1", Planned.AddHours(2)));
        // Before any fill the current plan answers; a fill before the plan was written reads the first revision.
        Assert.Equal(new PlannedEntry("105", ExactDecimal.Format(750m / 105m), 2), PlayReceipts.Planned(play, revisions, "entry-1", null));
        Assert.Equal(new PlannedEntry("100", "7.5", 1), PlayReceipts.Planned(play, revisions, "entry-1", Planned.AddDays(-3)));
        Assert.Equal(new PlannedEntry(null, null, 2), PlayReceipts.Planned(play, revisions, "missing", null));
    }

    [Fact]
    public void Planned_values_read_plan_text_as_the_editor_does()
    {
        // Older stored text the editor reads as unknown stays unknown here, and nothing but plain decimals reaches the browser.
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Venue");
        PlannedEntry Read(string price, string leverage)
        {
            var plan = Plan(price) with { Leverage = leverage };
            var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
                System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), Planned);
            return PlayReceipts.Planned(play, [], "entry-1", null);
        }
        Assert.Equal(new PlannedEntry(null, null, null), Read("100.", "3"));
        Assert.Equal(new PlannedEntry(null, null, null), Read(".5", "3"));
        Assert.Equal(new PlannedEntry(null, null, null), Read("1e2", "3"));
        // More precision than a decimal holds is unknown, never rounded into a planned value.
        Assert.Equal(new PlannedEntry(null, null, null), Read("100.00000000000000000000000000001", "3"));
        Assert.Equal(new PlannedEntry(null, null, null), Read("0.12345678901234567890123456789", "3"));
        Assert.Equal(new PlannedEntry("100", "7.5", null), Read(" 100 ", "3"));
        // Leverage as leverageOf reads it: trimmed whole digits, otherwise 1.
        Assert.Equal("7.5", Read("100", " 3 ").Quantity);
        Assert.Equal("2.5", Read("100", "3.5").Quantity);
        Assert.Equal("2.5", Read("100", "0").Quantity);
    }

    [Theory]
    [InlineData("quantity", "2", "3", "1.2")]          // 2 units × 60% share
    [InlineData("margin", "100", "5", "3.125")]       // 500 notional over the 60/40 average entry of 96, × 60%
    [InlineData("margin", "", "5", null)]             // no size: unknown, not zero
    public void Planned_quantity_follows_the_editor_sizing(string sizing, string size, string leverage, string? expected)
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Venue");
        var second = new PlanEntry("entry-2", "Entry 2", "#b9c9e4", "40", "90", [new PlanExit("stop-2", "price", "85", "100")], []);
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(Plan(sizing: sizing, size: size, leverage: leverage, more: second)), Planned);
        Assert.Equal(expected, PlayReceipts.Planned(play, [], "entry-1", null).Quantity);
    }

    [Fact]
    public void R_is_known_only_for_a_closed_Play_Review_scores_and_says_why_otherwise()
    {
        var (play, revisions) = Revised();
        var entry = Fill(play, ExecutionFacts.Buy, ExecutionFacts.Open, 100, 2, Planned.AddHours(1));
        var exit = Fill(play, ExecutionFacts.Sell, ExecutionFacts.Close, 110, 2, Planned.AddHours(3), pnl: 20);
        play.MarkOpen("Filled.", Planned.AddHours(1));
        var open = PlayReceipts.Build(new ClosedPlayFacts(play, revisions, [new(OrderLinkRole.Entry, entry)]), Planned.AddDays(1));
        Assert.Equal((null, PlayReceipts.OpenReason, entry.OccurredAtUtc, (DateTimeOffset?)null, "100", (string?)null),
            (open.EstimatedR, open.EstimatedRReason, open.FirstEntryAtUtc, open.LastExitAtUtc, open.AverageEntryPrice, open.AverageExitPrice));

        play.MarkClosed("Exited.", Planned.AddHours(4));
        var facts = new ClosedPlayFacts(play, revisions, [new(OrderLinkRole.Entry, entry), new(OrderLinkRole.Target, exit)]);
        var closed = PlayReceipts.Build(facts, Planned.AddDays(1));
        // Risk from revision 1 (stop 95 under 100): 200 notional × 5% = 10; net 20 → 2R, exactly as Review scores it.
        Assert.Equal(("2", "10", (string?)null, "110", 2), (closed.EstimatedR, closed.InitialRisk, closed.EstimatedRReason, closed.AverageExitPrice, closed.FillCount));
        Assert.Equal(exit.OccurredAtUtc, closed.LastExitAtUtc);

        // Excluded by Review: a known missing execution leaves R unknown with Review's reason.
        Assert.Equal(PlayReceipts.ExecutionsReason, PlayReceipts.Build(facts with { IncompleteOrders = ["o-1"] }, Planned.AddDays(1)).EstimatedRReason);
        var unknownFee = Fill(play, ExecutionFacts.Sell, ExecutionFacts.Close, 110, 2, Planned.AddHours(3), pnl: 20);
        unknownFee.Fee = 0.5m; unknownFee.FeeToken = "BTC";
        Assert.Equal(PlayReceipts.FeesReason, PlayReceipts.Build(facts with { Fills = [new(OrderLinkRole.Entry, entry), new(OrderLinkRole.Target, unknownFee)] },
            Planned.AddDays(1)).EstimatedRReason);
        // A plan written after the first entry fill was never in force at it (D-17).
        var early = Fill(play, ExecutionFacts.Buy, ExecutionFacts.Open, 100, 2, Planned.AddHours(-1));
        Assert.Equal(PlayReceipts.PlanAfterFillReason, PlayReceipts.Build(facts with { Fills = [new(OrderLinkRole.Entry, early), new(OrderLinkRole.Target, exit)] },
            Planned.AddDays(1)).EstimatedRReason);
        typeof(Play).GetProperty(nameof(Play.Origin))!.SetValue(play, PlayOrigin.Retrospective);
        Assert.Equal(PlayReceipts.RetrospectiveReason, PlayReceipts.Build(facts, Planned.AddDays(1)).EstimatedRReason);
    }

    [Fact]
    public void A_plan_written_after_the_first_execution_is_marked_from_stored_times_only()
    {
        var (play, revisions) = Revised();
        play.MarkOpen("Filled.", Planned.AddHours(1));
        LinkedFill At(DateTimeOffset at, OrderLinkRole role = OrderLinkRole.Entry) =>
            new(role, Fill(play, role == OrderLinkRole.Entry ? ExecutionFacts.Buy : ExecutionFacts.Sell,
                role == OrderLinkRole.Entry ? ExecutionFacts.Open : ExecutionFacts.Close, 100, 1, at));
        Assert.False(PlayReceipts.PlannedAfterExecution(new ClosedPlayFacts(play, revisions, [At(Planned.AddHours(1))])));
        // Planned exactly at the first fill is not after it; an earlier fill of any role makes it so.
        Assert.False(PlayReceipts.PlannedAfterExecution(new ClosedPlayFacts(play, revisions, [At(Planned)])));
        var facts = new ClosedPlayFacts(play, revisions, [At(Planned.AddHours(1)), At(Planned.AddMinutes(-1), OrderLinkRole.Exit)]);
        Assert.True(PlayReceipts.PlannedAfterExecution(facts));
        Assert.True(PlayReceipts.Build(facts, Planned.AddDays(1)).PlannedAfterExecution);
        Assert.False(PlayReceipts.PlannedAfterExecution(new ClosedPlayFacts(play, revisions, [])));
        // A retrospective Play is recorded after execution by definition and says so by its origin instead.
        typeof(Play).GetProperty(nameof(Play.Origin))!.SetValue(play, PlayOrigin.Retrospective);
        Assert.False(PlayReceipts.PlannedAfterExecution(facts));
    }

    [Fact]
    public void A_plan_without_a_complete_stop_has_no_R()
    {
        var (play, _) = Revised();
        var account = new Account(play.AccountId, play.OwnerId, "hyperliquid", "Venue");
        var stopless = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(Plan() with { Entries = [Plan().Entries[0] with { Stops = [] }] }), Planned);
        var revision = stopless.MarkPlanned(Planned).Revision;
        stopless.MarkOpen("Filled.", Planned.AddHours(1));
        stopless.MarkClosed("Exited.", Planned.AddHours(4));
        var facts = new ClosedPlayFacts(stopless, [revision], [new(OrderLinkRole.Entry, Fill(stopless, ExecutionFacts.Buy, ExecutionFacts.Open, 100, 2, Planned.AddHours(1))),
            new(OrderLinkRole.Target, Fill(stopless, ExecutionFacts.Sell, ExecutionFacts.Close, 110, 2, Planned.AddHours(3), pnl: 20))]);
        var receipt = PlayReceipts.Build(facts, Planned.AddDays(1));
        Assert.Equal((null, null, PlayReceipts.NoStopReason), (receipt.EstimatedR, receipt.InitialRisk, receipt.EstimatedRReason));
    }
}
