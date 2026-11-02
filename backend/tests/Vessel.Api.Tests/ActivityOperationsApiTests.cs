using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Activity;

namespace Vessel.Tests;

public sealed class ActivityOperationsApiTests : ActivityOperationsTestsSupport
{

    [Fact]
    public async Task Activity_endpoint_requires_authentication_before_reading_facts()
    {
        await using var factory = new CoreApiFactory(Owner); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/activity")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/activity/fills?accountId={AccountId}&operationId=x")).StatusCode);
    }

    [Fact]
    public async Task Activity_endpoints_have_exact_page_and_fill_payloads_with_no_store()
    {
        var facts = new ActivityFacts([Account(AccountId)], [Fill(0), Fill(1, "sell", "close", pnl: 0.0000000000000000000000000001m)], [], new Dictionary<Guid, Guid>());
        await using var factory = new CoreApiFactory(Owner, store: Workspace(facts), configure: services => services.AddSingleton<IActivityStore>(new Store(facts)));
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync($"/api/activity?accountId={AccountId}&scope=all&status=closed&period=custom&from=2026-10-12&to=2026-10-12&page=1&pageSize=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["operations", "page", "pageSize", "totalCount", "scopeCount", "retainedFillCount", "asOfUtc", "version", "facets", "coverage", "scopeNote",
                "unassignedFundingPayments", "omittedUnassignedFundingPaymentCount"],
            json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["accountIds", "contracts"], json.GetProperty("facets").EnumerateObject().Select(p => p.Name));
        var row = Assert.Single(json.GetProperty("operations").EnumerateArray());
        // Paper provenance is optional and paper-only: the default real wire remains byte-contract compatible.
        Assert.False(json.TryGetProperty("tradingMode", out _)); Assert.False(json.TryGetProperty("paperExecutionCount", out _));
        Assert.False(row.TryGetProperty("tradingMode", out _)); Assert.False(row.TryGetProperty("paperExecutionCount", out _));
        Assert.False(row.TryGetProperty("fills", out _)); Assert.False(row.TryGetProperty("omittedFillCount", out _));
        Assert.Equal("0.0000000000000000000000000001", row.GetProperty("netResultUsd").GetString());
        Assert.Equal("0.0000000000000000000000000001", row.GetProperty("grossClosedPnlUsd").GetString());
        Assert.DoesNotContain("500 operations", json.GetProperty("scopeNote").GetString());
        var fills = await client.GetAsync($"/api/activity/fills?accountId={AccountId}&operationId={row.GetProperty("id").GetString()}");
        Assert.Equal(HttpStatusCode.OK, fills.StatusCode); Assert.True(fills.Headers.CacheControl?.NoStore);
        var expanded = await fills.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["operationId", "fills", "omittedFillCount", "fundingPayments", "omittedFundingPaymentCount"], expanded.EnumerateObject().Select(p => p.Name));
        Assert.Equal(2, expanded.GetProperty("fills").GetArrayLength());
        Assert.False(expanded.TryGetProperty("paperExecutions", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/activity/fills?accountId={AccountId}&operationId=gone")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/activity/fills?operationId=gone")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/activity?accountId={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/activity?accountId=not-a-guid")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/activity?pageSize=500")).StatusCode);
    }
}
