using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.Signer;
using Npgsql;
using Vessel.Application.Credentials;
using Vessel.Application.MarketData;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Infrastructure.Auth;
using Vessel.Infrastructure.Credentials;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Tests.Identity;

namespace Vessel.Tests.Isolation;

/// <summary>
/// Test-only authentication: the <see cref="Header"/> names the owner of each request and the handler issues the
/// production owner claims (<see cref="JournalOwnerClaims"/>) with the production authentication type. Registered as
/// the default scheme, so the bearer token path never runs in this suite. Not production code: Wallet mode
/// authenticates sessions, never a header.
/// </summary>
internal sealed class TestOwnerAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "VesselIsolationTest";
    public const string Header = "X-Vessel-Test-Owner";

    /// <summary>
    /// The identity's authentication type, as the production bearer handler sets it; the session scheme keeps the same
    /// type. Wallet-mode cases do not use this handler: <see cref="WalletIsolationApi"/> runs the real session scheme.
    /// </summary>
    public const string AuthenticationType = BearerTokenHandler.SchemeName;

    public static string NameOf(Guid owner) => "Owner " + owner.ToString("N")[..8];

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var value)) return Task.FromResult(AuthenticateResult.NoResult());
        if (!Guid.TryParseExact(value.ToString(), "D", out var owner) || owner == Guid.Empty)
            return Task.FromResult(AuthenticateResult.Fail("Invalid test owner."));
        var identity = JournalOwnerClaims.Identity(owner, NameOf(owner), AuthenticationType);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>A settable clock shared by the API under test; venue fixtures observe at <see cref="AccountHistoryOrders.At"/> + 1 day.</summary>
internal sealed class IsolationClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = AccountHistoryOrders.At.AddDays(1);
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// The synthetic venues of the suite. Every read answers the same facts for any source, so two owners that follow
/// the same native address or account index import identical source fill, order and funding IDs.
/// </summary>
internal sealed class IsolationVenues
{
    public const string Address = "0x00000000000000000000000000000000000000a1";
    public const string Index = "7";
    public FundedReader Reader { get; } = new(AccountHistoryOrders.PublicReader());
    public AccountHistoryOrders Orders { get; } = new();
    public SecurityDiscovery Discovery { get; } = new([new(Index, "Main", "main"), new("8", "Sub", "subaccount")]);
    public SecurityVerifier Verifier { get; } = new();
    public FakeMarketStream Stream { get; } = new();
    public MarketReads Market { get; } = new();

    public VenueRegistry Registry() => new([VenueDescriptor.Manual, HyperliquidPerpetualReader.Descriptor, SecurityFixture.Venue],
        [Reader], [Orders], [Market], [Market], [Stream], [Discovery], [Verifier]);

    /// <summary>
    /// The existing public fixture reader, plus one settled funding payment inside its operation, and empty fill-history
    /// pages for backfills (Phase 2, item 6b).
    /// </summary>
    internal sealed class FundedReader(FixtureReader fixture) : IPerpetualVenueReader, IVenueFundingReader, IVenueFillHistoryReader
    {
        public int PageLimit => 2000;
        public int Reach => 10_000;
        public Task<VenueFillPage> ReadFillPageAsync(Account account, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
            Task.FromResult(new VenueFillPage([], 0, null, null, 0));
        public const string FundingId = "funding-shared-1";
        public string VenueId => fixture.VenueId;
        public FixtureReader Fixture => fixture;
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) => fixture.ReadInstrumentsAsync(ct);
        public Task<PerpetualVenueReadResult> ReadAsync(string address, CancellationToken ct) => fixture.ReadAsync(address, ct);
        public Task<VenueFundingReadResult> ReadFundingAsync(Account account, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
            Task.FromResult(new VenueFundingReadResult([new(FundingId, "BTC", "BTC", -0.25m, "USDC", AccountHistoryOrders.At.AddSeconds(30), "{}")],
                fromUtc, toUtc, true, "Synthetic settled funding."));
    }

