using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Application.Credentials;
using Vessel.Application.Monitoring;
using Vessel.Application.Ownership;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Infrastructure.Venues.Lighter;
using Vessel.Infrastructure.Monitoring;
using Vessel.Persistence;
using Vessel.Worker;

namespace Vessel.Tests;

public sealed class AccountOrderHistoryApiTests
{
    private static CoreApiFactory Factory(Guid owner, CoreDatabase database, FixtureReader reader, AccountHistoryOrders orders,
        IAccountCredentialReader? credential = null, TimeProvider? clock = null) =>
        new(owner, connection: database.ConnectionString, configure: services =>
        {
            services.AddSingleton<IVenueRegistry>(AccountHistoryOrders.Registry(reader, orders));
            if (credential is not null) services.AddSingleton(credential);
            if (clock is not null) services.AddSingleton(clock);
        });

    private sealed class RefreshClock : TimeProvider
    {
        private DateTimeOffset now = AccountHistoryOrders.At.AddDays(1);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(FixtureReader reader, AccountHistoryOrders orders, bool observations = true)
        {
            now = now.AddMinutes(1);
            if (!observations) return;
            reader.Result = reader.Result with { Snapshot = reader.Result.Snapshot with { ObservedAtUtc = now } };
            orders.Result = orders.Result with { ObservedAtUtc = now };
        }
    }

