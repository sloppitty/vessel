using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vessel.Application.Credentials;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Lighter;

namespace Vessel.Tests;

/// <summary>Offline synthetic payments; the catalogue fixture is public RH metadata, never owner history.</summary>
public sealed class LighterRobinhoodFundingTests
{
    private const string Source = "9007199254740993";
    private const string Address = "0x1111111111111111111111111111111111111111";
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset From = Now.AddDays(-30);
    private static readonly LighterExchange Rh = LighterExchange.Robinhood;
    private static string Token(string suffix = "aa", string source = Source) =>
        $"ro:{source}:single:{Now.AddDays(10).ToUnixTimeSeconds()}:{suffix}";

    private static string CatalogueJson => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "LighterRh", "orderBookDetails.json"));

    private static Account Account(string venue = "lighter-rh")
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), venue, "Synthetic funding account");
        account.Configure(null, null, null, Source);
        return account;
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static JsonObject Payment(long marketId, long fundingId, string change,
        DateTimeOffset? at = null, string side = "long") => new()
    {
        ["market_id"] = marketId, ["funding_id"] = fundingId,
        ["timestamp"] = (at ?? Now.AddHours(-1)).ToUnixTimeSeconds(),
        ["change"] = change, ["discount"] = "0.125", ["rate"] = "0.75",
        ["position_size"] = "25.5", ["position_side"] = side
    };

    private static string Page(string? cursor = null, params JsonObject[] rows)
    {
        var root = new JsonObject
        {
            ["code"] = 200, ["position_fundings"] = new JsonArray(rows.Cast<JsonNode>().ToArray())
        };
        if (cursor is not null) root["next_cursor"] = cursor;
        return root.ToJsonString();
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Credentials(params (Guid Id, string Token)[] entries) : IAccountCredentialReader
    {
        public List<Guid> Reads { get; } = [];
        public List<Guid> Refused { get; } = [];

        public Task<string?> ReadAsync(Guid accountId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Reads.Add(accountId);
            return Task.FromResult<string?>(entries.FirstOrDefault(e => e.Id == accountId).Token);
        }

        public async Task<CredentialAttempt?> ReadAttemptAsync(Guid accountId, CancellationToken ct) =>
            await ReadAsync(accountId, ct) is { } value ? new(value, "rh-funding-attempt-revision") : null;
        public Task MarkRefusedAsync(Guid accountId, string attemptedRevision, CancellationToken ct)
        {
            Assert.Equal("rh-funding-attempt-revision", attemptedRevision);
            Refused.Add(accountId);
            return Task.CompletedTask;
        }
    }

    private sealed record Request(Uri Uri, string? Authorization);

    private sealed class Handler(Func<HttpRequestMessage, string>? funding = null) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        public string Catalogue { get; init; } = CatalogueJson;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(new(request.RequestUri!, request.Headers.TryGetValues("Authorization", out var values)
                ? values.Single() : null));
            var body = request.RequestUri!.AbsolutePath.Split('/').Last() switch
            {
                "account" => $$"""{"code":200,"accounts":[{"index":{{Source}},"account_index":{{Source}},"l1_address":"{{Address}}"}]}""",
                "accountsByL1Address" => $$"""{"code":200,"l1_address":"{{Address}}","sub_accounts":[{"index":{{Source}},"account_type":0,"l1_address":"{{Address}}"}]}""",
                "orderBookDetails" => Catalogue,
                "positionFunding" => funding?.Invoke(request) ?? Page(),
                _ => throw new InvalidOperationException("Unexpected synthetic request.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static LighterReader Reader(Handler handler, Credentials credentials, LighterExchange? exchange = null)
    {
        exchange ??= Rh;
        var host = exchange == Rh ? "https://api.rh.lighter.xyz/" : "https://mainnet.zklighter.elliot.ai/";
        return new(new HttpClient(new LighterAuthenticationHandler(exchange.Name) { InnerHandler = handler })
            { BaseAddress = new Uri(host) }, new Clock(), credentials, exchange: exchange);
    }

    private static IReadOnlyList<VenueFundingPayment> Parse(string page, string? catalogue = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        LighterReader.ReadFunding(Json(page), LighterReader.ReadCatalogue(Json(catalogue ?? CatalogueJson), Rh),
            from ?? From, to ?? Now, Now);

    [Fact]
    public void Rh_preserves_signed_reported_USDG_change_exact_ids_and_originals()
    {
        const long fundingId = 9007199254740993;
        var payments = Parse(Page(null,
            Payment(1, fundingId, "0.000001", side: "short"),
            Payment(10, fundingId, "-2.345678", side: "long")));

        Assert.Equal([0.000001m, -2.345678m], payments.Select(p => p.Amount));
        Assert.All(payments, p => Assert.Equal("USDG", p.Asset));
        Assert.Equal(["1:9007199254740993", "10:9007199254740993"], payments.Select(p => p.SourceId));
        Assert.Equal(["1", "10"], payments.Select(p => p.VenueContractId));
        Assert.Equal(["BTC", "AAPL"], payments.Select(p => p.ContractId));
        Assert.Equal("-2.345678", Json(payments[1].RawJson).GetProperty("change").GetString());
        // Neither rate, position side nor discount rewrites an already settled signed payment.
        Assert.Equal("0.125", Json(payments[1].RawJson).GetProperty("discount").GetString());
    }

    [Fact]
    public void Retired_and_current_contracts_with_the_same_symbol_keep_separate_native_identity()
    {
        var catalogue = JsonNode.Parse(CatalogueJson)!.AsObject();
        var markets = catalogue["order_book_details"]!.AsArray();
        var retired = markets[0]!.DeepClone().AsObject();
        retired["market_id"] = 12345;
        retired["status"] = "inactive";
        markets.Add(retired);

        var payments = Parse(Page(null, Payment(1, 77, "1"), Payment(12345, 77, "-1")),
            catalogue.ToJsonString());

        Assert.Equal(["BTC", "BTC"], payments.Select(p => p.ContractId));
        Assert.Equal(["1", "12345"], payments.Select(p => p.VenueContractId));
        Assert.Equal(["1:77", "12345:77"], payments.Select(p => p.SourceId));
    }

    [Fact]
    public async Task Exact_inclusive_window_exhausts_cursor_pages_without_leaking_the_token()
    {
        var from = From.AddMilliseconds(123);
        var to = Now.AddMilliseconds(-123);
        var page = 0;
        var handler = new Handler(_ => ++page == 1
            ? Page("page /+&=2", Payment(1, 1, "1", From), Payment(1, 2, "-2", From.AddSeconds(1)))
            : Page(null, Payment(10, 3, "3", Now.AddSeconds(-1)), Payment(10, 4, "4", Now)));
        var account = Account();
        var token = Token();
        var result = await Reader(handler, new Credentials((account.Id, token)))
            .ReadFundingAsync(account, from, to, default);

        Assert.True(result.Complete);
        Assert.Equal(from, result.FromUtc);
        Assert.Equal(to, result.ToUtc);
        Assert.Equal([-2m, 3m], result.Payments.Select(p => p.Amount));
        Assert.Equal(FundingFacts.Unknown, result.PnlBasis);
        Assert.Contains("USDG", result.Notice);
        Assert.Contains(Rh.Name, result.Notice);
        var funding = handler.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/positionFunding")).ToArray();
        Assert.Equal(2, funding.Length);
        Assert.All(funding, r =>
        {
            Assert.Equal(token, r.Authorization);
            Assert.Equal("api.rh.lighter.xyz", r.Uri.Host);
            Assert.Contains("account_index=" + Source, r.Uri.Query);
            Assert.Contains("side=all", r.Uri.Query);
            Assert.DoesNotContain("auth", r.Uri.Query, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains("cursor=page%20%2F%2B%26%3D2", funding[1].Uri.Query);
        Assert.All(handler.Requests, r =>
        {
            Assert.DoesNotContain(token, r.Uri.AbsoluteUri);
            Assert.DoesNotContain(Uri.EscapeDataString(token), r.Uri.AbsoluteUri);
        });
        Assert.All(handler.Requests.Where(r => !funding.Contains(r)), r => Assert.Null(r.Authorization));
        Assert.DoesNotContain(token, result.Notice);
        Assert.All(result.Payments, p => Assert.DoesNotContain(token, p.RawJson));
    }

    [Theory]
    [InlineData("lighter", true)]
    [InlineData("lighter-rh", false)]
    public async Task Foreign_or_disabled_accounts_are_rejected_before_credential_or_network_access(string venue, bool enabled)
    {
        var account = Account(venue);
        if (!enabled) account.UpdateSettings(account.Name, null, false);
        var credentials = new Credentials((account.Id, Token()));
        var handler = new Handler();

        await Assert.ThrowsAsync<VenueReadException>(() =>
            Reader(handler, credentials).ReadFundingAsync(account, From, Now, default));

        Assert.Empty(credentials.Reads);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Identical_account_indexes_on_two_exchanges_do_not_share_tokens_or_collateral()
    {
        var rhAccount = Account();
        var mainnetAccount = Account("lighter");
        var rhToken = Token("aaaa");
        var mainnetToken = Token("bbbb");
        var credentials = new Credentials((rhAccount.Id, rhToken), (mainnetAccount.Id, mainnetToken));
        var rhHandler = new Handler(_ => Page(null, Payment(1, 7, "-1")));
        var mainnetHandler = new Handler(_ => Page(null, Payment(1, 7, "2")));

        var rh = await Reader(rhHandler, credentials).ReadFundingAsync(rhAccount, From, Now, default);
        var mainnet = await Reader(mainnetHandler, credentials, LighterExchange.Mainnet)
            .ReadFundingAsync(mainnetAccount, From, Now, default);

        Assert.True(rh.Complete);
        Assert.True(mainnet.Complete);
        Assert.Equal("USDG", Assert.Single(rh.Payments).Asset);
        Assert.Equal("USDC", Assert.Single(mainnet.Payments).Asset);
        Assert.Equal([rhAccount.Id, mainnetAccount.Id], credentials.Reads);
        Assert.Equal("lighter-rh-read-token", Rh.CredentialPurpose);
        Assert.Equal("lighter-read-token", LighterExchange.Mainnet.CredentialPurpose);
        Assert.Equal(rhToken, Assert.Single(rhHandler.Requests, IsFunding).Authorization);
        Assert.Equal(mainnetToken, Assert.Single(mainnetHandler.Requests, IsFunding).Authorization);
    }

    [Fact]
    public async Task Missing_token_is_incomplete_not_a_verified_empty_window()
    {
        var handler = new Handler();
        var result = await Reader(handler, new Credentials()).ReadFundingAsync(Account(), From, Now, default);

        Assert.False(result.Complete);
        Assert.Empty(result.Payments);
        Assert.Contains(Rh.Name, result.Notice);
        Assert.Contains("read-only token", result.Notice);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Single_scope_token_for_another_account_never_reaches_Rh()
    {
        var account = Account();
        var handler = new Handler();
        var result = await Reader(handler, new Credentials((account.Id, Token(source: "42"))))
            .ReadFundingAsync(account, From, Now, default);

        Assert.False(result.Complete);
        Assert.Empty(result.Payments);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain(Token(source: "42"), result.Notice);
    }

    [Fact]
    public async Task Token_echo_in_original_json_is_rejected_without_persisting_the_secret()
    {
        var account = Account();
        var token = Token();
        var row = Payment(1, 1, "1");
        row["unexpected"] = new JsonObject { ["echo"] = token };
        var handler = new Handler(_ => Page(null, row));

        var result = await Reader(handler, new Credentials((account.Id, token)))
            .ReadFundingAsync(account, From, Now, default);

        Assert.False(result.Complete);
        Assert.Empty(result.Payments);
        Assert.DoesNotContain(token, result.Notice);
    }

    [Fact]
    public async Task Unknown_native_contract_makes_the_window_incomplete_without_guessing_a_symbol()
    {
        var account = Account();
        var handler = new Handler(_ => Page(null, Payment(1, 1, "1"), Payment(12345, 2, "-2")));

        var result = await Reader(handler, new Credentials((account.Id, Token())))
            .ReadFundingAsync(account, From, Now, default);

        Assert.False(result.Complete);
        Assert.Empty(result.Payments);
    }

    [Fact]
    public async Task Refused_token_is_marked_only_on_the_selected_Rh_account_without_echoing_upstream_text()
    {
        var account = Account();
        var token = Token();
        var handler = new Handler(_ => $$"""{"code":20013,"message":"invalid token {{token}}"}""");
        var credentials = new Credentials((account.Id, token));

        var result = await Reader(handler, credentials).ReadFundingAsync(account, From, Now, default);

        Assert.False(result.Complete);
        Assert.Empty(result.Payments);
        Assert.Equal([account.Id], credentials.Refused);
        Assert.Contains(Rh.Name, result.Notice);
        Assert.DoesNotContain(token, result.Notice);
        Assert.All(handler.Requests, r => Assert.DoesNotContain(token, r.Uri.AbsoluteUri));
    }

    private static bool IsFunding(Request request) => request.Uri.AbsolutePath.EndsWith("/positionFunding");
}
