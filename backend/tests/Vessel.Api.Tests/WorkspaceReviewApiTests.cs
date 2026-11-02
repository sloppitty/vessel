using System.Net;
using System.Net.Http.Json;
using System.Text;
using Vessel.Application.Workspace;

namespace Vessel.Tests;

public sealed class WorkspaceReviewApiTests
{
    [Fact]
    public async Task Accepted_extreme_values_do_not_break_any_read_route()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        await using var factory = new CoreApiFactory(owner, store); using var client = factory.AuthorizedClient();
        var portfolioResponse = await client.PostAsJsonAsync("/api/portfolios", new { name = "Exact" });
        var portfolio = (await portfolioResponse.Content.ReadFromJsonAsync<PortfolioDto>())!;
        Guid id = default;
        foreach (var value in new[] { "79228162514264337593543950335", "1" })
        {
            var response = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest(portfolio.Id, value, "manual", ManualAccountValueUsd: value));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode); id = (await response.Content.ReadFromJsonAsync<AccountDto>())!.Id;
        }
        var overview = (await client.GetFromJsonAsync<OverviewDto>("/api/overview"))!;
        Assert.Equal("79228162514264337593543950336", overview.Totals.TotalAccountValueUsd);
        Assert.Equal(2, (await client.GetFromJsonAsync<AccountDto[]>("/api/accounts"))!.Length);
        Assert.Equal("79228162514264337593543950336", (await client.GetFromJsonAsync<PortfolioDto[]>("/api/portfolios"))!.Single().TotalValueUsd);
        Assert.Equal("79228162514264337593543950336", (await client.GetFromJsonAsync<PortfolioDto>($"/api/portfolios/{portfolio.Id}"))!.TotalValueUsd);
        Assert.NotNull(await client.GetFromJsonAsync<AccountDto>($"/api/accounts/{id}"));
        Assert.Equal("null", await client.GetStringAsync($"/api/accounts/{id}/snapshot"));
    }

    [Theory]
    [InlineData("{\"name\":\"New\",\"portfolioId\":null,\"isEnabled\":true}", 400)]
    [InlineData("{\"portfolioId\":null,\"isEnabled\":true,\"expectedRevision\":1}", 400)]
    [InlineData("{\"name\":\"New\",\"isEnabled\":true,\"expectedRevision\":1}", 400)]
    [InlineData("{\"name\":\"New\",\"portfolioId\":null,\"expectedRevision\":1}", 400)]
    [InlineData("{\"name\":null,\"portfolioId\":null,\"isEnabled\":true,\"expectedRevision\":1}", 400)]
    [InlineData("{\"name\":\"New\",\"portfolioId\":null,\"isEnabled\":true,\"expectedRevision\":null}", 400)]
    [InlineData("{\"name\":\"New\",\"portfolioId\":null,\"isEnabled\":true,\"expectedRevision\":1.5}", 400)]
    [InlineData("{\"name\":\"New\",\"portfolioId\":null,\"isEnabled\":true,\"expectedRevision\":0}", 409)]
    [InlineData("{\"name\":\"New\",\"portfolioId\":null,\"isEnabled\":true,\"expectedRevision\":-1}", 409)]
    [InlineData("{\"name\":\"New\",\"portfolioId\":null,\"isEnabled\":true,\"expectedRevision\":9223372036854775807}", 409)]
    public async Task PUT_requires_valid_matching_revision_without_mutation(string body, int status)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(new FixtureReader()), TestUnitOfWork.None);
        var account = await service.CreateAccountAsync(new(null, "Original", "manual"), default);
        await using var factory = new CoreApiFactory(owner, store); using var client = factory.AuthorizedClient();
        var response = await client.PutAsync($"/api/accounts/{account.Id}", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(status, (int)response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var current = (await client.GetFromJsonAsync<AccountDto>($"/api/accounts/{account.Id}"))!;
        Assert.Equal(1, current.SettingsRevision); Assert.Equal("Original", current.Name);
    }

    [Fact]
    public async Task Stale_rename_does_not_reenable_and_an_explicit_reload_allows_save()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(new FixtureReader()), TestUnitOfWork.None);
        var account = await service.CreateAccountAsync(new(null, "Original", "manual"), default);
        await using var factory = new CoreApiFactory(owner, store); using var client = factory.AuthorizedClient();
        var disable = await client.PutAsJsonAsync($"/api/accounts/{account.Id}", new UpdateAccountRequest("Original", null, false, 1));
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode); Assert.Equal(2, (await disable.Content.ReadFromJsonAsync<AccountDto>())!.SettingsRevision);
        var stale = await client.PutAsJsonAsync($"/api/accounts/{account.Id}", new UpdateAccountRequest("Stale rename", null, true, 1));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); Assert.DoesNotContain("Original", await stale.Content.ReadAsStringAsync());
        var reload = (await client.GetFromJsonAsync<AccountDto>($"/api/accounts/{account.Id}"))!;
        Assert.False(reload.IsEnabled); Assert.Equal("Original", reload.Name);
        var save = await client.PutAsJsonAsync($"/api/accounts/{account.Id}", new UpdateAccountRequest("Explicit rename", reload.PortfolioId, reload.IsEnabled, reload.SettingsRevision));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode); var final = (await save.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.False(final.IsEnabled); Assert.Equal("Explicit rename", final.Name); Assert.Equal(3, final.SettingsRevision);
    }

    [Fact]
    public async Task Duplicate_source_has_safe_conflict_even_if_original_is_disabled()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(new FixtureReader()), TestUnitOfWork.None);
        var account = await service.CreateAccountAsync(new(null, "Private name", "hyperliquid", "0x" + new string('a', 40)), default);
        await service.UpdateAccountAsync(account.Id, new("Private name", null, false, 1), default);
        await using var factory = new CoreApiFactory(owner, store); using var client = factory.AuthorizedClient();
        var response = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest(null, "Alias", "hyperliquid", "0x" + new string('A', 40)));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(); Assert.Contains("re-enable", body); Assert.DoesNotContain("Private name", body); Assert.DoesNotContain(account.Address!, body);
        Assert.Single(store.Accounts);
    }
    [Fact]
    public async Task Account_and_portfolio_collection_reads_do_not_depend_on_overview_activity_queries()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(new FixtureReader()), TestUnitOfWork.None);
        var portfolio = await service.CreatePortfolioAsync(new("Independent"), default);
        var account = await service.CreateAccountAsync(new(portfolio.Id, "Manual", "manual"), default);
        store.RejectActivityReads = true;
        await using var factory = new CoreApiFactory(owner, store); using var client = factory.AuthorizedClient();
        Assert.Single((await client.GetFromJsonAsync<AccountDto[]>("/api/accounts"))!);
        Assert.Single((await client.GetFromJsonAsync<PortfolioDto[]>("/api/portfolios"))!);
        Assert.Equal(account.Id, (await client.GetFromJsonAsync<AccountDto>($"/api/accounts/{account.Id}"))!.Id);
        Assert.Equal(portfolio.Id, (await client.GetFromJsonAsync<PortfolioDto>($"/api/portfolios/{portfolio.Id}"))!.Id);
        Assert.Equal("null", await client.GetStringAsync($"/api/accounts/{account.Id}/snapshot"));
    }

}
