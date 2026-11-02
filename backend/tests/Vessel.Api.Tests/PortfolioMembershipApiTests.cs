using System.Net;
using System.Net.Http.Json;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class PortfolioMembershipApiTests
{
    [Theory]
    [InlineData("/api/accounts/portfolio")]
    [InlineData("/api/portfolios")]
    public async Task Requires_authentication(string path)
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid()); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path, new { name = "Test", accountIds = new[] { Guid.NewGuid() }, portfolioId = (Guid?)null })).StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"accountIds\":[]}")]
    [InlineData("{\"portfolioId\":null}")]
    [InlineData("{\"accountIds\":null,\"portfolioId\":null}")]
    [InlineData("{\"accountIds\":[],\"portfolioId\":null}")]
    public async Task Rejects_missing_null_or_empty_ids_and_requires_explicit_target(string json)
    {
        var owner = Guid.NewGuid(); await using var factory = new CoreApiFactory(owner, new MemoryWorkspaceStore(owner)); using var client = factory.AuthorizedClient();
        var response = await client.PostAsync("/api/accounts/portfolio", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Guarded_requests_return_conflict_without_moving_or_creating()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, "manual", "Account"); store.Accounts.Add(account);
        account.UpdateSettings("Current", null, true);
        await using var factory = new CoreApiFactory(owner, store); using var client = factory.AuthorizedClient();
        var expected = new[] { new AccountSettingsExpectation(account.Id, 1) };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/accounts/portfolio",
            new MoveAccountsRequest([account.Id], null, expected))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/portfolios",
            new CreatePortfolioRequest("Rejected", AccountIds: [account.Id], Expected: expected))).StatusCode);
        Assert.Null(account.PortfolioId); Assert.Equal(2, account.SettingsRevision); Assert.Empty(store.Portfolios);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"accountId\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"}")]
    [InlineData("{\"settingsRevision\":1}")]
    [InlineData("{\"accountId\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"settingsRevision\":0}")]
    [InlineData("{\"accountId\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"settingsRevision\":1.5}")]
    [InlineData("null")]
    public async Task Rejects_malformed_expected_items_on_both_routes(string item)
    {
        var owner = Guid.NewGuid(); await using var factory = new CoreApiFactory(owner, new MemoryWorkspaceStore(owner)); using var client = factory.AuthorizedClient();
        foreach (var path in new[] { "/api/accounts/portfolio", "/api/portfolios" })
        {
            var json = "{\"name\":\"Rejected\",\"accountIds\":[\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"],\"portfolioId\":null,\"expected\":[" + item + "]}";
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path,
                new StringContent(json, System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        }
    }

    [Fact]
    public async Task Move_and_create_endpoints_return_updated_resources()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, "manual", "Account"); store.Accounts.Add(account);
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Target"); store.Portfolios.Add(portfolio);
        await using var factory = new CoreApiFactory(owner, store); using var client = factory.AuthorizedClient();
        var moved = await client.PostAsJsonAsync("/api/accounts/portfolio", new MoveAccountsRequest([account.Id], portfolio.Id));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode); Assert.Equal(portfolio.Id, (await moved.Content.ReadFromJsonAsync<List<AccountDto>>())!.Single().PortfolioId);
        var created = await client.PostAsJsonAsync("/api/portfolios", new CreatePortfolioRequest("New", AccountIds: [account.Id]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var result = (await created.Content.ReadFromJsonAsync<PortfolioDto>())!;
        Assert.Equal(1, result.AccountCount); Assert.Equal(result.Id, account.PortfolioId); Assert.Equal(3, account.SettingsRevision);
    }
}
