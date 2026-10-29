using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Credentials;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Infrastructure.Venues.Lighter;

namespace Vessel.Tests;

/// <summary>
/// Lighter on Robinhood Chain is a second configured instance of the Lighter module. Fixtures under
/// Fixtures/LighterRh were captured from the public, unauthenticated https://api.rh.lighter.xyz/api/v1/orderBookDetails,
/// orderBooks and assetDetails on 2026-10-14 and trimmed to a few markets (crypto, a stock, a commodity, a pre-IPO
/// market and two spot markets). No account, address or credential was used.
/// </summary>
public sealed class LighterRobinhoodTests
{
    private const string Source = "281474976710700";
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly LighterExchange Rh = LighterExchange.Robinhood;

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LighterRh", name));

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static LighterReader.Catalogue RhCatalogue => LighterReader.ReadCatalogue(Json(Fixture("orderBookDetails.json")), Rh);

    /// <summary>Asset 3 is the collateral: read its identity from the live assetDetails fixture, not a constant.</summary>
    private static JsonObject Asset(string symbol, string margin)
    {
        var details = JsonNode.Parse(Fixture("assetDetails.json"))!["asset_details"]!.AsArray();
        var id = details.Single(a => (string)a!["symbol"]! == symbol)!["asset_id"]!.GetValue<int>();
        return new JsonObject { ["asset_id"] = id, ["symbol"] = symbol, ["margin_balance"] = margin, ["balance"] = margin, ["locked_balance"] = "0" };
    }

    private static string Account(params JsonObject[] assets) => new JsonObject
    {
        ["index"] = long.Parse(Source), ["account_index"] = long.Parse(Source), ["account_type"] = 0,
        ["l1_address"] = "0x1111111111111111111111111111111111111111", ["total_asset_value"] = "250.5",
        ["available_balance"] = "200.25", ["cross_initial_margin_requirement"] = "17.2",
        ["assets"] = new JsonArray(assets),
        ["positions"] = new JsonArray(new JsonObject
        {
            ["market_id"] = 10, ["symbol"] = "AAPL", ["position"] = "1.5", ["sign"] = 1, ["initial_margin_fraction"] = "5.00",
            ["margin_mode"] = 0, ["position_value"] = "344", ["avg_entry_price"] = "229.33", ["unrealized_pnl"] = "0.005", ["allocated_margin"] = "0"
        })
    }.ToJsonString();

