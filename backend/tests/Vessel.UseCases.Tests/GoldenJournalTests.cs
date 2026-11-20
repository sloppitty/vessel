using Vessel.Domain.Common;
namespace Vessel.Tests;

/// <summary>
/// Issue #88: the golden journal through every screen, without a database. The figures and their arithmetic are in
/// docs/architecture/golden-journal.md; <see cref="GoldenJournalChecks"/> holds them. Tests skipped here assert the value
/// the document says is right where a known open defect makes the code disagree; each names its issue.
/// </summary>
public sealed class GoldenJournalTests
{
    private readonly GoldenJournal j = new();
    private GoldenJournalScreens Screens => GoldenJournalScreens.InMemory(j);

    [Fact]
    public Task Activity_shows_every_operation_with_its_hand_computed_figures() => GoldenJournalChecks.ActivityAsync(j, Screens);

    [Fact]
    public Task Overview_shows_balances_unrealized_and_the_net_accounts_realized_result() => GoldenJournalChecks.OverviewAsync(j, Screens);

    [Fact]
    public Task Review_scores_the_plays_with_average_r_a_short_and_a_scratch_between_two_wins() => GoldenJournalChecks.ReviewPlaysAsync(j, Screens);

    [Fact]
    public Task Review_with_positions_adds_the_flipped_operations_and_omits_the_unknown_net() => GoldenJournalChecks.ReviewPositionsAsync(j, Screens);

    [Fact]
    public Task Sizing_builds_its_record_from_the_settled_plays_and_discloses_the_tie() => GoldenJournalChecks.SizingAsync(j, Screens);

    [Fact]
    public Task Open_risk_sums_the_planned_long_and_the_half_filled_short_apart() => GoldenJournalChecks.OpenRiskAsync(j, Screens);

    [Fact]
    public Task Identities_hold_across_the_screens() => GoldenJournalChecks.IdentitiesAsync(j, Screens);

    /// <summary>Section 2.3: the average-cost basis of what is still open, 110, gives 10 × (130 − 110).</summary>
    [Fact]
    public async Task Avax_unrealized_uses_the_cost_of_what_is_still_open()
    {
        var page = await Screens.Activity.PageAsync(new(PageSize: "100"), default);
        var avax = page.Operations.Single(o => o.Id == j.Operation("a15", "long"));
        Assert.Equal(("200", "mark"), (avax.UnrealizedPnlUsd, avax.UnrealizedPnlSource));
        // The mark path and the venue's own snapshot figure then agree on the same position.
        var reported = j.Snapshots.Single(s => s.AccountId == j.Gross.Id).Positions.Single(p => p.ContractId == "AVAX").UnrealizedPnlUsd;
        Assert.Equal(GoldenJournalChecks.ExactSum([avax.UnrealizedPnlUsd]), Vessel.Domain.Numerics.ExactAmount.From(reported));
    }

    /// <summary>Section 7.2: Overview leaves out P6, whose entry order misses an execution, as Review and Sizing do.</summary>
    [Fact]
    public Task Overview_realized_equals_activity_closed_nets_minus_known_missing_executions() =>
        GoldenJournalChecks.OverviewRealizedAsync(j, Screens);

    /// <summary>Section 7.4: SOL-S closes exactly on D(7); the rolling-period rule decides whether it is in, but once for all.</summary>
    [Fact(Skip = "#125: Activity and Overview count SOL-S, which closed exactly on the 7-day boundary; Review's week leaves it out.")]
    public async Task The_seven_day_period_selects_the_same_operations_on_every_screen()
    {
        var s = Screens;
        var activity = await s.Activity.PageAsync(new(Period: "7", Status: "closed", PageSize: "100"), default);
        var overview = (await s.Overview.GetAsync("portfolio", "7", "all", TradingMode.Real, default)).Totals.RealizedResult;
        var review = await s.Review.GetAsync(new(Period: "week", Positions: "include"), default);
        var reviewed = review.Metrics.Count + review.Coverage.IncompleteExecutions + review.Positions!.Omitted!.Value;
        Assert.Equal(activity.TotalCount, overview.Coverage.ContributingOperationCount + overview.Coverage.ExcludedOperationCount);
        Assert.Equal(activity.TotalCount, reviewed);
        Assert.Equal(overview.AmountUsd, review.Metrics.Net);
    }

    /// <summary>
    /// Section 2.3: the snapshot path counts the venue's figure only for the position it describes. With one current snapshot
    /// and unambiguous markets, Activity anchors its walk to the snapshot, so the quantities agree by construction; the guards
    /// matter when a fill carries no native market ID and the snapshot or other fills name more than one. Unknown is never zero.
    /// </summary>
    [Theory]
    [InlineData("as recorded", "-0.2")]
    [InlineData("snapshot before the last execution", null)]
    [InlineData("two native markets, fill without an ID", null)]
    [InlineData("other quantity, fill without an ID", null)]
    [InlineData("two native markets, fill with its ID", "-0.2")]
    public async Task Snapshot_unrealized_counts_only_for_the_position_it_describes(string variant, string? expected)
    {
        var snapshot = j.Snapshots.Single(s => s.AccountId == j.Net.Id);
        var position = snapshot.Positions.Single();
        Vessel.Domain.Workspace.AccountPosition Other(decimal quantity, decimal unrealized) => new()
        {
            OwnerId = position.OwnerId, AccountId = position.AccountId, ContractId = "XRP", VenueContractId = "XRP-2",
            SignedQuantity = quantity, EntryPrice = 0.5m, UnrealizedPnlUsd = unrealized,
        };
        switch (variant)
        {
            case "snapshot before the last execution":
                snapshot.ObservedAtUtc = GoldenJournal.D(2);
                break;
            case "two native markets, fill without an ID":
                position.VenueContractId = "XRP-1";
                snapshot.Positions.Add(Other(-1, -7));
                break;
            case "other quantity, fill without an ID":
                // A retired market's closed round trip makes b6's market ambiguous, so the snapshot cannot anchor it.
                (position.VenueContractId, position.SignedQuantity) = ("XRP-1", -3);
                foreach (var (name, side, effect) in new[] { ("x1", "buy", "open"), ("x2", "sell", "close") })
                    j.Fills.Add(new Vessel.Domain.Workspace.ImportedFill
                    {
                        Id = Guid.NewGuid(), OwnerId = position.OwnerId, AccountId = j.Net.Id, ContractId = "XRP", VenueContractId = "XRP-2",
                        SourceFillId = name, ExternalOrderId = name, TransactionHash = "", Side = side, Direction = "Golden journal",
                        PositionEffect = effect, Quantity = 1, Price = 0.4m, FeeToken = "USDC", PnlBasis = "net-of-fee",
                        OccurredAtUtc = GoldenJournal.D(20).AddHours(effect == "open" ? 0 : 1),
                    });
                break;
            case "two native markets, fill with its ID":
                (j.Fill["b6"].VenueContractId, position.VenueContractId) = ("XRP-1", "XRP-1");
                snapshot.Positions.Add(Other(-1, -7));
                break;
        }
        var page = await Screens.Activity.PageAsync(new(PageSize: "100"), default);
        var xrp = page.Operations.Single(o => o.Id == j.Operation("b6", "short"));
        Assert.Equal(("open", "4"), (xrp.Status, xrp.OpenQuantity));
        Assert.Equal((expected, expected is null ? null : "snapshot"), (xrp.UnrealizedPnlUsd, xrp.UnrealizedPnlSource));
    }
}
