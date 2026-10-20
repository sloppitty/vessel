using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Credentials;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Infrastructure.Venues.Lighter;
using Vessel.Infrastructure.Venues.Risex;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Order-history retention on the owner's account Refresh, through the production monitor path: the public read
/// commits first, then the optional order read is retained without linking orders or changing Plays.
/// </summary>
public sealed class AccountOrderHistoryTests
{
    private const string OrderRetryNotice = "Public account data refreshed; order coverage is unavailable. Retry the account check later.";
    private const string CredentialNotice = "Public account data refreshed; order coverage is unavailable. Update the read-only credential.";
    private const string InterruptedNotice = "Public account data refreshed; order coverage is unavailable because the order check did not complete.";
    private const string DeferredNotice = "Public account data refreshed; the venue rate limit deferred the order check.";

    private sealed class History(CoreDatabase database, Guid owner, Account account, FixtureReader reader, VenueRegistry venues) : IAsyncDisposable
    {
        public Account Account => account;
        public FixtureReader Reader => reader;
        public VesselDbContext Context() => database.Context(owner);
        public async Task<AccountDto> RefreshAsync(IAccountCredentialReader? credentials = null, CancellationToken ct = default)
        {
            await using var db = Context();
            return await MonitorTestPaths.RefreshAsync(db, owner, venues, account.Id, credentials, ct);
        }
        public ValueTask DisposeAsync() => database.DisposeAsync();
    }

    private static async Task<History> Harness(AccountHistoryOrders orders, bool enabled = true, bool foreign = false,
        bool supportsOrders = true, bool readerAvailable = true, bool hasSource = true, Action<Account>? seed = null)
    {
        var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var accountOwner = foreign ? Guid.NewGuid() : owner;
        var account = new Account(Guid.NewGuid(), accountOwner, orders.VenueId, "History");
        account.Configure(null, null, null, hasSource ? "42" : null);
        if (!enabled) account.UpdateSettings(account.Name, null, false);
        seed?.Invoke(account);
        await using (var db = database.Context(accountOwner)) { db.Accounts.Add(account); await db.SaveChangesAsync(); }
        var reader = AccountHistoryOrders.PublicReader(orders.VenueId);
        var descriptor = AccountHistoryOrders.Descriptor(orders.VenueId);
        var venues = new VenueRegistry([VenueDescriptor.Manual, descriptor with { Capabilities = descriptor.Capabilities with { Orders = supportsOrders } }],
            [reader], readerAvailable ? [orders] : [], [], [], [], [], []);
        return new(database, owner, account, reader, venues);
    }

    [PostgresFact]
    public async Task Retains_old_orders_for_every_instrument_without_active_plays_or_matching()
    {
        var orders = new AccountHistoryOrders();
        orders.Result = orders.Result with { Orders = [..orders.Result.Orders,
            orders.Result.Orders[0] with { OrderId = "other-instrument", ContractId = "ETH", VenueContractId = "ETH", Status = VenueOrderStatus.Rejected }] };
        await using var h = await Harness(orders);
        var refreshed = await h.RefreshAsync();
        Assert.Equal(h.Reader.Result.HistoryNotice + "\nOrder history: " + orders.Result.Notice, refreshed.HistoryNotice);
        Assert.Equal(1, orders.Reads);
        Assert.Equal(h.Account.Id, orders.Account!.Id);
        await using var verify = h.Context();
        Assert.Equal(orders.Result.Orders.Select(o => o.OrderId).Order(), (await verify.Orders.Select(o => o.ExternalOrderId).ToListAsync()).Order());
        Assert.Empty(await verify.OrderLinks.ToListAsync());
        Assert.Empty(await verify.Plays.ToListAsync());
    }