    private static async Task<AccountDto> Import(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/accounts",
            new CreateAccountRequest(null, "Old unassigned history", "hyperliquid", "0x" + new string('a', 40)));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private static async Task<AccountDto> Sync(HttpClient client, Guid accountId)
    {
        using var response = await client.PostAsync($"/api/accounts/{accountId}/sync", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    /// <summary>An owner Check needs a live worker: record its heartbeat at the test clock.</summary>
    private static async Task WorkerOnline(CoreDatabase database, Guid owner, TimeProvider clock)
    {
        await using var db = database.Context(owner);
        await MonitorServerState.For(db).RecordHeartbeatAsync("monitor", clock.GetUtcNow(), default);
    }

    private static async Task CompleteCheck(CoreApiFactory factory, Guid accountId, HttpResponseMessage accepted,
        FixtureReader reader, AccountHistoryOrders orders, TimeProvider clock, IAccountCredentialReader? credential = null)
    {
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var request = (await accepted.Content.ReadFromJsonAsync<MonitorRunAccepted>())!;
        Assert.NotEqual(Guid.Empty, request.RequestId);
        Assert.Equal("pending", request.Monitor.ManualState);
        // The HTTP route only admits work. Exercise the production worker claim/executor in a fresh
        // scope with the injected synthetic venue; real broker dispatch has its own host E2E tests.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddConfiguration(factory.Services.GetRequiredService<IConfiguration>());
        WorkerHost.Configure(builder);
        VesselEventTestHosting.ConfigureServices(builder.Services);
        // No identity schema here: the configured owner is the only active one.
        builder.Services.AddSingleton<IJournalOwnerDirectory>(
            new ConfiguredOwnerDirectory(JournalOwner.Parse(builder.Configuration[JournalOwner.ConfigurationKey])));
        builder.Services.AddSingleton(clock);
        builder.Services.AddSingleton<IVenueRegistry>(AccountHistoryOrders.Registry(reader, orders));
        if (credential is not null) builder.Services.AddSingleton(credential);
        using var host = builder.Build();
        await using var worker = host.Services.GetRequiredService<IOwnerScopeFactory>().Create(
            JournalOwner.Parse(builder.Configuration[JournalOwner.ConfigurationKey]));
        var store = worker.Services.GetRequiredService<IMonitorStore>();
        var time = worker.Services.GetRequiredService<TimeProvider>();
        var lease = await store.ClaimAsync(accountId, request.RequestId, "synthetic-worker", time.GetUtcNow(), default);
        Assert.NotNull(lease);
        Assert.True(await worker.Services.GetRequiredService<MonitorRunExecutor>().RunAsync(lease, default));
        Assert.Null(await store.ClaimAsync(accountId, request.RequestId, "duplicate", time.GetUtcNow(), default));
    }

    private static async Task<(ActivityOperationSummaryDto Operation, RetrospectivePreviewDto Preview)> Preview(HttpClient client, Guid accountId)
    {
        var activity = (await client.GetFromJsonAsync<ActivityPageDto>("/api/activity"))!;
        var operation = Assert.Single(activity.Operations);
        var preview = (await client.GetFromJsonAsync<RetrospectivePreviewDto>(
            $"/api/activity/retrospective?accountId={accountId}&operationId={operation.Id}"))!;
        return (operation, preview);
    }

    private static async Task AssertReloadedCoverage(HttpClient client, AccountDto expected)
    {
        var account = (await client.GetFromJsonAsync<AccountDto>($"/api/accounts/{expected.Id}"))!;
        var accounts = (await client.GetFromJsonAsync<AccountDto[]>("/api/accounts"))!;
        var overview = (await client.GetFromJsonAsync<OverviewDto>("/api/overview"))!;
        foreach (var actual in new[] { account, accounts.Single(a => a.Id == expected.Id), overview.Accounts.Single(a => a.Id == expected.Id) })
        {
            Assert.Equal(expected.HistoryNotice, actual.HistoryNotice);
            Assert.Equal((expected.SyncStatus, expected.LastSyncedAtUtc, expected.LastSyncError, expected.SettingsRevision),
                (actual.SyncStatus, actual.LastSyncedAtUtc, actual.LastSyncError, actual.SettingsRevision));
        }
        var activity = (await client.GetFromJsonAsync<ActivityPageDto>("/api/activity"))!;
        Assert.Contains(activity.Coverage, coverage => coverage.AccountId == expected.Id && coverage.Notice == expected.HistoryNotice);
    }

    [PostgresFact]
    public async Task Explicit_sync_retains_old_orders_without_active_plays_and_unlocks_retrospective_preview()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = AccountHistoryOrders.PublicReader(); var orders = new AccountHistoryOrders();
        await using var factory = Factory(owner, database, reader, orders);
        using var client = factory.AuthorizedClient();
        var account = await Import(client);
        await client.GetAsync($"/api/accounts/{account.Id}");
        using var emptyPreview = await client.GetAsync($"/api/activity/retrospective?accountId={account.Id}&operationId=unknown");
        Assert.Equal(HttpStatusCode.NotFound, emptyPreview.StatusCode);
        Assert.Equal(0, reader.Reads); Assert.Equal(0, orders.Reads);
        using var sync = await client.PostAsync($"/api/accounts/{account.Id}/sync", null);
        sync.EnsureSuccessStatusCode();
        var refreshed = (await sync.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.Equal(("synced", "1000", AccountHistoryOrders.At.AddDays(1)),
            (refreshed.SyncStatus, refreshed.AccountValueUsd, refreshed.LastSyncedAtUtc));
        Assert.Contains(reader.Result.HistoryNotice, refreshed.HistoryNotice);
        Assert.Contains(orders.Result.Notice, refreshed.HistoryNotice);
        await AssertReloadedCoverage(client, refreshed);
        Assert.Equal(1, reader.Reads); Assert.Equal(1, orders.Reads);
        await using (var retained = database.Context(owner))
        {
            Assert.Equal(2, await retained.Orders.CountAsync());
            Assert.Equal(2, await retained.Fills.CountAsync());
            Assert.Empty(await retained.Plays.ToListAsync());
            Assert.Empty(await retained.OrderLinks.ToListAsync());
            Assert.All(await retained.Orders.ToListAsync(), order =>
                Assert.True(order.PlacedAtUtc < AccountHistoryOrders.At.AddDays(1)));
        }
        var (operation, preview) = await Preview(client, account.Id);
        Assert.Equal(("closed", "unassigned"), (operation.Status, operation.PlayAssociation));
        Assert.True(preview.Eligible, string.Join(" ", preview.Reasons));
        Assert.Empty(preview.Reasons);
        Assert.Equal(1, reader.Reads); Assert.Equal(1, orders.Reads);
        using var create = await client.PostAsJsonAsync("/api/plays/retrospective",
            new CreateRetrospectiveRequest(Guid.NewGuid(), account.Id, operation.Id, preview.Fingerprint, "Reviewed history"));
        create.EnsureSuccessStatusCode();
        var play = (await create.Content.ReadFromJsonAsync<PlayDto>())!;
        Assert.Equal(("retrospective", "closed"), (play.Summary.Origin, play.Summary.Status));
        Assert.Equal(AccountHistoryOrders.At.AddSeconds(60), play.Summary.EndedAtUtc);
        await using var verify = database.Context(owner);
        Assert.Equal(2, await verify.OrderLinks.CountAsync());
        Assert.Equal(2, await verify.Fills.CountAsync());
        Assert.Equal(10m, await verify.Fills.SumAsync(f => f.ClosedPnlUsd));
        Assert.All(await verify.Fills.ToListAsync(), fill =>
        {
            Assert.Equal(1m, fill.Quantity);
            var original = reader.Result.Fills.Single(f => f.SourceFillId == fill.SourceFillId);
            Assert.Equal((original.Price, original.ClosedPnlUsd, original.Side, original.PositionEffect, original.OccurredAtUtc),
                (fill.Price, fill.ClosedPnlUsd, fill.Side, fill.PositionEffect, fill.OccurredAtUtc));
        });
    }

    [PostgresFact]
    public async Task Sync_never_infers_missing_orders_and_known_incomplete_or_live_orders_still_block_preview()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = AccountHistoryOrders.PublicReader(); var orders = new AccountHistoryOrders();
        var complete = orders.Result;
        orders.Result = complete with { Orders = [] };
        var clock = new RefreshClock();
        await using var factory = Factory(owner, database, reader, orders, clock: clock);
        using var client = factory.AuthorizedClient(); var account = await Import(client);
        using var firstSync = await client.PostAsync($"/api/accounts/{account.Id}/sync", null);
        firstSync.EnsureSuccessStatusCode();
        var (_, missing) = await Preview(client, account.Id);
        Assert.False(missing.Eligible);
        Assert.Contains(missing.Reasons, reason => reason.Contains("no retained order record"));
        await using (var missingDb = database.Context(owner)) Assert.Empty(await missingDb.Orders.ToListAsync());

        orders.Result = complete with { Orders = [complete.Orders[0] with { OriginalSize = 2 }, complete.Orders[1]] };
        clock.Advance(reader, orders);
        using var incompleteSync = await client.PostAsync($"/api/accounts/{account.Id}/sync", null);
        incompleteSync.EnsureSuccessStatusCode();
        var (_, incomplete) = await Preview(client, account.Id);
        Assert.False(incomplete.Eligible);
        Assert.Contains(incomplete.Reasons, reason => reason.Contains("known to have missing executions"));

        orders.Result = complete with { Orders = [complete.Orders[0] with
            { Status = VenueOrderStatus.Open, VenueStatus = "open", OriginalSize = 2, RemainingSize = 1 }, complete.Orders[1]] };
        clock.Advance(reader, orders);
        using var liveSync = await client.PostAsync($"/api/accounts/{account.Id}/sync", null);
        liveSync.EnsureSuccessStatusCode();
        var (_, live) = await Preview(client, account.Id);
        Assert.False(live.Eligible);
        Assert.Contains(live.Reasons, reason => reason.Contains("can still execute"));
        await using var verify = database.Context(owner);
        Assert.Equal(2, await verify.Fills.CountAsync());
        Assert.Empty(await verify.OrderLinks.ToListAsync());
        Assert.Empty(await verify.Plays.ToListAsync());
    }

    [PostgresFact]
    public async Task Order_http_failure_is_partial_success_with_safe_notice_and_preserved_public_refresh()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = AccountHistoryOrders.PublicReader();
        var orders = new AccountHistoryOrders { Failure = new HttpRequestException("ro:synthetic-secret provider body") };
        var clock = new RefreshClock();
        await using var factory = Factory(owner, database, reader, orders, clock: clock);
        using var client = factory.AuthorizedClient(); var account = await Import(client);
        using var response = await client.PostAsync($"/api/accounts/{account.Id}/sync", null);
        response.EnsureSuccessStatusCode();
        var refreshed = (await response.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.Equal("synced", refreshed.SyncStatus);
        Assert.Equal("1000", refreshed.AccountValueUsd);
        Assert.Null(refreshed.LastSyncError);
        Assert.Contains("Public account data refreshed; order coverage is unavailable. Retry the account check later.", refreshed.HistoryNotice);
        Assert.DoesNotContain("ro:", await response.Content.ReadAsStringAsync());
        await AssertReloadedCoverage(client, refreshed);
        for (var refresh = 0; refresh < 2; refresh++)
        {
            clock.Advance(reader, orders);
            var repeated = await Sync(client, account.Id);
            Assert.Equal(refreshed.HistoryNotice, repeated.HistoryNotice);
            await AssertReloadedCoverage(client, repeated);
        }
        var (_, preview) = await Preview(client, account.Id);
        Assert.False(preview.Eligible);
        Assert.Contains(preview.Reasons, reason => reason.Contains("no retained order record"));
        await using var verify = database.Context(owner);
        Assert.Equal(2, await verify.Fills.CountAsync());
        Assert.Single(await verify.Snapshots.ToListAsync());
        Assert.Empty(await verify.Orders.ToListAsync());
    }

    [PostgresFact]
    public async Task Cancelled_order_read_preserves_completed_public_refresh_without_retaining_orders()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = AccountHistoryOrders.PublicReader(); var orders = new AccountHistoryOrders();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        orders.WaitForRead = async ct =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { stopped.SetResult(); }
        };
        await using var factory = Factory(owner, database, reader, orders);
        using var client = factory.AuthorizedClient(); var account = await Import(client);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sync = client.PostAsync($"/api/accounts/{account.Id}/sync", null, cancelled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var verify = database.Context(owner);
        Assert.Equal("synced", (await verify.Accounts.SingleAsync()).SyncStatus);
        Assert.Equal(2, await verify.Fills.CountAsync());
        Assert.Single(await verify.Snapshots.ToListAsync());
        Assert.Empty(await verify.Orders.ToListAsync());
        Assert.Empty(await verify.OrderLinks.ToListAsync());
    }

