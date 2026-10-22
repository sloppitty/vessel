using Vessel.Application.Venues;
using System.Net.Http.Json;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class WorkspaceBalanceTests
{
    private readonly Guid owner = Guid.NewGuid();
    private readonly MemoryWorkspaceStore store;
    private readonly WorkspaceService service;

    public WorkspaceBalanceTests()
    {
        store = new(owner);
        var nonWallet = new VenueDescriptor("public-perps", "Public perps", "read-only", VenueSources.EvmAddress,
            VenueCapabilities.None with { Sync = true });
        service = new(store, new CoreOwner(owner), new VenueRegistry([VenueDescriptor.Manual, Vessel.Infrastructure.Venues.Hyperliquid.HyperliquidPerpetualReader.Descriptor, nonWallet],
                [], [], [], [], [], [], []), TestUnitOfWork.None);
    }

    private Account Add(Guid? portfolio, string venue, decimal? equity, decimal? withdrawable = null,
        string? mode = null, decimal? walletTotal = null, decimal? walletAvailable = null, bool enabled = true, decimal? availableMargin = null)
    {
        var account = new Account(Guid.NewGuid(), owner, venue, "Fixture account");
        account.Configure(portfolio, null, venue == "manual" ? equity : null);
        if (!enabled) account.UpdateSettings(account.Name, portfolio, false);
        store.Accounts.Add(account);
        if (venue != "manual")
            store.Snapshots.Add(new AccountSnapshot
            {
                OwnerId = owner, AccountId = account.Id, ObservedAtUtc = DateTimeOffset.UtcNow, ValueScope = "fixture",
                AccountValueUsd = equity, WithdrawableUsd = withdrawable, AvailableMarginUsd = availableMargin, AccountMode = mode,
                StablecoinsObservedAtUtc = walletTotal.HasValue ? DateTimeOffset.UtcNow : null,
                Stablecoins = walletTotal is { } total
                    ? [new() { Total = total, Held = total - (walletAvailable ?? total), Available = walletAvailable ?? total }]
                    : [],
            });
        return account;
    }

    [Fact]
    public async Task Availability_aggregates_keep_all_digits_beyond_decimal_sum_precision()
    {
        var portfolio = await service.CreatePortfolioAsync(new("Exact availability"), default);
        var large = Add(portfolio.Id, "public-perps", 0m, decimal.MaxValue);
        var tiny = Add(portfolio.Id, "public-perps", 0m, availableMargin: 0.0000000000000000000000000001m);
        const string exact = "79228162514264337593543950335.0000000000000000000000000001";
        Assert.Equal(exact, (await service.OverviewAsync(default)).Totals.AvailableBalanceUsd);
        var funds = await service.PortfolioAsync(portfolio.Id, default);
        Assert.Equal(exact, funds.AvailableBalanceUsd);
        Assert.Equal((2, 2, "complete"), (funds.AvailableBalanceAccountCount, funds.EnabledAccountCount, funds.AvailableBalanceCoverage));
        Assert.Equal("79228162514264337593543950335", (await service.AccountAsync(large.Id, default)).AvailableBalanceUsd);
        Assert.Equal("0.0000000000000000000000000001", (await service.AccountAsync(tiny.Id, default)).AvailableBalanceUsd);
    }

    [Fact]
    public async Task Available_margin_precedes_withdrawal_and_each_account_contributes_once_to_overview_portfolio_and_detail()
    {
        var portfolio = await service.CreatePortfolioAsync(new("Available"), default);
        var lighter = Add(portfolio.Id, "public-perps", 100m, 50m, availableMargin: 12.12345678901234567890123456m);
        var risex = Add(portfolio.Id, "public-perps", 80m, availableMargin: 3m);
        var standard = Add(portfolio.Id, "hyperliquid", 40m, 35m, "default", 10m, 7m);
        var unified = Add(null, "hyperliquid", 999m, 888m, "unifiedAccount", 100m, 80m, availableMargin: 777m);
        var zero = Add(portfolio.Id, "public-perps", 100m, 50m, availableMargin: 0m);
        Add(portfolio.Id, "public-perps", 100m, 50m, enabled: false, availableMargin: 1000m);
        Add(portfolio.Id, "manual", 100m);
        Add(null, "public-perps", 100m); // Known equity is not availability.
        var totals = (await service.OverviewAsync(default)).Totals;
        Assert.Equal("130.12345678901234567890123456", totals.AvailableBalanceUsd);
        Assert.Equal(5, totals.AvailableBalanceAccountCount);
        Assert.Equal(7, totals.EnabledAccountCount);
        Assert.Equal("partial", totals.AvailableBalanceCoverage);
        var funds = await service.PortfolioAsync(portfolio.Id, default);
        Assert.Equal("50.12345678901234567890123456", funds.AvailableBalanceUsd);
        Assert.Equal((4, 5, "partial"), (funds.AvailableBalanceAccountCount, funds.EnabledAccountCount, funds.AvailableBalanceCoverage));
        foreach (var account in new[] { lighter, risex, zero })
            Assert.Equal("margin", (await service.AccountAsync(account.Id, default)).AvailableBalanceSource);
        Assert.Equal("0", (await service.AccountAsync(zero.Id, default)).AvailableBalanceUsd);
        Assert.Equal(("35", "withdrawable"), ((await service.AccountAsync(standard.Id, default)).AvailableBalanceUsd, (await service.AccountAsync(standard.Id, default)).AvailableBalanceSource));
        Assert.Equal(("80", "wallet"), ((await service.AccountAsync(unified.Id, default)).AvailableBalanceUsd, (await service.AccountAsync(unified.Id, default)).AvailableBalanceSource));
        Assert.Equal("12.12345678901234567890123456", (await service.SnapshotAsync(lighter.Id, default))!.AvailableMarginUsd);
        await service.UpdateAccountAsync(lighter.Id, new(lighter.Name, null, false, lighter.SettingsRevision), default);
        Assert.Null((await service.AccountAsync(lighter.Id, default)).AvailableBalanceUsd);
        Assert.Null((await service.AccountAsync(lighter.Id, default)).AvailableBalanceSource);
        Assert.Equal("118", (await service.OverviewAsync(default)).Totals.AvailableBalanceUsd);
        Assert.Equal("38", (await service.PortfolioAsync(portfolio.Id, default)).AvailableBalanceUsd);
    }

    [Fact]
    public async Task Overview_combines_enabled_balances_across_portfolios_and_unassigned_without_counting_ledgers_twice()
    {
        var first = await service.CreatePortfolioAsync(new("First"), default);
        var second = await service.CreatePortfolioAsync(new("Second"), default);
        Add(first.Id, "hyperliquid", 999m, 888m, "unifiedAccount", 100m, 80m);
        Add(second.Id, "hyperliquid", 40m, 35m, "default", 10m, 7m);
        Add(null, "manual", 25m);
        Add(second.Id, "public-perps", 30m, 20m);
        Add(null, "public-perps", null); // Lighter-like unknown equity/withdrawal: collateral is not equity.
        Add(null, "hyperliquid", 500m, 400m, "portfolioMargin", 200m, 150m, enabled: false);
        Add(null, "manual", 1000m, enabled: false);

        var totals = (await service.OverviewAsync(default)).Totals;
        Assert.Equal(7, totals.AccountCount);
        Assert.Equal(5, totals.EnabledAccountCount);
        Assert.Equal("205", totals.TotalBalanceUsd);
        Assert.Equal(4, totals.BalanceAccountCount);
        Assert.Equal("partial", totals.BalanceCoverage);
        // Unified uses wallet only; standard uses withdrawal, not the separate spot wallet.
        Assert.Equal("135", totals.AvailableBalanceUsd);
        Assert.Equal(3, totals.AvailableBalanceAccountCount);
        Assert.Equal("partial", totals.AvailableBalanceCoverage);
        Assert.Equal("87", totals.AvailableStablecoinNominalUsd);
        Assert.Equal(2, totals.StablecoinAccountCount);
    }

    [Fact]
    public async Task Moving_disabling_and_reenabling_a_unified_account_recomputes_portfolio_balance_and_counts()
    {
        var first = await service.CreatePortfolioAsync(new("First"), default);
        var second = await service.CreatePortfolioAsync(new("Second"), default);
        var account = Add(first.Id, "hyperliquid", 0m, 0m, "unifiedAccount", 750m, 600m);
        await service.UpdateAccountAsync(account.Id, new(account.Name, second.Id, true, account.SettingsRevision), default);
        var portfolios = await service.PortfoliosAsync(default);
        Assert.Equal((0, (string?)null), (portfolios[0].AccountCount, portfolios[0].BalanceUsd));
        Assert.Equal((1, "750", "complete"), (portfolios[1].AccountCount, portfolios[1].BalanceUsd, portfolios[1].BalanceCoverage));
        Assert.Equal("0", portfolios[1].TotalValueUsd);
        await service.UpdateAccountAsync(account.Id, new(account.Name, second.Id, false, account.SettingsRevision), default);
        Assert.Null((await service.PortfolioAsync(second.Id, default)).BalanceUsd);
        Assert.Equal(1, (await service.PortfolioAsync(second.Id, default)).AccountCount);
        Assert.Null((await service.OverviewAsync(default)).Totals.TotalBalanceUsd);
        await service.UpdateAccountAsync(account.Id, new(account.Name, null, true, account.SettingsRevision), default);
        Assert.Equal("750", (await service.OverviewAsync(default)).Totals.TotalBalanceUsd);
        Assert.Equal(0, (await service.PortfolioAsync(second.Id, default)).AccountCount);
    }

    [Fact]
    public async Task Balance_sums_are_exact_even_when_a_single_account_exceeds_decimal_precision()
    {
        var portfolio = await service.CreatePortfolioAsync(new("Exact"), default);
        var account = Add(portfolio.Id, "hyperliquid", decimal.MaxValue, null, "default",
            0.0000000000000000000000000001m, 0.0000000000000000000000000001m);
        const string expected = "79228162514264337593543950335.0000000000000000000000000001";
        Assert.Equal(expected, (await service.AccountAsync(account.Id, default)).BalanceUsd);
        Assert.Equal(expected, (await service.PortfolioAsync(portfolio.Id, default)).BalanceUsd);
        Assert.Equal(expected, (await service.OverviewAsync(default)).Totals.TotalBalanceUsd);
    }

    [Fact]
    public async Task A_separate_ledger_wallet_read_before_the_perps_leaves_the_balance_unknown()
    {
        // Phase 2, item 14b, Sol round 1: never perps plus a wallet that may hold funds since moved into them.
        var stale = Add(null, "hyperliquid", 40m, 35m, "default", 10m, 7m);
        var snapshot = store.Snapshots.Single(s => s.AccountId == stale.Id);
        snapshot.StablecoinsObservedAtUtc = snapshot.ObservedAtUtc.AddMinutes(-10);
        var unified = Add(null, "hyperliquid", 999m, 888m, "unifiedAccount", 100m, 80m);
        store.Snapshots.Single(s => s.AccountId == unified.Id).StablecoinsObservedAtUtc = snapshot.ObservedAtUtc.AddMinutes(-10);
        var current = Add(null, "hyperliquid", 40m, 35m, "default", 10m, 7m);
        Assert.Null((await service.AccountAsync(stale.Id, default)).BalanceUsd);
        Assert.Equal("35", (await service.AccountAsync(stale.Id, default)).AvailableBalanceUsd);
        Assert.Equal("100", (await service.AccountAsync(unified.Id, default)).BalanceUsd); // The wallet is its only ledger.
        Assert.Equal("50", (await service.AccountAsync(current.Id, default)).BalanceUsd);
    }

    [Fact]
    public async Task Unknown_and_empty_observed_wallets_are_distinct_and_manual_value_is_not_available_funds()
    {
        Assert.Null((await service.OverviewAsync(default)).Totals.AvailableBalanceUsd);
        Add(null, "hyperliquid", 100m, 70m, "unifiedAccount");
        Add(null, "manual", 0m);
        var unknown = (await service.OverviewAsync(default)).Totals;
        Assert.Equal("0", unknown.TotalBalanceUsd);
        Assert.Equal(1, unknown.BalanceAccountCount);
        Assert.Null(unknown.AvailableBalanceUsd);
        Assert.Equal(0, unknown.AvailableBalanceAccountCount);
        var empty = Add(null, "hyperliquid", 999m, 500m, "portfolioMargin", 0m);
        store.Snapshots.Single(s => s.AccountId == empty.Id).Stablecoins.Clear();
        var observed = (await service.OverviewAsync(default)).Totals;
        Assert.Equal("0", observed.AvailableBalanceUsd);
        Assert.Equal(1, observed.AvailableBalanceAccountCount);
        Assert.Equal("partial", observed.AvailableBalanceCoverage);
    }

    [Fact]
    public async Task Non_wallet_withdrawal_can_be_negative_and_coverage_follows_enabled_accounts()
    {
        Add(null, "public-perps", 20m, -2.0000000000000000000000000001m);
        var totals = (await service.OverviewAsync(default)).Totals;
        Assert.Equal("-2.0000000000000000000000000001", totals.AvailableBalanceUsd);
        Assert.Equal("complete", totals.AvailableBalanceCoverage);
        Assert.Equal("complete", totals.BalanceCoverage);
    }

    [Fact]
    public async Task Overview_is_uncompressed_no_store_and_rereads_after_a_settings_change()
    {
        var portfolio = await service.CreatePortfolioAsync(new("Live grouping"), default);
        var account = Add(portfolio.Id, "hyperliquid", 0m, 0m, "unifiedAccount", 750m, 600m);
        await using var factory = new CoreApiFactory(owner, store);
        using var client = factory.AuthorizedClient();
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, br");
        using var first = await client.GetAsync("/api/overview");
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        Assert.Empty(first.Content.Headers.ContentEncoding);
        var before = (await first.Content.ReadFromJsonAsync<OverviewDto>())!;
        Assert.Equal("750", before.Totals.TotalBalanceUsd);
        Assert.Equal("600", before.Totals.AvailableBalanceUsd);
        using var changed = await client.PutAsJsonAsync($"/api/accounts/{account.Id}",
            new UpdateAccountRequest(account.Name, null, false, account.SettingsRevision));
        changed.EnsureSuccessStatusCode();
        var after = (await client.GetFromJsonAsync<OverviewDto>("/api/overview"))!;
        Assert.Null(after.Totals.TotalBalanceUsd);
        Assert.Null(after.Totals.AvailableBalanceUsd);
        Assert.Equal(0, after.Portfolios.Single().AccountCount);
        Assert.Null(after.Portfolios.Single().BalanceUsd);
    }
}