    /// <summary>Public candles and market context for any account that resolves; they hold no owner data.</summary>
    internal sealed class MarketReads : ICandleReader, IMarketContextReader
    {
        public string VenueId => "hyperliquid";
        public Task<IReadOnlyList<VenueCandle>> ReadCandlesAsync(string contractId, string interval, long fromMs, long toMs, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VenueCandle>>([]);
        public Task<IReadOnlyList<VenueMarketContext>> ReadMarketContextsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VenueMarketContext>>([new("BTC", "100", "100", "100", "99", "1000", "10", "0.0001", null)]);
    }
}

/// <summary>
/// One API over one isolated PostgreSQL schema, shared by a test class. Tests mint fresh owners per case, so they
/// stay independent while the class keeps a single factory. Server state (per-user budgets, stream slots, caches)
/// lives as long as the host and is shared by the class's cases, so cases that count it use fresh owners.
/// </summary>
public abstract class IsolationHost : IAsyncLifetime
{
    private CoreDatabase? database;
    private CoreApiFactory? factory;
    private AesGcmCredentialVault? vault;
    private readonly string evidenceRoot = Path.Combine(Path.GetTempPath(), "vessel-isolation-" + Guid.NewGuid().ToString("N"));

    internal IsolationVenues Venues { get; } = new();
    internal IsolationClock Clock { get; } = new();
    internal CoreDatabase Database => database ?? throw new InvalidOperationException("Set Vessel_TEST_POSTGRES to run the isolation suite.");
    internal CoreApiFactory Factory => factory ?? throw new InvalidOperationException("Set Vessel_TEST_POSTGRES to run the isolation suite.");
    internal ICredentialVault Vault => vault!;

    /// <summary>The configured installation owner (<c>Vessel:Auth:OwnerId</c>); never one of the owners a case mints.</summary>
    internal Guid InstallationOwner { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        // PostgresFact skips every case without a database; the fixture then stays empty.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Vessel_TEST_POSTGRES"))) return;
        database = await CoreDatabase.CreateAsync();
        vault = SecurityFixture.Vault();
        await PrepareAsync(database);
        var registry = Venues.Registry();
        var settings = new Dictionary<string, string?>
        {
            ["Vessel:Evidence:Storage:Local:RootPath"] = evidenceRoot,
            ["Vessel:Events:StreamPollSeconds"] = "1"
        };
        foreach (var (key, value) in Settings(database)) settings[key] = value;
        factory = new CoreApiFactory(InstallationOwner, connection: database.ConnectionString, settings: settings, configure: services =>
        {
            Configure(services);
            services.AddSingleton<IVenueRegistry>(registry);
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<ICredentialVault>(vault);
        });
        _ = factory.Server;
    }

    /// <summary>Runs once the journal schema exists, before the host starts.</summary>
    private protected virtual Task PrepareAsync(CoreDatabase journal) => Task.CompletedTask;

    /// <summary>Settings beside the suite's evidence root and stream poll interval.</summary>
    private protected virtual IEnumerable<KeyValuePair<string, string?>> Settings(CoreDatabase journal) => [];

    protected abstract void Configure(IServiceCollection services);

    public async Task DisposeAsync()
    {
        if (factory is not null) await factory.DisposeAsync();
        await CleanUpAsync();
        if (database is not null) await database.DisposeAsync();
        vault?.Dispose();
        if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, recursive: true);
    }

    protected virtual Task CleanUpAsync() => Task.CompletedTask;

