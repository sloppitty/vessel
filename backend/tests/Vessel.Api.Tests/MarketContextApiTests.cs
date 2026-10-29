using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Text;
using System.Text.Json;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed class MarketContextApiTests : MarketContextApiTestsSupport
{
    private static string Url(Guid id, string query) => $"/api/accounts/{id}/market-context?{query}";

    private static string Meta(string name) => $$"""{"name":"{{name}}","szDecimals":5,"maxLeverage":40}""";

    private static string Ctx(string funding = "0.0000125", string mark = "65000.50", string? mid = "65000.5", string? premium = "-0.00031774",
        string oi = "29010.12340", string prev = "64000.0", string vol = "1234567890.5500") =>
        $$"""{"funding":"{{funding}}","openInterest":"{{oi}}","prevDayPx":"{{prev}}","dayNtlVlm":"{{vol}}","premium":{{(premium is null ? "null" : $"\"{premium}\"")}},"oraclePx":"65010.0","markPx":"{{mark}}","midPx":{{(mid is null ? "null" : $"\"{mid}\"")}},"impactPxs":null,"dayBaseVlm":"1.0"}""";

    private static string Payload(string[]? names = null, string[]? ctxs = null) =>
        $"[{{\"universe\":[{string.Join(",", (names ?? ["BTC", "ETH"]).Select(Meta))}]}},[{string.Join(",", ctxs ?? [Ctx(), Ctx(funding: "-0.0001", mid: null, premium: null, oi: "5.0")])}]]";

    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static HyperliquidPerpetualReader ReaderFor(Handler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.hyperliquid.xyz/") }, TimeProvider.System);

    [Fact]
    public async Task Requires_authentication()
    {
        var (owner, store, account) = Setup(); var reader = new Contexts();
        await using var factory = new CoreApiFactory(owner, store, market: reader);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Url(account.Id, "instrument=BTC"))).StatusCode);
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task Maps_exact_strings_and_nulls_and_shares_one_upstream_call_across_instruments()
    {
        var (owner, store, account) = Setup();
        var handler = new Handler(Payload());
        await using var factory = new CoreApiFactory(owner, store, market: ReaderFor(handler));
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync(Url(account.Id, "instrument=BTC"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(new[] { "venueId", "instrument", "markPrice", "oraclePrice", "midPrice", "previousDayPrice", "dayNotionalVolume", "openInterest", "fundingRate", "premium", "observedAt", "notice" },
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("hyperliquid", root.GetProperty("venueId").GetString());
        Assert.Equal("BTC", root.GetProperty("instrument").GetString());
        Assert.Equal("65000.50", root.GetProperty("markPrice").GetString());
        Assert.Equal("65010.0", root.GetProperty("oraclePrice").GetString());
        Assert.Equal("65000.5", root.GetProperty("midPrice").GetString());
        Assert.Equal("64000.0", root.GetProperty("previousDayPrice").GetString());
        Assert.Equal("1234567890.5500", root.GetProperty("dayNotionalVolume").GetString());
        Assert.Equal("29010.12340", root.GetProperty("openInterest").GetString());
        Assert.Equal("0.0000125", root.GetProperty("fundingRate").GetString());
        Assert.Equal("-0.00031774", root.GetProperty("premium").GetString());
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("observedAt").GetString(), out _));
        Assert.Equal("Venue market context for the primary perpetual DEX. Funding is the current hourly rate; open interest is in base units. Not a fill or valuation.",
            root.GetProperty("notice").GetString());

        var eth = await client.GetAsync(Url(account.Id, "instrument=ETH"));
        using var ethJson = JsonDocument.Parse(await eth.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, ethJson.RootElement.GetProperty("midPrice").ValueKind);
        Assert.Equal(JsonValueKind.Null, ethJson.RootElement.GetProperty("premium").ValueKind);
        Assert.Equal("-0.0001", ethJson.RootElement.GetProperty("fundingRate").GetString());

        using var sent = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(new[] { "type" }, sent.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("metaAndAssetCtxs", sent.RootElement.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("instrument=BT%20C")]
    [InlineData("instrument=xyz:BTC")]
    [InlineData("instrument=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Invalid_instrument_returns_400_without_venue_read(string query)
    {
        var (owner, store, account) = Setup(); var reader = new Contexts();
        await using var factory = new CoreApiFactory(owner, store, market: reader);
        using var client = factory.AuthorizedClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Url(account.Id, query))).StatusCode);
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task Unknown_instrument_returns_400()
    {
        var (owner, store, account) = Setup();
        await using var factory = new CoreApiFactory(owner, store, market: ReaderFor(new Handler(Payload())));
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync(Url(account.Id, "instrument=DOGE"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("The instrument is not in the venue's perpetual catalogue.", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("manual", false, false, HttpStatusCode.Conflict)]
    [InlineData("hyperliquid", true, false, HttpStatusCode.NotFound)]
    [InlineData("hyperliquid", false, true, HttpStatusCode.Conflict)]
    public async Task Guards_return_expected_status_without_venue_read(string venue, bool foreign, bool disabled, HttpStatusCode status)
    {
        var (owner, store, account) = Setup(venue, foreign, disabled); var reader = new Contexts();
        await using var factory = new CoreApiFactory(owner, store, market: reader);
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync(Url(account.Id, "instrument=BTC"));
        Assert.Equal(status, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Private account", text);
        if (venue == "manual") Assert.Contains("No market data provider for manual accounts.", text);
        if (disabled) Assert.Contains("Enable the account before reading market data.", text);
        Assert.Equal(0, reader.Reads);
    }

    [Theory]
    [InlineData("venue")]
    [InlineData("http")]
    [InlineData("mismatch")]
    [InlineData("status")]
    [InlineData("malformed")]
    [InlineData("length-mismatch")]
    [InlineData("bad-decimal")]
    [InlineData("negative-price")]
    [InlineData("duplicate-names")]
    [InlineData("missing-field")]
    [InlineData("wrong-shape")]
    [InlineData("object")]
    public async Task Venue_failures_and_malformed_payloads_return_generic_502(string failure)
    {
        var (owner, store, account) = Setup();
        IMarketContextReader reader = failure switch
        {
            "venue" => new Contexts { Failure = new VenueReadException("secret") },
            "http" => new Contexts { Failure = new HttpRequestException("secret") },
            "mismatch" => new Contexts { VenueId = "other" },
            "status" => ReaderFor(new Handler("[]", HttpStatusCode.TooManyRequests)),
            "malformed" => ReaderFor(new Handler("[{\"secret\":")),
            "length-mismatch" => ReaderFor(new Handler(Payload(ctxs: [Ctx()]))),
            "bad-decimal" => ReaderFor(new Handler(Payload(ctxs: [Ctx(mark: "1e5"), Ctx()]))),
            "negative-price" => ReaderFor(new Handler(Payload(ctxs: [Ctx(prev: "-1"), Ctx()]))),
            "duplicate-names" => ReaderFor(new Handler(Payload(names: ["BTC", "BTC"]))),
            "missing-field" => ReaderFor(new Handler(Payload(ctxs: ["{\"funding\":\"0\"}", Ctx()]))),
            "wrong-shape" => ReaderFor(new Handler("[{\"universe\":[]}]")),
            _ => ReaderFor(new Handler("{\"secret\":1}"))
        };
        // "mismatch": the account's venue has no market adapter, only another venue's.
        var mismatch = failure == "mismatch";
        await using var factory = new CoreApiFactory(owner, store, market: mismatch ? null : reader,
            configure: mismatch ? (Action<IServiceCollection>)(services => { services.RemoveAll<IMarketContextReader>(); services.AddSingleton(reader); }) : null);
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync(Url(account.Id, "instrument=BTC"));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", text);
        Assert.Contains("The venue market read failed. Try again later.", text);
    }
}
