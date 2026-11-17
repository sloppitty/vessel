using Vessel.Application.Venues;

namespace Vessel.Tests;

/// <summary>
/// #94: a history gap or an omitted execution leaves fill quantities unknown only where it reaches, and never hides an
/// Open Play with no resting stop at all while the venue shows its position.
/// </summary>
public sealed class MonitorDiscrepancyGapScopeTests
{
    private static void Gap(DiscrepancyFixture f, string cause)
    {
        if (cause == "history-gap") f.HistoryGap = true;
        else f.Omitted = 1;
    }

    [Theory]
    [InlineData("history-gap")]
    [InlineData("omitted-executions")]
    public async Task A_gap_still_raises_the_missing_stop_critical_after_the_usual_two_observations(string cause)
    {
        var f = new DiscrepancyFixture(); f.VenueOrders.Clear();
        Gap(f, cause);
        var start = f.Clock.Now;
        var first = await f.Evaluate();
        // The gap still degrades the run and leaves the Play's quantity, so its coverage, unknown.
        Assert.True(first.Incomplete);
        Assert.Equal(cause, Assert.Single(first.Coverage!).Reason);
        var row = f.Store.Rows.Single(x => x.Kind == "protection.missing");
        Assert.Equal(("candidate", (DateTimeOffset?)start), (row.State, row.PendingSinceUtc));
        Assert.Empty(f.Events.Items);
        f.Advance(60); await f.Evaluate();
        Assert.Equal(("open", "critical", start), (row.State, row.Severity, row.OpenedAtUtc));
        Assert.Equal(("discrepancy.opened", "critical"), (Assert.Single(f.Events.Items).Type, f.Events.Items[0].Severity));
    }