    [PostgresTheory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("adapter")]
    [InlineData("invalid-side")]
    public async Task Optional_failure_is_a_safe_notice_and_never_partially_retains_orders(string failure)
    {
        var orders = new AccountHistoryOrders();
        orders.Failure = failure switch
        {
            "http" => new HttpRequestException("ro:synthetic-secret provider response"),
            "timeout" => new OperationCanceledException("ro:synthetic-secret"),
            "adapter" => new VenueReadException("ro:synthetic-secret"),
            _ => null
        };
        if (failure == "invalid-side")
            orders.Result = orders.Result with { Orders = [orders.Result.Orders[0], orders.Result.Orders[1] with { Side = "B" }] };
        await using var h = await Harness(orders);
        var refreshed = await h.RefreshAsync();
        Assert.Equal(1, orders.Reads);
        Assert.Contains(OrderRetryNotice, refreshed.HistoryNotice);
        Assert.DoesNotContain("ro:", refreshed.HistoryNotice);
        Assert.Equal(("synced", (string?)null), (refreshed.SyncStatus, refreshed.LastSyncError));
        await using var verify = h.Context();
        Assert.Empty(await verify.Orders.ToListAsync());
    }

    [PostgresFact]
    public async Task Caller_cancellation_propagates_before_retention_even_if_reader_returns()
    {
        using var cancelled = new CancellationTokenSource();
        var orders = new AccountHistoryOrders { AfterRead = cancelled.Cancel };
        await using var h = await Harness(orders);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.RefreshAsync(ct: cancelled.Token));
        Assert.Equal(1, orders.Reads);
        await using var verify = h.Context();
        Assert.Empty(await verify.Orders.ToListAsync());
        // The public refresh committed before the order read; only its order coverage is reported incomplete.
        var account = await verify.Accounts.SingleAsync();
        Assert.Equal("synced", account.SyncStatus);
        Assert.EndsWith("\nOrder history: " + InterruptedNotice, account.HistoryNotice);
    }

    [PostgresFact]
    public async Task Already_cancelled_request_does_not_read_the_venue()
    {
        var orders = new AccountHistoryOrders();
        await using var h = await Harness(orders);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.RefreshAsync(ct: new(true)));
        Assert.Equal((0, 0), (h.Reader.Reads, orders.Reads));
        await using var verify = h.Context();
        Assert.Empty(await verify.Orders.ToListAsync());
        Assert.Empty(await verify.Snapshots.ToListAsync());
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lighter_missing_or_unusable_credential_skips_order_read(bool readerRegistered)
    {
        var orders = new AccountHistoryOrders("lighter");
        var credential = readerRegistered ? new AccountHistoryCredential(null) : null;
        await using var h = await Harness(orders);
        var refreshed = await h.RefreshAsync(credential);
        Assert.Contains(CredentialNotice, refreshed.HistoryNotice);
        Assert.Equal("synced", refreshed.SyncStatus);
        Assert.Equal(0, orders.Reads);
        await using var verify = h.Context();
        Assert.Empty(await verify.Orders.ToListAsync());
        if (credential is not null) Assert.Equal(h.Account.Id, credential.AccountId);
    }

    [PostgresFact]
    public async Task Lighter_usable_credential_uses_account_aware_order_reader()
    {
        var orders = new AccountHistoryOrders("lighter");
        await using var h = await Harness(orders);
        await h.RefreshAsync(new AccountHistoryCredential("ro:synthetic-only"));
        Assert.Equal(h.Account.Id, orders.Account!.Id);
        Assert.Equal(1, orders.Reads);
        await using var verify = h.Context();
        Assert.Equal(2, await verify.Orders.CountAsync());
    }

    [PostgresFact]
    public async Task Cancellation_during_credential_lookup_propagates_without_order_reads()
    {
        using var cancelled = new CancellationTokenSource();
        var orders = new AccountHistoryOrders("lighter");
        var credential = new AccountHistoryCredential(null) { AfterRead = cancelled.Cancel };
        await using var h = await Harness(orders);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.RefreshAsync(credential, cancelled.Token));
        Assert.Equal(h.Account.Id, credential.AccountId);
        Assert.Equal(0, orders.Reads);
        await using var verify = h.Context();
        Assert.Empty(await verify.Orders.ToListAsync());
    }

    [PostgresTheory]
    [InlineData(false, true, 400)]
    [InlineData(true, false, 404)]
    public async Task Disabled_or_foreign_account_does_not_read_credentials_or_orders(bool enabled, bool owned, int status)
    {
        var orders = new AccountHistoryOrders("lighter");
        var credential = new AccountHistoryCredential("ro:synthetic-only");
        await using var h = await Harness(orders, enabled, !owned);
        var error = await Assert.ThrowsAsync<ProblemException>(() => h.RefreshAsync(credential));
        Assert.Equal(status, error.StatusCode);
        Assert.Null(credential.AccountId);
        Assert.Equal((0, 0), (h.Reader.Reads, orders.Reads));
    }

    [PostgresTheory]
    [InlineData(false, true, null)]
    [InlineData(true, false, OrderRetryNotice)]
    public async Task Descriptor_capability_and_missing_reader_never_read_orders(bool supportsOrders, bool available, string? notice)
    {
        var orders = new AccountHistoryOrders();
        await using var h = await Harness(orders, supportsOrders: supportsOrders, readerAvailable: available);
        var refreshed = await h.RefreshAsync();
        // A venue without order history adds no order section; a missing order reader is unavailable coverage.
        Assert.Equal(h.Reader.Result.HistoryNotice + (notice is null ? "" : "\nOrder history: " + notice), refreshed.HistoryNotice);
        Assert.Equal(0, orders.Reads);
        await using var verify = h.Context();
        Assert.Empty(await verify.Orders.ToListAsync());
    }

    [PostgresFact]
    public async Task Missing_source_is_rejected_before_reading()
    {
        var orders = new AccountHistoryOrders();
        await using var h = await Harness(orders, hasSource: false);
        var error = await Assert.ThrowsAsync<ProblemException>(() => h.RefreshAsync());
        Assert.Equal((400, "This account cannot be refreshed."), (error.StatusCode, error.Message));
        Assert.Equal((0, 0), (h.Reader.Reads, orders.Reads));
    }

    [Fact]
    public void Order_notice_replacement_keeps_latest_public_coverage_and_failure_state_without_growth()
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "History");
        account.RecordSync(AccountHistoryOrders.At, "Previous public coverage.");
        account.RecordOrderHistoryNotice("Previous order coverage.");
        account.RecordSync(AccountHistoryOrders.At.AddMinutes(1), "Latest public coverage.");
        Assert.Contains("Previous order coverage.", account.HistoryNotice);
        account.RecordSyncFailure();
        var state = (account.SyncStatus, account.LastSyncError, account.LastSyncedAtUtc, account.SettingsRevision);
        account.RecordOrderHistoryNotice("Latest order coverage.");
        var expected = account.HistoryNotice;
        for (var refresh = 0; refresh < 5; refresh++)
        {
            account.RecordOrderHistoryNotice("Latest order coverage.");
            Assert.Equal(expected, account.HistoryNotice);
            Assert.Equal(state, (account.SyncStatus, account.LastSyncError, account.LastSyncedAtUtc, account.SettingsRevision));
        }
        Assert.Contains("Latest public coverage.", account.HistoryNotice);
        Assert.DoesNotContain("Previous", account.HistoryNotice);
        account.RecordOrderHistoryNotice(null);
        Assert.Equal("Latest public coverage.", account.HistoryNotice);
        Assert.Equal(state, (account.SyncStatus, account.LastSyncError, account.LastSyncedAtUtc, account.SettingsRevision));
    }

    [PostgresFact]
    public async Task A_failed_public_refresh_keeps_retained_notices_and_settings_and_never_reads_orders()
    {
        var orders = new AccountHistoryOrders();
        await using var h = await Harness(orders, seed: account =>
        {
            account.RecordSync(AccountHistoryOrders.At, "Retained public coverage.");
            account.RecordOrderHistoryNotice("Retained order coverage.");
        });
        h.Reader.Fail = true;
        var error = await Assert.ThrowsAsync<ProblemException>(() => h.RefreshAsync());
        Assert.Equal((502, MonitorTestPaths.RefreshFailed), (error.StatusCode, error.Message));
        Assert.Equal(0, orders.Reads);
        await using (var verify = h.Context())
        {
            var failed = await verify.Accounts.SingleAsync();
            Assert.Equal("Retained public coverage.\nOrder history: Retained order coverage.", failed.HistoryNotice);
            Assert.Equal(("error", AccountHistoryOrders.At, h.Account.SettingsRevision),
                (failed.SyncStatus, failed.LastSyncedAtUtc, failed.SettingsRevision));
        }
        // The next successful refresh replaces both sections without a settings change.
        h.Reader.Fail = false;
        var refreshed = await h.RefreshAsync();
        Assert.Equal(h.Reader.Result.HistoryNotice + "\nOrder history: " + orders.Result.Notice, refreshed.HistoryNotice);
        Assert.Equal(("synced", (string?)null, h.Account.SettingsRevision), (refreshed.SyncStatus, refreshed.LastSyncError, refreshed.SettingsRevision));
    }

    [PostgresTheory]
    [InlineData("hyperliquid", 488)]
    [InlineData("risex", 312)]
    // Lighter's single omission sentence names liquidations, deleverages and market settlements as well (Phase 0, D-25).
    [InlineData("lighter", 877)]
    [InlineData("lighter-rh", 877)]
    public async Task Optional_failure_notice_fits_with_each_venues_maximum_public_coverage(string venue, int combinedLength)
    {
        var coverage = AccountHistoryNoticeBudget.CombinedCases().Where(c => c.Venue == venue).MaxBy(c => c.Public.Length);
        var orders = new AccountHistoryOrders(venue) { Failure = new HttpRequestException("synthetic optional failure") };
        await using var h = await Harness(orders, seed: account =>
        {
            account.RecordSync(AccountHistoryOrders.At, coverage.Public);
            account.RecordOrderHistoryNotice(coverage.Orders);
        });
        h.Reader.Result = h.Reader.Result with { HistoryNotice = coverage.Public };
        var refreshed = await h.RefreshAsync(new AccountHistoryCredential("ro:synthetic-only"));
        Assert.Equal(1, orders.Reads);
        Assert.Equal(coverage.Public + "\nOrder history: " + OrderRetryNotice, refreshed.HistoryNotice);
        Assert.Equal(combinedLength, refreshed.HistoryNotice!.Length);
        // Every order notice the monitor can record also fits the stored column with that public coverage.
        foreach (var notice in new[] { OrderRetryNotice, CredentialNotice, InterruptedNotice, DeferredNotice })
            Assert.True((coverage.Public + "\nOrder history: " + notice).Length <= 1000);
    }
}