    [PostgresFact]
    public async Task Failed_public_refresh_does_not_attempt_optional_order_read()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = AccountHistoryOrders.PublicReader(); var orders = new AccountHistoryOrders();
        reader.Fail = true;
        await using var factory = Factory(owner, database, reader, orders);
        using var client = factory.AuthorizedClient(); var account = await Import(client);
        using var sync = await client.PostAsync($"/api/accounts/{account.Id}/sync", null);
        Assert.Equal(HttpStatusCode.BadGateway, sync.StatusCode);
        Assert.Equal(1, reader.Reads); Assert.Equal(0, orders.Reads);
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.Orders.ToListAsync());
        Assert.Empty(await verify.Fills.ToListAsync());
    }

    [PostgresFact]
    public async Task Retention_does_not_link_active_plays_and_check_reads_orders_once_without_matching_rejections()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = AccountHistoryOrders.PublicReader(); var orders = new AccountHistoryOrders();
        var clock = new RefreshClock();
        await using var factory = Factory(owner, database, reader, orders, clock: clock);
        using var client = factory.AuthorizedClient(); var account = await Import(client);
        using var created = await client.PostAsJsonAsync("/api/plays",
            new CreatePlayRequest(account.Id, "BTC", "venue", "New plan", TestPlays.Plan()));
        created.EnsureSuccessStatusCode();
        var play = (await created.Content.ReadFromJsonAsync<PlayDto>())!;
        using var planned = await client.PostAsJsonAsync($"/api/plays/{play.Summary.Id}/status",
            new ChangePlayStatusRequest(play.Summary.Version, "planned"));
        planned.EnsureSuccessStatusCode();
        orders.Result = orders.Result with { Orders = [..orders.Result.Orders, orders.Result.Orders[0] with
        {
            OrderId = "rejected", Status = VenueOrderStatus.Rejected, VenueStatus = "rejected",
            PlacedAtUtc = play.Summary.CreatedAtUtc.AddSeconds(1), StatusAtUtc = play.Summary.CreatedAtUtc.AddSeconds(1)
        }] };
        using var sync = await client.PostAsync($"/api/accounts/{account.Id}/sync", null);
        sync.EnsureSuccessStatusCode();
        await using (var retained = database.Context(owner))
        {
            Assert.Equal(3, await retained.Orders.CountAsync());
            Assert.Empty(await retained.OrderLinks.ToListAsync());
        }
        using var cooling = await client.PostAsync($"/api/plays/{play.Summary.Id}/execution/check", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, cooling.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), cooling.Headers.RetryAfter!.Delta);
        clock.Advance(reader, orders);
        await WorkerOnline(database, owner, clock);
        using var check = await client.PostAsync($"/api/plays/{play.Summary.Id}/execution/check", null);
        Assert.Equal(1, reader.Reads); Assert.Equal(1, orders.Reads);
        await CompleteCheck(factory, account.Id, check, reader, orders, clock);
        var execution = (await client.GetFromJsonAsync<PlayExecutionDto>($"/api/plays/{play.Summary.Id}/execution"))!;
        Assert.Equal("planned", execution.Status);
        Assert.Empty(execution.Links);
        Assert.Empty(execution.Suggestions);
        Assert.Equal(2, reader.Reads); Assert.Equal(2, orders.Reads);
        await using var verify = database.Context(owner);
        Assert.Equal(2, await verify.Fills.CountAsync());
        Assert.Empty(await verify.OrderLinks.ToListAsync());
    }

    [PostgresFact]
    public async Task Lighter_without_credential_keeps_public_refresh_successful_and_order_completeness_unknown()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = AccountHistoryOrders.PublicReader("lighter"); var orders = new AccountHistoryOrders("lighter");
        var account = new Account(Guid.NewGuid(), owner, "lighter", "Imported account");
        account.Configure(null, null, null, "42");
        await using (var db = database.Context(owner)) { db.Accounts.Add(account); await db.SaveChangesAsync(); }
        // Use the production credential reader and unconfigured vault, not a fabricated retained order/token.
        var clock = new RefreshClock();
        await using var factory = Factory(owner, database, reader, orders, clock: clock);
        using var client = factory.AuthorizedClient();
        using var response = await client.PostAsync($"/api/accounts/{account.Id}/sync", null);
        response.EnsureSuccessStatusCode();
        var refreshed = (await response.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.Equal("synced", refreshed.SyncStatus);
        Assert.Equal("1000", refreshed.AccountValueUsd);
        Assert.Null(refreshed.LastSyncError);
        Assert.Contains("Public account data refreshed; order coverage is unavailable. Update the read-only credential.", refreshed.HistoryNotice);
        await AssertReloadedCoverage(client, refreshed);
        Assert.Equal(1, reader.Reads); Assert.Equal(0, orders.Reads);
        for (var refresh = 0; refresh < 2; refresh++)
        {
            clock.Advance(reader, orders);
            var repeated = await Sync(client, account.Id);
            Assert.Equal(refreshed.HistoryNotice, repeated.HistoryNotice);
            await AssertReloadedCoverage(client, repeated);
        }
        Assert.Equal(0, orders.Reads);
        var (_, preview) = await Preview(client, account.Id);
        Assert.False(preview.Eligible);
        Assert.Contains(preview.Reasons, reason => reason.Contains("no retained order record"));
        await using var verify = database.Context(owner);
        Assert.Equal(2, await verify.Fills.CountAsync());
        Assert.Empty(await verify.Orders.ToListAsync());
    }

    [PostgresFact]
    public async Task Disabled_and_foreign_accounts_never_start_public_or_order_reads()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = AccountHistoryOrders.PublicReader(); var orders = new AccountHistoryOrders();
        await using var factory = Factory(owner, database, reader, orders);
        using var client = factory.AuthorizedClient(); var account = await Import(client);
        await using (var db = database.Context(owner))
        {
            (await db.Accounts.SingleAsync()).UpdateSettings(account.Name, null, false);
            await db.SaveChangesAsync();
        }
        using var disabled = await client.PostAsync($"/api/accounts/{account.Id}/sync", null);
        Assert.Equal(HttpStatusCode.BadRequest, disabled.StatusCode);
        await using var foreignFactory = Factory(Guid.NewGuid(), database, reader, orders);
        using var foreignClient = foreignFactory.AuthorizedClient();
        using var foreign = await foreignClient.PostAsync($"/api/accounts/{account.Id}/sync", null);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(0, reader.Reads); Assert.Equal(0, orders.Reads);
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.Snapshots.ToListAsync());
        Assert.Empty(await verify.Fills.ToListAsync());
        Assert.Empty(await verify.Orders.ToListAsync());
    }

    [PostgresFact]
    public async Task Repeated_success_and_recovery_replace_order_coverage_instead_of_accumulating_stale_notices()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = AccountHistoryOrders.PublicReader(); var orders = new AccountHistoryOrders();
        var clock = new RefreshClock();
        await using var factory = Factory(owner, database, reader, orders, clock: clock);
        using var client = factory.AuthorizedClient(); var account = await Import(client);
        var successful = await Sync(client, account.Id);
        for (var refresh = 0; refresh < 3; refresh++)
        {
            clock.Advance(reader, orders);
            var repeated = await Sync(client, account.Id);
            Assert.Equal(successful.HistoryNotice, repeated.HistoryNotice);
            Assert.Equal(account.SettingsRevision, repeated.SettingsRevision);
            await AssertReloadedCoverage(client, repeated);
        }
        orders.Failure = new HttpRequestException("synthetic outage");
        clock.Advance(reader, orders);
        var partial = await Sync(client, account.Id);
        Assert.Contains("order coverage is unavailable", partial.HistoryNotice);
        Assert.DoesNotContain(orders.Result.Notice, partial.HistoryNotice);
        orders.Failure = null;
        reader.Result = reader.Result with { HistoryNotice = "New public coverage." };
        clock.Advance(reader, orders);
        var recovered = await Sync(client, account.Id);
        Assert.Contains(reader.Result.HistoryNotice, recovered.HistoryNotice);
        Assert.Contains(orders.Result.Notice, recovered.HistoryNotice);
        Assert.DoesNotContain("order coverage is unavailable", recovered.HistoryNotice);
        Assert.DoesNotContain("Bounded recent public fills only.", recovered.HistoryNotice);
        Assert.Equal(account.SettingsRevision, recovered.SettingsRevision);
        Assert.Equal(reader.Result.Snapshot.ObservedAtUtc, recovered.LastSyncedAtUtc);
        await AssertReloadedCoverage(client, recovered);
    }

    [PostgresFact]
    public async Task Order_phase_holds_the_refresh_lease_so_newer_refreshes_wait_and_a_later_public_failure_keeps_its_coverage()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = AccountHistoryOrders.PublicReader(); var orders = new AccountHistoryOrders();
        var clock = new RefreshClock();
        await using var factory = Factory(owner, database, reader, orders, clock: clock);
        using var client = factory.AuthorizedClient(); var account = await Import(client);
        await Sync(client, account.Id);
        var venues = AccountHistoryOrders.Registry(reader, orders);
        // The public phase commits first; the lease, not a database lock, spans the optional order read.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        orders.WaitForRead = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        orders.Failure = new HttpRequestException("synthetic optional failure");
        clock.Advance(reader, orders);
        reader.Result = reader.Result with { HistoryNotice = "Newer public coverage." };
        var reads = orders.Reads;
        await using var older = database.Context(owner);
        var pending = MonitorTestPaths.RefreshAsync(older, owner, venues, account.Id, time: clock);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // No newer refresh or check can interleave with the held order phase.
            using var busy = await client.PostAsync($"/api/accounts/{account.Id}/sync", null);
            Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
            await using (var newer = database.Context(owner))
                Assert.True((await Assert.ThrowsAsync<MonitorAdmissionException>(() =>
                    MonitorTestPaths.RefreshAsync(newer, owner, venues, account.Id, time: clock))).RetryAfter > 0);
        }
        finally { release.TrySetResult(); }
        var retained = await pending;
        orders.WaitForRead = null;
        Assert.Equal(reads + 1, orders.Reads);
        Assert.Equal(("synced", (string?)null), (retained.SyncStatus, retained.LastSyncError));
        Assert.Contains("Newer public coverage.", retained.HistoryNotice);
        Assert.Contains("order coverage is unavailable", retained.HistoryNotice);
        Assert.DoesNotContain(orders.Result.Notice, retained.HistoryNotice);
        Assert.DoesNotContain("Bounded recent public fills only.", retained.HistoryNotice);
        await AssertReloadedCoverage(client, retained);
        // A later public failure records the error and keeps that coverage, without an order read.
        reader.Fail = true;
        clock.Advance(reader, orders);
        using (var failure = await client.PostAsync($"/api/accounts/{account.Id}/sync", null))
            Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode);
        Assert.Equal(reads + 1, orders.Reads);
        var failed = (await client.GetFromJsonAsync<AccountDto>($"/api/accounts/{account.Id}"))!;
        Assert.Equal("error", failed.SyncStatus);
        Assert.Equal((retained.HistoryNotice, retained.LastSyncedAtUtc, retained.SettingsRevision),
            (failed.HistoryNotice, failed.LastSyncedAtUtc, failed.SettingsRevision));
        await AssertReloadedCoverage(client, failed);
        // The next complete refresh replaces the warning and clears the failure.
        reader.Fail = false; orders.Failure = null;
        clock.Advance(reader, orders);
        var recovered = await Sync(client, account.Id);
        Assert.Equal(("synced", (string?)null, account.SettingsRevision), (recovered.SyncStatus, recovered.LastSyncError, recovered.SettingsRevision));
        Assert.Contains("Newer public coverage.", recovered.HistoryNotice);
        Assert.Contains(orders.Result.Notice, recovered.HistoryNotice);
        Assert.DoesNotContain("order coverage is unavailable", recovered.HistoryNotice);
        await AssertReloadedCoverage(client, recovered);
    }

    [PostgresFact]
    public async Task Successful_play_check_replaces_saved_error_or_missing_token_notice_using_its_single_order_read()
    {
        foreach (var venue in new[] { "hyperliquid", "lighter" })
        {
            await using var database = await CoreDatabase.CreateAsync();
            var owner = Guid.NewGuid(); var reader = AccountHistoryOrders.PublicReader(venue); var orders = new AccountHistoryOrders(venue);
            var credential = new AccountHistoryCredential(null);
            var clock = new RefreshClock();
            await using var factory = Factory(owner, database, reader, orders, credential, clock);
            using var client = factory.AuthorizedClient();
            Guid accountId;
            if (venue == "lighter")
            {
                var imported = new Account(Guid.NewGuid(), owner, venue, "Imported account");
                imported.Configure(null, null, null, "42");
                await using var db = database.Context(owner);
                db.Accounts.Add(imported); await db.SaveChangesAsync();
                accountId = imported.Id;
            }
            else
            {
                accountId = (await Import(client)).Id;
                orders.Failure = new HttpRequestException("synthetic optional failure");
            }
            var partial = await Sync(client, accountId);
            Assert.Contains(venue == "lighter" ? "Update the read-only credential." : "Retry the account check later.", partial.HistoryNotice);
            using var created = await client.PostAsJsonAsync("/api/plays",
                new CreatePlayRequest(accountId, "BTC", "venue", "New plan", TestPlays.Plan()));
            created.EnsureSuccessStatusCode();
            var play = (await created.Content.ReadFromJsonAsync<PlayDto>())!;
            using var planned = await client.PostAsJsonAsync($"/api/plays/{play.Summary.Id}/status",
                new ChangePlayStatusRequest(play.Summary.Version, "planned"));
            planned.EnsureSuccessStatusCode();
            orders.Failure = null; credential.Token = "ro:synthetic-only";
            clock.Advance(reader, orders);
            await WorkerOnline(database, owner, clock);
            using var check = await client.PostAsync($"/api/plays/{play.Summary.Id}/execution/check", null);
            Assert.Equal(1, reader.Reads);
            Assert.Equal(venue == "lighter" ? 0 : 1, orders.Reads);
            await CompleteCheck(factory, accountId, check, reader, orders, clock, credential);
            var recovered = (await client.GetFromJsonAsync<AccountDto>($"/api/accounts/{accountId}"))!;
            Assert.Contains(orders.Result.Notice, recovered.HistoryNotice);
            Assert.DoesNotContain("order coverage is unavailable", recovered.HistoryNotice);
            Assert.DoesNotContain("read-only credential", recovered.HistoryNotice);
            Assert.Equal((reader.Result.Snapshot.ObservedAtUtc, partial.SyncStatus, partial.SettingsRevision),
                (recovered.LastSyncedAtUtc, recovered.SyncStatus, recovered.SettingsRevision));
            Assert.Equal(2, reader.Reads);
            Assert.Equal(venue == "lighter" ? 1 : 2, orders.Reads);
            await AssertReloadedCoverage(client, recovered);
        }
    }

    [PostgresFact]
    public async Task Equal_snapshot_refresh_retains_new_facts_without_overwriting_ledger_but_newer_observation_still_fences_it()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = AccountHistoryOrders.PublicReader(); var orders = new AccountHistoryOrders();
        var clock = new RefreshClock();
        await using var factory = Factory(owner, database, reader, orders, clock: clock);
        using var client = factory.AuthorizedClient(); var account = await Import(client);
        var first = await Sync(client, account.Id);
        // A fresh request can return the same ledger timestamp alongside newly available immutable
        // fills/orders. Keep the original ledger, as WorkspaceStore.SaveRefreshAsync always has.
        clock.Advance(reader, orders, observations: false);
        reader.Result = reader.Result with
        {
            Snapshot = reader.Result.Snapshot with { AccountValueUsd = 2000 },
            Fills = [..reader.Result.Fills, reader.Result.Fills[0] with { SourceFillId = "late-fill", OrderId = "late-order" }]
        };
        orders.Result = orders.Result with
        {
            Orders = [..orders.Result.Orders, orders.Result.Orders[0] with { OrderId = "late-order" }]
        };
        var equal = await Sync(client, account.Id);
        Assert.Equal(first.AccountValueUsd, equal.AccountValueUsd);
        Assert.Equal(first.LastSyncedAtUtc, equal.LastSyncedAtUtc);
        Assert.Equal(2, reader.Reads); Assert.Equal(2, orders.Reads);
        await using (var facts = database.Context(owner))
        {
            Assert.Equal(3, await facts.Fills.CountAsync());
            Assert.Equal(3, await facts.Orders.CountAsync());
            Assert.Empty(await facts.OrderLinks.ToListAsync());
            var current = await facts.Accounts.SingleAsync();
            current.RecordSync(reader.Result.Snapshot.ObservedAtUtc.AddSeconds(1), "A newer retained observation.");
            await facts.SaveChangesAsync();
        }
        clock.Advance(reader, orders, observations: false);
        reader.Result = reader.Result with
        {
            Fills = [..reader.Result.Fills, reader.Result.Fills[0] with { SourceFillId = "must-not-import" }]
        };
        using var stale = await client.PostAsync($"/api/accounts/{account.Id}/sync", null);
        Assert.Equal(HttpStatusCode.BadGateway, stale.StatusCode);
        await using var verify = database.Context(owner);
        Assert.Equal(3, await verify.Fills.CountAsync());
        Assert.Equal(3, await verify.Orders.CountAsync());
        Assert.Equal(reader.Result.Snapshot.ObservedAtUtc.AddSeconds(1), (await verify.Accounts.SingleAsync()).LastSyncedAtUtc);
    }

    [PostgresFact]
    public async Task Record_sync_preserves_real_capped_order_notice_without_rolling_back_large_public_refresh()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = AccountHistoryOrders.PublicReader("lighter"); var orders = new AccountHistoryOrders("lighter");
        var account = new Account(Guid.NewGuid(), owner, "lighter", "Coverage");
        account.Configure(null, null, null, "42");
        await using (var db = database.Context(owner)) { db.Accounts.Add(account); await db.SaveChangesAsync(); }
        reader.Result = reader.Result with { HistoryNotice = LighterReader.HistoryNotice };
        orders.Result = orders.Result with { Notice = AccountHistoryNoticeBudget.LighterCappedOrders };
        var clock = new RefreshClock();
        await using var factory = Factory(owner, database, reader, orders, new AccountHistoryCredential("ro:synthetic-only"), clock);
        using var client = factory.AuthorizedClient();
        await Sync(client, account.Id);
        using var cooling = await client.PostAsync($"/api/accounts/{account.Id}/sync", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, cooling.StatusCode);
        Assert.Equal(1, reader.Reads); Assert.Equal(1, orders.Reads);
        clock.Advance(reader, orders);
        // Maxwell's reachable sequence: omitted rows, then a refused credential page and refused public retry.
        reader.Result = reader.Result with
        {
            HistoryNotice = AccountHistoryNoticeBudget.LighterPublic(LighterReader.HistoryRetryNotice, LighterReader.HistoryUnavailableNotice, 100, 100, 10),
            Snapshot = reader.Result.Snapshot with { AccountValueUsd = 2000 }
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        orders.WaitForRead = async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var refresh = client.PostAsync($"/api/accounts/{account.Id}/sync", null, timeout.Token);
        try
        {
            var reachedOrderRead = await Task.WhenAny(entered.Task, refresh).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(entered.Task, reachedOrderRead);
            var publicRefresh = (await client.GetFromJsonAsync<AccountDto>($"/api/accounts/{account.Id}"))!;
            Assert.Equal("2000", publicRefresh.AccountValueUsd);
            Assert.Equal(reader.Result.Snapshot.ObservedAtUtc, publicRefresh.LastSyncedAtUtc);
            Assert.Equal(reader.Result.HistoryNotice + "\nOrder history: " + orders.Result.Notice, publicRefresh.HistoryNotice);
        }
        finally { release.TrySetResult(); }
        using var response = await refresh;
        response.EnsureSuccessStatusCode();
        var retained = (await response.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.Equal("2000", retained.AccountValueUsd);
        Assert.Equal(account.SettingsRevision, retained.SettingsRevision);
        await AssertReloadedCoverage(client, retained);
        await using var verify = database.Context(owner);
        Assert.Equal(retained.HistoryNotice, (await verify.Accounts.SingleAsync()).HistoryNotice);
        Assert.Equal(2000, (await verify.Snapshots.SingleAsync()).AccountValueUsd);
    }

    [PostgresFact]
    public async Task Record_order_notice_replacement_preserves_complete_maximum_source_coverage_in_postgres()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var reader = AccountHistoryOrders.PublicReader("lighter"); var orders = new AccountHistoryOrders("lighter");
        var account = new Account(Guid.NewGuid(), owner, "lighter", "Coverage");
        account.Configure(null, null, null, "42");
        await using (var db = database.Context(owner)) { db.Accounts.Add(account); await db.SaveChangesAsync(); }
        orders.Result = orders.Result with { Notice = AccountHistoryNoticeBudget.LighterCappedOrders };
        var clock = new RefreshClock();
        await using var factory = Factory(owner, database, reader, orders, new AccountHistoryCredential("ro:synthetic-only"), clock);
        using var client = factory.AuthorizedClient();
        var cases = AccountHistoryNoticeBudget.CombinedCases().Where(c => c.Venue == "lighter").OrderByDescending(c => c.Public.Length).ToList();
        foreach (var coverage in cases)
        {
            clock.Advance(reader, orders);
            reader.Result = reader.Result with { HistoryNotice = coverage.Public };
            var retained = await Sync(client, account.Id);
            Assert.Equal(coverage.Public + "\nOrder history: " + coverage.Orders, retained.HistoryNotice);
            Assert.Equal(("synced", reader.Result.Snapshot.ObservedAtUtc, account.SettingsRevision),
                (retained.SyncStatus, retained.LastSyncedAtUtc, retained.SettingsRevision));
            Assert.Null(retained.LastSyncError);
            await AssertReloadedCoverage(client, retained);
        }
        // A later public failure keeps the complete maximum coverage; it reads no orders and changes no settings.
        reader.Fail = true;
        clock.Advance(reader, orders);
        var orderReads = orders.Reads;
        using var failed = await client.PostAsync($"/api/accounts/{account.Id}/sync", null);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal(orderReads, orders.Reads);
        await using var verify = database.Context(owner);
        var row = await verify.Accounts.SingleAsync();
        Assert.Equal("error", row.SyncStatus);
        Assert.NotNull(row.LastSyncError);
        Assert.Equal(cases[^1].Public + "\nOrder history: " + orders.Result.Notice, row.HistoryNotice);
        Assert.Equal(account.SettingsRevision, row.SettingsRevision);
    }
}
