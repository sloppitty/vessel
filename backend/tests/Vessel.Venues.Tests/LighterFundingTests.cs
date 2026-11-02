using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vessel.Application.Credentials;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure;
using Vessel.Infrastructure.Venues.Lighter;

namespace Vessel.Tests;

public sealed class LighterFundingTests
{
    private const string Source = "9007199254740993";
    private const string Address = "0x1111111111111111111111111111111111111111";
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset From = Now.AddDays(-30);
    private static readonly string Token = $"ro:{Source}:single:{Now.AddDays(30).ToUnixTimeSeconds()}:abcdef";
    private const string Catalogue = """
        {"code":200,"order_book_details":[
          {"market_id":1,"market_type":"perp","symbol":"BTC","status":"active","is_frozen":false,
           "supported_size_decimals":5,"supported_price_decimals":1,
           "min_initial_margin_fraction":200,"maintenance_margin_fraction":120},
          {"market_id":5,"market_type":"perp","symbol":"OLD","status":"inactive",
           "supported_size_decimals":1,"supported_price_decimals":2,
           "min_initial_margin_fraction":0,"maintenance_margin_fraction":0}],
         "spot_order_book_details":[{"market_id":2048,"market_type":"spot","symbol":"ETH/USDC"}]}
        """;

    private static JsonObject Row(string id = Source, string change = "-1.25", int market = 1,
        DateTimeOffset? at = null, string side = "long") => new()
    {
        ["timestamp"] = (at ?? Now.AddHours(-1)).ToUnixTimeSeconds(), ["market_id"] = market,
        ["funding_id"] = long.Parse(id, CultureInfo.InvariantCulture), ["change"] = change,
        ["discount"] = "0.25", ["rate"] = "0.0001", ["position_size"] = "1.125", ["position_side"] = side
    };

