using System.Net;
using System.Net.Http.Json;
using System.Text;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class AccountManagementApiTests
{
    [Theory]
    [InlineData("PATCH", "/api/portfolios/11111111-1111-1111-1111-111111111111")]
    [InlineData("DELETE", "/api/portfolios/11111111-1111-1111-1111-111111111111")]
    [InlineData("PUT", "/api/accounts/11111111-1111-1111-1111-111111111111")]
    [InlineData("DELETE", "/api/accounts/11111111-1111-1111-1111-111111111111")]
    public async Task Management_requires_authentication_before_database(string method, string path)
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid()); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(new(new HttpMethod(method), path))).StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"New\"}")]
    [InlineData("{\"name\":\"New\",\"isEnabled\":true}")]
    [InlineData("{\"name\":\"New\",\"portfolioId\":null}")]
    [InlineData("{\"portfolioId\":null,\"isEnabled\":true}")]
    [InlineData("{\"name\":null,\"portfolioId\":null,\"isEnabled\":true}")]
    [InlineData("{\"name\":\"New\",\"portfolioId\":null,\"isEnabled\":null}")]
    [InlineData("{\"name\":\"New\",\"portfolioId\":\"not-a-guid\",\"isEnabled\":true}")]
    [InlineData("{\"name\":\"New\",\"portfolioId\":null,\"isEnabled\":\"false\"}")]
    [InlineData("{broken")]
    public async Task Full_PUT_rejects_missing_or_invalid_fields_without_mutation(string body)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var portfolio = new Portfolio(Guid.NewGuid(), owner, "Portfolio"); store.Portfolios.Add(portfolio);
        var account = new Account(Guid.NewGuid(), owner, "manual", "Original"); account.Configure(portfolio.Id, null, 123); store.Accounts.Add(account);
        await using var factory = new CoreApiFactory(owner, store); using var client = factory.AuthorizedClient();
        var response = await client.PutAsync($"/api/accounts/{account.Id}", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Original", account.Name); Assert.Equal(portfolio.Id, account.PortfolioId); Assert.True(account.IsEnabled);
        Assert.DoesNotContain("Exception", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":null}")]
    [InlineData("{\"name\":\" \"}")]
    public async Task PATCH_requires_valid_name_without_mutation(string body)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var portfolio = new Portfolio(Guid.NewGuid(), owner, "Original"); store.Portfolios.Add(portfolio);
        await using var factory = new CoreApiFactory(owner, store); using var client = factory.AuthorizedClient();
        var response = await client.PatchAsync($"/api/portfolios/{portfolio.Id}", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal("Original", portfolio.Name);
    }

    [Fact]
    public async Task Portfolio_icon_defaults_is_chosen_on_creation_and_survives_a_rename_that_omits_it()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        await using var factory = new CoreApiFactory(owner, store); using var client = factory.AuthorizedClient();
        var plain = (await (await client.PostAsJsonAsync("/api/portfolios", new { name = "Plain" })).Content.ReadFromJsonAsync<PortfolioDto>())!;
        Assert.Equal("folder", plain.Icon);
        var created = await client.PostAsJsonAsync("/api/portfolios", new { name = "Swing", icon = "rocket" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var swing = (await created.Content.ReadFromJsonAsync<PortfolioDto>())!;
        Assert.Equal("rocket", swing.Icon); Assert.Equal("rocket", store.Portfolios.Single(p => p.Id == swing.Id).Icon);
        var renamed = await client.PatchAsJsonAsync($"/api/portfolios/{swing.Id}", new { name = "Swing renamed" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode); Assert.Equal("rocket", (await renamed.Content.ReadFromJsonAsync<PortfolioDto>())!.Icon);
        var changed = await client.PatchAsJsonAsync($"/api/portfolios/{swing.Id}", new { name = "Swing renamed", icon = "chart-candlestick" });
        Assert.Equal("chart-candlestick", (await changed.Content.ReadFromJsonAsync<PortfolioDto>())!.Icon);
        var listed = (await client.GetFromJsonAsync<PortfolioDto[]>("/api/portfolios"))!;
        Assert.Equal("chart-candlestick", listed.Single(p => p.Id == swing.Id).Icon); Assert.Equal("folder", listed.Single(p => p.Id == plain.Id).Icon);
        Assert.Equal("chart-candlestick", (await client.GetFromJsonAsync<OverviewDto>("/api/overview"))!.Portfolios.Single(p => p.Id == swing.Id).Icon);
    }

    [Theory]
    [InlineData("{\"name\":\"New\",\"icon\":\"skull\"}")]
    [InlineData("{\"name\":\"New\",\"icon\":\"\"}")]
    [InlineData("{\"name\":\"New\",\"icon\":\"Rocket\"}")]
    [InlineData("{\"name\":\"New\",\"icon\":\"<svg onload=alert(1)>\"}")]
    [InlineData("{\"name\":\"New\",\"icon\":7}")]
    [InlineData("{\"name\":\" \",\"icon\":\"rocket\"}")]
    public async Task Unknown_portfolio_icons_are_rejected_without_mutation(string body)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var portfolio = new Portfolio(Guid.NewGuid(), owner, "Original", "gem"); store.Portfolios.Add(portfolio);
        await using var factory = new CoreApiFactory(owner, store); using var client = factory.AuthorizedClient();
        var patch = await client.PatchAsync($"/api/portfolios/{portfolio.Id}", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode); Assert.Equal("Original", portfolio.Name); Assert.Equal("gem", portfolio.Icon);
        var create = await client.PostAsync("/api/portfolios", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode); Assert.Single(store.Portfolios);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Creation_supports_explicit_null_or_omitted_portfolio(bool explicitNull)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        await using var factory = new CoreApiFactory(owner, store); using var client = factory.AuthorizedClient();
        var body = explicitNull ? "{\"name\":\"Account\",\"venueId\":\"manual\",\"portfolioId\":null}" : "{\"name\":\"Account\",\"venueId\":\"manual\"}";
        var response = await client.PostAsync("/api/accounts", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); var dto = (await response.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.True(dto.IsEnabled); Assert.Null(dto.PortfolioId); Assert.Empty(store.Portfolios);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_and_foreign_management_targets_are_safe_404(bool exists)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var portfolio = new Portfolio(Guid.NewGuid(), Guid.NewGuid(), "Foreign"); var account = new Account(Guid.NewGuid(), portfolio.OwnerId, "manual", "Foreign");
        if (exists) { store.Portfolios.Add(portfolio); store.Accounts.Add(account); }
        await using var factory = new CoreApiFactory(owner, store); using var client = factory.AuthorizedClient();
        var responses = new[] {
            await client.PatchAsJsonAsync($"/api/portfolios/{portfolio.Id}", new { name = "New" }),
            await client.DeleteAsync($"/api/portfolios/{portfolio.Id}"),
            await client.PutAsJsonAsync($"/api/accounts/{account.Id}", new UpdateAccountRequest("New", null, false, 1)),
            await client.DeleteAsync($"/api/accounts/{account.Id}") };
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain("Foreign", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal("Foreign", account.Name); Assert.True(account.IsEnabled); Assert.Equal("Foreign", portfolio.Name);
    }

    [Fact]
    public async Task Foreign_destination_PUT_is_404_and_does_not_apply_other_settings()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var foreign = new Portfolio(Guid.NewGuid(), Guid.NewGuid(), "Foreign"); store.Portfolios.Add(foreign);
        var account = new Account(Guid.NewGuid(), owner, "manual", "Original"); store.Accounts.Add(account);
        await using var factory = new CoreApiFactory(owner, store); using var client = factory.AuthorizedClient();
        var response = await client.PutAsJsonAsync($"/api/accounts/{account.Id}", new UpdateAccountRequest("New", foreign.Id, false, 1));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.Null(account.PortfolioId); Assert.True(account.IsEnabled); Assert.Equal("Original", account.Name);
    }
}
