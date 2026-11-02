using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Workspace;

namespace Vessel.Tests;

public sealed class CoreApiIntegrationTests : IClassFixture<CoreApiRejectionHosts>
{
    public CoreApiIntegrationTests(CoreApiRejectionHosts hosts) { _ = hosts; }

    [PostgresFact]
    public async Task Manual_account_shape_null_snapshot_and_owner_cannot_be_supplied_by_body()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString); using var client = factory.AuthorizedClient();
        var response = await client.PostAsJsonAsync("/api/portfolios", new { name = "Swing", ownerId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var portfolio = (await response.Content.ReadFromJsonAsync<PortfolioDto>())!;
        var accountResponse = await client.PostAsJsonAsync("/api/accounts", new
        { portfolioId = portfolio.Id, name = "Manual", venueId = "manual", manualAccountValueUsd = "123.12345678901234567890123456", ownerId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, accountResponse.StatusCode);
        var account = (await accountResponse.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.Equal("123.12345678901234567890123456", account.AccountValueUsd);
        await using (var db = database.Context(owner))
        {
            Assert.Equal(owner, (await db.Accounts.IgnoreQueryFilters().SingleAsync()).OwnerId);
            Assert.Equal(owner, (await db.Portfolios.IgnoreQueryFilters().SingleAsync()).OwnerId);
        }
        using var json = JsonDocument.Parse(await accountResponse.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "id", "portfolioId", "name", "venueId", "address", "accountValueUsd", "lastSyncedAtUtc", "syncStatus", "lastSyncError", "positionCount", "historyNotice", "isEnabled", "settingsRevision", "availableStablecoinNominalUsd", "stablecoinScope", "accountMode", "totalStablecoinNominalUsd", "balanceUsd", "sourceId", "availableBalanceUsd", "availableBalanceSource", "tradingMode" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("real", json.RootElement.GetProperty("tradingMode").GetString());
        Assert.False(json.RootElement.TryGetProperty("startingCapitalUsd", out _));
        Assert.Null(account.SourceId);
        Assert.Null(account.AvailableStablecoinNominalUsd);
        Assert.Null(account.AvailableBalanceUsd);
        Assert.Null(account.AvailableBalanceSource);
        Assert.Equal("null", await client.GetStringAsync($"/api/accounts/{account.Id}/snapshot"));
        var sync = await client.PostAsync($"/api/accounts/{account.Id}/sync", null);
        Assert.Equal(HttpStatusCode.BadRequest, sync.StatusCode); Assert.Equal("application/problem+json", sync.Content.Headers.ContentType?.MediaType);
    }
}