    private sealed class NoCredentials : IAccountCredentialReader
    {
        public Task<string?> ReadAsync(Guid accountId, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<CredentialAttempt?> ReadAttemptAsync(Guid accountId, CancellationToken ct) => Task.FromResult<CredentialAttempt?>(null);
        public Task MarkRefusedAsync(Guid accountId, string attemptedRevision, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<string, string?>? reply = null) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request.RequestUri!.GetLeftPart(UriPartial.Path));
            var name = request.RequestUri.AbsolutePath.Split('/').Last();
            var body = reply?.Invoke(name) ?? name switch
            {
                "orderBookDetails" => Fixture("orderBookDetails.json"),
                "account" => """{"code":200,"accounts":[""" + Account(Asset("USDG", "250.5")) + "]}",
                "trades" => """{"code":200,"trades":[],"next_cursor":"c""" + Requests.Count + "\"}",
                _ => """{"code":200}"""
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static LighterReader Reader(Handler handler, LighterExchange? exchange = null, LighterCatalogueCache? cache = null, string host = "https://api.rh.lighter.xyz/") =>
        new(new HttpClient(new LighterAuthenticationHandler((exchange ?? Rh).Name) { InnerHandler = handler }) { BaseAddress = new Uri(host) },
            new Clock(), new NoCredentials(), cache, exchange ?? Rh);

    [Fact]
    public void Longest_combined_stored_notice_keeps_the_same_margin_as_mainnet()
    {
        int Longest(string venue) => AccountHistoryNoticeBudget.CombinedCases().Where(c => c.Venue == venue)
            .Max(c => (c.Public + "\nOrder history: " + c.Orders).Length);
        Assert.True(Longest("lighter-rh") <= Longest("lighter"), $"{Longest("lighter-rh")} > {Longest("lighter")}");
        Assert.True(Longest("lighter-rh") <= 1000);
    }

    [Fact]
    public void Mainnet_keeps_its_exact_identity_and_wording()
    {
        var mainnet = LighterExchange.Mainnet;
        Assert.Same(mainnet.Descriptor, LighterReader.Descriptor);
        Assert.Equal(("lighter", "Lighter", "USDC", "lighter-perps-account", "lighter-read-token"),
            (mainnet.VenueId, mainnet.Name, mainnet.Collateral, mainnet.SnapshotScope, mainnet.CredentialPurpose));
        Assert.Equal("https://app.lighter.xyz/read-only-tokens", mainnet.Descriptor.CredentialSetupUrl);
        Assert.Null(mainnet.Descriptor.TradeUrlTemplate);
        Assert.Equal("lighter", mainnet.Descriptor.Logo);
        Assert.Equal(
            "Incomplete Lighter history: at most the latest 500 perpetual executions, no backfill. " +
            "Only fee-free executions with known realized PnL are imported; liquidation, deleverage, settlement and self-trade records are not. " +
            "USDC perpetual equity only, no spot wallets. Margin is the initial-margin requirement. Reported availability is not guaranteed withdrawal capacity.",
            LighterReader.HistoryNotice);
        Assert.Equal("The five-page history limit was reached; older executions remain unimported.", LighterReader.PageLimitNotice);
        Assert.Equal("Lighter perpetual orders: max 1,000 active, 500 recent inactive, five pages; no backfill. " +
            "Zero-size, flagged and TWAP omitted; position-tied/grouped execution unsupported.", LighterReader.OrdersNotice);
        Assert.Equal("Lighter trade candles, at most 500 per manual refresh. Earlier candles and trade counts are unavailable. Prices are observations, never fills.",
            mainnet.Descriptor.CandleNotice);
        Assert.Equal("Lighter returned an invalid or unsupported response.", mainnet.Json.InvalidResponse);
        Assert.Equal(
            [
                "Lighter is unavailable or refused the read. Try again or verify the read-only token.", "Lighter read timed out.",
                "Lighter did not accept this read-only token.", "Lighter refused the public read.",
                "A nonnegative Lighter account index within int64 is required.", "An enabled Lighter account is required.",
                "Lighter non-USDC collateral valuation is not supported. No partial snapshot was imported.",
                "Lighter binary positions are not supported. No partial snapshot was imported.",
                "A Lighter position uses an unsupported market.", "A native Lighter interval and a valid candle window are required.",
                "The instrument is not an active Lighter perpetual.", "A native Lighter market and a valid candle window are required.",
                "A valid Lighter L1 address is required.", "Enter a Lighter read-only token in ro:index:single|all:expiry:hex format.",
                "Lighter account discovery exceeded five pages. No partial account list was accepted.",
                "Lighter account names exceeded five pages. No partial names were accepted.",
                "The read-only token belongs to a different Lighter account.",
                "The read-only token scope does not match this Lighter account and its master.",
                "Add a valid read-only token to this Lighter account to track orders."
            ],
            [
                mainnet.Unavailable, mainnet.TimedOut, mainnet.TokenRefused, mainnet.PublicRefused, mainnet.IndexRequired,
                mainnet.AccountRequired, mainnet.CollateralUnsupported, mainnet.BinaryUnsupported, mainnet.PositionMarketUnsupported,
                mainnet.IntervalRequired, mainnet.InactiveInstrument, mainnet.MarketRequired, mainnet.AddressRequired, mainnet.TokenFormat,
                mainnet.DiscoveryPages, mainnet.NamePages, mainnet.TokenOtherAccount, mainnet.TokenScope, mainnet.TokenRequired
            ]);
    }

    [Fact]
    public void Robinhood_descriptor_is_a_separate_USDG_exchange_with_the_same_capabilities()
    {
        var descriptor = Rh.Descriptor;
        Assert.Equal(("lighter-rh", "Lighter · Robinhood", "read-only", VenueSources.AccountIndex, "USDG"),
            (descriptor.Id, descriptor.Name, descriptor.Status, descriptor.Source, descriptor.QuoteAsset));
        Assert.Equal(LighterReader.Descriptor.Capabilities, descriptor.Capabilities);
        Assert.Equal(LighterReader.Descriptor.CandleIntervals, descriptor.CandleIntervals);
        Assert.Equal(PriceRules.TickSize, descriptor.PriceRule);
        Assert.Equal("https://robinhoodchain.lighter.xyz/read-only-tokens", descriptor.CredentialSetupUrl);
        Assert.Equal("https://robinhoodchain.lighter.xyz/trade/{instrument}", descriptor.TradeUrlTemplate);
        Assert.Equal(VenueLogos.LighterRh, descriptor.Logo);
        Assert.StartsWith("Lighter · Robinhood trade candles", descriptor.CandleNotice);
        Assert.Equal("lighter-rh-read-token", Rh.CredentialPurpose);
        Assert.Equal("lighter-rh-perps-account", Rh.SnapshotScope);
        // The trades weight is three times mainnet's, so the history budget is smaller and the notice says so.
        Assert.Equal(2, Rh.TradePages);
        Assert.StartsWith("Incomplete Lighter history: at most the latest 200 perpetual executions", Rh.HistoryNotice);
        Assert.Contains("USDG perpetual equity only", Rh.HistoryNotice);
        Assert.Equal("The two-page history limit was reached; older executions remain unimported.", Rh.PageLimitNotice);
        Assert.StartsWith("Lighter perpetual orders:", Rh.OrdersNotice);
        Assert.True(Rh.HistoryNotice.Length <= LighterReader.HistoryNotice.Length + 40);
        Assert.Equal(["lighter", "lighter-rh"], LighterExchange.All.Select(e => e.VenueId));
    }

    [Fact]
    public void Live_catalogue_parses_crypto_stock_commodity_and_preipo_perpetuals_and_ignores_spot()
    {
        var catalogue = RhCatalogue;
        Assert.Same(Rh, catalogue.Exchange);
        var active = catalogue.Markets.Where(m => m.Active).Select(m => m.Instrument!).ToArray();
        Assert.Equal(["BTC", "AAPL", "XAU", "OPENAI"], active.Select(i => i.ContractId));
        Assert.All(active, i => Assert.Equal("USDG", i.QuoteAsset));
        var aapl = active.Single(i => i.ContractId == "AAPL");
        Assert.Equal(("10", 4, 0.01m, 20, 0.03m), (aapl.VenueContractId, aapl.QuantityDecimals, aapl.PriceStep, aapl.MaxLeverage, aapl.MaintenanceMarginFraction));
        var btc = active.Single(i => i.ContractId == "BTC");
        Assert.Equal((5, 0.1m, 50), (btc.QuantityDecimals, btc.PriceStep, btc.MaxLeverage));
        Assert.Equal(5, active.Single(i => i.ContractId == "OPENAI").MaxLeverage);
        Assert.Equal(new HashSet<string> { "2049", "2052" }, catalogue.SpotIds);
        // orderBooks (captured at the same time) agrees with orderBookDetails on market identity and type.
        foreach (var row in Json(Fixture("orderBooks.json")).GetProperty("order_books").EnumerateArray())
        {
            var id = row.GetProperty("market_id").GetRawText();
            if (row.GetProperty("market_type").GetString() == "spot") Assert.Contains(id, catalogue.SpotIds);
            else Assert.Equal(row.GetProperty("symbol").GetString(), catalogue.ById(id)!.Key);
        }
    }

    [Fact]
    public void Snapshot_uses_USDG_asset_3_and_refuses_other_margin_assets_by_name()
    {
        var snapshot = LighterReader.ReadSnapshot(Json(Account(Asset("USDG", "250.5"), Asset("AAPL", "0"))), RhCatalogue, Now);
        Assert.Equal("lighter-rh-perps-account", snapshot.ValueScope);
        Assert.Equal((250.5m, 17.2m, 200.25m), (snapshot.AccountValueUsd, snapshot.MarginUsedUsd, snapshot.AvailableMarginUsd!.Value));
        var position = Assert.Single(snapshot.Positions);
        Assert.Equal(("AAPL", 1.5m, "10", 20), (position.ContractId, position.SignedQuantity, position.VenueContractId, position.Leverage));

        // AMZN is margin-enabled on Robinhood (assetDetails margin_mode "enabled"); it is refused, not valued.
        var amzn = Assert.Throws<VenueReadException>(() =>
            LighterReader.ReadSnapshot(Json(Account(Asset("USDG", "100"), Asset("AMZN", "0.5"))), RhCatalogue, Now));
        Assert.Equal("Lighter · Robinhood non-USDG collateral valuation is not supported. No partial snapshot was imported.", amzn.Message);
        // Mainnet's collateral is just another non-collateral asset here.
        var usdc = new JsonObject { ["asset_id"] = 3, ["symbol"] = "USDC", ["margin_balance"] = "5", ["balance"] = "5", ["locked_balance"] = "0" };
        Assert.Throws<VenueReadException>(() => LighterReader.ReadSnapshot(Json(Account(Asset("USDG", "1"), usdc)), RhCatalogue, Now));
        // USDG under any asset ID but 3 is a different token, not the collateral.
        var misplaced = Asset("USDG", "1"); misplaced["asset_id"] = 4;
        var invalid = Assert.Throws<VenueReadException>(() => LighterReader.ReadSnapshot(Json(Account(misplaced)), RhCatalogue, Now));
        Assert.Equal("Lighter · Robinhood returned an invalid or unsupported response.", invalid.Message);
        // And mainnet still refuses USDG margin.
        var mainnet = LighterReader.ReadCatalogue(Json(Fixture("orderBookDetails.json")));
        Assert.Equal("USDC", mainnet.Markets[0].Instrument!.QuoteAsset);
        var refused = Assert.Throws<VenueReadException>(() => LighterReader.ReadSnapshot(Json(Account(Asset("USDG", "1"))), mainnet, Now));
        Assert.Equal("Lighter non-USDC collateral valuation is not supported. No partial snapshot was imported.", refused.Message);
    }

    [Fact]
    public void Fills_and_orders_use_the_exchange_collateral_and_messages()
    {
        var trade = $$"""
            {"code":200,"trades":[{"trade_id":7,"market_id":10,"ask_account_id":2,"bid_account_id":281474976710700,"is_maker_ask":true,
             "type":"trade","maker_position_size_before":"0","taker_position_size_before":"0","taker_fee":0,"size":"1.5",
             "price":"229.33","bid_account_pnl":"0","bid_id":9,"timestamp":{{Now.AddMinutes(-1).ToUnixTimeMilliseconds()}},"tx_hash":"ab12"}]}
            """;
        var fill = Assert.Single(LighterReader.ReadFills(Json(trade), RhCatalogue, Source, Now, standardTier: true).Fills);
        Assert.Equal(("AAPL", "USDG", 0m, "10"), (fill.ContractId, fill.FeeToken, fill.Fee, fill.VenueContractId));
        Assert.Equal(ExecutionFacts.FeeStandardAccountFree, fill.FeeBasis);
        var error = Assert.Throws<VenueReadException>(() => LighterReader.ReadFills(Json(trade.Replace("\"market_id\":10", "\"market_id\":99")), RhCatalogue, Source, Now, true));
        Assert.Equal("Lighter · Robinhood returned an invalid or unsupported response.", error.Message);
        Assert.Equal("A nonnegative Lighter · Robinhood account index within int64 is required.",
            Assert.Throws<VenueReadException>(() => LighterReader.Index("-1", Rh)).Message);
    }

    [Fact]
    public async Task Reader_reads_its_own_host_budget_and_refuses_the_other_exchanges_accounts()
    {
        var handler = new Handler();
        var reader = Reader(handler);
        Assert.Equal("lighter-rh", reader.VenueId);
        var result = await reader.ReadAsync(Source, default);
        Assert.Equal("lighter-rh-perps-account", result.Snapshot.ValueScope);
        Assert.All(result.Instruments, i => Assert.Equal("USDG", i.QuoteAsset));
        // Two trades pages, then the RH page-limit notice; never mainnet's five.
        Assert.Equal(2, handler.Requests.Count(r => r.EndsWith("/trades", StringComparison.Ordinal)));
        Assert.Contains(Rh.PageLimitNotice, result.HistoryNotice);
        Assert.StartsWith(Rh.HistoryNotice, result.HistoryNotice);
        Assert.All(handler.Requests, r => Assert.StartsWith("https://api.rh.lighter.xyz/api/v1/", r));

        var mainnetAccount = new Account(Guid.NewGuid(), Guid.NewGuid(), "lighter", "Mainnet");
        mainnetAccount.Configure(null, null, null, Source);
        var refused = await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadAsync(mainnetAccount, default));
        Assert.Equal("An enabled Lighter · Robinhood account is required.", refused.Message);

        var bad = Reader(new Handler(name => name == "orderBookDetails" ? """{"code":200,"order_book_details":{}}""" : null));
        var invalid = await Assert.ThrowsAsync<VenueReadException>(() => bad.ReadInstrumentsAsync(default));
        Assert.Equal("Lighter · Robinhood returned an invalid or unsupported response.", invalid.Message);
        var inactive = await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadCandlesAsync("ETH", "1h", 0, 3_600_000, default));
        Assert.Equal("The instrument is not an active Lighter · Robinhood perpetual.", inactive.Message);
    }

