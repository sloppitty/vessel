using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vessel.Application.MarketData;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed partial class HyperliquidAdapterTests : HyperliquidAdapterTestsSupport
{
    [Theory]
    [InlineData("kPEPE", "1000PEPE")]
    [InlineData("kSHIB", "1000SHIB")]
    [InlineData("kBONK", "1000BONK")]
    [InlineData("kLUNC", "1000LUNC")]
    [InlineData("kFLOKI", "1000FLOKI")]
    [InlineData("kDOGS", "1000DOGS")]
    [InlineData("kNEIRO", "1000NEIRO")]
    [InlineData("BTC", "BTC")]
    [InlineData("kUNKNOWN", "kUNKNOWN")]
    [InlineData("kilo", "kilo")]
    [InlineData("KPEPE", "KPEPE")]
    [InlineData("kPepe", "kPepe")]
    public async Task Canonical_catalogue_fills_positions_and_candles_round_trip_without_rescaling(string native, string canonical)
    {
        var meta = Meta.Replace("\"BTC\"", JsonSerializer.Serialize(native));
        var state = State.Replace("\"BTC\"", JsonSerializer.Serialize(native));
        var fill = Fill.Replace("\"BTC\"", JsonSerializer.Serialize(native));
        var result = await Read(meta, state, $"[{fill}]");
        var instrument = result.Instruments[0];
        Assert.Equal(canonical, instrument.ContractId);
        Assert.Equal(native == canonical ? null : native, instrument.VenueContractId);
        var position = Assert.Single(result.Snapshot.Positions);
        Assert.Equal(canonical, position.ContractId);
        Assert.Equal(native == canonical ? null : native, position.VenueContractId);
        Assert.Equal(-0.01234567890123456789m, position.SignedQuantity);
        Assert.Equal(61234.1234567890123456m, position.EntryPrice);
        var imported = Assert.Single(result.Fills);
        Assert.Equal(canonical, imported.ContractId);
        Assert.Equal(native == canonical ? null : native, imported.VenueContractId);
        Assert.Equal(61235.1234567890123456m, imported.Price);
        Assert.Equal(0.001234567890123456789m, imported.Quantity);
        Assert.Equal(native, JsonDocument.Parse(imported.RawJson).RootElement.GetProperty("coin").GetString());

        var candle = $$"""
            [{"s":"{{native}}","i":"1m","t":0,"T":59999,"o":"0.00100","h":"0.00200","l":"0.00090","c":"0.00150","v":"123.40","n":2}]
            """;
        using var handler = Fixtures(candle);
        using var client = Client(handler);
        var candles = await new HyperliquidPerpetualReader(client, new TestClock()).ReadCandlesAsync(canonical, "1m", 0, 59999, default);
        Assert.Equal("0.00150", Assert.Single(candles).Close);
        Assert.Equal("123.40", candles[0].Volume);
        using var request = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(native, request.RootElement.GetProperty("req").GetProperty("coin").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canonical_catalogue_rejects_native_and_canonical_collisions_including_history(bool delisted)
    {
        var meta = $$"""
            {"universe":[{"name":"kPEPE","szDecimals":0,"maxLeverage":10,"isDelisted":{{delisted.ToString().ToLowerInvariant()}}},
            {"name":"1000PEPE","szDecimals":0,"maxLeverage":10}]}
            """;
        using var handler = Fixtures(meta);
        using var client = Client(handler);
        await Assert.ThrowsAsync<VenueReadException>(() =>
            new HyperliquidPerpetualReader(client, new TestClock()).ReadInstrumentsAsync(default));
        await Invalid(() => Read(meta));
    }

    [Fact]
    public async Task Canonical_catalogue_rejects_a_native_name_reserved_for_a_different_contract()
    {
        // Even without kPEPE in a partial catalogue, the reverse mapping must not request a different coin.
        using var handler = Fixtures(Meta.Replace("\"BTC\"", "\"1000PEPE\""));
        using var client = Client(handler);
        await Assert.ThrowsAsync<VenueReadException>(() =>
            new HyperliquidPerpetualReader(client, new TestClock()).ReadInstrumentsAsync(default));
    }

    [Fact]
    public async Task Canonical_delisted_contracts_remain_in_history_but_not_picker()
    {
        var meta = """{"universe":[{"name":"kDOGS","szDecimals":0,"maxLeverage":3,"isDelisted":true}]}""";
        using var handler = Fixtures(meta);
        using var client = Client(handler);
        Assert.Empty(await new HyperliquidPerpetualReader(client, new TestClock()).ReadInstrumentsAsync(default));
        var result = await Read(meta, State.Replace("\"BTC\"", "\"kDOGS\""), $"[{Fill.Replace("\"BTC\"", "\"kDOGS\"")}]");
        Assert.Equal("1000DOGS", Assert.Single(result.Fills).ContractId);
        Assert.Equal("kDOGS", Assert.Single(result.Fills).VenueContractId);
    }

    [Fact]
    public async Task Canonical_orders_keep_native_ids_for_open_and_historical_facts()
    {
        const string order = """
            {"coin":"kPEPE","side":"B","limitPx":"0.0012345","sz":"12","oid":123,"timestamp":1790812800000,
             "isTrigger":false,"reduceOnly":false,"isPositionTpsl":false,"orderType":"Limit","origSz":"20"}
            """;
        var history = $$"""[{"order":{{order}},"status":"filled","statusTimestamp":1790812800000}]""";
        using var handler = Fixtures(Meta.Replace("\"BTC\"", "\"kPEPE\""), history, $"[{order.Replace("\"oid\":123", "\"oid\":124")}]");
        using var client = Client(handler);
        var result = await new HyperliquidPerpetualReader(client, new TestClock()).ReadOrdersAsync(Address, default);
        Assert.Equal(2, result.Orders.Count);
        Assert.All(result.Orders, item =>
        {
            Assert.Equal(("1000PEPE", "kPEPE"), (item.ContractId, item.VenueContractId));
            Assert.Equal((0.0012345m, 20m), (item.LimitPrice, item.OriginalSize));
        });
        Assert.Equal(VenueOrderStatus.Filled, result.Orders.Single(o => o.OrderId == "123").Status);
        Assert.Equal(VenueOrderStatus.Open, result.Orders.Single(o => o.OrderId == "124").Status);

        var now = DateTimeOffset.FromUnixTimeMilliseconds(StateTime - 1000);
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Test");
        var plan = new PlayPlanDocument("long", "margin", "1", "10", null,
            [new("entry", "Entry", "#ffffff", "100", "0.0012345", [], [])], new("", "", "", ""));
        var play = new Play(Guid.NewGuid(), account, new("hyperliquid", "1000PEPE"),
            InstrumentSource.Venue, PlayDocuments.Serialize(plan), now);
        play.MarkPlanned(now);
        var read = result.Orders[0];
        var stored = new ImportedOrder
        {
            ContractId = read.ContractId, VenueContractId = read.VenueContractId, Side = read.Side,
            OrderType = read.OrderType, LimitPrice = read.LimitPrice, PlacedAtUtc = read.PlacedAtUtc
        };
        Assert.Equal(play.Id, Assert.Single(ExecutionMatcher.Candidates(stored, [(play, plan)], (_, _) => false, false)).Play.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canonical_contexts_keep_metadata_indexing_and_reject_collisions(bool collision)
    {
        var meta = $$"""{"universe":[{"name":"kPEPE"},{"name":"{{(collision ? "1000PEPE" : "BTC")}}"}]}""";
        using var handler = Fixtures($"[{meta},[{HyperliquidCanonicalStreamTests.Context},{HyperliquidCanonicalStreamTests.Context}]]");
        using var client = Client(handler);
        var reader = new HyperliquidPerpetualReader(client, new TestClock());
        if (collision)
            await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadMarketContextsAsync(default));
        else
        {
            var result = await reader.ReadMarketContextsAsync(default);
            Assert.Equal(["1000PEPE", "BTC"], result.Select(c => c.ContractId));
            Assert.All(result, c => Assert.Equal("0.0012300", c.MarkPrice));
        }
    }

    [Fact]
    public void Canonical_normalization_does_not_rewrite_manual_or_other_venue_domain_instruments()
    {
        Assert.Equal("https://app.hyperliquid.xyz/trade/{venueContractId}", HyperliquidPerpetualReader.Descriptor.TradeUrlTemplate);
        Assert.Equal("kPEPE", new PerpetualInstrument("manual", "kPEPE").ContractId);
        Assert.Equal("kPEPE", new PerpetualInstrument("risex", "kPEPE").ContractId);
        Assert.Equal("kPEPE", new PerpetualInstrument("hyperliquid", "kPEPE").ContractId);
    }
}

