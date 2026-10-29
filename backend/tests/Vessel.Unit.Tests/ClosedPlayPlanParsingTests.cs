using System.Globalization;
using Vessel.Application.Plays;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

/// <summary>Plans parsed once per stored document give the same answers as parsing every time.</summary>
public sealed class ClosedPlayPlanParsingTests : ClosedPlayPlanParsingTestsValues
{

    [Theory]
    [InlineData(30, "10")]     // before revision 2: stop 95, risk 200 x 5%
    [InlineData(119, "10")]
    [InlineData(120, "20")]    // exactly at revision 2: stop 90
    [InlineData(300, "20")]    // after the current plan's later change, revision 2 is still the latest stored revision at entry
    public void Initial_risk_uses_the_revision_at_the_first_entry_not_the_current_plan(int entryMinutes, string risk)
    {
        var facts = Facts(entryMinutes);
        Assert.Equal(Json(PlayDocuments.Read(facts.Play.Plan)), Json(facts.CurrentPlan));
        Assert.Equal("85", facts.CurrentPlan.Entries[0].Stops[0].Value);
        var outcome = PlayOutcomeCalculator.Outcome(facts)!;
        Assert.Equal(decimal.Parse(risk, CultureInfo.InvariantCulture), outcome.PlannedRisk);
        // The current plan is still answered correctly after a revision was parsed through the same facts.
        Assert.Equal("85", facts.CurrentPlan.Entries[0].Stops[0].Value);
        Assert.Same(facts.CurrentPlan, facts.CurrentPlan);
        Assert.Equal("95", facts.ParsedPlan(facts.Revisions[0].Plan).Entries[0].Stops[0].Value);
        Assert.Equal("90", facts.ParsedPlan(facts.Revisions[1].Plan).Entries[0].Stops[0].Value);
    }

    [Fact]
    public void Retrospective_records_have_no_initial_risk()
    {
        var outcome = PlayOutcomeCalculator.Outcome(Facts(30, retrospective: true))!;
        Assert.Null(outcome.PlannedRisk);
        Assert.Equal(19.8m, outcome.NetResult);
    }

    [Fact]
    public void Repeated_outcomes_are_identical()
    {
        var facts = Facts(30);
        var first = PlayOutcomeCalculator.Outcome(facts);
        Assert.Equal(first, PlayOutcomeCalculator.Outcome(facts));
        Assert.Equal(first, PlayOutcomeCalculator.Outcome(facts with { }));
    }

    [Fact]
    public void The_parsed_plan_cache_takes_no_part_in_equality_and_never_answers_for_another_play()
    {
        var facts = Facts(30);
        var twin = new ClosedPlayFacts(facts.Play, facts.Revisions, facts.Fills);
        _ = facts.CurrentPlan;
        Assert.Equal(twin, facts);
        Assert.Equal(twin.GetHashCode(), facts.GetHashCode());

        var other = new Play(Guid.NewGuid(), new Account(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Venue"),
            new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue, PlayDocuments.Serialize(Plan("105", "short")), Planned);
        var copy = facts with { Play = other };
        Assert.Equal(("short", "105"), (copy.CurrentPlan.Direction, copy.CurrentPlan.Entries[0].Stops[0].Value));
        Assert.Equal("long", facts.CurrentPlan.Direction);
    }
}
