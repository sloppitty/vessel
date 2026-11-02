using System.Net;
using System.Net.Http.Json;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class AccountManagementApiIntegrationTests
{

    [PostgresFact]
    public async Task Full_PUT_preserves_venue_address_and_value_and_portfolio_delete_unlinks_disabled_account()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var portfolio = new Portfolio(Guid.NewGuid(), owner, "Original");
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Original"); account.Configure(portfolio.Id, "0x1111111111111111111111111111111111111111", null);
        await using (var db = database.Context(owner))
        {
            db.Portfolios.Add(portfolio); db.Accounts.Add(account);
            await db.SaveChangesAsync();
        }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString); using var client = factory.AuthorizedClient();
        var response = await client.PutAsJsonAsync($"/api/accounts/{account.Id}", new { name = "Renamed", portfolioId = portfolio.Id, isEnabled = false, expectedRevision = 1, venueId = "manual", address = "attacker", manualAccountValueUsd = "999", ownerId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var dto = (await response.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.False(dto.IsEnabled); Assert.Equal("hyperliquid", dto.VenueId); Assert.Equal(account.Address, dto.Address); Assert.Null(dto.AccountValueUsd);
        Assert.Equal("null", await client.GetStringAsync($"/api/accounts/{account.Id}/snapshot")); Assert.Equal("[]", await client.GetStringAsync($"/api/accounts/{account.Id}/fills"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/accounts/{account.Id}/sync", null)).StatusCode);
        var renamed = await client.PatchAsJsonAsync($"/api/portfolios/{portfolio.Id}", new { name = "Portfolio renamed" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode); Assert.Equal("Portfolio renamed", (await renamed.Content.ReadFromJsonAsync<PortfolioDto>())!.Name);
        var deletion = await client.DeleteAsync($"/api/portfolios/{portfolio.Id}"); Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode); Assert.Equal("", await deletion.Content.ReadAsStringAsync());
        var after = (await client.GetFromJsonAsync<AccountDto>($"/api/accounts/{account.Id}"))!; Assert.Null(after.PortfolioId); Assert.False(after.IsEnabled);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/accounts/{account.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/accounts/{account.Id}")).StatusCode);
    }
}