public sealed class HyperliquidCanonicalStreamTests
{
    internal const string Context = """
        {"funding":"0.00001","openInterest":"12.0","prevDayPx":"0.00120","dayNtlVlm":"100",
         "premium":null,"oraclePx":"0.00124","markPx":"0.0012300","midPx":null}
        """;

    [Fact]
    public async Task Stream_uses_native_subscriptions_acks_routing_replay_and_unsubscribe_but_canonical_context()
    {
        var sockets = new FakeSocketFactory();
        var time = new ManualTime(DateTimeOffset.FromUnixTimeMilliseconds(1790812800000));
        await using var stream = new HyperliquidMarketStream(sockets, time, NullLogger<HyperliquidMarketStream>.Instance);
        using var first = stream.Subscribe("1000PEPE", "1m");
        await Eventually.True(() => sockets.Sockets.Count == 1 && sockets.Sockets[0].Sent.Count == 2, "native subscriptions sent");
        var socket = sockets.Sockets[0];
        Assert.All(socket.Sent, text => Assert.Contains("\"coin\":\"kPEPE\"", text));
        using var second = stream.Subscribe("1000PEPE", "1m");
        Assert.Equal(2, stream.SubscriptionCount);
        socket.Push("""{"channel":"subscriptionResponse","data":{"method":"subscribe","subscription":{"type":"candle","coin":"kPEPE","interval":"1m"}}}""");
        await using var events = first.ReadAllAsync(default).GetAsyncEnumerator();
        Assert.Equal(MarketStreamState.Live, Assert.IsType<MarketStatusEvent>(await Eventually.Next(events)).State);
        socket.Push("""{"channel":"candle","data":{"s":"kPEPE","i":"1m","t":0,"T":59999,"o":"0.001","h":"0.002","l":"0.001","c":"0.002","v":"12","n":1}}""");
        Assert.Equal("0.002", Assert.IsType<MarketCandleEvent>(await Eventually.Next(events)).Candle.Close);
        socket.Push("""{"channel":"activeAssetCtx","data":{"coin":"kPEPE","ctx":""" + Context + "}}");
        var context = Assert.IsType<MarketContextEvent>(await Eventually.Next(events)).Context;
        Assert.Equal(("1000PEPE", "0.0012300"), (context.ContractId, context.MarkPrice));
        using var late = stream.Subscribe("1000PEPE", "1m");
        await using var replay = late.ReadAllAsync(default).GetAsyncEnumerator();
        Assert.IsType<MarketStatusEvent>(await Eventually.Next(replay));
        Assert.IsType<MarketCandleEvent>(await Eventually.Next(replay));
        Assert.Equal("1000PEPE", Assert.IsType<MarketContextEvent>(await Eventually.Next(replay)).Context.ContractId);
        first.Dispose();
        second.Dispose();
        late.Dispose();
        await Eventually.True(() => socket.Sent.Count == 4, "native unsubscribe sent only after last listener");
        Assert.All(socket.Sent, text => Assert.Contains("\"coin\":\"kPEPE\"", text));
        Assert.Equal(0, stream.Dropped);
    }
}