    [Theory]
    [InlineData("incomplete-book")]
    [InlineData("incomplete-positions")]
    [InlineData("flat")]
    [InlineData("opposite")]
    [InlineData("stale")]
    [InlineData("within-grace")]
    public async Task Under_a_gap_a_missing_stop_stays_unknown_without_a_complete_book_and_an_exposed_position(string reason)
    {
        var f = new DiscrepancyFixture { HistoryGap = true }; f.VenueOrders.Clear();
        switch (reason)
        {
            case "incomplete-book": f.OrdersComplete = false; break;
            case "incomplete-positions": f.PositionsComplete = false; break;
            case "flat": f.Position = 0; break;
            case "opposite": f.Position = -1; break;
            case "stale": f.ObservationOffset = TimeSpan.FromMinutes(-6); break;
            case "within-grace": f.Store.Fills[0].OccurredAtUtc = f.Clock.Now.AddMinutes(-5); break;
        }
        Assert.True((await f.Evaluate()).Incomplete);
        f.Advance(60); await f.Evaluate();
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind is "protection.missing" or "protection.cancelled");
        Assert.Empty(f.Events.Items);
    }

    [Theory]
    [InlineData("incomplete-book")]
    [InlineData("incomplete-positions")]
    [InlineData("flat")]
    [InlineData("stale")]
    public async Task Under_a_gap_an_open_missing_stop_never_resolves_on_incomplete_evidence(string reason)
    {
        var f = new DiscrepancyFixture(); f.VenueOrders.Clear();
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "protection.missing");
        Assert.Equal("open", row.State);
        f.HistoryGap = true;
        switch (reason)
        {
            case "incomplete-book": f.OrdersComplete = false; break;
            case "incomplete-positions": f.PositionsComplete = false; break;
            case "flat": f.Position = 0; break;
            case "stale": f.ObservationOffset = TimeSpan.FromMinutes(-6); break;
        }
        f.Advance(60); await f.Evaluate();
        f.Advance(60); await f.Evaluate();
        Assert.Equal("open", row.State);
        Assert.Single(f.Events.Items);
        // A complete, fresh book with a resting linked stop proves one rests, gap or not; its size stays unknown.
        (f.OrdersComplete, f.PositionsComplete, f.Position, f.ObservationOffset) = (true, true, 1, TimeSpan.Zero);
        ConditionKindsTestSupport.Clear(f, "protection.missing");
        f.Stop(o => o with { RemainingSize = .5m });
        f.Advance(60); await f.Evaluate();
        Assert.Equal("resolved", row.State);
        f.Advance(60); await f.Evaluate();
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind == "protection.short");
    }

    [Fact]
    public async Task Under_a_gap_a_stop_cancelled_without_filling_is_reported_once_as_cancelled()
    {
        var f = new DiscrepancyFixture { HistoryGap = true };
        f.Stop(o => o with { Status = VenueOrderStatus.Canceled, VenueStatus = "canceled" });
        await f.Confirm();
        Assert.Equal("open", f.Store.Rows.Single(x => x.Kind == "protection.cancelled").State);
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind == "protection.missing");
        Assert.Equal(["discrepancy.opened"], f.Events.Items.Select(x => x.Type).ToArray());
    }

    // An entry that keeps filling changes the Play's quantity, so the fingerprint, on every observation. Under a gap the
    // no-stop checks confirm on their condition: the critical still opens after two observations a minute apart.
    [Theory]
    [InlineData("protection.cancelled")]
    [InlineData("protection.missing")]
    public async Task Under_a_gap_an_entry_that_keeps_filling_still_confirms_the_no_stop_critical(string kind)
    {
        var f = new DiscrepancyFixture { HistoryGap = true };
        if (kind == "protection.cancelled") f.Stop(o => o with { Status = VenueOrderStatus.Canceled, VenueStatus = "canceled" });
        else f.VenueOrders.Clear();
        // The entry order is partly filled, so its later fills are not missing executions.
        var entry = f.Store.Orders.Single(x => x.ExternalOrderId == "entry");
        (entry.OriginalSize, entry.Status, entry.VenueStatus) = (5, "open", "open");
        var start = f.Clock.Now;
        await f.Evaluate();
        var row = f.Store.Rows.Single(x => x.Kind == kind);
        var quantities = new List<string?> { Quantity(row) };
        for (var i = 1; i <= 2; i++)
        {
            f.Advance(30);
            f.Fill($"entry-fill-{i}", "entry", .5m, f.Clock.Now);
            await f.Evaluate();
            quantities.Add(Quantity(row));
            // Half a minute is not a confirmation.
            if (i == 1) Assert.Equal(("candidate", (DateTimeOffset?)start), (row.State, row.PendingSinceUtc));
        }
        Assert.Equal(["1", "1.5", "2"], quantities);
        Assert.Equal(("open", "critical", start), (row.State, row.Severity, row.OpenedAtUtc));
        Assert.Equal(["discrepancy.opened"], f.Events.Items.Select(x => x.Type).ToArray());
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind is "protection.missing" or "protection.cancelled" && x.Kind != kind && x.HasWork);

        static string? Quantity(Vessel.Domain.Monitoring.PlayDiscrepancy row) =>
            System.Text.Json.JsonDocument.Parse(row.Details).RootElement.GetProperty("quantity").GetString();
    }

    [Fact]
    public async Task Under_a_gap_the_quantity_comparisons_stay_unknown()
    {
        var f = new DiscrepancyFixture { HistoryGap = true, Position = 2 };
        f.Stop(o => o with { RemainingSize = .5m });
        Assert.True((await f.Evaluate()).Incomplete);
        f.Advance(60); await f.Evaluate();
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind is "protection.short" or "protection.excess" or "position.excess" or "fill.unlinked");
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind == "protection.missing" && x.HasWork);
    }

    // The fixture's Play placed its entry order 25 minutes ago and filled it 20 minutes ago.
    [Theory]
    [InlineData(-60, true)]
    [InlineData(-25, false)]
    [InlineData(-22, false)]
    [InlineData(null, false)]
    public async Task Only_Plays_started_no_later_than_the_gap_end_lose_their_quantity_checks(int? endedMinutesAgo, bool full)
    {
        var f = new DiscrepancyFixture { HistoryGap = true, Position = 2 };
        f.HistoryGapEnd = endedMinutesAgo is { } minutes ? f.Clock.Now.AddMinutes(minutes) : null;
        f.Stop(o => o with { RemainingSize = .5m });
        var result = await f.Evaluate();
        f.Advance(60); await f.Evaluate();
        // The gap still degrades the run whatever it reaches.
        Assert.True(result.Incomplete);
        Assert.Equal(full ? "short" : "history-gap", Assert.Single(result.Coverage!).Reason);
        Assert.Equal(full, f.Store.Rows.Any(x => x.Kind == "protection.short" && x.State == "open"));
        Assert.Equal(full, f.Store.Rows.Any(x => x.Kind == "position.excess" && x.State == "open"));
    }

    [Theory]
    [InlineData("ETH", 1, true)]
    [InlineData("BTC", 1, false)]
    [InlineData(null, 1, false)]
    // One counted omission is not listed, so its contract is unknown.
    [InlineData("ETH", 2, false)]
    public async Task An_omission_reaches_only_its_own_contract(string? contract, int counted, bool full)
    {
        var f = new DiscrepancyFixture { Omitted = counted, Position = 2 };
        f.Omissions.Add(new("trade-1", "forced", "liquidation", contract, contract, null, null));
        f.Stop(o => o with { RemainingSize = .5m });
        var result = await f.Evaluate();
        f.Advance(60); await f.Evaluate();
        Assert.True(result.Incomplete);
        Assert.Equal(full ? "short" : "omitted-executions", Assert.Single(result.Coverage!).Reason);
        Assert.Equal(full, f.Store.Rows.Any(x => x.Kind == "protection.short" && x.State == "open"));
        Assert.Equal(full, f.Store.Rows.Any(x => x.Kind == "position.excess" && x.State == "open"));
    }
}