internal sealed class AccountHistoryOrders(string venueId = "hyperliquid") : IVenueOrderReader
{
    internal static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
    public string VenueId => venueId;
    public int Reads { get; private set; }
    public Account? Account { get; private set; }
    public Exception? Failure { get; set; }
    public Action? AfterRead { get; set; }
    public Func<CancellationToken, Task>? WaitForRead { get; set; }
    public VenueOrderReadResult Result { get; set; } = new(
        [Order("entry", "buy", false), Order("exit", "sell", true)], At.AddDays(1), "Bounded recent order history only; no full-history backfill.");
    private static VenueOrder Order(string id, string side, bool reduceOnly) =>
        new(id, "BTC", side, "limit", reduceOnly ? 110 : 100, null, reduceOnly, false, 1, 0,
            At.AddSeconds(reduceOnly ? 59 : -1), VenueOrderStatus.Filled, "filled", At.AddSeconds(reduceOnly ? 60 : 0), "BTC");
    public Task<VenueOrderReadResult> ReadOrdersAsync(string source, CancellationToken ct) => throw new NotSupportedException("Use account-aware reads.");
    public async Task<VenueOrderReadResult> ReadOrdersAsync(Account account, CancellationToken ct)
    {
        Reads++; Account = account; ct.ThrowIfCancellationRequested(); AfterRead?.Invoke();
        if (WaitForRead is not null) await WaitForRead(ct);
        if (Failure is not null) throw Failure;
        return Result;
    }
    internal static VenueDescriptor Descriptor(string venue) => venue switch
    {
        "lighter" => LighterReader.Descriptor,
        "lighter-rh" => LighterExchange.Robinhood.Descriptor,
        "risex" => RisexReader.Descriptor,
        _ => HyperliquidPerpetualReader.Descriptor
    };
    internal static VenueRegistry Registry(FixtureReader reader, AccountHistoryOrders orders) =>
        new([VenueDescriptor.Manual, Descriptor(reader.VenueId)], [reader], [orders], [], [], [], [], []);
    internal static FixtureReader PublicReader(string venue = "hyperliquid")
    {
        var reader = new FixtureReader { VenueId = venue };
        reader.Result = reader.Result with
        {
            Snapshot = new(At.AddDays(1), "synthetic-perpetuals", 1000, 900, 0, []),
            Fills =
            [
                new("entry-fill", "BTC", "buy", "Open Long", 100, 1, 0, "USDC", 0, At, "entry", "", "{}", ExecutionFacts.Open, VenueContractId: "BTC"),
                new("exit-fill", "BTC", "sell", "Close Long", 110, 1, 0, "USDC", 10, At.AddSeconds(60), "exit", "", "{}", ExecutionFacts.Close, VenueContractId: "BTC")
            ],
            HistoryNotice = "Bounded recent public fills only."
        };
        return reader;
    }
}

