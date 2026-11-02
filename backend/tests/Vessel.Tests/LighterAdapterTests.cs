using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vessel.Application.Credentials;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Microsoft.EntityFrameworkCore;
using Vessel.Infrastructure.Venues.Lighter;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed partial class LighterAdapterTests
{
    private const string Source = "9007199254740993";
    private const string Address = "0x1111111111111111111111111111111111111111";
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 18, 0, 0, TimeSpan.Zero);
    private static readonly string Token = $"ro:{Source}:single:{Now.AddDays(30).ToUnixTimeSeconds()}:abcdef";
    private const string Catalogue = """
        {"code":200,"order_book_details":[
         {"market_id":1,"market_type":"perp","symbol":"BTC","status":"active","is_frozen":false,
          "supported_size_decimals":5,"supported_price_decimals":1,"size_decimals":8,"price_decimals":4,
          "min_initial_margin_fraction":200,"maintenance_margin_fraction":120},
         {"market_id":4,"market_type":"perp","symbol":"1000PEPE","status":"active","is_frozen":false,
          "supported_size_decimals":0,"supported_price_decimals":6,"min_initial_margin_fraction":1000,"maintenance_margin_fraction":600},
         {"market_id":5,"market_type":"perp","symbol":"OLD","status":"inactive",
          "supported_size_decimals":1,"supported_price_decimals":2,"min_initial_margin_fraction":3333,"maintenance_margin_fraction":2000}],
         "spot_order_book_details":[{"market_id":2048,"market_type":"spot","symbol":"ETH/USDC"}]}
        """;
    private static readonly string Snapshot = $$"""
        {"code":200,"accounts":[{"index":{{Source}},"account_index":{{Source}},"account_type":0,
          "l1_address":"{{Address}}","total_asset_value":"1000.0012345678901234","available_balance":"939.9012345678901234","cross_initial_margin_requirement":"60.1",
          "assets":[{"asset_id":3,"symbol":"USDC","margin_balance":"998.0","balance":"100","locked_balance":"1"}],
          "positions":[{"market_id":1,"symbol":"BTC","position":"0.001","sign":-1,"initial_margin_fraction":"5.00",
          "margin_mode":0,"position_value":"80","avg_entry_price":"79000.1","unrealized_pnl":"-0.9999","allocated_margin":"0",
          "liquidation_price":"95123.4"}]}]}
        """;
    private static readonly string Discovery = $$"""
        {"code":200,"l1_address":"{{Address}}","sub_accounts":[
         {"index":{{Source}},"account_type":0,"l1_address":"{{Address}}","collateral":"998.0","available_balance":"-0.000000000000000000001"},
         {"index":9007199254740994,"account_type":1,"l1_address":"{{Address}}","collateral":"400.0","available_balance":"0.00"}]}
        """;
    private static readonly string Trade = $$"""
        {"trade_id":9223372036854775806,"trade_id_str":"9223372036854775806","market_id":1,
         "ask_account_id":{{Source}},"bid_account_id":2,"is_maker_ask":true,"type":"trade",
         "maker_position_size_before":"0.01","maker_position_sign_changed":false,"maker_fee":0,
         "taker_position_size_before":"0","size":"0.001","price":"80000.01","ask_account_pnl":"2.123400",
         "ask_id":9007199254740995,"ask_id_str":"9007199254740995","bid_id":3,
         "timestamp":{{Now.AddMinutes(-1).ToUnixTimeMilliseconds()}},"tx_hash":"abc123"}
        """;
    private static readonly string Order = $$"""
        {"order_index":9007199254740995,"order_id":"9007199254740995","owner_account_index":{{Source}},
         "market_index":1,"initial_base_amount":"0.01","remaining_base_amount":"0.005",
         "is_ask":true,"reduce_only":true,"price":"80000.01","trigger_price":"80000.1",
         "type":"stop-loss-limit","status":"pending","order_flags":0,"timestamp":{{Now.AddMinutes(-2).ToUnixTimeSeconds()}}}
        """;

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Change(string json, string key, JsonNode? value)
    {
        var node = JsonNode.Parse(json)!;
        if (value is null) node.AsObject().Remove(key); else node[key] = value;
        return node.ToJsonString();
    }

    private static string Trades(string row) => "{\"code\":200,\"trades\":[" + row + "]}";
    private static string Orders(string row) => "{\"code\":200,\"orders\":[" + row + "]}";
    private static LighterReader.Catalogue MarketMap => LighterReader.ReadCatalogue(Json(Catalogue));
    private static LighterReader.FillPage Fills(string row, bool standard = false) =>
        LighterReader.ReadFills(Json(Trades(row)), MarketMap, Source, Now, standard);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => Value;
    }
    private sealed class Credentials(string? value) : IAccountCredentialReader
    {
        public List<Guid> Reads { get; } = [];
        public List<Guid> Refused { get; } = [];
        public Task<string?> ReadAsync(Guid accountId, CancellationToken ct) { Reads.Add(accountId); return Task.FromResult(value); }
        public async Task<CredentialAttempt?> ReadAttemptAsync(Guid accountId, CancellationToken ct) =>
            await ReadAsync(accountId, ct) is { } token ? new(token, "synthetic-revision") : null;
        public Task MarkRefusedAsync(Guid accountId, string attemptedRevision, CancellationToken ct) { Refused.Add(accountId); return Task.CompletedTask; }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public List<(string Url, string? Token)> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage?>? Reply { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.PathAndQuery,
                request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null));
            if (Reply?.Invoke(request) is { } response) return Task.FromResult(response);
            var body = request.RequestUri.AbsolutePath.Split('/').Last() switch
            {
                "orderBookDetails" => Catalogue,
                "account" => Snapshot,
                "accountsByL1Address" => Discovery,
                "trades" => Trades(Trade),
                "accountActiveOrders" => Orders(Order),
                "accountInactiveOrders" => """{"code":200,"orders":[]}""",
                "accountLimits" => """{"code":200,"current_maker_fee_tick":0,"current_taker_fee_tick":0,"user_tier":"std","user_tier_name":"standard"}""",
                _ => """{"code":200}"""
            };
            return Task.FromResult(Response(body));
        }
    }
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static LighterReader Reader(Handler handler, Credentials? credentials = null, TimeProvider? clock = null) =>
        new(new HttpClient(new LighterAuthenticationHandler { InnerHandler = handler })
            { BaseAddress = new Uri("https://mainnet.zklighter.elliot.ai") }, clock ?? new Clock(), credentials ?? new(null));
    private static Account Account(string source = Source)
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "lighter", "Test");
        account.Configure(null, null, null, source);
        return account;
    }

    [Fact]
    public async Task Changed_copies_reach_conflict_recording_but_identical_reports_collapse()
    {
        var changed = Change(Trade, "price", JsonValue.Create("81000.01"));
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal)
            ? Response(Trades(Trade + "," + changed + "," + Trade)) : null };
        var result = await Reader(handler).ReadAsync(Source, default);
        Assert.Equal(2, result.Fills.Count);
        Assert.Equal(result.Fills[0].SourceFillId, result.Fills[1].SourceFillId);
        Assert.Equal([80000.01m, 81000.01m], result.Fills.Select(f => f.Price));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("null", null)]
    [InlineData("\"\"", null)]
    [InlineData("\"0\"", "0")]
    [InlineData("\"-0.0000000000000000000000000001\"", "-0.0000000000000000000000000001")]
    [InlineData("\"131.89123456789012345678901234\"", "131.89123456789012345678901234")]
    public async Task Available_margin_preserves_reported_value_and_distinguishes_missing_from_zero(string? field, string? expected)
    {
        var body = Snapshot.Replace("\"available_balance\":\"939.9012345678901234\",", field is null ? "" : "\"available_balance\":" + field + ",");
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/account", StringComparison.Ordinal) ? Response(body) : null };
        var snapshot = (await Reader(handler).ReadAsync(Source, default)).Snapshot;
        Assert.Equal(expected is null ? (decimal?)null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), snapshot.AvailableMarginUsd);
        Assert.Null(snapshot.WithdrawableUsd);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("\" \"")]
    [InlineData("\"1e3\"")]
    [InlineData("\"0.00000000000000000000000000001\"")]
    public async Task Invalid_available_margin_rejects_the_snapshot_without_rounding(string field)
    {
        var body = Snapshot.Replace("\"available_balance\":\"939.9012345678901234\",", field is null ? "" : "\"available_balance\":" + field + ",");
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/account", StringComparison.Ordinal) ? Response(body) : null };
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).ReadAsync(Source, default));
    }

    [Fact]
    public void Descriptor_advertises_only_implemented_reads()
    {
        var descriptor = LighterReader.Descriptor;
        Assert.Equal("account-index", descriptor.Source);
        Assert.True(descriptor.Capabilities.AccountDiscovery);
        Assert.True(descriptor.Capabilities.ReadOnlyCredential);
        Assert.False(descriptor.Capabilities.Stream);
        Assert.False(descriptor.Capabilities.MarketContext);
        Assert.False(descriptor.Capabilities.StablecoinWallet);
        Assert.DoesNotContain("1w", descriptor.CandleIntervals);
        Assert.Contains("500", descriptor.CandleNotice);
    }

    [Fact]
    public async Task Catalogue_preserves_canonical_multiplier_ticks_and_retired_history_map()
    {
        var instruments = await Reader(new()).ReadInstrumentsAsync(default);
        var btc = Assert.Single(instruments, i => i.ContractId == "BTC");
        Assert.Equal(5, btc.QuantityDecimals);
        Assert.Equal(0.1m, btc.PriceStep);
        Assert.Equal(50, btc.MaxLeverage);
        Assert.Equal(0.012m, btc.MaintenanceMarginFraction);
        Assert.Equal("1", btc.VenueContractId);
        Assert.Contains(instruments, i => i.ContractId == "1000PEPE");
        Assert.DoesNotContain(instruments, i => i.ContractId == "OLD");
        Assert.NotNull(MarketMap.ById("5"));
    }

    [Fact]
    public void Retired_zero_margin_market_keeps_identity_without_inventing_leverage()
    {
        var node = JsonNode.Parse(Catalogue)!;
        node["order_book_details"]![2]!["min_initial_margin_fraction"] = 0;
        node["order_book_details"]![2]!["maintenance_margin_fraction"] = 0;
        var map = LighterReader.ReadCatalogue(Json(node.ToJsonString()));
        Assert.False(map.ById("5")!.Active);
        Assert.Null(map.ById("5")!.Instrument);
        node["order_book_details"]![2]!["status"] = "active";
        Assert.Throws<VenueReadException>(() => LighterReader.ReadCatalogue(Json(node.ToJsonString())));
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-1")]
    [InlineData("1e3")]
    [InlineData("1.0")]
    [InlineData(" 1")]
    [InlineData("")]
    public void Invalid_source_ids_fail_without_rounding(string index) => Assert.Throws<VenueReadException>(() => LighterReader.Index(index));

    [Fact]
    public async Task A_negative_liquidation_price_is_an_invalid_response_never_unknown()
    {
        // Phase 2, item 8a (Sol round 1 on #41): zero means none; a negative price was silently read as unknown.
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/account", StringComparison.Ordinal)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(Snapshot.Replace("\"liquidation_price\":\"95123.4\"", "\"liquidation_price\":\"-1\""), System.Text.Encoding.UTF8, "application/json") }
            : null };
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).ReadAsync(Source, default));
    }

    [Fact]
    public async Task Public_snapshot_does_not_lookup_credentials_or_double_count_wallet()
    {
        var credentials = new Credentials(Token);
        var handler = new Handler();
        var result = await Reader(handler, credentials).ReadAsync(Source, default);
        Assert.Empty(credentials.Reads);
        Assert.All(handler.Requests, r => Assert.Null(r.Token));
        Assert.Equal(1000.0012345678901234m, result.Snapshot.AccountValueUsd);
        Assert.Equal(60.1m, result.Snapshot.MarginUsedUsd);
        Assert.Null(result.Snapshot.WithdrawableUsd);
        Assert.Null(result.Snapshot.StablecoinWallet);
        var position = Assert.Single(result.Snapshot.Positions);
        Assert.Equal(-0.001m, position.SignedQuantity);
        Assert.Equal(4m, position.MarginUsedUsd);
        Assert.Equal(20, position.Leverage);
        Assert.Equal("1", position.VenueContractId);
        Assert.Equal((95123.4m, MarginModes.Cross), (position.LiquidationPrice, position.MarginMode));
        Assert.Contains("Only fee-free", result.HistoryNotice);
    }

    [Fact]
    public void Snapshot_rejects_non_USDC_collateral_and_unknown_position_schema()
    {
        var node = JsonNode.Parse(Snapshot)!["accounts"]![0]!;
        node["assets"]![0]!["symbol"] = "ETH";
        Assert.Throws<VenueReadException>(() => LighterReader.ReadSnapshot(Json(node.ToJsonString()), MarketMap, Now));
        node = JsonNode.Parse(Snapshot)!["accounts"]![0]!;
        node["positions"]![0]!["sign"] = 0;
        Assert.Throws<VenueReadException>(() => LighterReader.ReadSnapshot(Json(node.ToJsonString()), MarketMap, Now));
        node = JsonNode.Parse(Snapshot)!["accounts"]![0]!;
        node["positions"]![0]!["position_value"] = "0.0000000000000000000000000001";
        Assert.Throws<VenueReadException>(() => LighterReader.ReadSnapshot(Json(node.ToJsonString()), MarketMap, Now));
    }

    [Fact]
    public void Fill_ids_pnl_and_raw_projection_are_exact_and_token_free()
    {
        var row = Change(Trade, "authorization", JsonValue.Create(Token));
        var fill = Assert.Single(Fills(row).Fills);
        Assert.Equal("9223372036854775806", fill.SourceFillId);
        Assert.Equal("9007199254740995", fill.OrderId);
        Assert.Equal(2.123400m, fill.ClosedPnlUsd);
        Assert.Equal(ExecutionFacts.Sell, fill.Side);
        Assert.Equal(ExecutionFacts.Close, fill.PositionEffect);
        Assert.Equal(ExecutionFacts.FeeReported, fill.FeeBasis);
        Assert.DoesNotContain(Token, fill.RawJson);
        Assert.DoesNotContain("authorization", fill.RawJson);
    }

    [Theory]
    [InlineData("buy", "0", "1", "open")]
    [InlineData("sell", "0", "1", "open")]
    [InlineData("buy", "2", "1", "open")]
    [InlineData("sell", "-2", "1", "open")]
    [InlineData("buy", "-2", "1", "close")]
    [InlineData("sell", "2", "2", "close")]
    [InlineData("buy", "-2", "3", "flip")]
    [InlineData("sell", "2", "3", "flip")]
    public void Effect_uses_signed_before_and_quantity(string side, string before, string quantity, string effect) =>
        Assert.Equal(effect, LighterReader.Effect(side, decimal.Parse(before), decimal.Parse(quantity)));

    [Fact]
    public void Buyer_taker_uses_its_own_before_fee_and_Pnl_not_counterparty_fields()
    {
        var row = Change(Trade, "ask_account_id", JsonValue.Create(2));
        row = Change(row, "bid_account_id", JsonValue.Create(long.Parse(Source)));
        row = Change(row, "taker_fee", JsonValue.Create(0));
        row = Change(row, "maker_fee", JsonValue.Create(100));
        row = Change(row, "bid_account_pnl", JsonValue.Create("0.00"));
        var fill = Assert.Single(Fills(row).Fills);
        Assert.Equal(ExecutionFacts.Buy, fill.Side);
        Assert.Equal(ExecutionFacts.Open, fill.PositionEffect);
        Assert.Equal(0, fill.ClosedPnlUsd);
    }

    [Fact]
    public void Unknown_Pnl_is_not_zero_and_nonzero_applicable_fees_are_not_hidden()
    {
        Assert.Empty(Fills(Change(Trade, "ask_account_pnl", null)).Fills);
        Assert.Equal(1, Fills(Change(Trade, "ask_account_pnl", null)).MissingPnl);
        Assert.Equal(1, Fills(Change(Trade, "maker_fee", JsonValue.Create(100)), standard: true).Fees);
        Assert.Equal(1, Fills(Change(Trade, "maker_fee", JsonValue.Create(-1)), standard: true).Fees);
        Assert.Equal(1, Fills(Change(Trade, "integrator_maker_fee", JsonValue.Create(1)), standard: true).Fees);
        Assert.Single(Fills(Change(Trade, "maker_fee", null)).Fills);
        Assert.Single(Fills(Change(Trade, "maker_fee", null), standard: true).Fills);
    }

    [Theory]
    [InlineData(true, true, "0")]
    [InlineData(true, false, "0")]
    [InlineData(false, true, "0")]
    [InlineData(false, false, "0")]
    [InlineData(true, true, "-0.01")]
    [InlineData(true, false, "-0.01")]
    [InlineData(false, true, "0.01")]
    [InlineData(false, false, "0.01")]
    public void Omitted_opening_Pnl_is_zero_for_the_queried_side_and_role(bool ask, bool maker, string before)
    {
        foreach (var exchange in LighterExchange.All)
        foreach (var absent in new[] { "missing", "null", "empty" })
        {
            var row = JsonNode.Parse(Trade)!.AsObject();
            row["ask_account_id"] = ask ? long.Parse(Source) : 2;
            row["bid_account_id"] = ask ? 2 : long.Parse(Source);
            row["is_maker_ask"] = ask == maker;
            row[(maker ? "maker" : "taker") + "_position_size_before"] = before;
            // Only the other counterparty has a PnL and a fee.
            row.Remove("maker_fee");
            row.Remove("ask_account_pnl");
            row[ask ? "bid_account_pnl" : "ask_account_pnl"] = "123.45";
            row[(maker ? "taker" : "maker") + "_fee"] = 100;
            if (absent != "missing") row[ask ? "ask_account_pnl" : "bid_account_pnl"] =
                absent == "null" ? null : JsonValue.Create("");
            var markets = LighterReader.ReadCatalogue(Json(Catalogue), exchange);
            var page = LighterReader.ReadFills(Json(Trades(row.ToJsonString())), markets, Source, Now, false);
            var fill = Assert.Single(page.Fills);
            Assert.Equal(ExecutionFacts.Open, fill.PositionEffect);
            Assert.Equal(0m, fill.ClosedPnlUsd);
            Assert.Equal(0m, fill.Fee);
            Assert.Equal(ExecutionFacts.FeeReported, fill.FeeBasis);
            Assert.Equal(exchange.Collateral, fill.FeeToken);
            Assert.Equal(0, page.MissingPnl);
            Assert.Equal(0, page.Fees);
            Assert.Equal(JsonValueKind.Null, Json(fill.RawJson).GetProperty("realized_pnl").ValueKind);
        }
    }

    [Theory]
    [InlineData(true, true, "0.01", "0.001")]
    [InlineData(true, false, "0.01", "0.01")]
    [InlineData(false, true, "-0.01", "0.001")]
    [InlineData(false, false, "-0.01", "0.01")]
    [InlineData(true, true, "0.01", "0.02")]
    [InlineData(true, false, "0.01", "0.02")]
    [InlineData(false, true, "-0.01", "0.02")]
    [InlineData(false, false, "-0.01", "0.02")]
    public void Missing_reduction_or_flip_Pnl_is_never_inferred(bool ask, bool maker, string before, string quantity)
    {
        foreach (var exchange in LighterExchange.All)
        foreach (var absent in new[] { "missing", "null", "empty" })
        {
            var row = JsonNode.Parse(Trade)!.AsObject();
            row["ask_account_id"] = ask ? long.Parse(Source) : 2;
            row["bid_account_id"] = ask ? 2 : long.Parse(Source);
            row["is_maker_ask"] = ask == maker;
            row[(maker ? "maker" : "taker") + "_position_size_before"] = before;
            row["size"] = quantity;
            row.Remove("ask_account_pnl");
            row[ask ? "bid_account_pnl" : "ask_account_pnl"] = "123.45";
            if (absent != "missing") row[ask ? "ask_account_pnl" : "bid_account_pnl"] =
                absent == "null" ? null : JsonValue.Create("");
            var markets = LighterReader.ReadCatalogue(Json(Catalogue), exchange);
            var page = LighterReader.ReadFills(Json(Trades(row.ToJsonString())), markets, Source, Now, false);
            Assert.Empty(page.Fills);
            Assert.Equal(1, page.MissingPnl);
            Assert.Equal(0, page.Fees);
        }
    }

    [Theory]
    [InlineData("maker_fee", "null")]
    [InlineData("integrator_maker_fee", "null")]
    public void Null_zero_fee_fields_follow_the_official_client_convention(string key, string value)
    {
        var row = JsonNode.Parse(Trade)!.AsObject();
        row[key] = JsonNode.Parse(value);
        Assert.Equal(0m, Assert.Single(Fills(row.ToJsonString()).Fills).Fee);
    }

    [Theory]
    [InlineData("maker_fee", "\"0\"")]
    [InlineData("maker_fee", "true")]
    [InlineData("integrator_maker_fee", "\"0\"")]
    [InlineData("integrator_maker_fee", "true")]
    public void Malformed_fees_do_not_become_zero(string key, string value) =>
        Assert.Throws<VenueReadException>(() => Fills(Change(Trade, key, JsonNode.Parse(value))));

    [Fact]
    public async Task Public_history_retains_zero_omitted_executions_without_a_token_or_tier_lookup()
    {
        foreach (var exchange in LighterExchange.All)
        {
            var rows = Enumerable.Range(1, 30).Select(i =>
            {
                var row = JsonNode.Parse(Trade)!.AsObject();
                row["trade_id"] = i;
                row["trade_id_str"] = i.ToString();
                row.Remove("maker_fee");
                if (i <= 20)
                {
                    row["maker_position_size_before"] = "-0.01";
                    row.Remove("ask_account_pnl");
                }
                else if (i == 30) row["size"] = "0.02";
                return row.ToJsonString();
            });
            var handler = new Handler { Reply = request =>
                request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal)
                    ? Response(Trades(string.Join(",", rows)))
                    : request.RequestUri.AbsolutePath.EndsWith("/account", StringComparison.Ordinal)
                        ? Response(Snapshot.Replace("USDC", exchange.Collateral, StringComparison.Ordinal)) : null };
            var reader = new LighterReader(new HttpClient(new LighterAuthenticationHandler { InnerHandler = handler })
                { BaseAddress = new Uri("https://fixture.invalid") }, new Clock(), new Credentials(null), exchange: exchange);
            var result = await reader.ReadAsync(Source, default);
            Assert.Equal(30, result.Fills.Count);
            Assert.Equal(20, result.Fills.Count(f => f.PositionEffect == ExecutionFacts.Open && f.ClosedPnlUsd == 0m));
            Assert.Equal(9, result.Fills.Count(f => f.PositionEffect == ExecutionFacts.Close && f.ClosedPnlUsd == 2.123400m));
            Assert.Equal(1, result.Fills.Count(f => f.PositionEffect == ExecutionFacts.Flip && f.ClosedPnlUsd == 2.123400m));
            Assert.All(result.Fills, f => Assert.Equal(0m, f.Fee));
            Assert.DoesNotContain("omitted.", result.HistoryNotice);
            Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("accountLimits", StringComparison.Ordinal));
            Assert.All(handler.Requests, r => Assert.Null(r.Token));
        }
    }

    [Fact]
    public void History_notice_fits_its_column_even_with_every_sentence_at_the_largest_count()
    {
        // Conservative: no single refresh adds every sentence, and the omitted counts share at most 500 executions.
        var notice = string.Join(" ", LighterReader.HistoryNotice, LighterReader.NonzeroRateNotice,
            LighterReader.CredentialRefusedNotice, LighterReader.HistoryRetryNotice, LighterReader.HistoryUnavailableNotice,
            LighterReader.PageLimitNotice, LighterReader.OmissionNotice(500, 500, 500, 500, 500, 500));
        var options = new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql("Host=model-only").Options;
        using var db = new VesselDbContext(options, new CoreOwner(Guid.NewGuid()));
        var limit = db.Model.FindEntityType(typeof(Vessel.Domain.Accounts.Account))!.FindProperty(nameof(Vessel.Domain.Accounts.Account.HistoryNotice))!.GetMaxLength();
        Assert.NotNull(limit);
        Assert.Equal(1000, limit);
        Assert.True(notice.Length <= limit, $"{notice.Length} characters exceed the {limit}-character HistoryNotice column.");
        foreach (var coverage in AccountHistoryNoticeBudget.CombinedCases())
        {
            var account = new Account(Guid.NewGuid(), Guid.NewGuid(), coverage.Venue, "Coverage");
            account.RecordSync(DateTimeOffset.UnixEpoch, "Prior public coverage.");
            account.RecordOrderHistoryNotice(coverage.Orders);
            account.RecordSync(DateTimeOffset.UnixEpoch.AddSeconds(1), coverage.Public);
            var expected = coverage.Public + "\nOrder history: " + coverage.Orders;
            Assert.Equal(expected, account.HistoryNotice);
            Assert.True(expected.Length <= limit,
                $"{coverage.Venue}: {expected.Length} combined characters exceed the {limit}-character HistoryNotice column.");
            account.RecordOrderHistoryNotice(coverage.Orders);
            Assert.Equal(expected, account.HistoryNotice);
        }
    }

    [Fact]
    public void Self_trade_is_omitted_and_counted_instead_of_failing_the_page()
    {
        var page = Fills(Trade + "," + Change(Change(Trade, "bid_account_id", JsonValue.Create(long.Parse(Source))),
            "trade_id", JsonValue.Create(5)).Replace("\"trade_id_str\":\"9223372036854775806\"", "\"trade_id_str\":\"5\""));
        Assert.Single(page.Fills);
        Assert.Equal(1, page.SelfTrades);
    }

    [Theory]
    [InlineData("ask_id_str", "\"9007199254740994\"")]
    [InlineData("price", "\"0.12345678901234567890123456789\"")]
    [InlineData("size", "\"NaN\"")]
    [InlineData("ask_account_pnl", "true")]
    [InlineData("maker_position_size_before", "true")]
    [InlineData("is_maker_ask", "\"false\"")]
    public void Malformed_fill_schemas_fail_closed(string key, string value) =>
        Assert.Throws<VenueReadException>(() => Fills(Change(Trade, key, JsonNode.Parse(value) ?? JsonValue.Create((string?)null)!)));

    [Fact]
    public async Task Discovery_never_turns_int64_ids_into_double_or_collateral_into_equity()
    {
        var accounts = await Reader(new()).DiscoverAsync(Address, default);
        Assert.Equal(Source, accounts[0].SourceId);
        Assert.Equal("main", accounts[0].AccountType);
        Assert.Equal("9007199254740994", accounts[1].SourceId);
        Assert.Equal("subaccount", accounts[1].AccountType);
        Assert.All(accounts, a => Assert.Null(a.AccountValueUsd));
        Assert.Equal("998.0", accounts[0].CollateralUsd);
        Assert.Equal("-0.000000000000000000001", accounts[0].AvailableBalanceUsd);
        Assert.Equal("400.0", accounts[1].CollateralUsd);
        Assert.Equal("0.00", accounts[1].AvailableBalanceUsd);
    }

    [Fact]
    public async Task Discovery_names_use_exact_index_join_across_pages_and_header_only_token()
    {
        var all = Token.Replace(":single:", ":all:");
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/accountMetadata", StringComparison.Ordinal)
            ? Response(request.RequestUri.Query.Contains("cursor=", StringComparison.Ordinal)
                ? $$"""{"code":200,"account_metadatas":[{"account_index":{{Source}},"name":"Long term"}]}"""
                : """{"code":200,"account_metadatas":[{"account_index":2,"name":"Excluded pool"},{"account_index":9007199254740994,"name":"Swing / ETH"}],"next_cursor":"next"}""")
            : null };
        var result = await Reader(handler).DiscoverAsync(Address, all, default);
        Assert.Equal(["Long term", "Swing / ETH"], result.Select(a => a.Name));
        Assert.Equal("400.0", result[1].CollateralUsd);
        Assert.Null(handler.Requests[0].Token);
        Assert.All(handler.Requests.Skip(1), r =>
        {
            Assert.Equal(all, r.Token);
            Assert.Contains("by=l1_address", r.Url);
            Assert.DoesNotContain(all, r.Url);
        });
    }

    [Fact]
    public async Task Single_scope_names_leave_other_accounts_and_blank_names_as_index_labels()
    {
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/accountMetadata", StringComparison.Ordinal)
            ? Response($$"""{"code":200,"account_metadatas":[{"account_index":{{Source}},"name":""}]}""") : null };
        var result = await Reader(handler).DiscoverAsync(Address, Token, default);
        Assert.Equal("Main account", result[0].Name);
        Assert.Equal("Subaccount 9007199254740994", result[1].Name);
        Assert.Contains("by=index&value=" + Source, handler.Requests[1].Url);
    }

    [Theory]
    [InlineData("ro:1:all:9999999999:abcdef")]
    [InlineData("ro:9007199254740994:all:9999999999:abcdef")]
    [InlineData("trading-key")]
    public async Task Names_reject_foreign_wallet_nonmaster_all_scope_and_non_read_only_tokens(string token)
    {
        var handler = new Handler();
        await Assert.ThrowsAsync<VenueCredentialRejectedException>(() => Reader(handler).DiscoverAsync(Address, token, default));
        Assert.DoesNotContain(handler.Requests, r => r.Token is not null);
    }

    [Theory]
    [InlineData("""[{"account_index":2,"name":"Wrong account"}]""")]
    [InlineData("""[{"account_index":9007199254740993,"name":"A"},{"account_index":9007199254740993,"name":"B"}]""")]
    [InlineData("""[{"account_index":9007199254740993,"name":123}]""")]
    public async Task Names_fail_closed_for_foreign_duplicate_or_malformed_metadata(string rows)
    {
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/accountMetadata", StringComparison.Ordinal)
            ? Response($$"""{"code":200,"account_metadatas":{{rows}}}""") : null };
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).DiscoverAsync(Address, Token, default));
    }

    [Fact]
    public async Task Names_refusal_or_secret_echo_never_returns_the_secret()
    {
        foreach (var body in new[]
        {
            $$"""{"code":20013,"message":"{{Token}}"}""",
            $$"""{"code":200,"account_metadatas":[{"account_index":{{Source}},"name":"{{Token}}"}]}"""
        })
        {
            var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/accountMetadata", StringComparison.Ordinal)
                ? Response(body) : null };
            var error = body.Contains("\"code\":20013", StringComparison.Ordinal)
                ? (Exception)await Assert.ThrowsAsync<VenueCredentialRejectedException>(() => Reader(handler).DiscoverAsync(Address, Token, default))
                : await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).DiscoverAsync(Address, Token, default));
            Assert.DoesNotContain(Token, error.ToString());
        }
    }

    [Fact]
    public async Task Names_pagination_stops_after_five_pages_without_partial_results()
    {
        var pages = 0;
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/accountMetadata", StringComparison.Ordinal)
            ? Response($$"""{"code":200,"account_metadatas":[],"next_cursor":"page{{++pages}}"}""") : null };
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).DiscoverAsync(Address, Token, default));
        Assert.Equal(5, pages);
    }

    [Theory]
    [InlineData("\"NaN\"")]
    [InlineData("123")]
    [InlineData("\" \"")]
    public async Task Malformed_discovery_balances_are_not_silently_zeroed(string value)
    {
        var handler = new Handler { Reply = _ => Response(Discovery.Replace("\"400.0\"", value, StringComparison.Ordinal)) };
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).DiscoverAsync(Address, default));
    }

    [Fact]
    public async Task Empty_discovery_balances_are_unavailable_not_zero()
    {
        // Live mainnet shape: every row sends available_balance as "".
        var handler = new Handler { Reply = _ => Response(Discovery
            .Replace("\"-0.000000000000000000001\"", "\"\"", StringComparison.Ordinal)
            .Replace("\"0.00\"", "\"\"", StringComparison.Ordinal)
            .Replace("\"400.0\"", "\"\"", StringComparison.Ordinal)) };
        var accounts = await Reader(handler).DiscoverAsync(Address, default);
        Assert.Equal([Source, "9007199254740994"], accounts.Select(a => a.SourceId));
        Assert.Equal("998.0", accounts[0].CollateralUsd);
        Assert.All(accounts, a => Assert.Null(a.AvailableBalanceUsd));
        Assert.Null(accounts[1].CollateralUsd);
    }

    [Theory]
    [InlineData("canonical:trading:token")]
    [InlineData("ro:1:single:9999999999:ABCDEF")]
    [InlineData("ro:1:single:9999999999:abc\n")]
    [InlineData("ro:9223372036854775808:single:9999999999:abcdef")]
    [InlineData("ro:1:single:1000000000:abcdef")]
    [InlineData("ro:2:single:9999999999:abcdef")]
    public async Task Invalid_wrong_scope_and_expired_tokens_make_no_network_calls(string token)
    {
        var handler = new Handler();
        var error = token == "ro:1:single:1000000000:abcdef"
            ? (Exception)await Assert.ThrowsAsync<VenueCredentialRejectedException>(() => Reader(handler).VerifyAsync(Source, token, default))
            : await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).VerifyAsync(Source, token, default));
        Assert.DoesNotContain(token, error.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Oversized_tokens_are_rejected_before_network()
    {
        var handler = new Handler();
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).VerifyAsync(Source, Token + new string('a', 512), default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Verification_binds_discovery_and_sends_token_only_in_header()
    {
        var handler = new Handler();
        var verified = await Reader(handler).VerifyAsync(Source, Token, default);
        Assert.Equal("single", verified.Scope);
        Assert.Equal(Now.AddDays(30), verified.ExpiresAt);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Null(handler.Requests[0].Token);
        Assert.Null(handler.Requests[1].Token);
        Assert.Equal(Token, handler.Requests[2].Token);
        Assert.All(handler.Requests, r => { Assert.DoesNotContain("auth=", r.Url); Assert.DoesNotContain(Token, r.Url); });
    }

    [Fact]
    public async Task All_scope_uses_authoritative_master_and_target_membership()
    {
        const string sub = "9007199254740994";
        var all = Token.Replace(":single:", ":all:");
        var handler = new Handler { Reply = request =>
            request.RequestUri!.AbsolutePath.EndsWith("/account", StringComparison.Ordinal)
                ? Response(Snapshot.Replace(Source, sub))
                : request.RequestUri.AbsolutePath.EndsWith("/accountsByL1Address", StringComparison.Ordinal)
                    ? Response(Discovery) : Response("""{"code":200,"orders":[]}""") };
        Assert.Equal("all", (await Reader(handler).VerifyAsync(sub, all, default)).Scope);
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).VerifyAsync(sub, all.Replace(Source, sub), default));
    }

    [Fact]
    public async Task Discovery_foreign_address_and_duplicate_IDs_are_rejected()
    {
        var handler = new Handler { Reply = _ => Response(Discovery.Replace(Address, "0x2222222222222222222222222222222222222222")) };
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).DiscoverAsync(Address, default));
        handler.Reply = _ => Response(Discovery.Replace("9007199254740994", Source));
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).DiscoverAsync(Address, default));
    }

    [Fact]
    public async Task Secret_venue_errors_and_transport_exceptions_are_sanitized()
    {
        var handler = new Handler();
        handler.Reply = request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/account", StringComparison.Ordinal)) return Response(Snapshot);
            if (request.RequestUri.AbsolutePath.EndsWith("/accountsByL1Address", StringComparison.Ordinal)) return Response(Discovery);
            return Response($$"""{"code":401,"message":"{{Token}}"}""", HttpStatusCode.Unauthorized);
        };
        Exception error = await Assert.ThrowsAsync<VenueCredentialRejectedException>(() => Reader(handler).VerifyAsync(Source, Token, default));
        Assert.DoesNotContain(Token, error.ToString());
        handler.Reply = _ => throw new HttpRequestException(Token);
        error = await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).VerifyAsync(Source, Token, default));
        Assert.DoesNotContain(Token, error.ToString());
    }

    [Fact]
    public async Task Credentialless_orders_fail_actionably_and_account_overload_uses_account_id()
    {
        var handler = new Handler();
        var credentials = new Credentials(Token);
        var reader = Reader(handler, credentials);
        await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadOrdersAsync(Source, default));
        Assert.Empty(handler.Requests);
        var account = Account();
        var result = await reader.ReadOrdersAsync(account, default);
        Assert.Equal(account.Id, Assert.Single(credentials.Reads));
        var order = Assert.Single(result.Orders);
        Assert.Equal("9007199254740995", order.OrderId);
        Assert.Equal(80000.1m, order.TriggerPrice);
        Assert.True(order.ReduceOnly);
        Assert.Equal(VenueOrderStatus.Open, order.Status);
        Assert.Equal("1", order.VenueContractId);
        Assert.All(handler.Requests.Where(r => r.Url.Contains("Orders")), r => Assert.Equal(Token, r.Token));
    }

    [Theory]
    [InlineData("open", VenueOrderStatus.Open)]
    [InlineData("in-progress", VenueOrderStatus.Open)]
    [InlineData("filled", VenueOrderStatus.Filled)]
    [InlineData("canceled-oco", VenueOrderStatus.Canceled)]
    [InlineData("future-status", VenueOrderStatus.Other)]
    public void Order_status_preserves_venue_word(string status, VenueOrderStatus expected)
    {
        var row = Change(Order, "status", JsonValue.Create(status));
        var order = Assert.Single(LighterReader.ReadOrders(Json(Orders(row)), MarketMap, Source, Now, 100));
        Assert.Equal(expected, order.Status);
        Assert.Equal(status, order.VenueStatus);
    }

    [Fact]
    public void Spot_and_unverified_zero_sized_orders_are_not_normalized()
    {
        var row = Change(Order, "market_index", JsonValue.Create(2048));
        Assert.Empty(LighterReader.ReadOrders(Json(Orders(row)), MarketMap, Source, Now, 100));
        row = Change(Order, "initial_base_amount", JsonValue.Create("0"));
        row = Change(row, "remaining_base_amount", JsonValue.Create("0"));
        Assert.Empty(LighterReader.ReadOrders(Json(Orders(row)), MarketMap, Source, Now, 100));
    }

    [Fact]
    public void Candles_preserve_raw_decimal_text_and_do_not_confuse_trade_id_with_count()
    {
        var t = Now.AddHours(-1).ToUnixTimeMilliseconds();
        var root = $$"""{"code":200,"r":"1h","c":[{"t":{{t}},"o":1.2300,"h":2,"l":1,"c":1.5,"i":9223372036854775806}]}""";
        var candle = Assert.Single(LighterReader.ReadCandles(Json(root), "1h", t, t + 3600000, 3600000));
        Assert.Equal("1.2300", candle.Open);
        Assert.Equal("0", candle.Volume);
        Assert.Null(candle.Trades);
        Assert.Equal(t + 3599999, candle.CloseTime);
        Assert.Throws<VenueReadException>(() => LighterReader.ReadCandles(Json(root.Replace("\"r\":\"1h\"", "\"r\":\"1m\"")), "1h", t, t + 3600000, 3600000));
        Assert.Throws<VenueReadException>(() => LighterReader.ReadCandles(Json(root.Replace("\"h\":2", "\"h\":1")), "1h", t, t + 3600000, 3600000));
    }

    [Fact]
    public async Task Candle_requests_are_one_page_native_perps_and_seconds_not_milliseconds()
    {
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/orderBookDetails", StringComparison.Ordinal)
            ? Response(Catalogue) : Response("""{"code":200,"r":"1h","c":[]}""") };
        var reader = Reader(handler);
        await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadCandlesAsync("BTC", "1w", 0, Now.ToUnixTimeMilliseconds(), default));
        await reader.ReadCandlesAsync("BTC", "1h", 0, Now.ToUnixTimeMilliseconds(), default);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("count_back=500", handler.Requests[1].Url);
        Assert.Contains("end_timestamp=" + Now.ToUnixTimeSeconds(), handler.Requests[1].Url);
        Assert.All(handler.Requests, r => Assert.Null(r.Token));
    }

    [Fact]
    public async Task Duplicate_JSON_properties_and_oversized_responses_fail_safely()
    {
        var handler = new Handler { Reply = _ => Response("""{"code":200,"code":200,"order_book_details":[]}""") };
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).ReadInstrumentsAsync(default));
        handler.Reply = _ => Response(new string(' ', 4 * 1024 * 1024 + 1));
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).ReadInstrumentsAsync(default));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(-1, 0)]
    public async Task Either_nonzero_account_fee_rate_blocks_fill_import(int maker, int taker)
    {
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/accountLimits", StringComparison.Ordinal)
            ? Response($$"""{"code":200,"current_maker_fee_tick":{{maker}},"current_taker_fee_tick":{{taker}},"user_tier":"plus"}""")
            : null };
        var result = await Reader(handler, new(Token)).ReadAsync(Account(), default);
        Assert.Empty(result.Fills);
        Assert.DoesNotContain(handler.Requests, r => r.Url.StartsWith("/api/v1/trades", StringComparison.Ordinal));
        Assert.Contains("nonzero maker or taker rate", result.HistoryNotice);
        Assert.Equal(1000.0012345678901234m, result.Snapshot.AccountValueUsd);
    }

    [Fact]
    public async Task Token_account_reads_do_not_leak_headers_to_later_public_overload()
    {
        var handler = new Handler();
        var reader = Reader(handler, new(Token));
        var authenticated = await reader.ReadAsync(Account(), default);
        Assert.Equal(ExecutionFacts.FeeStandardAccountFree, Assert.Single(authenticated.Fills).FeeBasis);
        Assert.Contains(handler.Requests, r => r.Url.Contains("/trades") && r.Token == Token);
        handler.Requests.Clear();
        var publicRead = await reader.ReadAsync(Source, default);
        Assert.Equal(ExecutionFacts.FeeReported, Assert.Single(publicRead.Fills).FeeBasis);
        Assert.All(handler.Requests, r => Assert.Null(r.Token));
    }

    [Fact]
    public async Task Documented_standard_tier_code_allows_omitted_zero_rate_with_explicit_fee_basis()
    {
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal)
            ? Response(Trades(Change(Trade, "maker_fee", null))) : null };
        var result = await Reader(handler, new(Token)).ReadAsync(Account(), default);
        var fill = Assert.Single(result.Fills);
        Assert.Equal(0m, fill.Fee);
        Assert.Equal(ExecutionFacts.FeeStandardAccountFree, fill.FeeBasis);
    }

    [Fact]
    public async Task Token_expiring_after_public_snapshot_falls_back_without_losing_the_snapshot()
    {
        var clock = new Clock();
        var handler = new Handler { Reply = request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/account", StringComparison.Ordinal))
                clock.Value = Now.AddDays(31);
            return null;
        } };
        var result = await Reader(handler, new(Token), clock).ReadAsync(Account(), default);
        Assert.Equal(1000.0012345678901234m, result.Snapshot.AccountValueUsd);
        Assert.Contains("Public reads only", result.HistoryNotice);
        Assert.Equal(ExecutionFacts.FeeReported, Assert.Single(result.Fills).FeeBasis);
        Assert.All(handler.Requests, request => Assert.Null(request.Token));
    }

    [Fact]
    public async Task Fill_and_order_pagination_stop_after_five_pages_with_notice()
    {
        var fillPages = 0;
        var orderPages = 0;
        var handler = new Handler { Reply = request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal))
                return Response($$"""{"code":200,"trades":[{{Trade}}],"next_cursor":"page{{++fillPages}}"}""");
            if (request.RequestUri.AbsolutePath.EndsWith("/accountInactiveOrders", StringComparison.Ordinal))
                return Response($$"""{"code":200,"orders":[],"next_cursor":"page{{++orderPages}}"}""");
            return null;
        } };
        var reader = Reader(handler, new(Token));
        var result = await reader.ReadAsync(Source, default);
        Assert.Equal(5, fillPages);
        Assert.Single(result.Fills);
        Assert.Contains("five-page history limit", result.HistoryNotice);
        var orders = await reader.ReadOrdersAsync(Account(), default);
        Assert.Equal(5, orderPages);
        Assert.Contains("five-page history limit", orders.Notice);
    }

    [Fact]
    public async Task Repeated_cursor_and_overlarge_fill_page_fail_instead_of_looping_or_truncating()
    {
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal)
            ? Response("""{"code":200,"trades":[],"next_cursor":"same"}""") : null };
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).ReadAsync(Source, default));
        Assert.Equal(2, handler.Requests.Count(r => r.Url.Contains("/trades")));
        handler.Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal)
            ? Response("""{"code":200,"trades":[""" + string.Join(',', Enumerable.Repeat(Trade, 101)) + "]}") : null;
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).ReadAsync(Source, default));
    }

    [Fact]
    public async Task Discovery_cap_is_visible_and_does_not_return_incomplete_scope_membership()
    {
        var page = 0;
        var handler = new Handler { Reply = _ =>
        {
            page++;
            return Response($$"""
                {"code":200,"l1_address":"{{Address}}","sub_accounts":[
                 {"index":{{page}},"account_type":{{(page == 1 ? 0 : 1)}},"l1_address":"{{Address}}"}],
                 "next_cursor":"page{{page}}"}
                """);
        } };
        var error = await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).DiscoverAsync(Address, default));
        Assert.Equal(5, page);
        Assert.Contains("five pages", error.Message);
    }

    [Fact]
    public async Task Venue_echoing_secret_as_cursor_cannot_move_it_into_a_request_URL()
    {
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/accountInactiveOrders", StringComparison.Ordinal)
            ? Response($$"""{"code":200,"orders":[],"next_cursor":"{{Token}}"}""") : null };
        var error = await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler, new(Token)).ReadOrdersAsync(Account(), default));
        Assert.DoesNotContain(Token, error.ToString());
        Assert.All(handler.Requests, r =>
        {
            Assert.DoesNotContain(Token, r.Url);
            Assert.DoesNotContain(Uri.EscapeDataString(Token), r.Url);
        });
    }

    [Fact]
    public async Task Disabled_accounts_and_caller_cancellation_make_no_network_calls()
    {
        var handler = new Handler();
        var account = Account();
        account.UpdateSettings(account.Name, null, false);
        var credentials = new Credentials(Token);
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler, credentials).ReadAsync(account, default));
        Assert.Empty(credentials.Reads);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(handler).ReadAsync(Source, cancellation.Token));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Public_history_auth_refusal_keeps_verified_snapshot_with_visible_notice(HttpStatusCode status)
    {
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal)
            ? Response("""{"message":"history requires authentication"}""", status) : null };
        var result = await Reader(handler).ReadAsync(Source, default);
        Assert.Equal(1000.0012345678901234m, result.Snapshot.AccountValueUsd);
        Assert.Empty(result.Fills);
        Assert.Contains("history is unavailable", result.HistoryNotice);
        Assert.Equal(1, handler.Requests.Count(r => r.Url.Contains("/trades")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refused_history_token_retries_public_only_once_without_losing_snapshot(bool publicAccepted)
    {
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal)
            && (request.Headers.Contains("Authorization") || !publicAccepted)
                ? Response($$"""{"message":"{{Token}}"}""", HttpStatusCode.Unauthorized) : null };
        var credentials = new Credentials(Token);
        var account = Account();
        var result = await Reader(handler, credentials).ReadAsync(account, default);
        Assert.Equal([account.Id], credentials.Refused);
        Assert.Equal(1000.0012345678901234m, result.Snapshot.AccountValueUsd);
        Assert.Equal(publicAccepted ? 1 : 0, result.Fills.Count);
        Assert.DoesNotContain(Token, result.HistoryNotice);
        var history = handler.Requests.Where(r => r.Url.Contains("/trades")).ToArray();
        Assert.Equal(2, history.Length);
        Assert.Equal(Token, history[0].Token);
        Assert.Null(history[1].Token);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Order_authentication_refusal_keeps_its_category_and_marks_only_the_attempted_credential(HttpStatusCode status)
    {
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/accountActiveOrders", StringComparison.Ordinal)
            ? Response($$"""{"message":"{{Token}}"}""", status) : null };
        var credentials = new Credentials(Token);
        var account = Account();
        var error = await Assert.ThrowsAsync<VenueCredentialRejectedException>(() =>
            Reader(handler, credentials).ReadOrdersAsync(account, default));
        Assert.Equal([account.Id], credentials.Refused);
        Assert.DoesNotContain(Token, error.ToString());
        Assert.DoesNotContain(handler.Requests, request => request.Url.Contains("/accountInactiveOrders"));
    }

    [Fact]
    public async Task Refused_limits_token_is_reported_once_and_accepted_token_is_not()
    {
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/accountLimits", StringComparison.Ordinal)
            ? Response("""{"code":401}""", HttpStatusCode.Unauthorized) : null };
        var refused = new Credentials(Token);
        var account = Account();
        var result = await Reader(handler, refused).ReadAsync(account, default);
        Assert.Contains(LighterReader.CredentialRefusedNotice, result.HistoryNotice);
        Assert.Equal([account.Id], refused.Refused);
        var accepted = new Credentials(Token);
        await Reader(new Handler(), accepted).ReadAsync(account, default);
        Assert.Empty(accepted.Refused);
    }

    [Fact]
    public async Task Malformed_history_or_fee_schema_is_not_hidden_by_auth_fallback()
    {
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal)
            ? Response("""{"code":200,"trades":{}}""") : null };
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).ReadAsync(Source, default));
        handler.Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/accountLimits", StringComparison.Ordinal)
            ? Response("""{"code":200,"current_maker_fee_tick":0,"current_taker_fee_tick":"unknown","user_tier":"std","user_tier_name":"standard"}""")
            : null;
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler, new(Token)).ReadAsync(Account(), default));
    }

    [Theory]
    [InlineData(20013, "invalid auth", true)]
    [InlineData(61005, "token not found", true)]
    [InlineData(61006, "token revoked", true)]
    [InlineData(20001, "invalid param : auth query param and Authorization header are empty", true)]
    [InlineData(20001, "invalid param : price", false)]
    public async Task HTTP400_classifies_only_known_auth_refusals(int code, string message, bool auth)
    {
        var handler = new Handler { Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal)
            ? Response(JsonSerializer.Serialize(new { code, message }), HttpStatusCode.BadRequest) : null };
        if (auth)
        {
            var result = await Reader(handler).ReadAsync(Source, default);
            Assert.Empty(result.Fills);
            Assert.Contains("history is unavailable", result.HistoryNotice);
        }
        else await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).ReadAsync(Source, default));
    }
}
