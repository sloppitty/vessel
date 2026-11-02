using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Vessel.Application.Common;
using Vessel.Domain.Accounts;
using Vessel.Application.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class WorkspaceReviewPostgresTests
{
    private static WorkspaceService Service(VesselDbContext db, Guid owner, FixtureReader? reader = null) =>
        new(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TestVenues.With(reader ?? new FixtureReader()), TestUnitOfWork.For(db));
    /// <summary>The owner's account Refresh through the monitor path, as POST /api/accounts/{id}/sync runs it.</summary>
    private static Task<AccountDto> Refresh(VesselDbContext db, Guid owner, Guid id, FixtureReader? reader = null) =>
        MonitorTestPaths.RefreshAsync(db, owner, TestVenues.With(reader ?? new FixtureReader()), id);

    [PostgresFact]
    public async Task Account_inserted_disabled_is_not_stored_as_enabled_by_the_column_default()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "manual", "Disabled before insert");
        account.Configure(null, null, null);
        account.UpdateSettings(account.Name, null, false);
        await using (var db = database.Context(owner))
            await TestUnitOfWork.For(db).ExecuteAsync(async () =>
            {
                await new WorkspaceStore(db, TestUnitOfWork.For(db)).AddAccountAsync(account, default);
                return true;
            }, default);
        await using var read = database.Context(owner);
        Assert.False((await read.Accounts.SingleAsync()).IsEnabled);
    }

    [PostgresFact]
    public async Task Duplicate_normalized_source_is_rejected_including_disabled_record()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); Guid id;
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner);
            id = (await service.CreateAccountAsync(new(null, "First", "hyperliquid", "0x" + new string('a', 40)), default)).Id;
            await service.UpdateAccountAsync(id, new("First", null, false, 1), default);
        }
        await using var second = database.Context(owner);
        var error = await Assert.ThrowsAsync<ProblemException>(() => Service(second, owner).CreateAccountAsync(new(null, "Duplicate", "hyperliquid", "0x" + new string('A', 40)), default));
        Assert.Equal(409, error.StatusCode); Assert.Equal(id, (await second.Accounts.SingleAsync()).Id);
    }

    [PostgresFact]
    public async Task Perp_observations_10_then_9_then_11_preserve_freshness_and_discard_the_older_read()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var reader = new FixtureReader(); Guid id;
        await using (var db = database.Context(owner))
            id = (await Service(db, owner).CreateAccountAsync(new(null, "Venue", "hyperliquid", "0x" + new string('b', 40)), default)).Id;
        var baseline = reader.Result;
        foreach (var hour in new[] { 10, 9, 11 })
        {
            reader.Result = baseline with
            {
                Snapshot = baseline.Snapshot with
                {
                    ObservedAtUtc = DateTimeOffset.Parse($"2026-10-01T{hour:00}:00:00Z"),
                    AccountValueUsd = hour,
                    WithdrawableUsd = hour,
                    MarginUsedUsd = hour,
                    ValueScope = $"scope{hour}",
                    Positions = [baseline.Snapshot.Positions.Single() with { SignedQuantity = hour }]
                },
                Fills = [baseline.Fills.Single() with { SourceFillId = hour.ToString() }]
            };
            await using var db = database.Context(owner); var service = Service(db, owner, reader);
            // A refresh older than the retained observation is discarded whole, its fills included.
            AccountDto result;
            if (hour == 9)
            {
                Assert.Equal(502, (await Assert.ThrowsAsync<ProblemException>(() => Refresh(db, owner, id, reader))).StatusCode);
                result = await service.AccountAsync(id, default);
            }
            else result = await Refresh(db, owner, id, reader);
            var snapshot = (await service.SnapshotAsync(id, default))!;
            var retainedHour = hour == 9 ? 10 : hour;
            Assert.Equal(retainedHour.ToString(), snapshot.AccountValueUsd); Assert.Equal(retainedHour.ToString(), snapshot.Positions.Single().SignedQuantity);
            Assert.Equal(retainedHour.ToString(), snapshot.WithdrawableUsd); Assert.Equal(retainedHour.ToString(), snapshot.MarginUsedUsd); Assert.Equal($"scope{retainedHour}", snapshot.ValueScope);
            Assert.Equal(DateTimeOffset.Parse($"2026-10-01T{retainedHour:00}:00:00Z"), snapshot.ObservedAtUtc);
            Assert.Equal(DateTimeOffset.Parse($"2026-10-01T{retainedHour:00}:00:00Z"), result.LastSyncedAtUtc);
        }
        await using var verify = database.Context(owner);
        Assert.Equal(["10", "11"], (await verify.Fills.Select(f => f.SourceFillId).ToListAsync()).Order());
    }
    [PostgresFact]
    public async Task Source_uniqueness_handles_concurrent_requests_separate_owners_and_manual_nulls()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var address = "0x" + new string('c', 40);
        var statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using var db = database.Context(owner);
            try { await Service(db, owner).CreateAccountAsync(new(null, $"Concurrent {i}", "hyperliquid", address), default); return 201; }
            catch (ProblemException error) { return error.StatusCode; }
        }));
        Assert.Equal(1, statuses.Count(s => s == 201)); Assert.Equal(7, statuses.Count(s => s == 409));
        await using (var db = database.Context(owner))
        {
            Assert.Single(await db.Accounts.ToListAsync()); var service = Service(db, owner);
            for (var i = 0; i < 3; i++) await service.CreateAccountAsync(new(null, "Manual", "manual"), default);
            Assert.Equal(4, await db.Accounts.CountAsync());
        }
        await using var foreign = database.Context(Guid.NewGuid());
        var foreignDto = await Service(foreign, foreign.CurrentOwnerId).CreateAccountAsync(new(null, "Another owner", "hyperliquid", address), default);
        Assert.Equal(address, foreignDto.Address); Assert.Single(await foreign.Accounts.ToListAsync());
    }

    [PostgresFact]
    public async Task Database_unique_guard_maps_racing_inserts_to_safe_conflict_without_precheck()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var address = "0x" + new string('d', 40);
        var statuses = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var db = database.Context(owner); var store = new WorkspaceStore(db, TestUnitOfWork.For(db));
            var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Source"); account.Configure(null, address, null);
            try { return await TestUnitOfWork.For(db).ExecuteAsync(async () => { await store.AddAccountAsync(account, default); return 201; }, default); }
            catch (ProblemException error)
            {
                Assert.DoesNotContain(address, error.Message); Assert.DoesNotContain("23505", error.Message); return error.StatusCode;
            }
        }));
        Assert.Equal(1, statuses.Count(s => s == 201)); Assert.Equal(3, statuses.Count(s => s == 409));
        await using var verify = database.Context(owner); Assert.Single(await verify.Accounts.ToListAsync());
    }

    [PostgresFact]
    public async Task Stale_disable_rename_and_move_are_rejected_across_contexts_and_sync_does_not_change_revision()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); Guid id; Guid first; Guid second; AccountDto original;
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner); first = (await service.CreatePortfolioAsync(new("First"), default)).Id;
            second = (await service.CreatePortfolioAsync(new("Second"), default)).Id;
            original = await service.CreateAccountAsync(new(first, "Original", "hyperliquid", "0x" + new string('e', 40)), default); id = original.Id;
            await Refresh(db, owner, id); Assert.Equal(1, (await service.AccountAsync(id, default)).SettingsRevision);
        }
        await using var staleContext = database.Context(owner); var staleService = Service(staleContext, owner);
        Assert.True((await staleService.AccountAsync(id, default)).IsEnabled); // Track stale state deliberately.
        await using (var db = database.Context(owner))
            Assert.Equal(2, (await Service(db, owner).UpdateAccountAsync(id, new("Original", first, false, original.SettingsRevision), default)).SettingsRevision);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => staleService.UpdateAccountAsync(id, new("Rename", first, true, original.SettingsRevision), default))).StatusCode);
        Assert.False((await staleContext.Accounts.SingleAsync()).IsEnabled);
        await using (var db = database.Context(owner))
            Assert.Equal(3, (await Service(db, owner).UpdateAccountAsync(id, new("Original", second, false, 2), default)).SettingsRevision);
        await using (var db = database.Context(owner))
            Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => Service(db, owner).UpdateAccountAsync(id, new("Rename", first, false, 2), default))).StatusCode);
        await using var verify = database.Context(owner); var current = await Service(verify, owner).AccountAsync(id, default);
        Assert.Equal(second, current.PortfolioId); Assert.False(current.IsEnabled); Assert.Equal("Original", current.Name); Assert.Equal(3, current.SettingsRevision);
        Assert.NotNull(current.LastSyncedAtUtc); Assert.Single(await verify.Fills.ToListAsync());
    }

    [PostgresFact]
    public async Task Portfolio_deletion_invalidates_forms_for_disabled_accounts_but_not_other_owners()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var foreignOwner = Guid.NewGuid(); Guid portfolio; Guid id; Guid foreignId;
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner); portfolio = (await service.CreatePortfolioAsync(new("Delete"), default)).Id;
            id = (await service.CreateAccountAsync(new(portfolio, "Manual", "manual"), default)).Id;
            await service.UpdateAccountAsync(id, new("Manual", portfolio, false, 1), default);
        }
        await using (var db = database.Context(foreignOwner))
            foreignId = (await Service(db, foreignOwner).CreateAccountAsync(new(null, "Foreign", "manual"), default)).Id;
        await using (var db = database.Context(owner)) await Service(db, owner).DeletePortfolioAsync(portfolio, default);
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner); var current = await service.AccountAsync(id, default);
            Assert.Null(current.PortfolioId); Assert.False(current.IsEnabled); Assert.Equal(3, current.SettingsRevision);
            Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => service.UpdateAccountAsync(id, new("Rename", null, true, 2), default))).StatusCode);
            Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.UpdateAccountAsync(foreignId, new("Foreign", null, false, 1), default))).StatusCode);
        }
        await using var foreign = database.Context(foreignOwner);
        Assert.Equal(1, (await foreign.Accounts.SingleAsync()).SettingsRevision); Assert.True((await foreign.Accounts.SingleAsync()).IsEnabled);
    }

    [PostgresFact]
    public async Task Concurrent_forms_with_same_revision_have_only_one_accepted_save()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); Guid id;
        await using (var db = database.Context(owner)) id = (await Service(db, owner).CreateAccountAsync(new(null, "Original", "manual"), default)).Id;
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async i =>
        {
            await using var db = database.Context(owner);
            try { await Service(db, owner).UpdateAccountAsync(id, new($"Rename {i}", null, i % 2 == 0, 1), default); return 200; }
            catch (ProblemException error) { return error.StatusCode; }
        }));
        Assert.Equal(1, results.Count(r => r == 200)); Assert.Equal(4, results.Count(r => r == 409));
        await using var verify = database.Context(owner); Assert.Equal(2, (await verify.Accounts.SingleAsync()).SettingsRevision);
    }

    [PostgresFact]
    public async Task Equal_perp_observation_is_noop_for_every_field_but_new_fills_are_still_imported()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var reader = new FixtureReader(); Guid id;
        await using (var db = database.Context(owner)) id = (await Service(db, owner).CreateAccountAsync(new(null, "Venue", "hyperliquid", "0x" + new string('f', 40)), default)).Id;
        var original = reader.Result.Snapshot;
        await using (var db = database.Context(owner)) await Refresh(db, owner, id, reader);
        reader.Result = reader.Result with
        {
            Snapshot = original with { AccountValueUsd = 999, WithdrawableUsd = 998, MarginUsedUsd = 997, ValueScope = "changed", Positions = [] },
            Fills = [reader.Result.Fills.Single(), reader.Result.Fills.Single() with { SourceFillId = "new-on-equal" }]
        };
        await using var verify = database.Context(owner); await Refresh(verify, owner, id, reader);
        var snapshot = await verify.Snapshots.Include(s => s.Positions).SingleAsync();
        Assert.Equal(original.AccountValueUsd, snapshot.AccountValueUsd); Assert.Equal(original.WithdrawableUsd, snapshot.WithdrawableUsd);
        Assert.Equal(original.MarginUsedUsd, snapshot.MarginUsedUsd); Assert.Equal(original.ValueScope, snapshot.ValueScope); Assert.Single(snapshot.Positions);
        Assert.Equal(original.ObservedAtUtc, (await verify.Accounts.SingleAsync()).LastSyncedAtUtc); Assert.Equal(2, await verify.Fills.CountAsync());
    }

    [PostgresFact]
    public async Task Wallet_and_perp_ledgers_advance_independently_without_regression_or_implicit_clearing()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var reader = new FixtureReader(); Guid id;
        await using (var db = database.Context(owner)) id = (await Service(db, owner).CreateAccountAsync(new(null, "Venue", "hyperliquid", "0x" + new string('1', 40)), default)).Id;
        static DateTimeOffset At(int hour) => DateTimeOffset.Parse($"2026-10-01T{hour:00}:00:00Z");
        var baseline = reader.Result;
        async Task Advance(int perpsHour, int? walletHour, int expectedPerps, int expectedWallet, bool superseded = false)
        {
            reader.Result = baseline with
            {
                Snapshot = baseline.Snapshot with
                {
                    ObservedAtUtc = At(perpsHour),
                    AccountValueUsd = perpsHour,
                    Positions = [baseline.Snapshot.Positions.Single() with { SignedQuantity = perpsHour }],
                    StablecoinWallet = walletHour is { } w ? new(At(w), $"mode{w}", "hypercore-spot-stablecoins",
                    [new("USDC", 0, "usdc-token", w, 0, w)]) : null
                }
            };
            await using var db = database.Context(owner); var service = Service(db, owner, reader);
            if (superseded) Assert.Equal(502, (await Assert.ThrowsAsync<ProblemException>(() => Refresh(db, owner, id, reader))).StatusCode);
            var dto = superseded ? await service.AccountAsync(id, default) : await Refresh(db, owner, id, reader);
            var snapshot = (await service.SnapshotAsync(id, default))!;
            Assert.Equal(expectedPerps.ToString(), snapshot.AccountValueUsd); Assert.Equal(expectedPerps.ToString(), snapshot.Positions.Single().SignedQuantity);
            Assert.Equal(At(expectedPerps), dto.LastSyncedAtUtc); Assert.Equal(At(expectedWallet), snapshot.StablecoinWallet!.ObservedAtUtc);
            Assert.Equal(expectedWallet.ToString(), dto.AvailableStablecoinNominalUsd); Assert.Equal($"mode{expectedWallet}", dto.AccountMode);
        }
        await Advance(10, 10, 10, 10);
        await Advance(10, 11, 10, 11); // New wallet despite unchanged perps.
        await Advance(11, 9, 11, 11); // New perps despite stale wallet.
        await Advance(11, 11, 11, 11); // Equal wallet and perps do not change retained fields.
        await Advance(12, null, 12, 11); // Missing wallet never clears known wallet.
        await Advance(9, 13, 12, 11, superseded: true); // Stale perps discard the whole read, its newer wallet included.
        reader.Result = baseline with
        {
            Snapshot = baseline.Snapshot with
            {
                ObservedAtUtc = At(12),
                AccountValueUsd = 999,
                Positions = [],
                StablecoinWallet = new(At(11), "wrong-mode", "wrong-scope", [])
            }
        };
        await using var verify = database.Context(owner); var finalService = Service(verify, owner, reader);
        var final = await Refresh(verify, owner, id, reader);
        Assert.Equal("12", final.AccountValueUsd); Assert.Equal("11", final.AvailableStablecoinNominalUsd);
        Assert.Equal("mode11", final.AccountMode); Assert.Equal("hypercore-spot-stablecoins", final.StablecoinScope);
        Assert.Single((await finalService.SnapshotAsync(id, default))!.StablecoinWallet!.Balances);
    }

    [PostgresFact]
    public async Task Targeted_snapshot_SQL_loads_only_requested_positions_wallet_and_enabled_owner_record()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); Guid id; Guid unrelated;
        var reader = new FixtureReader(); reader.Result = reader.Result with
        {
            Snapshot = reader.Result.Snapshot with
            { StablecoinWallet = new(DateTimeOffset.UtcNow, "unifiedAccount", "hypercore-spot-stablecoins", [new("USDC", 0, "usdc-token", 10, 1, 9)]) }
        };
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner, reader); id = (await service.CreateAccountAsync(new(null, "Target", "hyperliquid", "0x" + new string('2', 40)), default)).Id;
            unrelated = (await service.CreateAccountAsync(new(null, "Unrelated", "hyperliquid", "0x" + new string('3', 40)), default)).Id;
            await Refresh(db, owner, id, reader); await Refresh(db, owner, unrelated, reader);
        }
        var probe = new SnapshotProbe(); var options = new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(probe).Options;
        await using var target = new VesselDbContext(options, new CoreOwner(owner)); var targetService = Service(target, owner);
        Assert.Equal(id, (await targetService.AccountAsync(id, default)).Id);
        Assert.Single((await targetService.SnapshotAsync(id, default))!.Positions);
        Assert.Equal(2, (await targetService.UpdateAccountAsync(id, new("Rename", null, true, 1), default)).SettingsRevision);
        Assert.NotEmpty(probe.Queries);
        Assert.All(probe.Queries, query =>
        {
            Assert.Contains(id, query.Parameters); Assert.DoesNotContain(unrelated, query.Parameters);
            Assert.Contains("WHERE", query.Sql); Assert.Contains("IsEnabled", query.Sql);
        });
        await targetService.UpdateAccountAsync(id, new("Disabled", null, false, 2), default);
        Assert.Null(await new WorkspaceStore(target, TestUnitOfWork.For(target)).SnapshotAsync(id, default));
        await using var foreign = database.Context(Guid.NewGuid()); Assert.Null(await new WorkspaceStore(foreign, TestUnitOfWork.For(foreign)).SnapshotAsync(unrelated, default));
    }

    [PostgresFact]
    public async Task Two_API_clients_cannot_reenable_or_reparent_with_stale_forms()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); Guid id; Guid first; Guid second;
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner); first = (await service.CreatePortfolioAsync(new("First"), default)).Id;
            second = (await service.CreatePortfolioAsync(new("Second"), default)).Id;
            id = (await service.CreateAccountAsync(new(first, "Original", "manual"), default)).Id;
        }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var tabA = factory.AuthorizedClient(); using var tabB = factory.AuthorizedClient();
        var original = (await tabA.GetFromJsonAsync<AccountDto>($"/api/accounts/{id}"))!;
        Assert.Equal(1, original.SettingsRevision);
        var disable = await tabB.PutAsJsonAsync($"/api/accounts/{id}", new UpdateAccountRequest(original.Name, first, false, original.SettingsRevision));
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        var staleRename = await tabA.PutAsJsonAsync($"/api/accounts/{id}", new UpdateAccountRequest("Stale rename", first, true, original.SettingsRevision));
        Assert.Equal(HttpStatusCode.Conflict, staleRename.StatusCode);
        var fresh = (await tabA.GetFromJsonAsync<AccountDto>($"/api/accounts/{id}"))!; Assert.False(fresh.IsEnabled);
        var move = await tabB.PutAsJsonAsync($"/api/accounts/{id}", new UpdateAccountRequest(fresh.Name, second, false, fresh.SettingsRevision)); Assert.Equal(HttpStatusCode.OK, move.StatusCode);
        var staleMove = await tabA.PutAsJsonAsync($"/api/accounts/{id}", new UpdateAccountRequest("Stale rename", first, false, fresh.SettingsRevision)); Assert.Equal(HttpStatusCode.Conflict, staleMove.StatusCode);
        var current = (await tabA.GetFromJsonAsync<AccountDto>($"/api/accounts/{id}"))!;
        Assert.False(current.IsEnabled); Assert.Equal(second, current.PortfolioId); Assert.Equal("Original", current.Name); Assert.Equal(3, current.SettingsRevision);
        Assert.Null(current.LastSyncedAtUtc);
    }

    private sealed class SnapshotProbe : DbCommandInterceptor
    {
        public List<(string Sql, Guid[] Parameters)> Queries { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("account_snapshots", StringComparison.Ordinal))
                Queries.Add((command.CommandText, command.Parameters.Cast<DbParameter>().Where(p => p.Value is Guid).Select(p => (Guid)p.Value!).ToArray()));
            return ValueTask.FromResult(result);
        }
    }

}