    private static string Page(IEnumerable<JsonObject>? rows = null, string? cursor = null) =>
        $$"""{"code":200,"position_fundings":[{{string.Join(",", (rows ?? []).Select(r => r.ToJsonString()))}}],"next_cursor":{{JsonSerializer.Serialize(cursor)}}}""";

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Credentials(string? token = null) : IAccountCredentialReader
    {
        public List<Guid> Reads { get; } = [];
        public List<Guid> Refused { get; } = [];
        public Task<string?> ReadAsync(Guid accountId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Reads.Add(accountId);
            return Task.FromResult(token);
        }
        public async Task<CredentialAttempt?> ReadAttemptAsync(Guid accountId, CancellationToken ct) =>
            await ReadAsync(accountId, ct) is { } value ? new(value, "funding-attempt-revision") : null;
        public Task MarkRefusedAsync(Guid accountId, string attemptedRevision, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal("funding-attempt-revision", attemptedRevision);
            Refused.Add(accountId);
            return Task.CompletedTask;
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<(Uri Url, string? Token)> Requests { get; } = [];
        public Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>? Funding { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add((request.RequestUri!, request.Headers.TryGetValues("Authorization", out var values)
                ? values.Single() : null));
            var body = request.RequestUri!.AbsolutePath.Split('/').Last() switch
            {
                "account" => $$"""{"code":200,"accounts":[{"index":{{Source}},"account_index":{{Source}},"l1_address":"{{Address}}"}]}""",
                "accountsByL1Address" => $$"""{"code":200,"l1_address":"{{Address}}","sub_accounts":[{"index":{{Source}},"account_type":0,"l1_address":"{{Address}}"}]}""",
                "orderBookDetails" => Catalogue,
                "positionFunding" => null,
                _ => throw new InvalidOperationException("Unexpected synthetic request.")
            };
            return Task.FromResult(body is null ? Funding?.Invoke(request, ct) ?? Response(Page()) : Response(body));
        }
    }

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static LighterReader Reader(Handler handler, Credentials? credentials = null) =>
        new(new HttpClient(new LighterAuthenticationHandler { InnerHandler = handler })
            { BaseAddress = new Uri("https://mainnet.zklighter.elliot.ai/") }, new Clock(), credentials ?? new(Token));

    private static Account Account(string venue = "lighter", string? source = Source, bool enabled = true)
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), venue, "Synthetic funding account");
        account.Configure(null, null, null, source);
        account.UpdateSettings(account.Name, null, enabled);
        return account;
    }

    [Fact]
    public async Task Funding_rate_limit_reaches_the_shared_venue_backoff_without_refusing_the_credential()
    {
        var handler = new Handler
        {
            Funding = (_, _) =>
            {
                var response = Response("Private upstream body.", HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new(System.TimeSpan.FromMinutes(10));
                return response;
            }
        };
        var credentials = new Credentials(Token);
        var error = await Assert.ThrowsAsync<VenueRateLimitException>(() =>
            Reader(handler, credentials).ReadFundingAsync(Account(), From, Now, default));
        Assert.Equal(Now.AddMinutes(10), error.RetryAtUtc);
        Assert.Empty(credentials.Refused);
        Assert.DoesNotContain("Private", error.Message);
        Assert.DoesNotContain(Token, error.Message);
    }

    [Fact]
    public async Task Settled_changes_keep_exact_signs_native_ids_collateral_and_retired_markets()
    {
        var rows = new[]
        {
            Row(change: "0.0000000000000000000000000001"),
            Row("9007199254740994", "-1234567890.123456789012345678", side: "short"),
            Row(market: 5, change: "10.001234"), Row("3", "0")
        };
        var handler = new Handler { Funding = (_, _) => Response(Page(rows)) };
        var credentials = new Credentials(Token);
        var account = Account();
        IVenueFundingReader reader = Reader(handler, credentials);
        var result = await reader.ReadFundingAsync(account, From, Now, default);

        Assert.True(result.Complete);
        Assert.Equal(From, result.FromUtc);
        Assert.Equal(Now, result.ToUtc);
        Assert.Equal(FundingFacts.Unknown, result.PnlBasis);
        Assert.Equal([0.0000000000000000000000000001m, -1234567890.123456789012345678m, 10.001234m, 0m],
            result.Payments.Select(p => p.Amount));
        Assert.Equal(["1:" + Source, "1:9007199254740994", "5:" + Source, "1:3"],
            result.Payments.Select(p => p.SourceId));
        Assert.Equal(["BTC", "BTC", "OLD", "BTC"], result.Payments.Select(p => p.ContractId));
        Assert.Equal(["1", "1", "5", "1"], result.Payments.Select(p => p.VenueContractId));
        Assert.All(result.Payments, p => Assert.Equal("USDC", p.Asset));
        Assert.Equal(rows[2].ToJsonString(), result.Payments[2].RawJson);
        Assert.Equal([account.Id], credentials.Reads);
        Assert.Empty(credentials.Refused);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("mainnet.zklighter.elliot.ai", r.Url.Host);
            Assert.DoesNotContain(Token, r.Url.ToString());
            Assert.DoesNotContain(Uri.EscapeDataString(Token), r.Url.ToString());
            Assert.DoesNotContain("auth=", r.Url.Query);
            Assert.Equal(r.Url.AbsolutePath.EndsWith("/positionFunding", StringComparison.Ordinal) ? Token : null, r.Token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exhausted_empty_response_is_complete_with_null_or_absent_cursor(bool absent)
    {
        var body = absent ? """{"code":200,"position_fundings":[]}""" : Page();
        var handler = new Handler { Funding = (_, _) => Response(body) };
        var result = await Reader(handler).ReadFundingAsync(Account(), From, Now, default);
        Assert.True(result.Complete);
        Assert.Empty(result.Payments);
        Assert.Single(handler.Requests, r => r.Url.AbsolutePath.EndsWith("/positionFunding", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Window_is_inclusive_and_subsecond_bounds_are_filtered_without_rounding()
    {
        var from = Now.AddHours(-2).AddMilliseconds(123);
        var to = Now.AddMilliseconds(-456);
        var rows = new[] { Row("1", at: from.AddMilliseconds(-123)), Row("2", at: from.AddSeconds(1)),
            Row("3", at: Now.AddSeconds(-1)), Row("4", at: Now) };
        var handler = new Handler { Funding = (_, _) => Response(Page(rows)) };
        var result = await Reader(handler).ReadFundingAsync(Account(), from, to, default);
        Assert.True(result.Complete);
        Assert.Equal(["1:2", "1:3"], result.Payments.Select(p => p.SourceId));
        Assert.Equal(from, result.FromUtc);
        Assert.Equal(to, result.ToUtc);
        var request = handler.Requests.Last().Url.Query;
        Assert.Contains("start_timestamp=" + (from.ToUnixTimeSeconds() - 1), request);
        Assert.Contains("end_timestamp=" + (to.ToUnixTimeSeconds() + 1), request);
        Assert.Contains("side=all", request);
        Assert.DoesNotContain("market_ids", request);

        handler = new Handler { Funding = (_, _) => Response(Page([Row("1", at: From), Row("2", at: Now)])) };
        result = await Reader(handler).ReadFundingAsync(Account(), From, Now, default);
        Assert.Equal([From, Now], result.Payments.Select(p => p.OccurredAtUtc));
    }

    [Fact]
    public async Task Cursor_not_page_length_drives_paging_and_overlapping_native_facts_are_deduplicated()
    {
        const string cursor = "next /?=+&";
        var page = 0;
        var handler = new Handler { Funding = (_, _) => Response(++page == 1
            ? Page([Row()], cursor) : Page([Row(), Row("2")])) };
        var result = await Reader(handler).ReadFundingAsync(Account(), From, Now, default);
        Assert.True(result.Complete);
        Assert.Equal(2, result.Payments.Count);
        Assert.Equal(2, page);
        Assert.Contains("&cursor=" + Uri.EscapeDataString(cursor), handler.Requests.Last().Url.PathAndQuery);
    }

    [Fact]
    public async Task Page_budget_retains_verified_payments_but_never_claims_complete()
    {
        var page = 0;
        var handler = new Handler { Funding = (_, _) =>
        {
            page++;
            return Response(Page([Row(page.ToString(CultureInfo.InvariantCulture))], "c" + page));
        }};
        var result = await Reader(handler).ReadFundingAsync(Account(), From, Now, default);
        Assert.False(result.Complete);
        Assert.Equal(5, page);
        Assert.Equal(5, result.Payments.Count);
        Assert.Contains("five-page", result.Notice);
        Assert.All(handler.Requests.Where(r => r.Token is not null), r => Assert.Contains("limit=100", r.Url.Query));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Repeating_cursors_or_conflicting_native_facts_reject_the_whole_later_page(bool cursorLoop)
    {
        var page = 0;
        var handler = new Handler { Funding = (_, _) => Response(++page == 1 ? Page([Row()], "c")
            : Page([Row("2"), Row(change: "100")], cursorLoop ? "c" : null)) };
        var result = await Reader(handler).ReadFundingAsync(Account(), From, Now, default);
        Assert.False(result.Complete);
        Assert.Equal(-1.25m, Assert.Single(result.Payments).Amount);
        Assert.Equal(2, page);
    }

    [Fact]
    public async Task Missing_token_is_explicitly_incomplete_and_makes_no_public_funding_attempt()
    {
        var handler = new Handler();
        var result = await Reader(handler, new Credentials()).ReadFundingAsync(Account(), From, Now, default);
        Assert.False(result.Complete);
        Assert.Empty(result.Payments);
        Assert.Contains("read-only token", result.Notice);
        Assert.Contains("ordinary account history is unchanged", result.Notice);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(401, 401)]
    [InlineData(403, 403)]
    [InlineData(400, 20013)]
    [InlineData(400, 61005)]
    [InlineData(200, 61006)]
    public async Task Refusal_keeps_prior_pages_marks_only_that_account_and_never_falls_back_to_public(int status, int code)
    {
        var page = 0;
        var handler = new Handler { Funding = (_, _) => ++page == 1 ? Response(Page([Row()], "next"))
            : Response(JsonSerializer.Serialize(new { code, message = Token }), (HttpStatusCode)status) };
        var credentials = new Credentials(Token);
        var account = Account();
        var result = await Reader(handler, credentials).ReadFundingAsync(account, From, Now, default);
        Assert.False(result.Complete);
        Assert.Single(result.Payments);
        Assert.Contains("refused or expired", result.Notice);
        Assert.DoesNotContain(Token, result.Notice);
        Assert.Equal([account.Id], credentials.Refused);
        Assert.Equal(2, page);
        Assert.All(handler.Requests.Where(r => r.Url.AbsolutePath.EndsWith("/positionFunding", StringComparison.Ordinal)),
            r => Assert.Equal(Token, r.Token));
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("other-account")]
    [InlineData("expired")]
    public async Task Unusable_tokens_are_incomplete_before_any_network_call(string kind)
    {
        var token = kind switch
        {
            "other-account" => Token.Replace(Source, "123", StringComparison.Ordinal),
            "expired" => $"ro:{Source}:single:{Now.ToUnixTimeSeconds()}:abcdef",
            _ => "trading-key-not-a-read-only-token"
        };
        var handler = new Handler();
        var credentials = new Credentials(token);
        var result = await Reader(handler, credentials).ReadFundingAsync(Account(), From, Now, default);
        Assert.False(result.Complete);
        Assert.Empty(handler.Requests);
        Assert.Empty(credentials.Refused);
        Assert.DoesNotContain(token, result.Notice);
    }

    [Theory]
    [InlineData("change", null)]
    [InlineData("change", "1.25")]
    [InlineData("change", "\"0.00000000000000000000000000001\"")]
    [InlineData("change", "\"79228162514264337593543950336\"")]
    [InlineData("change", "\"NaN\"")]
    [InlineData("discount", "\"-1\"")]
    [InlineData("rate", "0.01")]
    [InlineData("position_size", "\"-1\"")]
    [InlineData("position_side", "\"buy\"")]
    [InlineData("timestamp", "\"1792148400\"")]
    [InlineData("timestamp", "1792148400000")]
    [InlineData("timestamp", "-1")]
    [InlineData("market_id", "2048")]
    [InlineData("market_id", "99")]
    [InlineData("funding_id", "1e0")]
    [InlineData("funding_id", "9223372036854775808")]
    public async Task Malformed_settlement_fields_are_never_rounded_or_treated_as_empty_coverage(string field, string? value)
    {
        var row = Row();
        if (value is null) row.Remove(field);
        else row[field] = JsonNode.Parse(value);
        var handler = new Handler { Funding = (_, _) => Response(Page([Row("2"), row])) };
        var credentials = new Credentials(Token);
        var result = await Reader(handler, credentials).ReadFundingAsync(Account(), From, Now, default);
        Assert.False(result.Complete);
        Assert.Empty(result.Payments); // A valid earlier row on a malformed page is not a verified page.
        Assert.Empty(credentials.Refused);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("duplicate-property")]
    [InlineData("duplicate-id")]
    [InlineData("oversized-page")]
    [InlineData("bad-cursor")]
    [InlineData("secret-in-cursor")]
    [InlineData("encoded-secret-in-cursor")]
    [InlineData("secret-in-row")]
    [InlineData("encoded-secret-in-row")]
    public async Task Invalid_envelopes_and_secret_echoes_fail_closed_without_retaining_or_leaking_them(string kind)
    {
        var row = Row();
        row["unrecognized"] = new JsonObject { ["nested"] = new JsonArray(kind == "encoded-secret-in-row"
            ? Uri.EscapeDataString(Token) : Token) };
        var body = kind switch
        {
            "malformed" => "{",
            "duplicate-property" => Page([Row()]).Replace("\"code\":200", "\"code\":200,\"code\":200", StringComparison.Ordinal),
            "duplicate-id" => Page([Row(), Row()]),
            "oversized-page" => Page(Enumerable.Range(0, 101).Select(i => Row(i.ToString(CultureInfo.InvariantCulture)))),
            "bad-cursor" => Page().Replace("\"next_cursor\":null", "\"next_cursor\":1", StringComparison.Ordinal),
            "secret-in-cursor" => Page([Row()], Token),
            "encoded-secret-in-cursor" => Page([Row()], Uri.EscapeDataString(Token)),
            _ => Page([row])
        };
        var handler = new Handler { Funding = (_, _) => Response(body) };
        var result = await Reader(handler).ReadFundingAsync(Account(), From, Now, default);
        Assert.False(result.Complete);
        Assert.Empty(result.Payments);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result));
        Assert.All(handler.Requests, r => Assert.DoesNotContain(Token, r.Url.ToString()));
    }

    [Theory]
    [InlineData("lighter", null, true)]
    [InlineData("lighter-rh", Source, true)]
    [InlineData("lighter", Source, false)]
    [InlineData("lighter", "-1", true)]
    public async Task Invalid_account_scope_is_rejected_before_credential_or_network_access(string venue, string? source, bool enabled)
    {
        var handler = new Handler();
        var credentials = new Credentials(Token);
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler, credentials)
            .ReadFundingAsync(Account(venue, source, enabled), From, Now, default));
        Assert.Empty(credentials.Reads);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("reversed")]
    [InlineData("future")]
    [InlineData("negative")]
    [InlineData("over31days")]
    [InlineData("submillisecond-from")]
    [InlineData("submillisecond-to")]
    public async Task Invalid_time_windows_are_rejected_before_reading_credentials(string kind)
    {
        var handler = new Handler();
        var credentials = new Credentials(Token);
        var from = kind switch
        {
            "negative" => DateTimeOffset.UnixEpoch.AddSeconds(-1),
            "reversed" => Now.AddSeconds(1),
            "over31days" => Now.AddDays(-31).AddMilliseconds(-1),
            "submillisecond-from" => From.AddTicks(1),
            _ => From
        };
        var to = kind switch
        {
            "future" => Now.AddSeconds(1),
            "submillisecond-to" => Now.AddTicks(-1),
            _ => Now
        };
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler, credentials).ReadFundingAsync(Account(), from, to, default));
        Assert.Empty(credentials.Reads);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Exactly_31_days_with_millisecond_aligned_subsecond_bounds_is_accepted()
    {
        var handler = new Handler();
        var to = Now.AddMilliseconds(-1);
        var from = to.AddDays(-31);
        var result = await Reader(handler).ReadFundingAsync(Account(), from, to, default);
        Assert.True(result.Complete);
        Assert.Equal(from, result.FromUtc);
        Assert.Equal(to, result.ToUtc);
        Assert.Single(handler.Requests, r => r.Url.AbsolutePath.EndsWith("/positionFunding", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_before_and_during_the_read_propagates_without_marking_a_token_refused()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var credentials = new Credentials(Token);
        var handler = new Handler();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(handler, credentials)
            .ReadFundingAsync(Account(), From, Now, cancelled.Token));
        Assert.Empty(credentials.Reads);
        Assert.Empty(handler.Requests);

        using var during = new CancellationTokenSource();
        handler.Funding = (_, ct) =>
        {
            during.Cancel();
            ct.ThrowIfCancellationRequested();
            return Response(Page());
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(handler, credentials)
            .ReadFundingAsync(Account(), From, Now, during.Token));
        Assert.Empty(credentials.Refused);
    }

    private sealed class Logs : ILoggerProvider
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);
        public void Dispose() { }
        private sealed class CaptureLogger(Logs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => owner.Lines.Add(category + " " + formatter(state, exception));
        }
    }

    [Fact]
    public async Task Configured_logger_free_client_does_not_log_a_token_or_an_upstream_error_body()
    {
        var handler = new Handler { Funding = (_, _) => Response(JsonSerializer.Serialize(new { message = Token }),
            HttpStatusCode.InternalServerError) };
        var logs = new Logs();
        var credentials = new Credentials(Token);
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddVesselInfrastructure(new ConfigurationBuilder().Build());
        services.AddSingleton<TimeProvider>(new Clock());
        services.AddSingleton<IAccountCredentialReader>(credentials);
        services.AddHttpClient(LighterExchange.Mainnet.ClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var reader = scope.ServiceProvider.GetServices<IPerpetualVenueReader>().OfType<LighterReader>()
            .Single(r => r.VenueId == "lighter");
        var result = await reader.ReadFundingAsync(Account(), From, Now, default);
        Assert.False(result.Complete);
        Assert.Empty(result.Payments);
        Assert.Empty(credentials.Refused); // An outage is not proof that a credential was refused.
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result));
        Assert.All(logs.Lines, line => Assert.DoesNotContain(Token, line));
        Assert.DoesNotContain(logs.Lines, line => line.StartsWith("System.Net.Http.HttpClient.lighter.", StringComparison.Ordinal));
    }
}
