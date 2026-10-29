using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Activity;
using Vessel.Application.Review;
using Vessel.Application.Sizing;

namespace Vessel.Tests;

public sealed class PaperReportingApiTests : PaperReportingTestsSupport
{

    [Fact]
    public async Task Financial_endpoints_serialize_typed_paper_provenance_and_reject_invalid_modes_or_paper_setting_writes()
    {
        var paper = Paper(owner);
        var workspace = new MemoryWorkspaceStore(owner); workspace.Accounts.Add(paper.Account); workspace.Portfolios.Add(paper.Portfolio);
        var sizing = new MemorySizingStore(); sizing.Closed.Add(paper.Facts);
        var review = new ReviewFixture(); review.Accounts.Add(paper.Account); review.Portfolios.Add(paper.Portfolio); review.Closed.Add(paper.Facts);
        await using var factory = new CoreApiFactory(owner, store: workspace, configure: services =>
        {
            services.AddSingleton<IActivityStore>(new ActivityRows(Facts(paper)));
            services.AddSingleton<ISizingStore>(sizing); services.AddSingleton<IReviewStore>(review);
            services.AddSingleton<TimeProvider>(new Clock());
        });
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/review?tradingMode=paper")).StatusCode);
        using var client = factory.AuthorizedClient();
        var pageResponse = await client.GetAsync("/api/activity?tradingMode=paper");
        pageResponse.EnsureSuccessStatusCode(); Assert.True(pageResponse.Headers.CacheControl!.NoStore);
        var page = await pageResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("paper", page.GetProperty("tradingMode").GetString());
        Assert.Equal(0, page.GetProperty("retainedFillCount").GetInt32());
        Assert.Equal(2, page.GetProperty("paperExecutionCount").GetInt32());
        var row = Assert.Single(page.GetProperty("operations").EnumerateArray());
        Assert.Equal("paper", row.GetProperty("executionSource").GetString());
        Assert.Equal("paper-simulation", row.GetProperty("pnlBasis").GetString());
        var expanded = await client.GetFromJsonAsync<JsonElement>(
            $"/api/activity/fills?accountId={paper.Account.Id}&operationId={paper.Facts.Play.Id}&tradingMode=paper");
        Assert.Equal(0, expanded.GetProperty("fills").GetArrayLength());
        Assert.Equal(2, expanded.GetProperty("paperExecutions").GetArrayLength());
        Assert.False(expanded.GetProperty("paperExecutions")[0].TryGetProperty("fill", out _));
        Assert.Equal("paper", (await client.GetFromJsonAsync<SizingDto>("/api/sizing?tradingMode=paper"))!.Record.Source);
        Assert.Equal("paper", (await client.GetFromJsonAsync<ReviewDto>("/api/review?period=all&tradingMode=paper"))!.Plays.Single().ExecutionSource);
        foreach (var path in new[] { "/api/activity", "/api/review", "/api/review/exclusions", "/api/sizing", "/api/overview/summary" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "?tradingMode=Paper")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/sizing/settings?tradingMode=paper", new UpdateSizingSettingsRequest("2"))).StatusCode);
        Assert.Null(sizing.Settings); Assert.Equal(0, sizing.Saves);
    }
}