    /// <summary>
    /// The monitor deployment's heartbeat, set to the suite clock: a Check is admitted only while a worker is seen
    /// alive, never after the clock has moved past its timeout. Set rather than raised, so a case that moved the clock
    /// forward and back can make it current again.
    /// </summary>
    internal async Task MonitorOnline()
    {
        await using var data = NpgsqlDataSource.Create(Database.ConnectionString);
        await using var command = data.CreateCommand("""
            INSERT INTO server_monitor_heartbeats (deployment, seen_at) VALUES ('monitor', @now)
            ON CONFLICT (deployment) DO UPDATE SET seen_at = EXCLUDED.seen_at
            """);
        command.Parameters.AddWithValue("now", Clock.Now);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Two fresh journal owners on this server.</summary>
    internal async Task<(Owner A, Owner B)> Owners() => (await Owner(), await Owner());

    internal Task<Owner> Owner() => Owner(Guid.NewGuid());

    /// <summary>A client for a known owner, such as one whose rows a fixture seeded directly.</summary>
    internal abstract Task<Owner> Owner(Guid id);
}

/// <summary>
/// Token mode with the suite's test scheme: <see cref="TestOwnerAuthHandler"/> selects the owner from a header. The
/// bearer token is configured but never sent.
/// </summary>
public sealed class IsolationApi : IsolationHost
{
    protected override void Configure(IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestOwnerAuthHandler>(TestOwnerAuthHandler.SchemeName, _ => { });
        services.PostConfigure<AuthenticationOptions>(options =>
        {
            options.DefaultScheme = TestOwnerAuthHandler.SchemeName;
            options.DefaultAuthenticateScheme = TestOwnerAuthHandler.SchemeName;
            options.DefaultChallengeScheme = TestOwnerAuthHandler.SchemeName;
        });
    }

    internal override Task<Owner> Owner(Guid id)
    {
        var client = Factory.CreateClient();
        // A stream answered where a 404 was expected must fail the case, not hang the run.
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add(TestOwnerAuthHandler.Header, id.ToString("D"));
        return Task.FromResult(new Owner(id, client, Journal: Database.ConnectionString));
    }
}

/// <summary>
/// Wallet mode as production runs it: the session scheme, the request guard and tab binding, over an identity schema
/// with real migrations in the journal's database. Each owner is a seeded identity user with its own synthetic EOA
/// that signs in through <c>/api/auth/challenge</c> and <c>/api/auth/verify</c>; its client then carries only the
/// session cookie, <c>X-Vessel-User</c> and the accepted <c>Origin</c>. No test scheme and no token exist here.
/// </summary>
public sealed class WalletIsolationApi : IsolationHost
{
    public const string Origin = "https://vessel.test";
    private IdentityDatabase? identity;
    private int clients;

    internal IdentityDatabase Identity => identity ?? throw new InvalidOperationException("Set Vessel_TEST_POSTGRES to run the isolation suite.");

    /// <summary>The host's configuration, as the offline <c>identity</c> commands read it.</summary>
    internal IConfiguration Configuration => new ConfigurationBuilder().AddInMemoryCollection(Settings(Database)).Build();

    private protected override async Task PrepareAsync(CoreDatabase journal) => identity = await IdentityDatabase.CreateAsync();

    private protected override IEnumerable<KeyValuePair<string, string?>> Settings(CoreDatabase journal) => new Dictionary<string, string?>
    {
        ["Vessel:Auth:Mode"] = "Wallet", ["Vessel:Auth:Token"] = null, ["Vessel:Auth:OwnerId"] = InstallationOwner.ToString(),
        ["Vessel:Identity:Schema"] = Identity.Schema.Name, ["Vessel:Identity:Origins:0"] = Origin,
        ["ConnectionStrings:Vessel"] = journal.ConnectionString
    };

    protected override void Configure(IServiceCollection services) => services.AddSingleton<IStartupFilter, ClientAddress>();

    protected override async Task CleanUpAsync()
    {
        if (identity is not null) await identity.DisposeAsync();
    }

    /// <summary>Seeds the user and its wallet, then signs in over HTTP. The session's user is the owner.</summary>
    internal override async Task<Owner> Owner(Guid id)
    {
        var key = EthECKey.GenerateKey();
        var (_, wallet) = await Identity.SeedAsync(key, id, role: "member");
        var client = Client();
        var session = await IdentityHttpTest.SignInAsync(client, key);
        Assert.Equal((id, wallet.Id), (session.User.Id, session.Wallet.Id));
        return new(id, client, new(key, wallet.Id, session.Session.Id), Database.ConnectionString);
    }

