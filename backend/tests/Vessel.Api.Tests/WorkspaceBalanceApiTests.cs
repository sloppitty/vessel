using System.Net.Http.Json;
using Vessel.Application.Workspace;

namespace Vessel.Tests;

public sealed class WorkspaceBalanceApiTests : WorkspaceBalanceTestsSupport
{

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
