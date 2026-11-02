
namespace Vessel.Tests;

/// <summary>Sixth review round for PR #46: every lease-owned run outcome settles discrepancy housekeeping (Sol r5 repros).</summary>
public sealed class MonitorDiscrepancyRound6UseCasesTests : MonitorDiscrepancyRound6TestsSupport
{
    [Fact]
    public async Task Pending_announced_recurrence_can_be_dismissed_without_later_expiry_or_duplicate_close()
    {
        var f = Untracked(); await f.Confirm();
        f.Position = 0; f.Advance(60); await f.Evaluate();
        f.Position = 2; f.Advance(60); await f.Evaluate();
        f.Retired = true; f.Advance((int)TimeSpan.FromDays(7).TotalSeconds + 60); await f.Evaluate();
        f.Retired = false; f.Position = 3; f.Advance(60); await f.Evaluate();
        var row = Assert.Single(f.Store.Rows);
        Assert.NotNull(row.PendingFingerprint); Assert.True(row.PublicOpen);
        await f.Service.DismissAsync(f.Account.Id, null, row.Id, "accepted", default);
        var events = f.Events.Items.Count;
        f.Retired = true; f.Advance((int)TimeSpan.FromDays(7).TotalSeconds + 60);
        await f.Evaluate(); await f.Evaluate();
        Assert.Equal(events, f.Events.Items.Count);
        Assert.False(row.HasWork); Assert.False(row.PublicOpen); Assert.True(row.Visible);
    }
    [Fact]
    public async Task Unverifiable_changed_present_absent_closes_once_and_repeated_expiry_is_silent()
    {
        var f = Untracked(); await f.Evaluate();
        f.Retired = true; f.Advance((int)TimeSpan.FromDays(7).TotalSeconds + 60); await f.Evaluate();
        var events = f.Events.Items.Count;
        await f.Evaluate(); f.Advance(60); await f.Evaluate();
        Assert.Equal(events, f.Events.Items.Count);
        f.Retired = false; f.Position = 2; f.Advance(60); await f.Evaluate();
        f.Position = 0; f.Advance(60); await f.Evaluate();
        var close = Assert.Single(f.Events.Items, x => x.Type == "discrepancy.resolved");
        var revision = Assert.Single(f.Store.Rows).Revision;
        f.Advance(60); await f.Evaluate();
        Assert.Single(f.Events.Items, x => x.Type == "discrepancy.resolved");
        Assert.Equal(revision, Assert.Single(f.Store.Rows).Revision);
    }
}
