using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Monitoring;
using Vessel.Application.Ownership;
using Vessel.Application.Plays;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Monitoring;
using Vessel.Infrastructure.Monitoring;
using Vessel.Persistence;

namespace Vessel.Tests.Isolation;

/// <summary>
/// The monitor's background path with two Wallet-mode users, as <c>MonitorScheduler</c> drives it: the identity-backed
/// owner directory, the cross-owner due-candidate query, and discovery, claims and runs in scopes from the production
/// <see cref="IOwnerScopeFactory"/>. A scope of one owner can neither claim, renew, capture nor run the other's
/// account, while the other's own scope can.
/// </summary>
public sealed class BackgroundIsolationTests(WalletIsolationApi api) : IClassFixture<WalletIsolationApi>
{
    private const string Worker = "isolation-worker";

    /// <summary>An account with an active Play, so the monitor watches it.</summary>
    private static async Task<Guid> Monitored(Owner owner)
    {
        var account = (await owner.SyncedVenueAccount(Isolated.Marker("venue"))).GetProperty("id").GetGuid();
        var draft = await owner.Post("/api/plays", new CreatePlayRequest(account, "BTC", "venue", Isolated.Marker("monitored"), TestPlays.Plan()),
            HttpStatusCode.Created);
        await owner.Post($"/api/plays/{draft.GetProperty("summary").GetProperty("id").GetGuid()}/status",
            new ChangePlayStatusRequest(draft.GetProperty("summary").GetProperty("version").GetInt64(), "planned"));
        return account;
    }

    /// <summary>The stored monitor row, read in its owner's own context.</summary>
    private async Task<string> Row(Guid owner, Guid account)
    {
        await using var db = api.Database.Context(owner);
        return JsonSerializer.Serialize(await db.Set<MonitorAccount>().AsNoTracking().SingleAsync(row => row.AccountId == account));
    }

    [PostgresFact]
    public async Task Owner_scopes_claim_and_run_only_their_own_monitor_work()
    {
        var (a, b) = await api.Owners();
        var aAccount = await Monitored(a);
        var bAccount = await Monitored(b);
        var scopes = api.Factory.Services.GetRequiredService<IOwnerScopeFactory>();
        var directory = api.Factory.Services.GetRequiredService<IJournalOwnerDirectory>();
        // Both are active users of the identity-backed directory, so the scheduler would serve both.
        Assert.Equal((JournalOwnerStatus.Active, JournalOwnerStatus.Active),
            (await directory.StatusAsync(a.Id, default), await directory.StatusAsync(b.Id, default)));
        var active = await directory.ActiveOwnersAsync(null, 200, default);
        Assert.Contains(a.Id, active);
        Assert.Contains(b.Id, active);

        // Past every refresh cooldown and the check interval, then discovery in each owner's own scope, as a tick does.
        var now = api.Clock.Now;
        api.Clock.Now = now.AddMinutes(15);
        try
        {
            var venues = api.Factory.Services.GetRequiredService<IVenueRegistry>().Descriptors
                .Where(v => v.Capabilities is { Sync: true, Orders: true }).Select(v => v.Id).ToArray();
            foreach (var owner in new[] { a, b })
            {
                await using var scope = scopes.Create(owner.Id);
                Assert.Equal(owner.Id, scope.Services.GetRequiredService<IJournalOwnerContext>().OwnerId);
                await scope.Services.GetRequiredService<MonitorCoordinator>().DiscoverAsync(venues, api.Clock.Now, default);
            }
            var due = await api.Factory.Services.GetRequiredService<ISystemMonitorStore>().DueCandidatesAsync(null, api.Clock.Now, 100, default);
            Assert.Equal(a.Id, Assert.Single(due, c => c.AccountId == aAccount).OwnerId);
            Assert.Equal(b.Id, Assert.Single(due, c => c.AccountId == bAccount).OwnerId);
            var aRow = await Row(a.Id, aAccount);
            var bRow = await Row(b.Id, bAccount);
            var aState = await Snapshot.Of(a, $"/api/accounts/{aAccount}/monitor", "/api/monitor");
            var bState = await Snapshot.Of(b, $"/api/accounts/{bAccount}/monitor", "/api/monitor");

            // A's scope cannot see, claim or capture B's due account; B's row and views stay unchanged.
            await using (var scope = scopes.Create(a.Id))
            {
                var store = scope.Services.GetRequiredService<IMonitorStore>();
                Assert.Null(await store.FindAsync(bAccount, default));
                Assert.Null(await store.CaptureAsync(bAccount, default));
                Assert.Null(await store.ClaimAsync(bAccount, null, Worker, api.Clock.Now, default));
            }
            Assert.Equal(bRow, await Row(b.Id, bAccount));
            await bState.Unchanged();

            // B's own scope claims it at the same clock: the work was due, not hidden behind a cooldown.
            MonitorLease lease;
            await using (var scope = scopes.Create(b.Id))
                lease = Assert.IsType<MonitorLease>(await scope.Services.GetRequiredService<IMonitorStore>()
                    .ClaimAsync(bAccount, null, Worker, api.Clock.Now, default));
            Assert.Equal((b.Id, bAccount), (lease.OwnerId, lease.AccountId));
            bRow = await Row(b.Id, bAccount);

            // B's lease is refused in A's scope as issued. Relabelled with A's owner, it renews and runs exactly as a
            // lease of an unknown account would: never successfully.
            await using (var scope = scopes.Create(a.Id))
            {
                var store = scope.Services.GetRequiredService<IMonitorStore>();
                var coordinator = scope.Services.GetRequiredService<MonitorCoordinator>();
                await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync(lease, default));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    scope.Services.GetRequiredService<MonitorRunExecutor>().RunAsync(lease, default));
                var relabelled = lease with { OwnerId = a.Id };
                var unknown = relabelled with { AccountId = Guid.NewGuid() };
                foreach (var attempt in new Func<MonitorLease, Task<bool>>[]
                {
                    l => store.RenewAsync(l, api.Clock.Now, default), l => coordinator.RunAsync(l, default)
                })
                {
                    var outcome = await Outcome(() => attempt(relabelled));
                    Assert.Equal(await Outcome(() => attempt(unknown)), outcome);
                    Assert.NotEqual(bool.TrueString, outcome);
                }
            }
            Assert.Equal(bRow, await Row(b.Id, bAccount));
            Assert.Equal(aRow, await Row(a.Id, aAccount));
            await aState.Unchanged();

