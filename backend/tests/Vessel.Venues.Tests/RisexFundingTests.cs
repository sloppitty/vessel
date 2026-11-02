using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Risex;

namespace Vessel.Tests;

/// <summary>Synthetic HTTP fixtures based on the official account funding-payment schema, not owner data.</summary>
public sealed class RisexFundingTests
{
    private const string Address = "0x00000000000000000000000000000000000000aa";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-16T12:00:00Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset From = Now.AddDays(-30);
    private static string Ns(DateTimeOffset time) => (time.ToUnixTimeMilliseconds() * 1_000_000).ToString(CultureInfo.InvariantCulture);

    private static string Market(string id, string name, bool active = true) => $$"""
        {"market_id":"{{id}}","config":{"name":"{{name}}","step_size":"0.001","step_price":"0.01",
        "max_leverage":"10","unlocked":true},"quote_asset_symbol":"USDC","active":{{(active ? "true" : "false")}},"category":"crypto"}
        """;

    private static string Markets => $$"""
        {"data":{"markets":[{{Market("1", "BTC/USDC")}},
        {{Market("13", "DOGE/USDC [deprecated-1779958099]", false)}},{{Market("14", "DOGE/USDC")}}]} }
        """;

    private static string Row(string amount = "0.123456789012345678", string market = "1", int transaction = 1,
        DateTimeOffset? time = null, bool settled = true, string kind = "funding", long? rawNanoseconds = null) => $$"""
        {"market_id":"{{market}}","funding_payment":"{{amount}}","position_size":"-0.005953",
        "block_number":"100","block_time":"{{rawNanoseconds?.ToString(CultureInfo.InvariantCulture) ?? Ns(time ?? Now.AddHours(-1))}}","tx_hash":"0x{{transaction.ToString("x64", CultureInfo.InvariantCulture)}}",
        "side":"short","funding_rate":"0.000012345678901234","is_settled":{{(settled ? "true" : "false")}},
        "funding_rate_8h":"0.000098765432109876","trade_side":"BUY","entry_type":"{{kind}}"}
        """;

    private static string Page(int number, bool next = false, params string[] rows) => $$"""
        {"data":{"account":"{{Address}}","records":[{{string.Join(",", rows)}}],"page":{{number}},"has_next_page":{{(next ? "true" : "false")}} } }
        """;