    [Fact]
    public async Task Catalogue_caches_are_per_exchange_and_cannot_be_shared()
    {
        var shared = new LighterCatalogueCache(new Clock());
        Assert.Equal("lighter", shared.VenueId);
        Assert.Throws<ArgumentException>(() => Reader(new Handler(), Rh, shared));

        var rhCache = new LighterCatalogueCache(new Clock(), "lighter-rh");
        var mainnetCache = new LighterCatalogueCache(new Clock());
        var rhHandler = new Handler();
        var mainnetHandler = new Handler(name => name == "orderBookDetails"
            ? """{"code":200,"order_book_details":[{"market_id":1,"market_type":"perp","symbol":"ETH","status":"active","supported_size_decimals":4,"supported_price_decimals":2,"min_initial_margin_fraction":400,"maintenance_margin_fraction":240}]}"""
            : null);
        var rh = await Reader(rhHandler, Rh, rhCache).ReadInstrumentsAsync(default);
        var mainnet = await Reader(mainnetHandler, LighterExchange.Mainnet, mainnetCache, "https://mainnet.zklighter.elliot.ai/").ReadInstrumentsAsync(default);
        Assert.Equal(["BTC", "AAPL", "XAU", "OPENAI"], rh.Select(i => i.ContractId));
        Assert.Equal("ETH", Assert.Single(mainnet).ContractId);
        Assert.Equal("USDC", mainnet[0].QuoteAsset);
        // Cached: a second read of each exchange does not refetch, and neither sees the other's markets.
        Assert.Equal(rh.Select(i => i.ContractId), (await Reader(rhHandler, Rh, rhCache).ReadInstrumentsAsync(default)).Select(i => i.ContractId));
        Assert.Single(rhHandler.Requests);
        Assert.Single(mainnetHandler.Requests);
    }

