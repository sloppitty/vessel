using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Vessel.Application.MarketData;
using Vessel.Domain.Accounts;

namespace Vessel.Tests;

public sealed class MarketDataCompressionTests
{
    private sealed class MarketReads : ICandleReader, IMarketContextReader
    {
        public string VenueId => "hyperliquid";
        public Task<IReadOnlyList<VenueCandle>> ReadCandlesAsync(string contractId, string interval,
            long fromMs, long toMs, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VenueCandle>>([new(toMs, toMs, "1.0000", "2", "0", "1", "10.00", null)]);
        public Task<IReadOnlyList<VenueMarketContext>> ReadMarketContextsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VenueMarketContext>>([new("BTC", "1.0000", "1", null, null, "10", "1", "0", null)]);
    }

    private static (Guid Owner, MemoryWorkspaceStore Store, Account Account) Setup()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Test");
        store.Accounts.Add(account);
        return (owner, store, account);
    }

    [Theory]
    [InlineData("candles?instrument=BTC&interval=1h", "gzip")]
    [InlineData("candles?instrument=BTC&interval=1h", "br")]
    [InlineData("instruments", "gzip")]
    [InlineData("instruments", "br")]
    [InlineData("market-context?instrument=BTC", "gzip")]
    [InlineData("market-context?instrument=BTC", "br")]
    public async Task Only_successful_market_JSON_is_compressed_and_remains_no_store(string route, string encoding)
    {
        var (owner, store, account) = Setup(); var reads = new MarketReads();
        await using var factory = new CoreApiFactory(owner, store, reader: new FixtureReader(), candles: reads, market: reads);
        using var client = factory.AuthorizedClient();
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd(encoding);
        using var response = await client.GetAsync($"/api/accounts/{account.Id}/{route}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(encoding, Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        await using var body = await response.Content.ReadAsStreamAsync();
        await using Stream decoded = encoding == "gzip" ? new GZipStream(body, CompressionMode.Decompress) :
            new BrotliStream(body, CompressionMode.Decompress);
        using var json = await JsonDocument.ParseAsync(decoded);
        Assert.Equal("hyperliquid", json.RootElement.GetProperty("venueId").GetString());
        if (route.StartsWith("candles", StringComparison.Ordinal))
            Assert.Equal("1.0000", json.RootElement.GetProperty("candles")[0].GetProperty("open").GetString());
    }

    [Theory]
    [InlineData("candles?instrument=BTC&interval=bad", 400)]
    [InlineData("candles?instrument=BTC&interval=1h&refresh=TRUE", 400)]
    [InlineData("candles?instrument=OLD&interval=1h", 409)]
    [InlineData("market-context?instrument=OLD", 400)]
    public async Task Market_errors_are_never_compressed(string route, int status)
    {
        var (owner, store, account) = Setup(); var reads = new MarketReads();
        await using var factory = new CoreApiFactory(owner, store, reader: new FixtureReader(), candles: reads, market: reads);
        using var client = factory.AuthorizedClient();
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, br");
        using var response = await client.GetAsync($"/api/accounts/{account.Id}/{route}");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/api/accounts")]
    [InlineData("/api/system")]
    public async Task Other_successful_JSON_is_never_compressed(string route)
    {
        var (owner, store, _) = Setup();
        await using var factory = new CoreApiFactory(owner, store);
        using var client = factory.AuthorizedClient();
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, br");
        using var response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task Authentication_failure_is_never_compressed()
    {
        var (owner, store, account) = Setup();
        await using var factory = new CoreApiFactory(owner, store);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, br");
        using var response = await client.GetAsync($"/api/accounts/{account.Id}/candles?instrument=BTC&interval=1h");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }
}
