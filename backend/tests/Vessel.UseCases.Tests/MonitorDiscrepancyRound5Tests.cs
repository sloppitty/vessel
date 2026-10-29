
namespace Vessel.Tests;

/// <summary>Fifth review round for PR #46: Sol r4 recovery repros and scheduling fairness.</summary>
public sealed class MonitorDiscrepancyRound5Tests : MonitorDiscrepancyRound5TestsSupport
{
    [Fact]
    public async Task Public_unverifiable_candidate_should_remain_visible_during_reconfirmation()
    {
        var f = Untracked();
        await f.Evaluate();
        f.Retired = true; f.Advance(Lifetime + 60); await f.Evaluate();
        Assert.Equal("unverifiable", Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default)).Outcome);
        f.Retired = false; f.Advance(60); await f.Evaluate();
        Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default));
    }
    [Fact]
    public async Task Public_unverifiable_candidate_should_emit_clearance_after_one_fresh_present_read()
    {
        var f = Untracked();
        await f.Evaluate();
        f.Retired = true; f.Advance(Lifetime + 60); await f.Evaluate();
        f.Retired = false; f.Advance(60); await f.Evaluate();
        f.Position = 0; f.Advance(60); await f.Evaluate();
        Assert.Contains(f.Events.Items, x => x.Type == "discrepancy.resolved");
    }
    [Fact]
    public async Task Dismissed_public_candidate_should_still_be_visible_when_same_evidence_returns()
    {
        var f = Untracked();
        await f.Evaluate();
        f.Retired = true; f.Advance(Lifetime + 60); await f.Evaluate();
        var row = Assert.Single(f.Store.Rows);
        await f.Service.DismissAsync(f.Account.Id, null, row.Id, null, default);
        f.Retired = false; f.Advance(60); await f.Evaluate();
        Assert.Equal("dismissed", row.State);
        Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default));
    }
    [Fact]
    public async Task Unverifiable_resolved_recurrence_should_publish_fresh_clearance()
    {
        var f = Untracked(); await f.Confirm();
        f.Position = 0; f.Advance(60); await f.Evaluate();
        f.Position = 1; f.Advance(60); await f.Evaluate();
        f.Retired = true; f.Advance(Lifetime + 60); await f.Evaluate();
        Assert.Equal("discrepancy.unverifiable", f.Events.Items.Last().Type);
        f.Retired = false; f.Position = 0; f.Advance(60); await f.Evaluate();
        Assert.Equal("discrepancy.resolved", f.Events.Items.Last().Type);
    }

}
