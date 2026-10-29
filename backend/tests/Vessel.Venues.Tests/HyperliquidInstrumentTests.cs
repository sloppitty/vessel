using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed partial class HyperliquidAdapterTests : HyperliquidAdapterTestsSupport
{
    [Fact]
    public async Task Catalogue_reads_only_primary_meta_preserves_exact_ids_and_excludes_delisted_contracts()
    {
        const string meta = """
            {"universe":[
              {"name":"BTC","szDecimals":5,"maxLeverage":40},
              {"name":"ETH","szDecimals":4,"maxLeverage":20,"isDelisted":true},
              {"name":"MiXeD","szDecimals":0,"maxLeverage":3,"isDelisted":false},
              {"name":"btc","szDecimals":28,"maxLeverage":1},
              {"name":"@123"},{"name":"PURR/USDC"},{"name":"xyz:BTC"}
            ]}
            """;
        using var handler = Fixtures(meta);
        using var client = Client(handler);
        var instruments = await new HyperliquidPerpetualReader(client, new TestClock())
            .ReadInstrumentsAsync(CancellationToken.None);
        // Maintenance margin is half the initial margin at the maximum leverage.
        Assert.Equal(new[] { new VenueInstrument("BTC", 5, 40, "USDC", MaintenanceMarginFraction: 0.0125m),
            new("MiXeD", 0, 3, "USDC", MaintenanceMarginFraction: 1m / 6), new("btc", 28, 1, "USDC", MaintenanceMarginFraction: 0.5m) }, instruments);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.hyperliquid.xyz/info", request.Uri);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(new[] { "type", "dex" }, body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("meta", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("", body.RootElement.GetProperty("dex").GetString());
        Assert.DoesNotContain(Address, request.Body);
    }

    [Theory]
    [InlineData("{\"universe\":[]}")]
    [InlineData("{\"universe\":[{\"name\":\"BTC\",\"szDecimals\":5,\"maxLeverage\":40,\"isDelisted\":true}]}")]
    public async Task Catalogue_can_be_empty_or_all_delisted(string meta) =>
        Assert.Empty(await ReadCatalogue(meta));

    [Fact]
    public async Task Delisted_metadata_still_recognizes_historical_fills_in_full_read()
    {
        var fill = Mutate(Fill, node => node["coin"] = "ETH");
        var result = await Read(fills: $"[{fill}]");
        Assert.Contains(new VenueInstrument("ETH", 4, 20, "USDC", MaintenanceMarginFraction: 0.025m), result.Instruments);
        Assert.Equal("ETH", Assert.Single(result.Fills).ContractId);
        Assert.DoesNotContain(await ReadCatalogue(Meta), instrument => instrument.ContractId == "ETH");
    }

    [Theory]
    [InlineData("private-secret")]
    [InlineData("{\"private-secret\":")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"universe\":null}")]
    [InlineData("{\"universe\":{}}")]
    [InlineData("{\"universe\":[null]}")]
    [InlineData("{\"universe\":[{\"name\":\"BTC\"}]}")]
    [InlineData("{\"universe\":[],\"universe\":[]}")]
    [InlineData("{\"universe\":[{\"name\":\"BTC\",\"name\":\"ETH\",\"szDecimals\":5,\"maxLeverage\":20}]}")]
    [InlineData("{\"universe\":[{\"name\":\"BTC\",\"szDecimals\":5,\"maxLeverage\":40,\"isDelisted\":true,\"isDelisted\":false}]}")]
    [InlineData("{\"universe\":[],}")]
    [InlineData("{/*comment*/\"universe\":[]}")]
    public async Task Catalogue_rejects_malformed_shapes_and_duplicate_properties(string meta) =>
        await InvalidCatalogue(() => ReadCatalogue(meta));

    [Theory]
    [InlineData("name", "null")]
    [InlineData("name", "\" \"")]
    [InlineData("name", "\"BTC\\n\"")]
    [InlineData("szDecimals", "-1")]
    [InlineData("szDecimals", "29")]
    [InlineData("szDecimals", "5.5")]
    [InlineData("szDecimals", "\"5\"")]
    [InlineData("maxLeverage", "0")]
    [InlineData("maxLeverage", "-1")]
    [InlineData("maxLeverage", "2147483648")]
    [InlineData("maxLeverage", "\"40\"")]
    [InlineData("isDelisted", "null")]
    [InlineData("isDelisted", "\"true\"")]
    [InlineData("isDelisted", "0")]
    public async Task Catalogue_validates_metadata_fields_including_optional_delisted_flag(string field, string value)
    {
        var meta = Mutate(Meta, node => node["universe"]![0]![field] = JsonNode.Parse(value));
        await InvalidCatalogue(() => ReadCatalogue(meta));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Catalogue_rejects_duplicate_contracts_even_when_one_or_both_are_delisted(bool first, bool second)
    {
        var meta = Mutate(Meta, node =>
        {
            node["universe"]![0]!["isDelisted"] = first;
            var duplicate = node["universe"]![0]!.DeepClone();
            duplicate["isDelisted"] = second;
            ((JsonArray)node["universe"]!).Add(duplicate);
        });
        await InvalidCatalogue(() => ReadCatalogue(meta));
    }

    [Fact]
    public async Task Catalogue_rejects_excessive_depth_and_oversized_contract_ids()
    {
        var deep = "{\"universe\":[],\"extra\":" + new string('[', 40) + "0" + new string(']', 40) + "}";
        await InvalidCatalogue(() => ReadCatalogue(deep));
        var meta = Mutate(Meta, node => node["universe"]![0]!["name"] = new string('A', 129));
        await InvalidCatalogue(() => ReadCatalogue(meta));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Catalogue_rejects_more_than_4MB_with_or_without_content_length(bool knownLength)
    {
        var data = new string(' ', 4 * 1024 * 1024 + 1);
        using var handler = new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = knownLength ? new StringContent(data) : new StreamingContent(new MemoryStream(Encoding.UTF8.GetBytes(data)))
        }));
        using var client = Client(handler);
        await InvalidCatalogue(() => new HyperliquidPerpetualReader(client, new TestClock()).ReadInstrumentsAsync(default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Catalogue_accepts_response_at_4MB_limit()
    {
        const string meta = "{\"universe\":[]}";
        Assert.Empty(await ReadCatalogue(meta + new string(' ', 4 * 1024 * 1024 - meta.Length)));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Catalogue_non_success_response_is_safe_and_makes_no_further_requests(HttpStatusCode status)
    {
        using var handler = new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("private-secret")
        }));
        using var client = Client(handler);
        Func<Task> read = () => new HyperliquidPerpetualReader(client, new TestClock()).ReadInstrumentsAsync(default);
        var exception = status == HttpStatusCode.TooManyRequests
            ? await Assert.ThrowsAsync<VenueRateLimitException>(read)
            : await Assert.ThrowsAsync<VenueReadException>(read);
        Assert.Equal(status == HttpStatusCode.TooManyRequests
            ? "The venue rate limit requires a later retry."
            : "Hyperliquid is unavailable. Try again later.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("body")]
    [InlineData("timeout")]
    public async Task Catalogue_transport_failures_do_not_expose_provider_details(string failure)
    {
        using var handler = new FixtureHandler((_, _) => throw failure switch
        {
            "network" => new HttpRequestException("private-secret"),
            "body" => new IOException("private-secret"),
            _ => new TaskCanceledException("private-secret")
        });
        using var client = Client(handler);
        var exception = await Assert.ThrowsAsync<VenueReadException>(() =>
            new HyperliquidPerpetualReader(client, new TestClock()).ReadInstrumentsAsync(default));
        Assert.Equal(failure == "timeout" ? "Hyperliquid read timed out." : "Hyperliquid is unavailable. Try again later.",
            exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Precancelled_catalogue_read_makes_no_requests()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        using var handler = Fixtures(Meta);
        using var client = Client(handler);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HyperliquidPerpetualReader(client, new TestClock()).ReadInstrumentsAsync(caller.Token));
        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Catalogue_request_cancellation_preserves_caller_or_reports_20_second_deadline(bool callerCancels)
    {
        var clock = new TestClock();
        using var caller = new CancellationTokenSource();
        using var handler = new FixtureHandler((_, token) =>
        {
            if (callerCancels) caller.Cancel();
            else clock.Advance(TimeSpan.FromSeconds(20));
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Json(Meta));
        });
        using var client = Client(handler);
        await AssertCatalogueCancellation(
            new HyperliquidPerpetualReader(client, clock).ReadInstrumentsAsync(caller.Token), caller, callerCancels);
        Assert.Single(handler.Requests);
        Assert.Equal(1, clock.TimerCount);
        Assert.Equal(TimeSpan.FromSeconds(20), clock.Deadline);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Catalogue_streamed_body_is_covered_by_caller_cancellation_and_deadline(bool callerCancels)
    {
        var clock = new TestClock();
        using var caller = new CancellationTokenSource();
        using var handler = new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamingContent(new InterruptingStream(() =>
            {
                if (callerCancels) caller.Cancel();
                else clock.Advance(TimeSpan.FromSeconds(20));
            }))
        }));
        using var client = Client(handler);
        await AssertCatalogueCancellation(
            new HyperliquidPerpetualReader(client, clock).ReadInstrumentsAsync(caller.Token), caller, callerCancels);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Catalogue_stream_failure_is_safe()
    {
        using var handler = new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamingContent(new InterruptingStream(() => throw new IOException("private-secret")))
        }));
        using var client = Client(handler);
        var exception = await Assert.ThrowsAsync<VenueReadException>(() =>
            new HyperliquidPerpetualReader(client, new TestClock()).ReadInstrumentsAsync(default));
        Assert.Equal("Hyperliquid is unavailable. Try again later.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    private static async Task AssertCatalogueCancellation(Task<IReadOnlyList<VenueInstrument>> operation,
        CancellationTokenSource caller, bool callerCancels)
    {
        if (callerCancels)
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Equal(caller.Token, exception.CancellationToken);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<VenueReadException>(() => operation);
            Assert.Equal("Hyperliquid read timed out.", exception.Message);
            Assert.Null(exception.InnerException);
        }
    }

    private static async Task InvalidCatalogue(Func<Task<IReadOnlyList<VenueInstrument>>> operation)
    {
        var exception = await Assert.ThrowsAsync<VenueReadException>(operation);
        Assert.Equal("Hyperliquid returned an invalid or unsupported response.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    private static async Task<IReadOnlyList<VenueInstrument>> ReadCatalogue(string meta)
    {
        using var handler = Fixtures(meta);
        using var client = Client(handler);
        return await new HyperliquidPerpetualReader(client, new TestClock()).ReadInstrumentsAsync(default);
    }
}
