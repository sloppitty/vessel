using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed class HyperliquidFundingTests
{
    private const string Address = "0x0123456789abcdef0123456789abcdef01234567";
    private const string Meta = """
        {"universe":[{"name":"BTC","szDecimals":5,"maxLeverage":40},
        {"name":"kPEPE","szDecimals":0,"maxLeverage":10},
        {"name":"kDOGS","szDecimals":0,"maxLeverage":5,"isDelisted":true}]}
        """;
    private static readonly DateTimeOffset From = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddHours(1);
    private static readonly long Start = From.ToUnixTimeMilliseconds();

    private static string Row(long time, string amount = "-0.0123456789012345678901234567",
        string coin = "BTC", int hash = 1) =>
        $$"""{"delta":{"type":"funding","coin":"{{coin}}","usdc":"{{amount}}","szi":"49.1477","fundingRate":"0.0000417","nSamples":null},"time":{{time}},"hash":"0x{{hash.ToString("x64")}}"}""";
    private static string Page(IEnumerable<string> rows) => "[" + string.Join(',', rows) + "]";
    private static Account Account(string venue = "hyperliquid", string? source = Address, bool enabled = true)
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), venue, "Synthetic funding");
        account.Configure(null, null, null, source);
        if (!enabled) account.UpdateSettings(account.Name, null, false);
        return account;
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => From.AddDays(1);
    }

    private sealed class Handler(Func<int, CancellationToken, Task<string>> funding) : HttpMessageHandler
    {
        public Handler(params string[] pages) : this((index, _) => Task.FromResult(pages[index])) { }
        public List<JsonElement> Requests { get; } = [];
        public List<JsonElement> FundingRequests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/info", request.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var payload = document.RootElement.Clone();
            Requests.Add(payload);
            var body = Meta;
            if (payload.GetProperty("type").GetString() != "meta")
            {
                Assert.Equal("userFunding", payload.GetProperty("type").GetString());
                FundingRequests.Add(payload);
                body = await funding(FundingRequests.Count - 1, ct);
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static HyperliquidPerpetualReader Reader(Handler handler) =>
        new(new HttpClient(handler) { BaseAddress = new("https://api.hyperliquid.xyz/") }, new Clock());

    [Fact]
    public async Task Reads_actual_signed_exact_USDC_payments_and_preserves_native_retired_and_canonical_contracts()
    {
        var first = Row(Start, coin: "kPEPE");
        using var handler = new Handler(Page([first, Row(Start, "79228162514264337593543950335"),
            Row(To.ToUnixTimeMilliseconds(), "0", "kDOGS")]));
        IVenueFundingReader reader = Reader(handler);
        var result = await reader.ReadFundingAsync(Account(), From, To, default);
        Assert.True(result.Complete);
        Assert.Equal((From, To), (result.FromUtc, result.ToUtc));
        Assert.Equal(FundingFacts.Unknown, result.PnlBasis); // Never infer closedPnl's funding basis.
        Assert.Equal(3, result.Payments.Count); // Shared hash/time is not a shared payment identity.
        var paid = Assert.Single(result.Payments, p => p.ContractId == "1000PEPE");
        Assert.Equal(-0.0123456789012345678901234567m, paid.Amount);
        Assert.Equal(("kPEPE", "USDC", From, first),
            (paid.VenueContractId, paid.Asset, paid.OccurredAtUtc, paid.RawJson));
        Assert.Equal(decimal.MaxValue, Assert.Single(result.Payments, p => p.ContractId == "BTC").Amount);
        Assert.Equal(("kDOGS", 0m), (result.Payments.Single(p => p.ContractId == "1000DOGS").VenueContractId,
            result.Payments.Single(p => p.ContractId == "1000DOGS").Amount));
        Assert.All(result.Payments, p => Assert.Equal(72, p.SourceId.Length));
        var request = Assert.Single(handler.FundingRequests);
        Assert.Equal(Address, request.GetProperty("user").GetString());
        Assert.Equal(Start, request.GetProperty("startTime").GetInt64());
        Assert.Equal(To.ToUnixTimeMilliseconds(), request.GetProperty("endTime").GetInt64());
    }

    [Fact]
    public async Task An_empty_success_is_verified_complete_and_a_single_millisecond_window_is_supported()
    {
        using var handler = new Handler("[]");
        var result = await Reader(handler).ReadFundingAsync(Account(), From, From, default);
        Assert.True(result.Complete);
        Assert.Empty(result.Payments);
        Assert.Equal((From, From), (result.FromUtc, result.ToUtc));
    }

    [Fact]
    public async Task Full_pages_overlap_inclusively_and_deduplicate_without_skipping_boundary_payments()
    {
        var first = Enumerable.Range(0, 500).Select(i => Row(Start + i)).ToArray();
        using var handler = new Handler(Page(first), Page([first[^1].Replace(",\"time\":", ", \"time\":"), Row(Start + 499, "2", hash: 2),
            Row(Start + 500)]));
        var result = await Reader(handler).ReadFundingAsync(Account(), From, To, default);
        Assert.True(result.Complete);
        Assert.Equal(502, result.Payments.Count);
        Assert.Equal(Start + 499, handler.FundingRequests[1].GetProperty("startTime").GetInt64());
        Assert.Equal(502, result.Payments.Select(p => p.SourceId).Distinct().Count());

        using var repeated = new Handler(Page([Row(Start + 499, "2", hash: 2)]));
        var payment = Assert.Single((await Reader(repeated).ReadFundingAsync(Account(), From, To, default)).Payments);
        Assert.Equal(payment.SourceId, result.Payments.Single(p => p.Amount == 2m).SourceId);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(501)] // The documented cap can be distinct blocks, not just individual rows.
    public async Task A_saturated_same_millisecond_group_is_partial_never_skipped_or_claimed_complete(int count)
    {
        using var handler = new Handler(Page(Enumerable.Range(0, count).Select(i => Row(Start, hash: i + 1))));
        var result = await Reader(handler).ReadFundingAsync(Account(), From, To, default);
        Assert.False(result.Complete);
        Assert.Equal(count, result.Payments.Count);
        Assert.Contains("non-advancing timestamp", result.Notice);
        Assert.Single(handler.FundingRequests);
    }

    [Fact]
    public async Task Pagination_has_a_total_bound_and_reports_partial_coverage()
    {
        using var handler = new Handler((page, _) => Task.FromResult(Page(
            Enumerable.Range(page * 499, 500).Select(i => Row(Start + i)))));
        var result = await Reader(handler).ReadFundingAsync(Account(), From, To, default);
        Assert.False(result.Complete);
        Assert.Equal(20, handler.FundingRequests.Count);
        Assert.Equal(9981, result.Payments.Count);
        Assert.Contains("Incomplete funding", result.Notice);
    }

    [Fact]
    public async Task Distinct_block_pages_cannot_exceed_the_total_row_budget()
    {
        using var handler = new Handler((page, _) => Task.FromResult(Page(
            Enumerable.Range(page * 5999, 6000).Select(i => Row(Start + i)))));
        var result = await Reader(handler).ReadFundingAsync(Account(), From, To, default);
        Assert.False(result.Complete);
        Assert.Equal(2, handler.FundingRequests.Count);
        Assert.Equal(6000, result.Payments.Count);
        Assert.Contains("page/row bound", result.Notice);
    }

    [Theory]
    [InlineData("xyz:XYZ100")]
    [InlineData("1000PEPE")] // Canonical spelling is not a native venue identity.
    [InlineData("UNKNOWN")]
    public async Task Unrecognized_or_non_primary_contracts_cannot_establish_complete_coverage(string coin)
    {
        using var handler = new Handler(Page([Row(Start, coin: coin), Row(Start)]));
        var result = await Reader(handler).ReadFundingAsync(Account(), From, To, default);
        Assert.False(result.Complete);
        Assert.Equal("BTC", Assert.Single(result.Payments).ContractId);
        Assert.Contains("contracts were excluded", result.Notice);
    }

    [Theory]
    [InlineData("usdc", "null")]
    [InlineData("usdc", "0.1")]
    [InlineData("usdc", "\"0.00000000000000000000000000001\"")]
    [InlineData("usdc", "\"79228162514264337593543950336\"")]
    [InlineData("usdc", "\"1.23456789012345678901234567891\"")]
    [InlineData("usdc", "\"NaN\"")]
    [InlineData("usdc", "\"1e-3\"")]
    [InlineData("usdc", "\"+1\"")]
    [InlineData("usdc", "\" 1\"")]
    [InlineData("type", "\"deposit\"")]
    [InlineData("coin", "\"\"")]
    [InlineData("coin", "17")]
    public async Task Invalid_funding_fields_are_safe_failures(string field, string value)
    {
        var row = JsonNode.Parse(Row(Start))!;
        row["delta"]![field] = JsonNode.Parse(value);
        using var handler = new Handler(Page([row.ToJsonString()]));
        var error = await Assert.ThrowsAsync<VenueReadException>(() =>
            Reader(handler).ReadFundingAsync(Account(), From, To, default));
        Assert.Equal("Hyperliquid returned an invalid or unsupported response.", error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[{}]")]
    [InlineData("[{")]
    public async Task Invalid_reply_shapes_fail_closed(string reply)
    {
        using var handler = new Handler(reply);
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).ReadFundingAsync(Account(), From, To, default));
    }

    [Theory]
    [InlineData("hash", "\"private-invalid-hash\"")]
    [InlineData("hash", "\"0x0000000000000000000000000000000000000000000000000000000000000000\"")]
    [InlineData("hash", "\"0xgggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg\"")]
    [InlineData("time", "-1")]
    [InlineData("time", "1")]
    [InlineData("time", "9223372036854775808")]
    [InlineData("time", "253402300799999")]
    [InlineData("time", "\"1\"")]
    [InlineData("time", "1.5")]
    [InlineData("delta", "null")]
    public async Task Invalid_identity_and_out_of_window_times_fail_closed(string field, string value)
    {
        var row = JsonNode.Parse(Row(Start))!;
        row[field] = JsonNode.Parse(value);
        using var handler = new Handler(Page([row.ToJsonString()]));
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).ReadFundingAsync(Account(), From, To, default));
    }

    [Fact]
    public async Task Duplicate_keys_conflicting_payment_facts_and_out_of_order_pages_are_rejected()
    {
        string[] replies = [
            Page([Row(Start).Replace("\"delta\":", "\"time\":0,\"delta\":")]),
            Page([Row(Start).Replace("\"usdc\":", "\"usdc\":\"3\",\"usdc\":")]),
            Page([Row(Start), Row(Start, "3")]),
            Page([Row(Start), Row(Start).Replace("\"szi\":\"49.1477\"", "\"szi\":\"49.1478\"")]),
            Page([Row(Start + 1), Row(Start)])
        ];
        foreach (var reply in replies)
        {
            using var handler = new Handler(reply);
            await Assert.ThrowsAsync<VenueReadException>(() =>
                Reader(handler).ReadFundingAsync(Account(), From, To, default));
        }
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("wrong-venue")]
    [InlineData("missing-source")]
    [InlineData("invalid-source")]
    public async Task Invalid_accounts_never_send_HTTP(string scenario)
    {
        var account = scenario switch
        {
            "disabled" => Account(enabled: false),
            "wrong-venue" => Account(venue: "risex"),
            "missing-source" => Account(source: null),
            _ => Account(source: "invalid")
        };
        using var handler = new Handler("[]");
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).ReadFundingAsync(account, From, To, default));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("reverse")]
    [InlineData("too-long")]
    [InlineData("future")]
    [InlineData("sub-ms-start")]
    [InlineData("sub-ms-end")]
    [InlineData("pre-epoch")]
    public async Task Invalid_windows_never_send_HTTP(string scenario)
    {
        var (from, to) = scenario switch
        {
            "reverse" => (To, From),
            "too-long" => (From.AddDays(-32), To),
            "future" => (From, From.AddDays(1).AddMilliseconds(1)),
            "sub-ms-start" => (From.AddTicks(1), To),
            "sub-ms-end" => (From, To.AddTicks(1)),
            _ => (DateTimeOffset.UnixEpoch.AddMilliseconds(-1), DateTimeOffset.UnixEpoch)
        };
        using var handler = new Handler("[]");
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).ReadFundingAsync(Account(), from, to, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_before_or_during_paging()
    {
        using var caller = new CancellationTokenSource();
        using var handler = new Handler(async (_, token) =>
        {
            caller.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "[]";
        });
        var reader = Reader(handler);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadFundingAsync(Account(), From, To, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Single(handler.FundingRequests);
        var count = handler.Requests.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadFundingAsync(Account(), From, To, caller.Token));
        Assert.Equal(count, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Response_row_and_byte_bounds_fail_safely(bool bytes)
    {
        var body = bytes ? "[\"" + new string('a', 4 * 1024 * 1024) + "\"]" :
            Page(Enumerable.Range(0, 10001).Select(i => Row(Start, hash: i + 1)));
        using var handler = new Handler(body);
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).ReadFundingAsync(Account(), From, To, default));
    }
}
