using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed class MarketStreamApiTests : MarketStreamApiTestsSupport
{
    private static string Url(Guid id, string query) => $"/api/accounts/{id}/market-stream?{query}";
    private static readonly VenueCandle SampleCandle = new(1_790_927_520_000, 1_790_927_579_999, "85891.0", "85903.0", "85887.0", "85903.0", "6.19755", 58);
    private static readonly VenueMarketContext SampleContext = new("BTC", "85896.40", "85916", null, "83388.0", "2524355036.618314743", "34715.04450", "0.0000125", null);

    [Theory]
    [InlineData("manual", false, false, "instrument=BTC&interval=1m", 409)]
    [InlineData("hyperliquid", true, false, "instrument=BTC&interval=1m", 404)]
    [InlineData("hyperliquid", false, true, "instrument=BTC&interval=1m", 409)]
    [InlineData("hyperliquid", false, true, "instrument=bad%20coin&interval=nope", 409)]
    [InlineData("hyperliquid", false, false, "interval=1m", 400)]
    [InlineData("hyperliquid", false, false, "instrument=xyz:BTC&interval=1m", 400)]
    [InlineData("hyperliquid", false, false, "instrument=BTC", 400)]
    [InlineData("hyperliquid", false, false, "instrument=BTC&interval=2m", 400)]
    public async Task Invalid_requests_return_problem_details_before_subscribing(string venue, bool foreign, bool disabled, string query, int status)
    {
        var (owner, store, account) = Setup(venue, foreign, disabled); var stream = new FakeMarketStream();
        await using var factory = new CoreApiFactory(owner, store, stream: stream);
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync(Url(account.Id, query));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Private account", await response.Content.ReadAsStringAsync());
        Assert.Empty(stream.Subscriptions);
    }

    [Fact]
    public async Task Requires_authentication()
    {
        var (owner, store, account) = Setup(); var stream = new FakeMarketStream();
        await using var factory = new CoreApiFactory(owner, store, stream: stream);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Url(account.Id, "instrument=BTC&interval=1m"))).StatusCode);
        Assert.Empty(stream.Subscriptions);
    }

    [Fact]
    public async Task Venue_mismatch_and_upstream_failures_return_problem_details()
    {
        var (owner, store, account) = Setup();
        // The account's venue has no stream adapter, only another venue's.
        var other = new FakeMarketStream { VenueId = "other" };
        await using (var factory = new CoreApiFactory(owner, store, configure: services =>
        {
            services.RemoveAll<IMarketStream>();
            services.AddSingleton<IMarketStream>(other);
        }))
        {
            using var client = factory.AuthorizedClient();
            var response = await client.GetAsync(Url(account.Id, "instrument=BTC&interval=1m"));
            Assert.Equal(502, (int)response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Empty(other.Subscriptions);
            Assert.Equal(0, factory.Services.GetRequiredService<MarketStreamLimiter>().Active);
        }
        foreach (var (stream, status) in new[]
        {
            (new FakeMarketStream { Failure = new VenueReadException("secret") }, 502),
            (new FakeMarketStream { Failure = new MarketStreamCapacityException("secret") }, 429)
        })
        {
            await using var factory = new CoreApiFactory(owner, store, stream: stream);
            using var client = factory.AuthorizedClient();
            var response = await client.GetAsync(Url(account.Id, "instrument=BTC&interval=1m"));
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync());
            Assert.Equal(0, factory.Services.GetRequiredService<MarketStreamLimiter>().Active);
        }
    }

    [Fact]
    public async Task More_than_eight_concurrent_streams_return_429()
    {
        var (owner, store, account) = Setup(); var stream = new FakeMarketStream();
        await using var factory = new CoreApiFactory(owner, store, stream: stream);
        var limiter = factory.Services.GetRequiredService<MarketStreamLimiter>();
        var leases = Enumerable.Range(0, MarketStreamLimiter.MaxStreams).Select(_ => limiter.TryAcquire(Guid.NewGuid())!).ToList();
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync(Url(account.Id, "instrument=BTC&interval=1m"));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("Too many live market streams are open.", await response.Content.ReadAsStringAsync());
        Assert.Empty(stream.Subscriptions);
        leases.ForEach(lease => lease.Dispose());
    }

    [Fact]
    public async Task Streams_server_sent_events_with_rest_shapes_and_flushes_each_event()
    {
        var (owner, store, account) = Setup(); var stream = new FakeMarketStream();
        await using var factory = new CoreApiFactory(owner, store, stream: stream);
        using var client = factory.AuthorizedClient();
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("br, gzip");
        using var response = await client.GetAsync(Url(account.Id, "instrument=BTC&interval=1m"), HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));
        var subscription = Assert.Single(stream.Subscriptions);
        Assert.Equal(("BTC", "1m"), (subscription.ContractId, subscription.Interval));
        Assert.Equal(1, factory.Services.GetRequiredService<MarketStreamLimiter>().Active);

        await using var body = await response.Content.ReadAsStreamAsync();
        var observed = DateTimeOffset.FromUnixTimeMilliseconds(1_790_927_530_000);
        subscription.Write(new MarketStatusEvent(MarketStreamState.Live, observed));
        // Readable before more data arrives: the server flushes per event.
        var first = await ReadEventAsync(body);
        Assert.Equal($"event: status\ndata: {{\"state\":\"live\",\"observedAt\":{JsonSerializer.Serialize(observed)}}}", first);

        subscription.Write(new MarketCandleEvent(SampleCandle));
        Assert.Equal("event: candle\ndata: {\"openTime\":1790927520000,\"closeTime\":1790927579999,\"open\":\"85891.0\",\"high\":\"85903.0\",\"low\":\"85887.0\",\"close\":\"85903.0\",\"volume\":\"6.19755\",\"trades\":58}",
            await ReadEventAsync(body));

        subscription.Write(new MarketContextEvent(SampleContext with { ContractId = "ETH" }, observed)); // Not this instrument: ignored.
        subscription.Write(new MarketContextEvent(SampleContext, observed));
        var context = await ReadEventAsync(body);
        Assert.StartsWith("event: context\ndata: ", context);
        var data = context.Split('\n')[1]["data: ".Length..];
        using var json = JsonDocument.Parse(data);
        Assert.Equal(new[] { "venueId", "instrument", "markPrice", "oraclePrice", "midPrice", "previousDayPrice", "dayNotionalVolume", "openInterest", "fundingRate", "premium", "observedAt", "notice" },
            json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("BTC", json.RootElement.GetProperty("instrument").GetString());
        Assert.Equal("85896.40", json.RootElement.GetProperty("markPrice").GetString());
        Assert.Equal("34715.04450", json.RootElement.GetProperty("openInterest").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("midPrice").ValueKind);
        Assert.Equal(HyperliquidPerpetualReader.Descriptor.MarketContextNotice, json.RootElement.GetProperty("notice").GetString());

        subscription.Complete();
        Assert.Equal("", await new StreamReader(body).ReadToEndAsync());
        await Eventually.True(() => subscription.Disposed && factory.Services.GetRequiredService<MarketStreamLimiter>().Active == 0,
            "the session releases its subscription and slot");
    }

    [Fact]
    public async Task Client_disconnect_releases_the_subscription_and_slot()
    {
        var (owner, store, account) = Setup(); var stream = new FakeMarketStream();
        await using var factory = new CoreApiFactory(owner, store, stream: stream);
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync(Url(account.Id, "instrument=BTC&interval=1m"), HttpCompletionOption.ResponseHeadersRead);
        var subscription = Assert.Single(stream.Subscriptions);
        response.Dispose();
        await Eventually.True(() => subscription.Disposed && factory.Services.GetRequiredService<MarketStreamLimiter>().Active == 0,
            "disconnect ends the stream");
    }

    private static async Task<string> ReadEventAsync(Stream body)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!Encoding.UTF8.GetString([.. bytes]).EndsWith("\n\n", StringComparison.Ordinal))
        {
            Assert.Equal(1, await body.ReadAsync(buffer, timeout.Token));
            bytes.Add(buffer[0]);
        }
        return Encoding.UTF8.GetString([.. bytes])[..^2];
    }
}
