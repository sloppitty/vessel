using System.Globalization;
using System.Reflection;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Settlement and outcome calculation of one closed Play read its plans through the facts' per-document cache, so
/// each stored plan text is parsed once however many checks consume it.
/// </summary>
public sealed class ClosedPlaySettlementPlanParsingTests
{
    private static readonly DateTimeOffset Planned = DateTimeOffset.Parse("2026-10-01T10:00:00Z", CultureInfo.InvariantCulture);

    private static PlayPlanDocument Plan(string stop, string direction = "long") => new(direction, "margin", "250", "3", null,
        [new PlanEntry("entry-1", "entry-1", "#b9c9e4", "100", "100", [new PlanExit("stop-1", "price", stop, "100")],
            [new PlanExit("t", "price", direction == "long" ? "120" : "80", "100")])], new PlanNotes("", "", "", ""));

    private static ImportedFill Fill(string side, decimal price, decimal pnl, int minutes) => new()
    {
        Id = Guid.NewGuid(), Price = price, Quantity = 2, ClosedPnlUsd = pnl, Fee = 0.1m, FeeToken = "USDC", PnlBasis = ExecutionFacts.PnlGross,
        OccurredAtUtc = Planned.AddMinutes(minutes), ExternalOrderId = "o" + minutes, ContractId = "BTC", SourceFillId = "f" + minutes, Side = side,
        PositionEffect = ExecutionFacts.Unknown, Direction = "", TransactionHash = "",
    };

    /// <summary>A long Play planned with stop 95, revised to 90, then trailed to 85 as its current plan.</summary>
    private static ClosedPlayFacts Facts()
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Venue");
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(Plan("95")), Planned);
        List<PlayPlanRevision> revisions = [play.MarkPlanned(Planned).Revision];
        revisions.Add(play.ChangePlan(PlayDocuments.Serialize(Plan("90")), "Moved stop", Planned.AddHours(2))!);
        play.MarkOpen("Filled.", Planned.AddHours(3));
        play.ChangePlan(PlayDocuments.Serialize(Plan("85")), "Trailed", Planned.AddHours(4));
        play.MarkClosed("Exited.", Planned.AddDays(1));
        return new ClosedPlayFacts(play, revisions,
            [new(OrderLinkRole.Entry, Fill(ExecutionFacts.Buy, 100, 0, 30)), new(OrderLinkRole.Target, Fill(ExecutionFacts.Sell, 110, 20, 630))]);
    }

    /// <summary>The facts' parsed-plan cache, keyed by stored plan text (private; read for this test only).</summary>
    private static Dictionary<string, PlayPlanDocument> Cache(ClosedPlayFacts facts)
    {
        var plans = typeof(ClosedPlayFacts).GetField("plans", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(facts)!;
        return (Dictionary<string, PlayPlanDocument>)plans.GetType().GetField("parsed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(plans)!;
    }

    [Fact]
    public void Settlement_and_outcome_parse_each_stored_plan_once_through_the_facts()
    {
        var facts = Facts();
        Assert.True(ExecutionIntegrity.Settled(facts));
        Assert.NotNull(PlayOutcomeCalculator.Outcome(facts));
        Assert.True(ExecutionIntegrity.Settled(facts));

        // Settlement read the current plan and the outcome the revision at the first entry: one parse each.
        var cache = Cache(facts);
        Assert.Equal(new HashSet<string>([facts.Play.Plan, facts.Revisions[0].Plan]), cache.Keys.ToHashSet());
        Assert.Equal(2, cache.Count);
        Assert.Same(cache[facts.Play.Plan], facts.CurrentPlan);
        Assert.Same(cache[facts.Revisions[0].Plan], facts.ParsedPlan(facts.Revisions[0].Plan));
    }

    [Fact]
    public void Settlement_uses_the_cached_current_plan_document()
    {
        // A cached document is what settlement must consume: a short document cached under the stored long text makes
        // the buy entry contradict, which only shows if settlement does not parse the text again.
        var facts = Facts();
        Assert.True(ExecutionIntegrity.Settled(Facts()));
        Cache(facts)[facts.Play.Plan] = Plan("105", "short");
        Assert.False(ExecutionIntegrity.Settled(facts));
    }
}