internal sealed class AccountHistoryCredential(string? token) : IAccountCredentialReader
{
    public string? Token { get; set; } = token;
    public Guid? AccountId { get; private set; }
    public Action? AfterRead { get; set; }
    public Task<string?> ReadAsync(Guid accountId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); AccountId = accountId; AfterRead?.Invoke();
        return Task.FromResult(Token);
    }
    public async Task<CredentialAttempt?> ReadAttemptAsync(Guid accountId, CancellationToken ct) =>
        await ReadAsync(accountId, ct) is { } token ? new(token, "synthetic-revision") : null;
    public Task MarkRefusedAsync(Guid accountId, string attemptedRevision, CancellationToken ct) => throw new NotSupportedException();
}

internal static class AccountHistoryNoticeBudget
{
    public static string LighterCappedOrders => Capped(LighterExchange.Mainnet);

    public static string Capped(LighterExchange exchange) => exchange.OrdersNotice + LighterReader.OrdersPageLimitNotice;

    public static string LighterPublic(string credentialNotice, string terminalNotice, int missingPnl = 500, int fees = 500, int selfTrades = 500,
        int forced = 500) =>
        Public(LighterExchange.Mainnet, credentialNotice, terminalNotice, missingPnl, fees, selfTrades, forced);

    public static string Public(LighterExchange exchange, string credentialNotice, string terminalNotice,
        int missingPnl = 500, int fees = 500, int selfTrades = 500, int forced = 500) =>
        string.Join(" ", new[] { exchange.HistoryNotice, credentialNotice, terminalNotice,
            LighterReader.OmissionNotice(missingPnl, fees, forced, forced, forced, selfTrades) }
            .Where(text => !string.IsNullOrEmpty(text)));