            // Positive controls: B's run settles in B's scope, releasing B's lease (a Planned Play has nothing to watch
            // yet, so the run is superseded), and A's own claim is still admitted.
            Assert.Equal(Worker, JsonDocument.Parse(bRow).RootElement.GetProperty(nameof(MonitorAccount.LeaseHolder)).GetString());
            await using (var scope = scopes.Create(b.Id))
                await scope.Services.GetRequiredService<MonitorRunExecutor>().RunAsync(lease, default);
            var settled = JsonDocument.Parse(await Row(b.Id, bAccount)).RootElement;
            Assert.Equal((JsonValueKind.Null, "idle"), (settled.GetProperty(nameof(MonitorAccount.LeaseHolder)).ValueKind,
                settled.GetProperty(nameof(MonitorAccount.ManualState)).GetString()));
            await aState.Unchanged();
            await using (var scope = scopes.Create(a.Id))
                Assert.Equal(a.Id, Assert.IsType<MonitorLease>(await scope.Services.GetRequiredService<IMonitorStore>()
                    .ClaimAsync(aAccount, null, Worker, api.Clock.Now, default)).OwnerId);
        }
        finally { api.Clock.Now = now; }
    }

    /// <summary>A synced account that keeps its execution history, so the slow funding schedule reads it (Phase 2, item 9c).</summary>
    private static async Task<Guid> KeptHistory(Owner owner)
    {
        var account = (await owner.SyncedVenueAccount(Isolated.Marker("venue"))).GetProperty("id").GetGuid();
        var current = await owner.GetJson($"/api/accounts/{account}");
        await owner.Send(HttpMethod.Put, $"/api/accounts/{account}", new UpdateAccountRequest(current.GetProperty("name").GetString()!, null, true,
            current.GetProperty("settingsRevision").GetInt64(), KeepExecutionHistory: true));
        return account;
    }

    /// <summary>The stored funding state and settlements, read in their owner's own context.</summary>
    private async Task<string> Funding(Guid owner, Guid account)
    {
        await using var db = api.Database.Context(owner);
        return JsonSerializer.Serialize(new
        {
            State = await db.FundingStates.AsNoTracking().SingleOrDefaultAsync(s => s.AccountId == account),
            Payments = await db.FundingPayments.AsNoTracking().Where(p => p.AccountId == account).OrderBy(p => p.SourceId).ToListAsync()
        });
    }

    /// <summary>
    /// Phase 2, item 9c: the slow funding schedule lists each due account under its own owner. Only that owner's scope can
    /// claim and run it, and the run writes that owner's funding only.
    /// </summary>
    [PostgresFact]
    public async Task Owner_scopes_claim_and_run_only_their_own_scheduled_funding_reads()
    {
        var (a, b) = await api.Owners();
        var aAccount = await KeptHistory(a);
        var bAccount = await KeptHistory(b);
        var scopes = api.Factory.Services.GetRequiredService<IOwnerScopeFactory>();
        var registry = api.Factory.Services.GetRequiredService<IVenueRegistry>();
        var venues = registry.Descriptors.Where(v => v.Capabilities.Sync && registry.Funding(v.Id) is not null).Select(v => v.Id).ToArray();
        var due = await api.Factory.Services.GetRequiredService<ISystemMonitorStore>().DueFundingAsync(venues, null, 1000, default);
        Assert.Equal(a.Id, Assert.Single(due, c => c.AccountId == aAccount).OwnerId);
        Assert.Equal(b.Id, Assert.Single(due, c => c.AccountId == bAccount).OwnerId);
        var aRow = await Row(a.Id, aAccount);
        var aFunding = await Funding(a.Id, aAccount);
        var bFunding = await Funding(b.Id, bAccount);

        // A's scope cannot claim B's account, and B's row stays as it was.
        var bRow = await Row(b.Id, bAccount);
        await using (var scope = scopes.Create(a.Id))
            Assert.Null(await scope.Services.GetRequiredService<IMonitorStore>().ClaimFundingAsync(bAccount, Worker, api.Clock.Now, default));
        Assert.Equal(bRow, await Row(b.Id, bAccount));

        // B's own scope claims it; A's scope refuses B's lease as issued.
        MonitorLease lease;
        await using (var scope = scopes.Create(b.Id))
            lease = Assert.IsType<MonitorLease>(await scope.Services.GetRequiredService<IMonitorStore>()
                .ClaimFundingAsync(bAccount, Worker, api.Clock.Now, default));
        Assert.Equal((b.Id, bAccount, "funding"), (lease.OwnerId, lease.AccountId, lease.Trigger));
        await using (var scope = scopes.Create(a.Id))
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Services.GetRequiredService<MonitorCoordinator>().RunAsync(lease, default));

        // B's run reads B's funding again and moves only B's next read.
        await using (var scope = scopes.Create(b.Id))
            Assert.True(await scope.Services.GetRequiredService<MonitorRunExecutor>().RunAsync(lease, default));
        var settled = JsonDocument.Parse(await Row(b.Id, bAccount)).RootElement;
        Assert.Equal(("idle", api.Clock.Now.AddHours(6)), (settled.GetProperty(nameof(MonitorAccount.ManualState)).GetString(),
            settled.GetProperty(nameof(MonitorAccount.NextFundingAtUtc)).GetDateTimeOffset()));
        Assert.NotEqual(bFunding, await Funding(b.Id, bAccount));
        Assert.Equal(aRow, await Row(a.Id, aAccount));
        Assert.Equal(aFunding, await Funding(a.Id, aAccount));

        // A's own claim is still admitted.
        await using (var scope = scopes.Create(a.Id))
            Assert.Equal(a.Id, Assert.IsType<MonitorLease>(await scope.Services.GetRequiredService<IMonitorStore>()
                .ClaimFundingAsync(aAccount, Worker, api.Clock.Now, default)).OwnerId);
    }

    /// <summary>A background call's result, or the refusal it threw, comparable between two attempts.</summary>
    private static async Task<string> Outcome(Func<Task<bool>> attempt)
    {
        try { return (await attempt()).ToString(); }
        catch (Vessel.Application.Common.ProblemException error) { return $"{error.StatusCode} {error.Code} {error.Message}"; }
    }
}
