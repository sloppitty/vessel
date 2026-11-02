using System.Net;
using System.Text.Json;

namespace Vessel.Tests;

public sealed class CandleCachingApiTests : CandleCachingTestsSupport
{

    [Theory]
    [InlineData("")]
    [InlineData("True")]
    [InlineData("FALSE")]
    [InlineData("1")]
    [InlineData("true&refresh=false")]
    public async Task Refresh_query_is_strict_and_returns_400(string refresh)
    {
        var (owner, store, account) = Setup(); var reader = new Reader();
        await using var factory = new CoreApiFactory(owner, store, candles: reader);
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync($"/api/accounts/{account.Id}/candles?instrument=BTC&interval=1h&refresh={refresh}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task API_refresh_and_guards_are_applied_even_to_cached_windows()
    {
        var (owner, store, account) = Setup(); var reader = new Reader();
        await using var factory = new CoreApiFactory(owner, store, candles: reader);
        using var client = factory.AuthorizedClient();
        var url = $"/api/accounts/{account.Id}/candles?instrument=BTC&interval=1h&endTime=1790000000000";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url + "&refresh=false")).StatusCode);
        Assert.Equal(1, reader.Reads);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url + "&refresh=true")).StatusCode);
        Assert.Equal(2, reader.Reads);
        account.UpdateSettings(account.Name, null, false);
        var disabled = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
        Assert.Equal(2, reader.Reads);
    }

    [Fact]
    public async Task Current_catalogue_rejects_retirement_before_a_historical_cache_hit()
    {
        var (owner, store, account) = Setup(); var reader = new Reader(); var catalogue = new FixtureReader();
        await using var factory = new CoreApiFactory(owner, store, reader: catalogue, candles: reader);
        using var client = factory.AuthorizedClient();
        var url = $"/api/accounts/{account.Id}/candles?instrument=BTC&interval=1h&endTime=1790000000000";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
        catalogue.Result = catalogue.Result with { Instruments = [] };
        var retired = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Conflict, retired.StatusCode);
        using var problem = JsonDocument.Parse(await retired.Content.ReadAsStringAsync());
        Assert.Equal("This market is no longer listed.", problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal(1, reader.Reads);
    }
}