    public static IEnumerable<(string Venue, string Public, string Orders)> CombinedCases()
    {
        // A refused bind clears the token, so it cannot also cause an authenticated-page retry.
        // A failed page clears the cursor, so unavailable history and the page cap cannot coexist.
        // Counters share at most 500 rows; use the maximum three-digit width for each conservatively.
        // Every Lighter exchange is checked: a longer exchange name must still fit.
        foreach (var exchange in LighterExchange.All)
        {
            foreach (var credential in new[] { "", LighterReader.CredentialRefusedNotice, LighterReader.HistoryRetryNotice })
            foreach (var terminal in new[] { "", exchange.PageLimitNotice, LighterReader.HistoryUnavailableNotice })
                yield return (exchange.VenueId, Public(exchange, credential, terminal), Capped(exchange));
            yield return (exchange.VenueId, string.Join(" ", exchange.HistoryNotice, LighterReader.NonzeroRateNotice), Capped(exchange));
        }
        yield return ("hyperliquid", Literal(typeof(HyperliquidPerpetualReader), "HistoryNotice"), Literal(typeof(HyperliquidPerpetualReader), "OrdersNotice"));
        yield return ("risex", Literal(typeof(RisexReader), "HistoryNotice"), Literal(typeof(RisexReader), "OrdersNotice"));
    }

    private static string Literal(Type type, string name) => (string)type.GetField(name,
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;
}
