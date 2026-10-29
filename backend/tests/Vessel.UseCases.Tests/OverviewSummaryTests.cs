using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Common;
using Vessel.Application.Overview;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class OverviewSummaryTests : OverviewSummaryTestsSupport
{
    private Portfolio Portfolio(string name = "Swing")
    {
        var p = new Portfolio(Guid.NewGuid(), owner, name, "wallet"); store.Portfolios.Add(p); return p;
    }
    private Account Account(Guid? portfolio = null, decimal? balance = null, bool enabled = true, string venue = "manual")
    {
        var a = new Account(Guid.NewGuid(), owner, venue, "Account " + store.Accounts.Count);
        a.Configure(portfolio, null, balance); if (!enabled) a.UpdateSettings(a.Name, portfolio, false);
        store.Accounts.Add(a); return a;
    }
    private void Snapshot(Account a, decimal? balance, decimal? available = null, decimal pnl = 0, decimal quantity = 0)
    {
        store.Snapshots.Add(new AccountSnapshot { OwnerId = owner, AccountId = a.Id, ObservedAtUtc = Now, ValueScope = "fixture",
            AccountValueUsd = balance, AvailableMarginUsd = available,
            Positions = quantity == 0 ? [] : [new() { OwnerId = owner, AccountId = a.Id, ContractId = "BTC", SignedQuantity = quantity, EntryPrice = 100, UnrealizedPnlUsd = pnl }] });
    }
    private void Operation(Account a, DateTimeOffset close, decimal pnl, decimal fee = 0, string feeToken = "USDC", bool incomplete = false, string basis = "gross")
    {
        var n = store.Fills.Count;
        ImportedFill Fill(string side, string effect, DateTimeOffset at, decimal result, decimal cost) => new()
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = a.Id, ContractId = "contract-" + n, Side = side, PositionEffect = effect,
            Direction = "Display only", Quantity = 1, Price = 100, ClosedPnlUsd = result, Fee = cost, FeeToken = feeToken,
            PnlBasis = basis, OccurredAtUtc = at, ExternalOrderId = "order-" + n + side, SourceFillId = "fill-" + n + side, TransactionHash = ""
        };
        if (!incomplete) store.Fills.Add(Fill("buy", "open", close.AddMinutes(-1), 0, 0));
        store.Fills.Add(Fill("sell", "close", close, pnl, fee));
    }

    [Fact]
    public async Task Groups_sort_by_exact_balance_with_unassigned_and_nulls_last_and_exclude_disabled()
    {
        var p = Portfolio(); var low = Portfolio("Unknown");
        Account(p.Id, 10); Account(p.Id, 20); Account(null, 50); Account(low.Id); Account(null, 999, false);
        var result = await service.GetAsync(null, null, null, default);
        Assert.Equal("portfolio", result.GroupBy); Assert.Equal("30", result.Period); Assert.Equal("all", result.Scope);
        Assert.Equal(4, result.Totals.AccountCount); Assert.Equal("80", result.Totals.Balance.AmountUsd);
        Assert.Equal(["unassigned", $"portfolio:{p.Id}", $"portfolio:{low.Id}"], result.Rows.Select(r => r.Id));
        Assert.Equal("wallet", result.Rows[1].Icon); Assert.Equal(2, result.Rows[1].Totals.AccountCount);
        Assert.Equal(new BalanceShare("30", "80"), result.Rows[1].ShareOfBalance); Assert.Null(result.Rows[2].ShareOfBalance);
        Assert.Equal((3, 4), (result.Totals.Balance.Coverage.ContributingAccountCount, result.Totals.Balance.Coverage.TotalAccountCount));
        Assert.Single(result.Totals.Balance.Coverage.Reasons);
        var byAccount = await service.GetAsync("account", "all", "all", default);
        Assert.Equal(4, byAccount.Rows.Count); Assert.All(byAccount.Rows, r => { Assert.Null(r.Icon); Assert.Equal("manual", r.VenueId); });
    }

    [Fact]
    public async Task Reported_zero_balance_and_zero_realized_result_are_known_but_zero_denominator_has_no_share()
    {
        var a = Account(balance: 0); Operation(a, Now.AddHours(-1), 0);
        var result = await service.GetAsync("account", "30", "all", default);
        Assert.Equal("0", result.Totals.Balance.AmountUsd); Assert.Equal("0", result.Totals.RealizedResult.AmountUsd);
        Assert.Equal(1, result.Totals.RealizedResult.Coverage.ContributingAccountCount);
        Assert.Null(result.Rows.Single().ShareOfBalance);
    }

    [Fact]
    public async Task Empty_groups_and_disabled_unassigned_are_not_rows_and_no_values_are_invented()
    {
        Portfolio(); Account(null, 100, false);
        var result = await service.GetAsync("portfolio", "7", "all", default);
        Assert.Empty(result.Rows); Assert.Equal(0, result.Totals.AccountCount); Assert.Null(result.Totals.Balance.AmountUsd);
        Assert.Null(result.Totals.RealizedResult.AmountUsd); Assert.Null(result.Totals.UnrealizedPnl.AmountUsd);
    }

    [Fact]
    public async Task Scope_filters_portfolio_unassigned_account_and_disabled_account()
    {
        var p = Portfolio(); var a = Account(p.Id, 10); Account(p.Id, 20); Account(null, 50); var disabled = Account(null, 999, false);
        Assert.Equal("30", (await service.GetAsync("account", "30", $"portfolio:{p.Id}", default)).Totals.Balance.AmountUsd);
        Assert.Equal("50", (await service.GetAsync("portfolio", "30", "unassigned", default)).Totals.Balance.AmountUsd);
        Assert.Equal("10", (await service.GetAsync("portfolio", "30", $"account:{a.Id}", default)).Totals.Balance.AmountUsd);
        Assert.Equal(0, (await service.GetAsync("portfolio", "30", $"account:{disabled.Id}", default)).Totals.AccountCount);
    }

    [Fact]
    public async Task Scope_passes_only_resolved_enabled_accounts_and_empty_scopes_do_not_read_activity()
    {
        var p = Portfolio(); var empty = Portfolio("Empty");
        var a = Account(p.Id); var b = Account(p.Id); var disabled = Account(p.Id, enabled: false); var unassigned = Account();
        await service.GetAsync("portfolio", "30", $"portfolio:{p.Id}", default);
        Assert.True(new HashSet<Guid> { a.Id, b.Id }.SetEquals(Assert.Single(activityStore.Reads)!));
        await service.GetAsync("account", "7", "unassigned", default);
        Assert.True(new HashSet<Guid> { unassigned.Id }.SetEquals(activityStore.Reads[1]!));
        await service.GetAsync("portfolio", "90", $"account:{a.Id}", default);
        Assert.True(new HashSet<Guid> { a.Id }.SetEquals(activityStore.Reads[2]!));
        await service.GetAsync("account", "all", "all", default);
        Assert.True(new HashSet<Guid> { a.Id, b.Id, unassigned.Id }.SetEquals(activityStore.Reads[3]!));
        await service.GetAsync("portfolio", "30", $"portfolio:{empty.Id}", default);
        await service.GetAsync("account", "30", $"account:{disabled.Id}", default);
        unassigned.UpdateSettings(unassigned.Name, null, false);
        await service.GetAsync("portfolio", "30", "unassigned", default);
        Assert.Equal(4, activityStore.Reads.Count);
    }

    [Fact]
    public async Task Restricted_summary_matches_unrestricted_per_account_totals_without_truncating_history()
    {
        var p = Portfolio(); var a = Account(p.Id, 10, venue: "hyperliquid"); var b = Account(p.Id, 20);
        var other = Account(balance: 100);
        Snapshot(a, 10, 5, pnl: 2, quantity: 1);
        for (var i = 0; i < 510; i++) Operation(a, Now.AddMinutes(-i), 1);
        Operation(b, Now.AddHours(-1), 5, 1);
        Operation(other, Now.AddHours(-1), 100);
        var all = await service.GetAsync("account", "all", "all", default);
        var restricted = await service.GetAsync("account", "all", $"portfolio:{p.Id}", default);
        foreach (var row in restricted.Rows)
            Assert.Equal(JsonSerializer.Serialize(all.Rows.Single(r => r.Id == row.Id).Totals), JsonSerializer.Serialize(row.Totals));
        Assert.Equal(1022, restricted.Totals.ImportedFillCount);
        Assert.Equal("514", restricted.Totals.RealizedResult.AmountUsd);
    }

    [Theory]
    [InlineData("bad", "30", "all")]
    [InlineData("", "30", "all")]
    [InlineData("portfolio", "0", "all")]
    [InlineData("portfolio", "030", "all")]
    [InlineData("portfolio", "30", "portfolio:00000000-0000-0000-0000-000000000000")]
    [InlineData("portfolio", "30", "account:no")]
    [InlineData("portfolio", "30", "portfolio:{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}")]
    [InlineData("portfolio", "30", "All")]
    [InlineData("portfolio", "30", "")]
    public async Task Invalid_parameters_are_400(string group, string period, string scope) =>
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.GetAsync(group, period, scope, default))).StatusCode);

    [Theory]
    [InlineData("portfolio")]
    [InlineData("account")]
    public async Task Missing_and_foreign_scopes_are_404(string kind)
    {
        var foreign = Guid.NewGuid();
        store.Portfolios.Add(new Portfolio(foreign, Guid.NewGuid(), "Foreign"));
        store.Accounts.Add(new Account(foreign, Guid.NewGuid(), "manual", "Foreign"));
        foreach (var id in new[] { foreign, Guid.NewGuid() })
            Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.GetAsync("account", "all", $"{kind}:{id}", default))).StatusCode);
    }

    [Theory]
    [InlineData("7", 7)]
    [InlineData("30", 30)]
    [InlineData("90", 90)]
    public async Task Rolling_window_uses_exit_time_and_inclusive_utc_boundaries(string period, int days)
    {
        var a = Account(balance: 10);
        Operation(a, Now.AddDays(-days), 1); Operation(a, Now, 2);
        Operation(a, Now.AddDays(-days).AddTicks(-1), 50); Operation(a, Now.AddTicks(1), 100);
        var result = await service.GetAsync("account", period, "all", default);
        Assert.Equal(Now.AddDays(-days), result.FromUtc); Assert.Equal(Now, result.AsOfUtc);
        Assert.Equal("3", result.Totals.RealizedResult.AmountUsd); Assert.Equal(2, result.Totals.RealizedResult.Coverage.ContributingOperationCount);
        Assert.Equal("53", (await service.GetAsync("account", "all", "all", default)).Totals.RealizedResult.AmountUsd);
    }

    [Fact]
    public async Task Unknown_fees_incomplete_operations_and_accounts_without_results_have_explicit_coverage()
    {
        var a = Account(balance: 10); var unknown = Account(); var incomplete = Account(); Account();
        Operation(a, Now.AddHours(-1), 10, 1); Operation(a, Now.AddHours(-2), 5, 1, "ETH", basis: "net-of-fee");
        Operation(unknown, Now.AddHours(-1), 100, 1, "ETH"); Operation(incomplete, Now.AddHours(-1), 100, incomplete: true);
        var realized = (await service.GetAsync("account", "30", "all", default)).Totals.RealizedResult;
        Assert.Equal("14", realized.AmountUsd); Assert.Equal((1, 4, 2, 2), (realized.Coverage.ContributingAccountCount,
            realized.Coverage.TotalAccountCount, realized.Coverage.ContributingOperationCount, realized.Coverage.ExcludedOperationCount));
        Assert.Contains(realized.Coverage.Reasons, r => r.Reason.Contains("Incomplete") && r.Count == 1);
        Assert.Contains(realized.Coverage.Reasons, r => r.Reason.Contains("net result") && r.Count == 1);
    }

    [Fact]
    public async Task Listed_stablecoin_fees_pool_nominally_across_venues_with_the_caveat_visible()
    {
        var usdc = Account(); var usdg = Account(); var lower = Account();
        Operation(usdc, Now.AddHours(-1), 10, 1); Operation(usdg, Now.AddHours(-1), 5, 0.5m, "USDG");
        Operation(lower, Now.AddHours(-1), 100, 1, "usdg");
        var result = await service.GetAsync("account", "30", "all", default);
        Assert.Equal("13.5", result.Totals.RealizedResult.AmountUsd);
        Assert.Equal((2, 1), (result.Totals.RealizedResult.Coverage.ContributingOperationCount, result.Totals.RealizedResult.Coverage.ExcludedOperationCount));
        Assert.Contains(NominalStablecoins.Caveat, result.ScopeNote);
    }

    [Fact]
    public async Task Snapshot_pnl_counts_only_open_positions_and_preserves_missing_snapshots()
    {
        var a = Account(venue: "hyperliquid"); Snapshot(a, 100, -2, -1.0000000000000000000000000001m, 2);
        var b = Account(venue: "hyperliquid"); Snapshot(b, 200, 150); Account(balance: 10);
        var result = await service.GetAsync("account", "30", "all", default);
        Assert.Equal(1, result.Totals.OpenPositionCount); Assert.Equal("-1.0000000000000000000000000001", result.Totals.UnrealizedPnl.AmountUsd);
        Assert.Equal(2, result.Totals.UnrealizedPnl.Coverage.ContributingAccountCount);
        Assert.Equal("148", result.Totals.Available.AmountUsd); Assert.Equal(2, result.Totals.Available.Coverage.ContributingAccountCount);
    }

    [Fact]
    public async Task Exact_sums_and_ratio_support_amounts_beyond_decimal_range()
    {
        var a = Account(balance: decimal.MaxValue); Account(balance: 0.0000000000000000000000000001m);
        Operation(a, Now.AddHours(-1), decimal.MaxValue); Operation(a, Now.AddHours(-2), 0.0000000000000000000000000001m);
        const string exact = "79228162514264337593543950335.0000000000000000000000000001";
        var result = await service.GetAsync("account", "all", "all", default);
        Assert.Equal(exact, result.Totals.Balance.AmountUsd); Assert.Equal(exact, result.Totals.RealizedResult.AmountUsd);
        Assert.Equal(exact, result.Rows[0].ShareOfBalance!.Denominator);
    }

    [Fact]
    public async Task Unified_wallet_avoids_double_counting_and_uses_wallet_availability()
    {
        var a = Account(venue: "hyperliquid"); Snapshot(a, 1000, 800);
        var s = store.Snapshots.Single(); s.AccountMode = "unifiedAccount"; s.StablecoinsObservedAtUtc = Now;
        s.Stablecoins.Add(new() { OwnerId = owner, AccountId = a.Id, Total = 10, Available = 8 });
        var result = await service.GetAsync("account", "30", "all", default);
        Assert.Equal("10", result.Totals.Balance.AmountUsd); Assert.Equal("8", result.Totals.Available.AmountUsd);
    }

    [Fact]
    public async Task Summary_totals_include_operations_and_groups_beyond_display_limits()
    {
        var a = Account(balance: 10);
        for (var i = 0; i < 510; i++) Operation(a, Now.AddMinutes(-i), 1);
        for (var i = 0; i < 205; i++) Account(balance: 1);
        var result = await service.GetAsync("account", "30", "all", default);
        Assert.Equal("510", result.Totals.RealizedResult.AmountUsd); Assert.Equal(1020, result.Totals.ImportedFillCount);
        Assert.Equal(200, result.Rows.Count); Assert.Equal(206, result.TotalGroupCount); Assert.Equal(6, result.OmittedGroupCount);
        Assert.Equal("215", result.Totals.Balance.AmountUsd);
    }
}