    [Theory]
    [InlineData("BaseUrl", "http://api.rh.lighter.xyz/")]
    [InlineData("BaseUrl", "wss://api.rh.lighter.xyz/")]
    [InlineData("BaseUrl", "https://user:secret-password@api.rh.lighter.xyz/")]
    [InlineData("BaseUrl", "https://api.rh.lighter.xyz/?key=secret-value")]
    [InlineData("BaseUrl", "https://api.rh.lighter.xyz/#secret-fragment")]
    [InlineData("BaseUrl", "https://api.rh.lighter.xyz/api/v1/")]
    [InlineData("BaseUrl", "/api/v1/")]
    [InlineData("BaseUrl", " https://api.rh.lighter.xyz/")]
    [InlineData("BaseUrl", "")]
    public void Invalid_hosts_fail_naming_only_the_key(string field, string value)
    {
        var options = new VenueHostOptions();
        options.LighterRh = new VenueHost { BaseUrl = field == "BaseUrl" ? value : options.LighterRh.BaseUrl };
        var result = new VenueHostValidation().Validate(null, options);
        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures!);
        Assert.StartsWith("Vessel:Venues:LighterRh:BaseUrl ", failure);
        if (value.Trim().Length > 0) Assert.DoesNotContain(value.Trim(), failure);
        Assert.DoesNotContain("secret", failure);
    }

    [Theory]
    [InlineData("https://api.hyperliquid.xyz/ws")]
    [InlineData("ws://api.hyperliquid.xyz/ws")]
    [InlineData("wss://user:secret@api.hyperliquid.xyz/ws")]
    [InlineData("wss://api.hyperliquid.xyz/ws?token=secret")]
    [InlineData(null)]
    public void Invalid_stream_hosts_fail_naming_only_the_key(string? value)
    {
        var options = new VenueHostOptions();
        options.Hyperliquid.StreamUrl = value;
        var failure = Assert.Single(new VenueHostValidation().Validate(null, options).Failures!);
        Assert.StartsWith("Vessel:Venues:Hyperliquid:StreamUrl ", failure);
        Assert.DoesNotContain("secret", failure);
    }

    [Fact]
    public void Default_hosts_are_todays_and_origins_are_normalized_with_a_trailing_slash()
    {
        var options = new VenueHostOptions();
        Assert.True(new VenueHostValidation().Validate(null, options).Succeeded);
        Assert.Equal(
            ["https://api.hyperliquid.xyz/", "https://api.rise.trade/", "https://mainnet.zklighter.elliot.ai/", "https://api.rh.lighter.xyz/"],
            new[] { "Hyperliquid", "Risex", "Lighter", "LighterRh" }.Select(s => options.BaseUri(s).AbsoluteUri));
        Assert.Equal("wss://api.hyperliquid.xyz/ws", options.StreamUri("Hyperliquid").AbsoluteUri);
        Assert.Equal(HyperliquidMarketStream.DefaultEndpoint, options.StreamUri("Hyperliquid"));
        options.LighterRh.BaseUrl = "https://rh.example.test";
        Assert.True(new VenueHostValidation().Validate(null, options).Succeeded);
        Assert.Equal("https://rh.example.test/", options.BaseUri("LighterRh").AbsoluteUri);
        options.Lighter.BaseUrl = "https://localhost:8443/";
        Assert.Equal("https://localhost:8443/", options.BaseUri("Lighter").AbsoluteUri);
    }
}
