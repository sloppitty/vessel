using Vessel.Application.Activity;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class FundingTests : FundingTestsValues
{
    private static readonly Guid Owner = Guid.NewGuid(), AccountId = Guid.NewGuid();
    private static ImportedFill Fill(int minute, string side, string effect, string native = "1", decimal pnl = 0) => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = AccountId, SourceFillId = Guid.NewGuid().ToString(),
        ContractId = "BTC", VenueContractId = native, Side = side, PositionEffect = effect, Direction = "fixture",
        Price = 100, Quantity = 1, Fee = 0.1m, FeeToken = "USDC", ClosedPnlUsd = pnl,
        OccurredAtUtc = At.AddMinutes(minute), ExternalOrderId = Guid.NewGuid().ToString(), TransactionHash = ""
    };
    private static FundingPayment Payment(decimal amount, int minute = 5, string native = "1", string asset = "USDC") => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = AccountId, SourceId = Guid.NewGuid().ToString(),
        ContractId = "BTC", VenueContractId = native, Amount = amount, Asset = asset, OccurredAtUtc = At.AddMinutes(minute)
    };
    private static ActivityFacts Facts(IReadOnlyList<FundingPayment> payments, string basis = FundingFacts.Excluded,
        bool complete = true, IReadOnlyList<ImportedFill>? fills = null)
    {
        var account = new Account(AccountId, Owner, "hyperliquid", "Synthetic");
        return new([account], fills ?? [Fill(0, "buy", "open"), Fill(10, "sell", "close", pnl: 10)],
            [new() { OwnerId = Owner, AccountId = AccountId, ObservedAtUtc = At.AddMinutes(20), ValueScope = "fixture" }],
            new Dictionary<Guid, Guid>())
        {
            FundingPayments = payments,
            FundingStates = [new()
            {
                OwnerId = Owner, AccountId = AccountId, CoveredFromUtc = complete ? At.AddDays(-1) : null,
                CoveredToUtc = complete ? At.AddMinutes(20) : null, LastAttemptFromUtc = At.AddDays(-1),
                LastAttemptToUtc = At.AddMinutes(20), LastReadComplete = complete, PnlBasis = basis,
                Notice = complete ? "Synthetic complete funding window." : "Synthetic incomplete funding."
            }]
        };
    }
    private sealed class Store(ActivityFacts facts) : IActivityStore
    {
        public Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null) =>
            Task.FromResult(facts);
        public Task<IReadOnlyDictionary<Guid, string>> PlayTitlesAsync(IReadOnlyCollection<Guid> playIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    }
    private static ActivityService Service(ActivityFacts facts)
    {
        var workspace = new MemoryWorkspaceStore(Owner); workspace.Accounts.AddRange(facts.Accounts);
        return new(new Store(facts), workspace, TimeProvider.System);
    }
    private static async Task<ActivityOperationSummaryDto> Operation(ActivityFacts facts) =>
        Assert.Single((await Service(facts).PageAsync(new(), default)).Operations);

    [Theory]
    [InlineData("excluded", "complete", "9.8", "10.9234567890123456789012345678")]
    [InlineData("included", "complete", "9.8", "9.8")]
    [InlineData("unknown", "complete", "9.8", null)]
    public async Task Exact_funding_uses_verified_basis_and_never_charges_twice(string basis, string status, string trading, string? total)
    {
        var facts = Facts([Payment(1.1234567890123456789012345678m)], basis);
        var operation = await Operation(facts);
        Assert.Equal(trading, operation.NetResultUsd);
        Assert.Equal("1.1234567890123456789012345678", operation.FundingNetUsd);
        Assert.Equal(status, operation.FundingStatus);
        Assert.Equal(total, operation.TotalResultUsd);
        var detail = await Service(facts).FillsAsync(AccountId, operation.Id, default);
        Assert.Equal("1.1234567890123456789012345678", Assert.Single(detail.FundingPayments).Amount);
    }

    [Fact]
    public async Task Known_payments_are_not_zero_or_complete_when_history_is_partial()
    {
        var operation = await Operation(Facts([Payment(-2m)], complete: false));
        Assert.Equal("-2", operation.FundingNetUsd); Assert.Equal("partial", operation.FundingStatus);
        Assert.Null(operation.TotalResultUsd); Assert.Equal("9.8", operation.NetResultUsd);
        var empty = await Operation(Facts([], complete: false));
        Assert.Null(empty.FundingNetUsd); Assert.Null(empty.TotalResultUsd);
    }

    [Fact]
    public async Task Verified_empty_window_is_zero_and_a_gap_never_becomes_complete()
    {
        var facts = Facts([]);
        var operation = await Operation(facts);
        Assert.Equal("0", operation.FundingNetUsd); Assert.Equal("complete", operation.FundingStatus);
        Assert.Equal("9.8", operation.TotalResultUsd);
        facts.FundingStates[0].CoveredFromUtc = At.AddMinutes(1);
        operation = await Operation(facts);
        Assert.Null(operation.FundingNetUsd); Assert.Equal("partial", operation.FundingStatus);
        Assert.Null(operation.TotalResultUsd);
    }

    [Fact]
    public async Task Included_pnl_still_requires_coverage_for_the_combined_total()
    {
        var operation = await Operation(Facts([], FundingFacts.Included, complete: false));
        Assert.Equal("9.8", operation.NetResultUsd); Assert.Null(operation.TotalResultUsd);
    }

    [Fact]
    public async Task Sub_millisecond_snapshot_uses_the_same_whole_ms_refresh_cutoff_for_open_operations()
    {
        var facts = Facts([], fills: [Fill(0, "buy", "open")]);
        facts.Snapshots[0].ObservedAtUtc = At.AddMinutes(20).AddTicks(1234);
        facts.Snapshots[0].Positions = [new() { OwnerId = Owner, AccountId = AccountId, ContractId = "BTC",
            VenueContractId = "1", SignedQuantity = 1, EntryPrice = 100 }];
        var operation = await Operation(facts);
        Assert.Equal("complete", operation.FundingStatus); Assert.Equal("0", operation.FundingNetUsd);
    }

    [Fact]
    public async Task Every_same_time_flip_boundary_is_uncertain_not_only_the_latest_pair()
    {
        var open = Fill(0, "buy", "open");
        var first = Fill(10, "sell", "flip"); first.Quantity = 2;
        var second = Fill(10, "buy", "flip"); second.Quantity = 3;
        var third = Fill(10, "sell", "flip"); third.Quantity = 4;
        var close = Fill(20, "buy", "close"); close.Quantity = 2;
        var page = await Service(Facts([Payment(100, minute: 10)], fills: [open, first, second, third, close])).PageAsync(new(), default);
        Assert.Equal(4, page.Operations.Count);
        Assert.All(page.Operations, o => { Assert.Equal("partial", o.FundingStatus); Assert.Null(o.TotalResultUsd); });
        Assert.Single(page.UnassignedFundingPayments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Funding_after_same_time_flips_uses_the_containing_interval_not_uuid_order(bool reverseIds)
    {
        var open = Fill(0, "buy", "open");
        var first = Fill(10, "sell", "flip"); first.Quantity = 2;
        var second = Fill(10, "buy", "flip"); second.Quantity = 2;
        var low = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var high = Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff");
        first.Id = reverseIds ? low : high;
        second.Id = reverseIds ? high : low;
        var service = Service(Facts([Payment(5, minute: 15)],
            fills: [open, first, second, Fill(20, "sell", "close")]));
        var page = await service.PageAsync(new(), default);
        Assert.Equal(3, page.Operations.Count);
        var final = Assert.Single(page.Operations, o => o.OpenedAtUtc == At.AddMinutes(10) && o.ClosedAtUtc == At.AddMinutes(20));
        Assert.Equal("complete", final.FundingStatus);
        Assert.Equal("5", final.FundingNetUsd);
        Assert.Empty(page.UnassignedFundingPayments);
        Assert.Equal("5", Assert.Single((await service.FillsAsync(AccountId, final.Id, default)).FundingPayments).Amount);
    }

    [Fact]
    public async Task Native_nanoseconds_survive_dto_without_inventing_order_against_millisecond_execution_boundaries()
    {
        var payment = Payment(2, minute: 0);
        payment.OccurredAtUnixNanoseconds = At.ToUnixTimeMilliseconds() * 1_000_000 + 67;
        // RISEx's older execution imports lost their sub-ms timestamp. This could be the very same instant.
        var facts = Facts([payment]);
        var page = await Service(facts).PageAsync(new(), default);
        var operation = Assert.Single(page.Operations);
        Assert.Equal("partial", operation.FundingStatus); Assert.Null(operation.FundingNetUsd); Assert.Null(operation.TotalResultUsd);
        Assert.Equal(payment.OccurredAtUnixNanoseconds!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Assert.Single(page.UnassignedFundingPayments).OccurredAtUnixNanoseconds);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(100, false)]
    public async Task Whole_second_funding_cannot_be_ordered_against_fractional_boundaries_in_that_second(
        int paymentMilliseconds, bool ambiguous)
    {
        var close = Fill(10, "sell", "close", pnl: 10);
        close.OccurredAtUtc = close.OccurredAtUtc.AddMilliseconds(250);
        var open = Fill(10, "sell", "open");
        open.OccurredAtUtc = open.OccurredAtUtc.AddMilliseconds(700);
        var payment = Payment(2, minute: 10);
        payment.OccurredAtUtc = payment.OccurredAtUtc.AddMilliseconds(paymentMilliseconds);
        var service = Service(Facts([payment], fills:
            [Fill(0, "buy", "open"), close, open, Fill(20, "buy", "close")]));
        var page = await service.PageAsync(new(), default);
        Assert.Equal(2, page.Operations.Count);
        Assert.All(page.Operations, operation =>
        {
            Assert.Equal(ambiguous ? "partial" : "complete", operation.FundingStatus);
            if (ambiguous) { Assert.Null(operation.FundingNetUsd); Assert.Null(operation.TotalResultUsd); }
        });
        if (ambiguous)
        {
            Assert.Equal(payment.SourceId, Assert.Single(page.UnassignedFundingPayments).SourceId);
            foreach (var operation in page.Operations)
                Assert.Empty((await service.FillsAsync(AccountId, operation.Id, default)).FundingPayments);
        }
        else
        {
            Assert.Empty(page.UnassignedFundingPayments);
            Assert.Equal("2", Assert.Single(page.Operations, o => o.Direction == "long").FundingNetUsd);
            Assert.Equal("0", Assert.Single(page.Operations, o => o.Direction == "short").FundingNetUsd);
        }
    }

    [Fact]
    public async Task Unique_funding_native_identity_resolves_legacy_fills_but_multiple_candidates_do_not()
    {
        var open = Fill(0, "buy", "open"); open.VenueContractId = null;
        var close = Fill(10, "sell", "close", pnl: 10); close.VenueContractId = null;
        var facts = Facts([Payment(2)], fills: [open, close]);
        var operation = await Operation(facts);
        Assert.Equal("2", operation.FundingNetUsd); Assert.Equal("complete", operation.FundingStatus);
        var ambiguous = Facts([Payment(2), Payment(10, native: "retired")], fills: [open, close]);
        var page = await Service(ambiguous).PageAsync(new(), default);
        Assert.Equal("partial", Assert.Single(page.Operations).FundingStatus);
        Assert.Null(page.Operations[0].FundingNetUsd); Assert.Equal(2, page.UnassignedFundingPayments.Count);
    }

    [Fact]
    public async Task Native_markets_are_never_joined_by_display_asset_or_assigned_outside_boundaries()
    {
        var facts = Facts([Payment(2), Payment(30, native: "retired"), Payment(40, minute: 11),
            Payment(50, minute: -1), Payment(60, minute: 0), Payment(70, minute: 10)]);
        var page = await Service(facts).PageAsync(new(), default);
        var operation = Assert.Single(page.Operations);
        Assert.Equal("2", operation.FundingNetUsd); Assert.Equal(1, operation.FundingPaymentCount);
        Assert.Equal("partial", operation.FundingStatus); Assert.Null(operation.TotalResultUsd);
        Assert.Equal(5, page.UnassignedFundingPayments.Count);
        Assert.Contains(operation.Coverage, c => c.Contains("shares an opening/closing timestamp"));
    }

    [Fact]
    public async Task Flip_boundary_payment_is_never_counted_in_both_operations()
    {
        var opening = Fill(0, "buy", "open");
        var flip = Fill(10, "sell", "flip", pnl: 10); flip.Quantity = 2;
        var closing = Fill(20, "buy", "close", pnl: 4);
        var page = await Service(Facts([Payment(3), Payment(100, minute: 10), Payment(-2, minute: 15)],
            fills: [opening, flip, closing])).PageAsync(new(), default);
        Assert.Equal(2, page.Operations.Count);
        Assert.Equal("3", Assert.Single(page.Operations, o => o.Direction == "long").FundingNetUsd);
        Assert.Equal("-2", Assert.Single(page.Operations, o => o.Direction == "short").FundingNetUsd);
        Assert.All(page.Operations, operation => { Assert.Equal("partial", operation.FundingStatus); Assert.Null(operation.TotalResultUsd); });
        Assert.Equal("100", Assert.Single(page.UnassignedFundingPayments).Amount);
    }

    [Fact]
    public async Task Unsupported_asset_preserves_fact_without_nominal_conversion()
    {
        var facts = Facts([Payment(-1, asset: "BTC")]);
        var operation = await Operation(facts);
        Assert.Null(operation.FundingNetUsd); Assert.Null(operation.TotalResultUsd);
        var detail = await Service(facts).FillsAsync(AccountId, operation.Id, default);
        Assert.Equal("BTC", Assert.Single(detail.FundingPayments).Asset);
    }

    [Fact]
    public async Task Unknown_boundaries_and_disabled_or_foreign_facts_do_not_receive_funding()
    {
        var incomplete = Facts([Payment(2)], fills: [Fill(10, "sell", "close", pnl: 10)]);
        var page = await Service(incomplete).PageAsync(new(), default);
        Assert.Equal("incomplete", Assert.Single(page.Operations).Status);
        Assert.Null(page.Operations[0].TotalResultUsd); Assert.Single(page.UnassignedFundingPayments);
        var disabled = Facts([Payment(2)]);
        disabled.Accounts[0].UpdateSettings("Synthetic", null, false);
        page = await Service(disabled).PageAsync(new(), default);
        Assert.Empty(page.Operations); Assert.Empty(page.UnassignedFundingPayments);
        var foreign = Payment(999); foreign.AccountId = Guid.NewGuid();
        page = await Service(Facts([Payment(2), foreign])).PageAsync(new(), default);
        Assert.Equal("2", Assert.Single(page.Operations).FundingNetUsd);
    }

    [Fact]
    public async Task Funding_and_coverage_change_the_page_version_even_when_only_unassigned()
    {
        var facts = Facts([Payment(2, minute: 11)]);
        var first = await Service(facts).PageAsync(new(), default);
        facts.FundingPayments[0].Amount = 3;
        var second = await Service(facts).PageAsync(new(), default);
        Assert.NotEqual(first.Version, second.Version);
        facts.FundingStates[0].PnlBasis = FundingFacts.Unknown;
        Assert.NotEqual(second.Version, (await Service(facts).PageAsync(new(), default)).Version);
    }

    [Fact]
    public async Task Unassigned_response_is_bounded_and_scope_count_includes_omissions()
    {
        var page = await Service(Facts(Enumerable.Range(0, 150).Select(_ => Payment(1, minute: 11)).ToList()))
            .PageAsync(new(), default);
        Assert.Equal(100, page.UnassignedFundingPayments.Count); Assert.Equal(50, page.OmittedUnassignedFundingPaymentCount);
    }
}