    /// <summary>A browser on its own device: cookies, the accepted origin and its own client address, but no session yet.</summary>
    internal HttpClient Client()
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new(Origin) });
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("Origin", Origin);
        client.DefaultRequestHeaders.Add("X-Vessel-Request", "1");
        var device = Interlocked.Increment(ref clients);
        client.DefaultRequestHeaders.Add(ClientAddress.Header, $"10.{device / 250 % 250}.{device % 250}.1");
        return client;
    }

    /// <summary>A second session of an existing owner, as from another browser.</summary>
    internal async Task<Owner> SignInAgain(Owner owner)
    {
        var client = Client();
        var session = await IdentityHttpTest.SignInAsync(client, owner.Login!.Key);
        Assert.Equal(owner.Id, session.User.Id);
        return owner with { Http = client, Login = owner.Login with { Session = session.Session.Id } };
    }

    /// <summary>
    /// Gives each test client its own remote address, so the per-client sign-in limits partition per device as they
    /// would in production instead of every owner of a class sharing one test-server address. Test hosting only.
    /// </summary>
    private sealed class ClientAddress : IStartupFilter
    {
        public const string Header = "X-Isolation-Client-Address";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[Header], out var address)) context.Connection.RemoteIpAddress = address;
                return following(context);
            });
            next(app);
        };
    }
}

/// <summary>A Wallet-mode owner's sign-in: its synthetic EOA, the wallet row and the session its client holds.</summary>
internal sealed record WalletLogin(EthECKey Key, Guid Wallet, Guid Session);

/// <summary>
/// An owner's HTTP client. In Token mode every request carries only the test owner header, never a bearer token; in
/// Wallet mode only the session cookie and the tab-binding and origin headers (<see cref="Login"/>). The
/// <see cref="Journal"/> connection lets a <see cref="Snapshot"/> fingerprint the owner's persisted rows.
/// </summary>
internal sealed record Owner(Guid Id, HttpClient Http, WalletLogin? Login = null, string? Journal = null)
{
    public async Task<JsonElement> GetJson(string path)
    {
        using var response = await Http.GetAsync(path);
        return await Isolated.Json(response, HttpStatusCode.OK);
    }

    public async Task<string> GetText(string path)
    {
        using var response = await Http.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {(int)response.StatusCode}: {body}");
        return body;
    }

    public async Task<JsonElement> Send(HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await Http.SendAsync(request);
        return await Isolated.Json(response, expected);
    }

    public Task<JsonElement> Post(string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK) =>
        Send(HttpMethod.Post, path, body, expected);

    public async Task<Guid> Portfolio(string name)
    {
        var created = await Post("/api/portfolios", new CreatePortfolioRequest(name), HttpStatusCode.Created);
        return created.GetProperty("id").GetGuid();
    }

    public async Task<JsonElement> ManualAccount(string name, Guid? portfolio = null) =>
        await Post("/api/accounts", new CreateAccountRequest(portfolio, name, "manual", ManualAccountValueUsd: "100"), HttpStatusCode.Created);

    /// <summary>A Hyperliquid account on the shared synthetic address, refreshed once so its fills, orders and funding are retained.</summary>
    public async Task<JsonElement> SyncedVenueAccount(string name, Guid? portfolio = null)
    {
        var account = await Post("/api/accounts", new CreateAccountRequest(portfolio, name, "hyperliquid", IsolationVenues.Address), HttpStatusCode.Created);
        return await Post($"/api/accounts/{account.GetProperty("id").GetGuid()}/sync");
    }

    /// <summary>
    /// A planned Play on a synced account, opened and closed by explicitly linking the shared entry and exit orders.
    /// Its status changes append the owner's <c>play.opened</c> and <c>play.closed</c> events.
    /// </summary>
    public async Task<Guid> ClosedPlay(Guid account, string title)
    {
        var draft = await Post("/api/plays", new CreatePlayRequest(account, "BTC", "venue", title, TestPlays.Plan()), HttpStatusCode.Created);
        var id = draft.GetProperty("summary").GetProperty("id").GetGuid();
        await Post($"/api/plays/{id}/status", new ChangePlayStatusRequest(draft.GetProperty("summary").GetProperty("version").GetInt64(), "planned"));
        await Post($"/api/plays/{id}/execution/links", new LinkOrderRequest("entry", "entry", "entry-1"));
        await Post($"/api/plays/{id}/execution/links", new LinkOrderRequest("exit", "exit"));
        Assert.Equal("closed", (await GetJson($"/api/plays/{id}")).GetProperty("summary").GetProperty("status").GetString());
        return id;
    }