    private static Account Account(string venue = RisexReader.Id, string? address = Address, string? source = null, bool enabled = true)
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), venue, "Synthetic RISEx");
        account.Configure(null, address, null, source);
        if (!enabled) account.UpdateSettings(account.Name, null, false);
        return account;
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Responses(Func<int, string> pages) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Func<CancellationToken, Task>? BeforeResponse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (BeforeResponse is not null) await BeforeResponse(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/v1/markets" => Markets,
                "/v1/account/funding-payments" => pages(int.Parse(Query(request.RequestUri)["page"], CultureInfo.InvariantCulture)),
                _ => throw new InvalidOperationException("Unexpected synthetic endpoint.")
            };
            return new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => part[1], StringComparer.Ordinal);

    private static (RisexReader Reader, Responses Responses) Reader(Func<int, string>? pages = null)
    {
        var responses = new Responses(pages ?? (page => Page(page)));
        var time = new FixedTime();
        return (new RisexReader(new HttpClient(responses) { BaseAddress = new Uri("https://api.rise.trade/") },
            time, new RisexCatalogueCache(time)), responses);
    }

    [Fact]
    public async Task Settled_decimal_USDC_signs_are_inverted_without_scaling_or_rounding_and_originals_survive()
    {
        var paid = Row("5.012345678901234567");
        var received = Row("-0.035893853717451273", transaction: 2);
        var zero = Row("0.000000000000000000", transaction: 3);
        var result = await Reader(page => Page(page, false, paid, received, zero)).Reader.ReadFundingAsync(Account(), From, Now, default);

        Assert.True(result.Complete);
        Assert.Equal(From, result.FromUtc);
        Assert.Equal(Now, result.ToUtc);
        Assert.Equal(FundingFacts.Unknown, result.PnlBasis);
        Assert.Equal([-5.012345678901234567m, 0.035893853717451273m, 0m], result.Payments.Select(payment => payment.Amount));
        Assert.All(result.Payments, payment =>
        {
            Assert.Equal("BTC", payment.ContractId);
            Assert.Equal("1", payment.VenueContractId);
            Assert.Equal("USDC", payment.Asset);
            Assert.Equal(Now.AddHours(-1), payment.OccurredAtUtc);
            Assert.Equal(long.Parse(Ns(Now.AddHours(-1)), CultureInfo.InvariantCulture), payment.OccurredAtUnixNanoseconds);
            Assert.StartsWith("risex-funding:", payment.SourceId);
        });
        Assert.Equal(paid, result.Payments[0].RawJson);
        Assert.Contains("must not be added", result.Notice);
    }

    [Fact]
    public async Task Native_nanoseconds_are_retained_exactly_beside_a_microsecond_floored_database_timestamp()
    {
        const long rawNanoseconds = 1792144800001234567;
        var row = Row(rawNanoseconds: rawNanoseconds);
        var result = await Reader(page => Page(page, false, row)).Reader.ReadFundingAsync(Account(), From, Now, default);
        var payment = Assert.Single(result.Payments);

        Assert.True(result.Complete);
        Assert.Equal(rawNanoseconds, payment.OccurredAtUnixNanoseconds);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks(17921448000012340), payment.OccurredAtUtc);
        Assert.Equal(0, payment.OccurredAtUtc.Ticks % 10);
        Assert.Equal(row, payment.RawJson);
        // The 567 native nanoseconds lost from the display/storage timestamp remain available for exact attribution.
        Assert.Equal(567, rawNanoseconds - (payment.OccurredAtUtc - DateTimeOffset.UnixEpoch).Ticks * 100);
        VenueFundingChecks.Validate(result, From, Now);
    }

    [Fact]
    public async Task Distinct_native_instants_within_one_database_microsecond_keep_distinct_source_identities()
    {
        const long rawNanoseconds = 1792144800001234567;
        var result = await Reader(page => Page(page, false, Row(rawNanoseconds: rawNanoseconds),
            Row(rawNanoseconds: rawNanoseconds + 1))).Reader.ReadFundingAsync(Account(), From, Now, default);
        Assert.True(result.Complete);
        Assert.Equal(2, result.Payments.Count);
        Assert.Equal(result.Payments[0].OccurredAtUtc, result.Payments[1].OccurredAtUtc);
        Assert.NotEqual(result.Payments[0].OccurredAtUnixNanoseconds, result.Payments[1].OccurredAtUnixNanoseconds);
        Assert.NotEqual(result.Payments[0].SourceId, result.Payments[1].SourceId);
    }

    [Fact]
    public async Task Exactly_31_days_is_allowed()
    {
        var result = await Reader().Reader.ReadFundingAsync(Account(), Now.AddDays(-31), Now, default);
        Assert.True(result.Complete);
        Assert.Equal(Now.AddDays(-31), result.FromUtc);
    }

    [Fact]
    public async Task Explicit_inclusive_nanosecond_bounds_and_one_based_pages_cover_the_requested_window()
    {
        var (reader, responses) = Reader(page => page == 1
            ? Page(page, true, Row(time: From)) : Page(page, false, Row(transaction: 2, time: Now)));
        var result = await reader.ReadFundingAsync(Account(), From, Now, default);

        Assert.True(result.Complete);
        Assert.Equal(2, result.Payments.Count);
        Assert.Equal([From, Now], result.Payments.Select(payment => payment.OccurredAtUtc));
        var requests = responses.Requests.Where(uri => uri.AbsolutePath.EndsWith("funding-payments", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, requests.Count);
        for (var index = 0; index < requests.Count; index++)
        {
            var query = Query(requests[index]);
            Assert.Equal(Address, query["account"]);
            Assert.Equal((long.Parse(Ns(From), CultureInfo.InvariantCulture) - 1).ToString(CultureInfo.InvariantCulture), query["start_time"]);
            Assert.Equal((long.Parse(Ns(Now), CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture), query["end_time"]);
            Assert.Equal((index + 1).ToString(CultureInfo.InvariantCulture), query["page"]);
            Assert.Equal("1000", query["limit"]);
            Assert.Equal("time", query["sorted_by"]);
        }
    }

    [Fact]
    public async Task Empty_terminal_page_is_verified_empty_not_unknown()
    {
        var result = await Reader().Reader.ReadFundingAsync(Account(), From, Now, default);
        Assert.True(result.Complete);
        Assert.Empty(result.Payments);
        Assert.Equal(FundingFacts.Unknown, result.PnlBasis);
    }

    [Fact]
    public async Task Unsettled_accruals_and_dividends_are_not_imported_and_do_not_hide_later_pages()
    {
        var result = await Reader(page => page == 1
            ? Page(page, true, Row(settled: false), Row(transaction: 2, kind: "dividend"))
            : Page(page, false, Row(transaction: 3))).Reader.ReadFundingAsync(Account(), From, Now, default);
        Assert.True(result.Complete);
        Assert.Single(result.Payments);
        Assert.Contains("unsettled accruals and dividend adjustments are excluded", result.Notice);
    }

    [Fact]
    public async Task Retired_and_replacement_markets_keep_distinct_native_contracts()
    {
        var result = await Reader(page => Page(page, false, Row(market: "13"), Row(market: "14")))
            .Reader.ReadFundingAsync(Account(), From, Now, default);
        Assert.True(result.Complete);
        Assert.Equal(["DOGE", "DOGE"], result.Payments.Select(payment => payment.ContractId));
        Assert.Equal(["13", "14"], result.Payments.Select(payment => payment.VenueContractId));
        Assert.NotEqual(result.Payments[0].SourceId, result.Payments[1].SourceId);
    }

    [Fact]
    public async Task Unrecognized_native_market_is_partial_never_made_up_or_silently_complete()
    {
        var result = await Reader(page => Page(page, false, Row(), Row(market: "999", transaction: 2)))
            .Reader.ReadFundingAsync(Account(), From, Now, default);
        Assert.False(result.Complete);
        Assert.Single(result.Payments);
        Assert.Contains("unrecognized native markets", result.Notice);
        Assert.Equal(From, result.FromUtc);
        Assert.Equal(Now, result.ToUtc);
    }

    [Fact]
    public async Task Page_limit_returns_bounded_partial_coverage_without_claiming_a_shorter_window_complete()
    {
        var (reader, responses) = Reader(page => Page(page, true, Row(transaction: page)));
        var result = await reader.ReadFundingAsync(Account(), From, Now, default);
        Assert.False(result.Complete);
        Assert.Equal(10, result.Payments.Count);
        Assert.Equal(11, responses.Requests.Count); // Catalogue plus ten funding pages.
        Assert.Contains("10-page / 10,000-row", result.Notice);
        Assert.Equal(From, result.FromUtc);
        Assert.Equal(Now, result.ToUtc);
    }

    [Fact]
    public async Task Stable_identity_ignores_JSON_property_order_decimal_format_and_transaction_case()
    {
        var row = Row(transaction: 0xab);
        var original = await Reader(page => Page(page, false, row)).Reader.ReadFundingAsync(Account(), From, Now, default);
        using var document = JsonDocument.Parse(row);
        var reordered = "{" + string.Join(",", document.RootElement.EnumerateObject().Reverse()
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + property.Value.GetRawText())) + "}";
        reordered = reordered.Replace("\"0.123456789012345678\"", "\"0.1234567890123456780\"", StringComparison.Ordinal);
        reordered = reordered.Replace("ab\"", "AB\"", StringComparison.Ordinal);
        var again = await Reader(page => Page(page, false, reordered)).Reader.ReadFundingAsync(Account(), From, Now, default);
        Assert.Equal(original.Payments[0].SourceId, again.Payments[0].SourceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_native_coordinates_fail_even_if_the_amount_differs(bool differentAmount)
    {
        var first = Row();
        var second = differentAmount ? Row("0.9") : first;
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(page => Page(page, false, first, second))
            .Reader.ReadFundingAsync(Account(), From, Now, default));
    }

    [Fact]
    public async Task Repeated_page_content_is_not_silently_deduplicated()
    {
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(page => Page(page, page == 1, Row()))
            .Reader.ReadFundingAsync(Account(), From, Now, default));
    }

    [Theory]
    [InlineData("funding_payment", "true")]
    [InlineData("funding_payment", "0.1")]
    [InlineData("funding_payment", "\"1e3\"")]
    [InlineData("funding_payment", "\"0.00000000000000000000000000001\"")]
    [InlineData("funding_payment", "\"79228162514264337593543950336\"")]
    [InlineData("market_id", "\"1x\"")]
    [InlineData("market_id", "\"0\"")]
    [InlineData("block_time", "\"-1\"")]
    [InlineData("block_time", "\"18446744073709551615\"")]
    [InlineData("block_number", "\"0\"")]
    [InlineData("tx_hash", "\"0xnot-a-transaction\"")]
    [InlineData("entry_type", "\"interest\"")]
    [InlineData("is_settled", "\"true\"")]
    public async Task Malformed_or_inexact_settlement_is_rejected(string field, string replacement)
    {
        using var document = JsonDocument.Parse(Row());
        var value = document.RootElement.GetProperty(field).GetRawText();
        var malformed = Row().Replace(JsonSerializer.Serialize(field) + ":" + value,
            JsonSerializer.Serialize(field) + ":" + replacement, StringComparison.Ordinal);
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(page => Page(page, false, malformed))
            .Reader.ReadFundingAsync(Account(), From, Now, default));
    }

    [Fact]
    public async Task Duplicate_JSON_properties_are_rejected_by_the_shared_parser()
    {
        var duplicate = Row().Replace("\"funding_payment\":", "\"funding_payment\":\"0\",\"funding_payment\":", StringComparison.Ordinal);
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(page => Page(page, false, duplicate))
            .Reader.ReadFundingAsync(Account(), From, Now, default));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("page")]
    [InlineData("has_next_page")]
    [InlineData("too_many_rows")]
    [InlineData("empty_nonterminal")]
    public async Task Invalid_pagination_and_account_binding_are_rejected(string fault)
    {
        var body = fault switch
        {
            "account" => Page(1).Replace(Address, "0x00000000000000000000000000000000000000bb", StringComparison.Ordinal),
            "page" => Page(2),
            "has_next_page" => Page(1).Replace("\"has_next_page\":false", "\"has_next_page\":\"false\"", StringComparison.Ordinal),
            "too_many_rows" => Page(1, false, Enumerable.Range(1, 1001).Select(index => Row(transaction: index)).ToArray()),
            "empty_nonterminal" => Page(1, true),
            _ => throw new InvalidOperationException()
        };
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(_ => body).Reader.ReadFundingAsync(Account(), From, Now, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Returned_rows_outside_the_padded_request_are_rejected_before_timestamp_truncation(bool after)
    {
        var row = Row(time: after ? Now : From);
        var bound = long.Parse(Ns(after ? Now : From), CultureInfo.InvariantCulture);
        row = row.Replace(bound.ToString(CultureInfo.InvariantCulture), (bound + (after ? 2 : -2)).ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(page => Page(page, false, row))
            .Reader.ReadFundingAsync(Account(), From, Now, default));
    }

    [Fact]
    public async Task One_nanosecond_guard_rows_are_filtered_without_losing_inclusive_exact_millisecond_boundaries()
    {
        var before = Row(transaction: 1, time: From).Replace(Ns(From),
            (long.Parse(Ns(From), CultureInfo.InvariantCulture) - 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var after = Row(transaction: 4, time: Now).Replace(Ns(Now),
            (long.Parse(Ns(Now), CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var result = await Reader(page => Page(page, false, before, Row(transaction: 2, time: From),
            Row(transaction: 3, time: Now), after)).Reader.ReadFundingAsync(Account(), From, Now, default);
        Assert.True(result.Complete);
        Assert.Equal([From, Now], result.Payments.Select(payment => payment.OccurredAtUtc));
    }

    [Fact]
    public async Task Reversed_page_order_is_rejected()
    {
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(page => Page(page, false,
            Row(time: Now), Row(transaction: 2, time: From))).Reader.ReadFundingAsync(Account(), From, Now, default));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("wrong_venue")]
    [InlineData("missing_source")]
    [InlineData("invalid_source")]
    [InlineData("mismatched_source")]
    [InlineData("reversed_window")]
    [InlineData("future_window")]
    [InlineData("submillisecond_window")]
    [InlineData("pre_epoch_window")]
    [InlineData("too_wide_window")]
    public async Task Invalid_accounts_and_windows_make_no_HTTP_requests(string fault)
    {
        var account = fault switch
        {
            "disabled" => Account(enabled: false),
            "wrong_venue" => Account(venue: "hyperliquid"),
            "missing_source" => Account(address: null),
            "invalid_source" => Account(source: "not-an-address"),
            "mismatched_source" => Account(source: "0x00000000000000000000000000000000000000bb"),
            _ => Account()
        };
        var from = fault switch
        {
            "pre_epoch_window" => DateTimeOffset.UnixEpoch.AddMilliseconds(-1),
            "too_wide_window" => Now.AddDays(-31).AddMilliseconds(-1),
            _ => From
        };
        var to = fault switch
        {
            "reversed_window" => From.AddMilliseconds(-1),
            "future_window" => Now.AddMilliseconds(1),
            "submillisecond_window" => Now.AddTicks(-1),
            _ => Now
        };
        var (reader, responses) = Reader();
        await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadFundingAsync(account, from, to, default));
        Assert.Empty(responses.Requests);
    }

    [Fact]
    public async Task Source_only_account_is_valid_and_case_normalized()
    {
        var (reader, responses) = Reader();
        await reader.ReadFundingAsync(Account(address: null, source: Address.ToUpperInvariant().Replace("0X", "0x", StringComparison.Ordinal)),
            From, Now, default);
        Assert.Equal(Address, Query(responses.Requests.Last())["account"]);
    }

    [Fact]
    public async Task Cancellation_before_and_during_a_read_is_not_converted_to_incomplete_funding()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var (reader, responses) = Reader();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadFundingAsync(Account(), From, Now, cancelled.Token));
        Assert.Empty(responses.Requests);

        using var active = new CancellationTokenSource();
        responses.BeforeResponse = _ => { active.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadFundingAsync(Account(), From, Now, active.Token));
    }

    [Fact]
    public async Task Unavailable_and_oversized_responses_do_not_become_verified_empty_history()
    {
        var (reader, responses) = Reader();
        responses.Status = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadFundingAsync(Account(), From, Now, default));
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(_ => new string(' ', 4 * 1024 * 1024) + "{}")
            .Reader.ReadFundingAsync(Account(), From, Now, default));
    }
}
