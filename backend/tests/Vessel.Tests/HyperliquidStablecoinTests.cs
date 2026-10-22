using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed partial class HyperliquidAdapterTests
{
    // Official spotMeta/spotClearinghouseState shapes, with synthetic balances.
    // Curated public token IDs supplied from the October 1, 2026 metadata check.
    private const string SpotMeta = """
        {
          "tokens":[
            {"name":"USDC","szDecimals":8,"weiDecimals":8,"index":0,
             "tokenId":"0x6d1e7cde53ba9467b783cb7c530ce054","isCanonical":true,"evmContract":null,"fullName":null},
            {"name":"USDE","szDecimals":2,"weiDecimals":8,"index":235,
             "tokenId":"0x2e6d84f2d7ca82e6581e03523e4389f7","isCanonical":false},
            {"name":"USDT0","szDecimals":2,"weiDecimals":8,"index":268,
             "tokenId":"0x25faedc3f054130dbb4e4203aca63567","isCanonical":false},
            {"name":"USDH","szDecimals":2,"weiDecimals":8,"index":360,
             "tokenId":"0x54e00a5988577cb0b0c9ab0cb6ef7f4b","isCanonical":false},
            {"name":"HYPE","szDecimals":2,"weiDecimals":8,"index":150,
             "tokenId":"0x11111111111111111111111111111111","isCanonical":true},
            {"name":"UBTC","szDecimals":5,"weiDecimals":8,"index":197,
             "tokenId":"0x22222222222222222222222222222222","isCanonical":true},
            {"name":"USDC","szDecimals":8,"weiDecimals":8,"index":999,
             "tokenId":"0x33333333333333333333333333333333","isCanonical":true}
          ],
          "universe":[{"name":"HYPE/USDC","tokens":[150,0],"index":107,"isCanonical":true}]
        }
        """;
    private const string SpotState = """
        {
          "balances":[
            {"coin":"USDC","token":0,"hold":"20.123456789012345678","total":"100.987654321098765432","entryNtl":"0.0"},
            {"coin":"USDE","token":235,"hold":"3.5","total":"3.5","entryNtl":"0.0"},
            {"coin":"USDT0","token":268,"hold":"0.0","total":"7.89","entryNtl":"0.0"},
            {"coin":"HYPE","token":150,"hold":"0","total":"12.3","entryNtl":"3.0"},
            {"coin":"UBTC","token":197,"hold":"0","total":"0.001","entryNtl":"90.0"},
            {"coin":"USDC","token":999,"hold":"0","total":"999999","entryNtl":"0"}
          ]
        }
        """;

    [Theory]
    [InlineData("unifiedAccount")]
    [InlineData("portfolioMargin")]
    [InlineData("default")]
    [InlineData("disabled")]
    [InlineData("dexAbstraction")]
    public async Task Stable_wallet_preserves_account_mode_and_each_real_token_without_summing_perps(string mode)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        PerpetualVenueReadResult result;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            result = await ReadWallet(mode: $"\"{mode}\"");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
        Assert.Equal(12345.678901234567890123m, result.Snapshot.AccountValueUsd);
        Assert.Equal(12095.555444445567890123m, result.Snapshot.WithdrawableUsd);
        Assert.Equal(250.123456789m, result.Snapshot.MarginUsedUsd);
        Assert.Single(result.Snapshot.Positions);
        Assert.Single(result.Fills);
        var wallet = Assert.IsType<VenueStablecoinWallet>(result.Snapshot.StablecoinWallet);
        Assert.Equal("hypercore-spot-stablecoins", wallet.Scope);
        Assert.Equal(mode, wallet.AccountMode);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(StateTime), wallet.ObservedAtUtc);
        Assert.Collection(wallet.Balances,
            balance => Assert.Equal(new VenueStablecoinBalance("USDC", 0, "0x6d1e7cde53ba9467b783cb7c530ce054",
                100.987654321098765432m, 20.123456789012345678m, 80.864197532086419754m), balance),
            balance => Assert.Equal(new VenueStablecoinBalance("USDE", 235, "0x2e6d84f2d7ca82e6581e03523e4389f7",
                3.5m, 3.5m, 0m), balance),
            balance => Assert.Equal(new VenueStablecoinBalance("USDT0", 268, "0x25faedc3f054130dbb4e4203aca63567",
                7.89m, 0m, 7.89m), balance),
            balance => Assert.Equal(new VenueStablecoinBalance("USDH", 360, "0x54e00a5988577cb0b0c9ab0cb6ef7f4b",
                0m, 0m, 0m), balance));
    }

    [Fact]
    public async Task Valid_observed_empty_balances_establish_zero_for_all_metadata_approved_tokens()
    {
        var result = await ReadWallet(state: "{\"balances\":[]}");
        var wallet = Assert.IsType<VenueStablecoinWallet>(result.Snapshot.StablecoinWallet);
        Assert.Equal(4, wallet.Balances.Count);
        Assert.All(wallet.Balances, balance =>
        {
            Assert.Equal(0m, balance.Total);
            Assert.Equal(0m, balance.Held);
            Assert.Equal(0m, balance.Available);
        });
        Assert.Equal(12345.678901234567890123m, result.Snapshot.AccountValueUsd);
    }

    [Fact]
    public async Task All_four_tokens_are_supported_without_requiring_canonical_status()
    {
        var meta = Mutate(SpotMeta, node =>
        {
            foreach (var token in (JsonArray)node["tokens"]!)
                token!["isCanonical"] = false;
        });
        var state = Mutate(SpotState, node => ((JsonArray)node["balances"]!).Add(
            new JsonObject { ["coin"] = "USDH", ["token"] = 360, ["total"] = "123.456", ["hold"] = "2.0", ["entryNtl"] = "0" }));
        var result = await ReadWallet(meta: meta, state: state);
        Assert.Equal(4, result.Snapshot.StablecoinWallet!.Balances.Count);
        Assert.Equal(121.456m, result.Snapshot.StablecoinWallet.Balances.Single(balance => balance.Symbol == "USDH").Available);
    }

    [Fact]
    public async Task Index_changes_are_resolved_by_exact_identity_and_metadata_name_not_hardcoded_index()
    {
        var meta = Mutate(SpotMeta, node => node["tokens"]![0]!["index"] = 42);
        var state = Mutate(SpotState, node => node["balances"]![0]!["token"] = 42);
        var result = await ReadWallet(meta: meta, state: state);
        var usdc = result.Snapshot.StablecoinWallet!.Balances.Single(balance => balance.Symbol == "USDC");
        Assert.Equal(42, usdc.TokenIndex);
        Assert.Equal("0x6d1e7cde53ba9467b783cb7c530ce054", usdc.TokenId);
        Assert.Equal(100.987654321098765432m, usdc.Total);
    }

    [Fact]
    public async Task Duplicate_names_and_canonical_spoofs_do_not_grant_token_identity()
    {
        // SpotMeta already has a different, canonical "USDC" at index 999.
        var result = await ReadWallet();
        Assert.Equal(4, result.Snapshot.StablecoinWallet!.Balances.Count);
        Assert.DoesNotContain(result.Snapshot.StablecoinWallet.Balances, balance => balance.TokenIndex is 999 or 150 or 197);
        Assert.Single(result.Snapshot.StablecoinWallet.Balances, balance => balance.Symbol == "USDC");
        Assert.Equal(100.987654321098765432m, result.Snapshot.StablecoinWallet.Balances[0].Total);
    }

    [Fact]
    public async Task Unknown_balance_indices_are_excluded_even_when_name_matches_a_stablecoin()
    {
        var state = Mutate(SpotState, node => ((JsonArray)node["balances"]!).Add(
            new JsonObject { ["coin"] = "USDH", ["token"] = 10001, ["total"] = "9999", ["hold"] = "0" }));
        var result = await ReadWallet(state: state);
        Assert.Equal(0m, result.Snapshot.StablecoinWallet!.Balances.Single(balance => balance.Symbol == "USDH").Total);
        Assert.Equal(4, result.Snapshot.StablecoinWallet.Balances.Count);
    }

    [Fact]
    public async Task Unknown_metadata_identity_with_supported_name_is_not_fabricated_as_stable_zero()
    {
        var meta = Mutate(SpotMeta, node => node["tokens"]![0]!["tokenId"] = "0x44444444444444444444444444444444");
        var result = await ReadWallet(meta: meta);
        Assert.Equal(3, result.Snapshot.StablecoinWallet!.Balances.Count);
        Assert.DoesNotContain(result.Snapshot.StablecoinWallet.Balances, balance => balance.Symbol == "USDC");
    }

    [Fact]
    public async Task Hex_identity_case_is_normalized_without_changing_metadata_identity()
    {
        var meta = Mutate(SpotMeta, node =>
            node["tokens"]![0]!["tokenId"] = "0x6D1E7CDE53BA9467B783CB7C530CE054");
        var result = await ReadWallet(meta: meta);
        Assert.Equal("0x6d1e7cde53ba9467b783cb7c530ce054", result.Snapshot.StablecoinWallet!.Balances[0].TokenId);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("true")]
    [InlineData("\"unknownMode\"")]
    [InlineData("\"UnifiedAccount\"")]
    [InlineData("\"unifiedAccount \"")]
    public async Task Unknown_or_missing_account_mode_fails_coherently(string mode) =>
        await Invalid(() => ReadWallet(mode: mode));

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"balances\":null}")]
    [InlineData("{\"balances\":{}}")]
    [InlineData("{\"balances\":[],\"balances\":[]}")]
    [InlineData("{\"balances\":[{}]}")]
    public async Task Missing_or_invalid_spot_ledger_never_becomes_fake_zero(string state) =>
        await Invalid(() => ReadWallet(state: state));

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"tokens\":null}")]
    [InlineData("{\"tokens\":[]}")]
    [InlineData("{\"tokens\":[{}]}")]
    [InlineData("{\"tokens\":[{\"index\":150,\"name\":\"HYPE\",\"tokenId\":\"0x11111111111111111111111111111111\"}]}")]
    public async Task Missing_or_unrecognized_catalog_cannot_establish_supported_wallet_zeros(string meta) =>
        await Invalid(() => ReadWallet(meta: meta));

    [Theory]
    [InlineData("total", "\"-1\"")]
    [InlineData("hold", "\"-1\"")]
    [InlineData("hold", "\"101\"")]
    [InlineData("total", "\"79228162514264337593543950336\"")]
    [InlineData("hold", "\"0.00000000000000000000000000001\"")]
    [InlineData("total", "\"1.00000000000000000000000000001\"")]
    [InlineData("total", "\"7.9228162514264337593543950336\"")]
    [InlineData("total", "\"1e3\"")]
    [InlineData("total", "\"NaN\"")]
    [InlineData("total", "\"1,000\"")]
    [InlineData("total", "\" 1\"")]
    [InlineData("total", "100.5")]
    [InlineData("hold", "null")]
    [InlineData("coin", "\"USDE\"")]
    [InlineData("token", "\"0\"")]
    [InlineData("token", "-1")]
    [InlineData("token", "2147483648")]
    [InlineData("token", "0.5")]
    public async Task Bad_stable_balances_fail_without_clamping_rounding_or_raw_details(string field, string json)
    {
        var state = Mutate(SpotState, node => node["balances"]![0]![field] = JsonNode.Parse(json));
        await Invalid(() => ReadWallet(state: state));
    }

    [Theory]
    [InlineData("default", 4)]
    [InlineData("disabled", 5)]
    [InlineData("dexAbstraction", 5)]
    public async Task A_wallet_failure_on_separate_ledgers_degrades_only_the_wallet(string mode, int failing)
    {
        // Phase 2, item 14b (F-14, probe V6): the wallet is not the perps balance here, so perps and fills still apply.
        var responses = WalletResponses($"\"{mode}\"");
        using var handler = new FixtureHandler((index, _) => Task.FromResult(index == failing
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Json(responses[index])));
        using var client = Client(handler);
        var result = await new HyperliquidPerpetualReader(client, new TestClock()).ReadAsync(Address, CancellationToken.None);
        Assert.Equal(12345.678901234567890123m, result.Snapshot.AccountValueUsd);
        Assert.Single(result.Snapshot.Positions);
        Assert.Single(result.Fills);
        Assert.Null(result.Snapshot.StablecoinWallet);
        // The mode was read and decides how the ledgers combine, wallet or not (Sol round 1 on 14b).
        Assert.Equal(mode, result.Snapshot.AccountMode);
        Assert.EndsWith(HyperliquidPerpetualReader.WalletUnavailableNotice, result.HistoryNotice);
        // A malformed wallet response degrades the same way; it is never read as an empty wallet.
        var malformed = await ReadWallet(mode: $"\"{mode}\"", state: Mutate(SpotState, node => ((JsonObject)node["balances"]![0]!).Remove("total")));
        Assert.Null(malformed.Snapshot.StablecoinWallet);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("connection")]
    public async Task A_truncated_or_dropped_wallet_response_degrades_only_the_wallet(string failure)
    {
        // Sol round 1 on 14b: these escaped the wallet boundary and discarded the perps and fills already read.
        var responses = WalletResponses("\"default\"");
        using var handler = new FixtureHandler((index, _) => index != 5 ? Task.FromResult(Json(responses[index]))
            : failure == "truncated"
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{", Encoding.UTF8, "application/json") })
                : Task.FromException<HttpResponseMessage>(new HttpRequestException("The connection was reset.")));
        using var client = Client(handler);
        var result = await new HyperliquidPerpetualReader(client, new TestClock()).ReadAsync(Address, CancellationToken.None);
        Assert.Equal(12345.678901234567890123m, result.Snapshot.AccountValueUsd);
        Assert.Single(result.Fills);
        Assert.Null(result.Snapshot.StablecoinWallet);
        Assert.Equal("default", result.Snapshot.AccountMode);
    }

    [Theory]
    [InlineData("unifiedAccount")]
    [InlineData("portfolioMargin")]
    public async Task Unified_and_portfolio_margin_accounts_still_need_the_wallet(string mode)
    {
        var state = Mutate(SpotState, node => ((JsonObject)node["balances"]![0]!).Remove("total"));
        await Invalid(() => ReadWallet(mode: $"\"{mode}\"", state: state));
    }

    [Fact]
    public async Task A_wallet_rate_limit_still_stops_the_whole_read()
    {
        var responses = WalletResponses("\"default\"");
        using var handler = new FixtureHandler((index, _) => Task.FromResult(index == 5
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Json(responses[index])));
        using var client = Client(handler);
        await Assert.ThrowsAsync<VenueRateLimitException>(() =>
            new HyperliquidPerpetualReader(client, new TestClock()).ReadAsync(Address, CancellationToken.None));
    }

    [Theory]
    [InlineData("total")]
    [InlineData("hold")]
    [InlineData("coin")]
    [InlineData("token")]
    public async Task Missing_required_stable_balance_field_fails(string field)
    {
        var state = Mutate(SpotState, node => ((JsonObject)node["balances"]![0]!).Remove(field));
        await Invalid(() => ReadWallet(state: state));
    }

    [Theory]
    [InlineData("name", "\"usdc\"")]
    [InlineData("name", "\"USDE\"")]
    [InlineData("index", "-1")]
    [InlineData("index", "\"0\"")]
    [InlineData("index", "2147483648")]
    [InlineData("tokenId", "null")]
    [InlineData("tokenId", "\"0xshort\"")]
    [InlineData("tokenId", "\"0x6d1e7cde53ba9467b783cb7c530ce05g\"")]
    public async Task Inconsistent_or_malformed_stable_metadata_fails(string field, string json)
    {
        var meta = Mutate(SpotMeta, node => node["tokens"]![0]![field] = JsonNode.Parse(json));
        await Invalid(() => ReadWallet(meta: meta));
    }

    [Fact]
    public async Task Duplicate_metadata_index_or_identity_is_rejected()
    {
        var duplicateIndex = Mutate(SpotMeta, node => node["tokens"]![6]!["index"] = 0);
        await Invalid(() => ReadWallet(meta: duplicateIndex));
        var duplicateIdentity = Mutate(SpotMeta, node =>
            node["tokens"]![6]!["tokenId"] = "0x6d1e7cde53ba9467b783cb7c530ce054");
        await Invalid(() => ReadWallet(meta: duplicateIdentity));
        var duplicateUnknownIndex = Mutate(SpotMeta, node => node["tokens"]![5]!["index"] = 150);
        await Invalid(() => ReadWallet(meta: duplicateUnknownIndex));
    }

    [Fact]
    public async Task Duplicate_balance_index_is_rejected_instead_of_adding_or_overwriting()
    {
        var state = Mutate(SpotState, node =>
            ((JsonArray)node["balances"]!).Add(node["balances"]![0]!.DeepClone()));
        await Invalid(() => ReadWallet(state: state));
    }

    [Fact]
    public async Task Available_difference_must_be_exact_even_when_both_inputs_are_representable()
    {
        var state = Mutate(SpotState, node =>
        {
            node["balances"]![0]!["total"] = "79228162514264337593543950335";
            node["balances"]![0]!["hold"] = "0.1";
        });
        await Invalid(() => ReadWallet(state: state));
    }

    [Fact]
    public async Task Exact_subtraction_limits_and_equal_holds_are_supported()
    {
        var state = Mutate(SpotState, node =>
        {
            node["balances"]![0]!["total"] = "79228162514264337593543950335";
            node["balances"]![0]!["hold"] = "1";
            node["balances"]![1]!["total"] = "0.0000000000000000000000000001";
            node["balances"]![1]!["hold"] = "0.0000000000000000000000000001";
        });
        var result = await ReadWallet(state: state);
        Assert.Equal(decimal.MaxValue - 1m, result.Snapshot.StablecoinWallet!.Balances[0].Available);
        Assert.Equal(0m, result.Snapshot.StablecoinWallet.Balances[1].Available);
    }

    [Fact]
    public async Task Wallet_observation_is_local_time_after_ledger_read_not_perp_timestamp()
    {
        var clock = new TestClock();
        var responses = WalletResponses();
        using var handler = new FixtureHandler((index, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return Task.FromResult(Json(responses[index]));
        });
        using var client = Client(handler);
        var result = await new HyperliquidPerpetualReader(client, clock).ReadAsync(Address, CancellationToken.None);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(StateTime), result.Snapshot.ObservedAtUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(StateTime).AddSeconds(6),
            result.Snapshot.StablecoinWallet!.ObservedAtUtc);
        Assert.Equal(2, clock.TimerCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sixth_request_stream_shares_deadline_and_propagates_caller_cancellation(bool callerCancels)
    {
        var clock = new TestClock();
        using var caller = new CancellationTokenSource();
        var responses = WalletResponses();
        using var handler = new FixtureHandler((index, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            return Task.FromResult(index < 5 ? Json(responses[index]) : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamingContent(new InterruptingStream(() =>
                {
                    if (callerCancels)
                        caller.Cancel();
                    else
                        clock.Advance(TimeSpan.FromSeconds(3));
                }))
            });
        });
        using var client = Client(handler);
        var operation = new HyperliquidPerpetualReader(client, clock).ReadAsync(Address, caller.Token);
        if (callerCancels)
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Equal(caller.Token, exception.CancellationToken);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<VenueReadException>(() => operation);
            Assert.Equal("Hyperliquid read timed out.", exception.Message);
        }
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal(2, clock.TimerCount);
        Assert.Equal(TimeSpan.FromSeconds(20), clock.Deadline);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Extra_reads_retain_streamed_size_limits(int stage)
    {
        var responses = WalletResponses();
        using var handler = new FixtureHandler((index, _) => Task.FromResult(index == stage
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamingContent(new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', 4 * 1024 * 1024 + 1))))
            }
            : Json(responses[index])));
        using var client = Client(handler);
        await Invalid(() => new HyperliquidPerpetualReader(client, new TestClock()).ReadAsync(Address, CancellationToken.None));
        Assert.Equal(stage + 1, handler.Requests.Count);
    }

    private static string[] WalletResponses(string mode = "\"unifiedAccount\"", string meta = SpotMeta, string state = SpotState) =>
        [Meta, State, $"[{Fill}]", mode, meta, state];

    private static async Task<PerpetualVenueReadResult> ReadWallet(
        string mode = "\"unifiedAccount\"", string meta = SpotMeta, string state = SpotState)
    {
        using var handler = Fixtures(WalletResponses(mode, meta, state));
        using var client = Client(handler);
        return await new HyperliquidPerpetualReader(client, new TestClock()).ReadAsync(Address, CancellationToken.None);
    }
}