    /// <summary>A Lighter-style account on the shared synthetic account index.</summary>
    public async Task<JsonElement> IndexAccount(string name) =>
        await Post("/api/accounts", new CreateAccountRequest(null, name, SecurityFixture.Venue.Id, SourceId: IsolationVenues.Index), HttpStatusCode.Created);
}

/// <summary>Assertions shared by the isolation cases.</summary>
internal static class Isolated
{
    public static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected,
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery} answered {(int)response.StatusCode}, expected {(int)expected}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>A unique marker for names, titles and notes, so leaks can be found in any response body.</summary>
    public static string Marker(string label) => $"{label}-{Guid.NewGuid():N}";

    /// <summary>
    /// Sends the same request with the other owner's ID and with a random unknown ID. Both must answer the same status,
    /// the same body (ProblemDetails <c>title</c>, <c>detail</c>, <c>code</c> and everything else except the
    /// per-response <c>traceId</c>) and the same response and content headers apart from <see cref="VolatileHeaders"/>.
    /// A response may echo the ID the caller sent, so each body and header value is compared with its own ID replaced.
    /// Returns the shared status.
    /// </summary>
    public static async Task<HttpStatusCode> SameAsUnknown(Owner caller, Guid foreign, Func<Guid, HttpRequestMessage> request) =>
        (await Compare(caller, foreign.ToString("D"), Guid.NewGuid().ToString("D"), id => request(Guid.Parse(id)))).Status;

    /// <summary>
    /// As <see cref="SameAsUnknown(Owner, Guid, Func{Guid, HttpRequestMessage})"/> for a textual identifier, such as an
    /// Activity operation ID, with an <paramref name="unknown"/> one of the same shape.
    /// </summary>
    public static async Task<HttpStatusCode> SameAsUnknown(Owner caller, string foreign, string unknown, Func<string, HttpRequestMessage> request) =>
        (await Compare(caller, foreign, unknown, request)).Status;

    /// <summary>
    /// As <see cref="SameAsUnknown(Owner, Guid, Func{Guid, HttpRequestMessage})"/>, and the shared answer is 404. A
    /// <paramref name="detail"/> names the lookup that must refuse it, so the case cannot pass by failing earlier; a
    /// <paramref name="code"/> is the ProblemDetails <c>code</c> extension that refusal carries.
    /// </summary>
    public static async Task NotFound(Owner caller, Guid foreign, Func<Guid, HttpRequestMessage> request, string? detail = null,
        string? code = null) =>
        Refused(await Compare(caller, foreign.ToString("D"), Guid.NewGuid().ToString("D"), id => request(Guid.Parse(id))), detail, code);

    public static async Task NotFound(Owner caller, string foreign, string unknown, Func<string, HttpRequestMessage> request, string? detail = null) =>
        Refused(await Compare(caller, foreign, unknown, request), detail, null);

    private static void Refused((HttpStatusCode Status, string Body) answer, string? detail, string? code)
    {
        Assert.Equal(HttpStatusCode.NotFound, answer.Status);
        if (detail is not null) Assert.Equal(detail, JsonDocument.Parse(answer.Body).RootElement.GetProperty("detail").GetString());
        if (code is not null) Assert.Equal(code, Code(answer.Body));
    }

    /// <summary>The ProblemDetails <c>code</c> extension of a response body.</summary>
    public static string? Code(string body) => JsonDocument.Parse(body).RootElement.GetProperty("code").GetString();

    /// <summary>The response is a refusal with this status and <c>code</c>; returns its body.</summary>
    public static async Task<string> Problem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status,
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery} answered {(int)response.StatusCode}, expected {(int)status}: {body}");
        Assert.Equal(code, Code(body));
        return body;
    }

    private static async Task<(HttpStatusCode Status, string Body)> Compare(Owner caller, string foreign, string unknown,
        Func<string, HttpRequestMessage> request)
    {
        Assert.NotEqual(foreign, unknown);
        using var foreignRequest = request(foreign);
        using var unknownRequest = request(unknown);
        using var foreignResponse = await caller.Http.SendAsync(foreignRequest);
        using var unknownResponse = await caller.Http.SendAsync(unknownRequest);
        var foreignBody = await foreignResponse.Content.ReadAsStringAsync();
        var unknownBody = await unknownResponse.Content.ReadAsStringAsync();
        var what = $"{foreignRequest.Method} {foreignRequest.RequestUri}";
        Assert.True(foreignResponse.StatusCode == unknownResponse.StatusCode,
            $"{what}: foreign ID answered {(int)foreignResponse.StatusCode} ({foreignBody}), unknown ID {(int)unknownResponse.StatusCode} ({unknownBody}).");
        Assert.Equal(Headers(unknownResponse, unknown), Headers(foreignResponse, foreign));
        Assert.Equal(Comparable(unknownBody, unknown), Comparable(foreignBody, foreign));
        return (foreignResponse.StatusCode, foreignBody);
    }

    /// <summary>
    /// Headers that differ between two answers to the same request without telling anything about the ID: the
    /// response time, per-request correlation and trace identifiers, and the length of a body that is compared itself
    /// (an echoed ID of another length changes it). Every other response and content header, <c>ETag</c>,
    /// <c>Set-Cookie</c>, <c>Cache-Control</c>, <c>Vary</c>, <c>Location</c> and the full <c>Content-Type</c> with its
    /// charset included, must be identical.
    /// </summary>
    internal static readonly IReadOnlySet<string> VolatileHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Date", "X-Correlation-ID", "Request-Id", "X-Request-ID", "traceparent", "tracestate", "Content-Length"
    };

    /// <summary>The response and content headers, lower-cased and sorted by name with values in order, without the volatile ones.</summary>
    private static List<string> Headers(HttpResponseMessage response, string sent) =>
        response.Headers.Concat(response.Content.Headers)
            .Where(header => !VolatileHeaders.Contains(header.Key))
            .Select(header => header.Key.ToLowerInvariant() + ": " +
                string.Join(", ", header.Value).Replace(sent, "{sent-id}", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToList();

    public static Func<Guid, HttpRequestMessage> Request(HttpMethod method, Func<Guid, string> path, Func<Guid, object?>? body = null) =>
        id => new(method, path(id)) { Content = body?.Invoke(id) is { } content ? JsonContent.Create(content) : null };

    private static string Comparable(string body, string sent)
    {
        if (body.Length == 0) return body;
        var node = JsonNode.Parse(body);
        if (node is JsonObject problem) problem.Remove("traceId");
        return (node?.ToJsonString() ?? "null").Replace(sent, "{sent-id}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The body contains none of the other owner's IDs or markers.</summary>
    public static void NoLeak(string body, params object[] foreign)
    {
        foreach (var value in foreign)
        {
            var text = value is Guid id ? id.ToString("D") : value.ToString()!;
            Assert.False(body.Contains(text, StringComparison.OrdinalIgnoreCase), $"The response leaks {text}: {body}");
        }
    }

    /// <summary>Reads the owner's state through its own client; mutation attempts by another owner must leave it byte-identical.</summary>
    public static async Task<string> State(Owner owner, params string[] paths)
    {
        var parts = new List<string>();
        foreach (var path in paths) parts.Add(path + " " + await owner.GetText(path));
        return string.Join("\n", parts);
    }
}

/// <summary>
/// Fingerprints of an owner's persisted journal rows, so a change no response shows (a funding amount, a watch, an
/// event receipt) still fails a snapshot. Every table of the journal schema with an owner column (<c>"OwnerId"</c> or
/// <c>owner_id</c>) is read for that owner: its row count and an MD5 of its rows' text in a stable order. Tables are
/// discovered, not listed, so a new owner-scoped table is fingerprinted without a change here; <see cref="Required"/>
/// fails the read if discovery misses a table the suite relies on. This host disables Wolverine message persistence,
/// so no envelope table exists: the event journal tables are the record an outbox would relay. Identity rows live in
/// their own schema and are not part of the journal.
/// </summary>
internal static class Persisted
{
    /// <summary>Owner-scoped tables a snapshot must cover: imported facts, funding, monitor rows, events and notifications.</summary>
    internal static readonly string[] Required =
    [
        "activity_facts_revisions", "portfolios", "accounts", "account_snapshots", "account_positions", "account_stablecoins", "imported_orders", "imported_fills",
        "funding_payments", "account_funding_states", "plays", "play_order_links", "play_status_changes", "monitor_accounts",
        "monitor_exit_watches", "play_discrepancies", "events", "event_cursors", "event_dedup", "event_fills", "event_baselines",
        "event_receipts", "notifications", "notification_deliveries", "notification_sources", "notification_cursors"
    ];

    /// <summary>One line per owner-scoped table: <c>table rows=N md5=…</c>, ordered by table name.</summary>
    public static async Task<List<string>> Of(string journal, Guid owner) =>
        (await Rows(journal, owner)).Select(table => $"{table.Key} rows={table.Value.Rows} md5={table.Value.Hash}").ToList();

    /// <summary>Row count and fingerprint of the owner's rows per owner-scoped table.</summary>
    public static async Task<SortedDictionary<string, (long Rows, string Hash)>> Rows(string journal, Guid owner)
    {
        await using var connection = new NpgsqlConnection(journal);
        await connection.OpenAsync();
        var tables = new List<(string Table, string Column)>();
        await using (var discover = new NpgsqlCommand("""
            SELECT c.table_name, c.column_name FROM information_schema.columns c
            JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE c.table_schema = current_schema() AND t.table_type = 'BASE TABLE'
              AND c.column_name IN ('OwnerId', 'owner_id') AND c.data_type = 'uuid'
            ORDER BY c.table_name
            """, connection))
        await using (var reader = await discover.ExecuteReaderAsync())
            while (await reader.ReadAsync()) tables.Add((reader.GetString(0), reader.GetString(1)));
        var missing = Required.Except(tables.Select(t => t.Table)).ToList();
        Assert.True(missing.Count == 0, "Owner-scoped tables not found for fingerprinting: " + string.Join(", ", missing));
        var result = new SortedDictionary<string, (long, string)>(StringComparer.Ordinal);
        foreach (var (table, column) in tables)
        {
            // Row text includes every column; ordering by it makes the hash independent of physical order.
            await using var command = new NpgsqlCommand(
                $"SELECT count(*), coalesce(md5(string_agg(r::text, chr(10) ORDER BY r::text)), '') FROM {Quote(table)} r WHERE r.{Quote(column)} = @owner",
                connection);
            command.Parameters.AddWithValue("owner", owner);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            result[table] = (reader.GetInt64(0), reader.GetString(1));
        }
        return result;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}

/// <summary>
/// Owners' state before a sequence of attempts by another owner: the responses of the given paths and the owner's
/// persisted rows (<see cref="Persisted"/>). <see cref="After"/> runs one attempt and then requires every snapshot
/// identical, so any change is pinned to the attempt that made it.
/// </summary>
internal sealed class Snapshot
{
    private readonly List<(Owner Owner, string[] Paths, string State, List<string> Rows)> parts = [];

    public static Task<Snapshot> Of(Owner owner, params string[] paths) => Of((owner, paths));

    public static async Task<Snapshot> Of(params (Owner Owner, string[] Paths)[] owners)
    {
        var snapshot = new Snapshot();
        foreach (var (owner, paths) in owners) snapshot.parts.Add((owner, paths, await Isolated.State(owner, paths), await Rows(owner)));
        return snapshot;
    }

    private static Task<List<string>> Rows(Owner owner) =>
        Persisted.Of(owner.Journal ?? throw new InvalidOperationException("A snapshot needs the owner's journal connection."), owner.Id);

    public async Task Unchanged()
    {
        foreach (var (owner, paths, state, rows) in parts)
        {
            Assert.Equal(state, await Isolated.State(owner, paths));
            Assert.Equal(rows, await Rows(owner));
        }
    }

    public async Task After(Func<Task> attempt)
    {
        await attempt();
        await Unchanged();
    }

    public async Task<T> After<T>(Func<Task<T>> attempt)
    {
        var result = await attempt();
        await Unchanged();
        return result;
    }
}

/// <summary>Names a route (method and pattern as registered) that the annotated isolation case exercises across two owners.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class CoversAttribute(string method, string route) : Attribute
{
    public string Route { get; } = method + " " + route;
}
