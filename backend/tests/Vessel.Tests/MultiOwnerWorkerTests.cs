using Vessel.Infrastructure.Monitoring;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vessel.Application.Monitoring;
using Vessel.Application.Ownership;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Infrastructure.Auth;
using Vessel.Persistence;
using Vessel.Worker;

namespace Vessel.Tests;

public sealed class MultiOwnerWorkerTests
{
    [PostgresTheory]
    [InlineData("status")]
    [InlineData("discovery")]
    [InlineData("claim")]
    public async Task One_owners_failure_does_not_starve_later_owners_or_remove_their_heartbeat(string failure)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (failure == "claim")
        {
            await using var scope = fixture.Host.Services.GetRequiredService<IOwnerScopeFactory>().Create(fixture.First);
            await scope.Services.GetRequiredService<MonitorCoordinator>().DiscoverAsync(["hyperliquid"], fixture.Clock.GetUtcNow(), default);
        }
        fixture.Failures.Owner = fixture.First;
        fixture.Failures.Step = failure;
        if (failure == "status")
            fixture.Directory.Checking = (owner, _) => owner == fixture.First
                ? throw new InvalidOperationException("Synthetic private directory failure") : Task.CompletedTask;
        var scheduler = fixture.Host.Services.GetServices<IHostedService>().OfType<MonitorScheduler>().Single();
        await scheduler.TickAsync(default);
        await fixture.Venue.Started(fixture.Second).WaitAsync(TimeSpan.FromSeconds(20));
        await using (var later = fixture.Database.Context(fixture.Second))
        {
            var row = Assert.Single(await later.Set<MonitorAccount>().ToListAsync());
            Assert.Equal("running", row.ManualState);
            Assert.NotNull(await MonitorServerState.For(later).HeartbeatAsync(default));
        }
        Assert.True(File.Exists(fixture.Heartbeat));
        Assert.Contains(fixture.Logs.Messages, m => m.Level == LogLevel.Warning &&
            m.Text.Contains(fixture.First.ToString(), StringComparison.Ordinal) &&
            m.Text.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Logs.Messages.Where(m => m.Level >= LogLevel.Warning),
            m => m.Text.Contains("Synthetic private", StringComparison.Ordinal));
        if (failure != "status") Assert.True(fixture.Failures.Triggered);
        fixture.Venue.Release.TrySetResult();
        foreach (var owner in new[] { fixture.Second })
            await fixture.EventuallyAsync(async () =>
            {
                await using var db = fixture.Database.Context(owner);
                return (await db.Set<MonitorAccount>().SingleAsync()).ManualState == "idle";
            });
    }

    /// <summary>
    /// Phase 2, item 6 (Sol round 1): due accounts with only kept history behind them, sorted first, never take every worker
    /// slot before a due protection check is claimed.
    /// </summary>
    [PostgresFact]
    public async Task A_due_protection_check_is_claimed_before_kept_history_reads_that_sort_ahead_of_it()
    {
        await using var fixture = await Fixture.CreateAsync();
        var protection = fixture.Accounts[fixture.Second];
        var kept = new List<Guid> { fixture.Accounts[fixture.First] };
        await using (var first = fixture.Database.Context(fixture.First))
        {
            await first.Plays.ExecuteDeleteAsync();
            var account = await first.Accounts.SingleAsync();
            account.UpdateSettings(account.Name, null, true, keepExecutionHistory: true);
            await first.SaveChangesAsync();
        }
        await using (var second = fixture.Database.Context(fixture.Second))
        {
            // The smallest ID, so it sorts before the protection account at the same due time.
            var account = new Account(Guid.Parse("00000000-0000-4000-8000-000000000001"), fixture.Second, "hyperliquid", "Kept history");
            account.Configure(null, "0x" + new string('f', 40), null);
            account.UpdateSettings(account.Name, null, true, keepExecutionHistory: true);
            second.Accounts.Add(account);
            await second.SaveChangesAsync();
            kept.Add(account.Id);
        }
        var scheduler = fixture.Host.Services.GetServices<IHostedService>().OfType<MonitorScheduler>().Single();
        await scheduler.TickAsync(default);
        await fixture.EventuallyAsync(async () =>
        {
            await using var db = fixture.Database.Context(fixture.Second);
            return (await db.Set<MonitorAccount>().SingleAsync(m => m.AccountId == protection)).ManualState == "running";
        });
        // One of the two worker slots stays free of kept-history reads: at most one of them runs.
        var running = 0;
        foreach (var (owner, id) in new[] { (fixture.First, kept[0]), (fixture.Second, kept[1]) })
        {
            await using var db = fixture.Database.Context(owner);
            if (await db.Set<MonitorAccount>().AnyAsync(m => m.AccountId == id && m.ManualState == "running")) running++;
        }
        Assert.Equal(0, running);
        fixture.Venue.Release.TrySetResult();
    }

    [PostgresFact]
    public async Task Scheduler_shutdown_cancellation_propagates_instead_of_skipping_the_owner()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var stop = new CancellationTokenSource();
        fixture.Directory.Checking = (_, ct) =>
        {
            stop.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        var scheduler = fixture.Host.Services.GetServices<IHostedService>().OfType<MonitorScheduler>().Single();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.TickAsync(stop.Token));
        Assert.Empty(fixture.Venue.Reads);
        Assert.False(File.Exists(fixture.Heartbeat));
    }

    [PostgresFact]
    public async Task Multiple_due_accounts_per_owner_still_share_exactly_two_process_wide_reads()
    {
        await using var fixture = await Fixture.CreateAsync(firstAccounts: 2, secondAccounts: 3);
        var scheduler = fixture.Host.Services.GetServices<IHostedService>().OfType<MonitorScheduler>().Single();
        await scheduler.TickAsync(default);
        await fixture.EventuallyAsync(() => Task.FromResult(fixture.Venue.Reads.Count >= 2));
        await scheduler.TickAsync(default);
        Assert.Equal(2, fixture.Venue.Reads.Count);
        Assert.Equal(2, fixture.Venue.PeakReads);
        Assert.All(fixture.Venue.Reads, owner => Assert.Equal(fixture.First, owner));
        await using (var first = fixture.Database.Context(fixture.First))
            Assert.Equal(2, await first.Set<MonitorAccount>().CountAsync(m => m.ManualState == "running"));
        await using (var second = fixture.Database.Context(fixture.Second))
        {
            Assert.Equal(3, await second.Set<MonitorAccount>().CountAsync());
            Assert.False(await second.Set<MonitorAccount>().AnyAsync(m => m.ManualState == "running"));
        }
        fixture.Venue.Release.TrySetResult();
        await fixture.EventuallyAsync(async () =>
        {
            await using var db = fixture.Database.Context(fixture.First);
            return await db.Set<MonitorAccount>().AllAsync(m => m.ManualState == "idle");
        });
    }

    [PostgresFact]
    public async Task Scheduler_claims_each_active_owner_keeps_the_process_cap_and_renews_suspended_inflight_runs()
    {
        await using var fixture = await Fixture.CreateAsync();
        var scheduler = fixture.Host.Services.GetServices<IHostedService>().OfType<MonitorScheduler>().Single();
        await scheduler.TickAsync(default);
        await fixture.Venue.Started(fixture.First).WaitAsync(TimeSpan.FromSeconds(20));
        await fixture.Venue.Started(fixture.Second).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(2, fixture.Venue.Reads.Count);
        var leases = new Dictionary<Guid, DateTimeOffset>();
        foreach (var owner in new[] { fixture.First, fixture.Second })
        {
            await using var db = fixture.Database.Context(owner);
            var row = Assert.Single(await db.Set<MonitorAccount>().ToListAsync());
            Assert.Equal(owner, row.OwnerId);
            Assert.Equal("running", row.ManualState);
            leases[owner] = row.LeaseUntilUtc!.Value;
            Assert.NotNull(await MonitorServerState.For(db).HeartbeatAsync(default));
        }
        await using (var disabled = fixture.Database.Context(fixture.Suspended))
        {
            Assert.Empty(await disabled.Set<MonitorAccount>().ToListAsync());
        }
        // Another tick cannot start more than two in this process.
        await scheduler.TickAsync(default);
        Assert.Equal(2, fixture.Venue.Reads.Count);
        fixture.Directory.Set(fixture.First, JournalOwnerStatus.Suspended);
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        foreach (var owner in new[] { fixture.First, fixture.Second })
            await fixture.EventuallyAsync(async () =>
            {
                await using var db = fixture.Database.Context(owner);
                return (await db.Set<MonitorAccount>().SingleAsync()).LeaseUntilUtc > leases[owner];
            });
        fixture.Venue.Release.TrySetResult();
        await fixture.AssertSettledAsync();
        // Suspension prevented discovery/claims, not renewal or settlement of the already-started run.
        await scheduler.TickAsync(default);
        Assert.Equal(2, fixture.Venue.Reads.Count);
    }

    [PostgresFact]
    public async Task Api_refresh_renews_after_a_minute_in_each_request_owners_fresh_scope()
    {
        await using var fixture = await Fixture.CreateAsync();
        var runs = new[] { fixture.First, fixture.Second }.Select(owner => Task.Run(async () =>
        {
            var accessor = fixture.Host.Services.GetRequiredService<IHttpContextAccessor>();
            accessor.HttpContext = new DefaultHttpContext
            {
                User = TestAuthentication.Principal(owner, "Synthetic owner")
            };
            try
            {
                await using var request = fixture.Host.Services.CreateAsyncScope();
                return await request.ServiceProvider.GetRequiredService<MonitorRunExecutor>().RefreshAsync(fixture.Accounts[owner], default);
            }
            finally { accessor.HttpContext = null; }
        })).ToArray();
        foreach (var owner in new[] { fixture.First, fixture.Second })
            await fixture.Venue.Started(owner).WaitAsync(TimeSpan.FromSeconds(20));
        var previous = fixture.Clock.GetUtcNow() + MonitorOptions.Lease;
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        foreach (var owner in new[] { fixture.First, fixture.Second })
            await fixture.EventuallyAsync(async () =>
            {
                await using var db = fixture.Database.Context(owner);
                return (await db.Set<MonitorAccount>().SingleAsync()).LeaseUntilUtc > previous;
            });
        fixture.Venue.Release.TrySetResult();
        Assert.All(await Task.WhenAll(runs), Assert.True);
        await fixture.AssertSettledAsync(refresh: true);
    }

    [PostgresTheory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Configured_worker_concurrency_limits_process_reads(int concurrency)
    {
        await using var fixture = await Fixture.CreateAsync(firstAccounts: 2, secondAccounts: 2, concurrency: concurrency);
        var scheduler = fixture.Host.Services.GetServices<IHostedService>().OfType<MonitorScheduler>().Single();
        await scheduler.TickAsync(default);
        await fixture.EventuallyAsync(() => Task.FromResult(fixture.Venue.Reads.Count == concurrency));
        await scheduler.TickAsync(default);
        Assert.Equal(concurrency, fixture.Venue.PeakReads);
        fixture.Venue.Release.TrySetResult();
        await fixture.EventuallyAsync(async () =>
        {
            foreach (var owner in new[] { fixture.First, fixture.Second })
            {
                await using var db = fixture.Database.Context(owner);
                if (await db.Set<MonitorAccount>().AnyAsync(m => m.ManualState == "running")) return false;
            }
            return true;
        });
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_empty_or_all_suspended_directory_still_records_a_productive_deployment_heartbeat(bool empty)
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var owner in new[] { fixture.First, fixture.Second, fixture.Suspended })
            fixture.Directory.Set(owner, empty ? JournalOwnerStatus.Unknown : JournalOwnerStatus.Suspended);
        var scheduler = fixture.Host.Services.GetServices<IHostedService>().OfType<MonitorScheduler>().Single();
        await scheduler.TickAsync(default);
        Assert.Empty(fixture.Venue.Reads);
        Assert.True(File.Exists(fixture.Heartbeat));
        Assert.Equal("monitor", (await fixture.Host.Services.GetRequiredService<IServerStateStore>().HeartbeatAsync(default))!.Deployment);
    }

    [PostgresFact]
    public async Task Suspended_candidate_pages_and_capped_owners_do_not_starve_a_later_owner()
    {
        await using var fixture = await Fixture.CreateAsync(firstAccounts: 3, concurrency: 1);
        var now = fixture.Clock.GetUtcNow();
        await using (var db = fixture.Database.Context(fixture.First))
        {
            var accounts = await db.Accounts.OrderBy(a => a.Id).ToListAsync();
            foreach (var (account, index) in accounts.Select((a, i) => (a, i)))
            {
                var row = new MonitorAccount { OwnerId = fixture.First, AccountId = account.Id };
                row.Admit(Guid.NewGuid(), "manual", now.AddMinutes(-2));
                if (index < 2) row.Claim(row.RequestId!.Value, "other-process", now, MonitorOptions.Lease);
                db.Add(row);
            }
            await db.SaveChangesAsync();
        }
        for (var index = 0; index < 105; index++)
        {
            var owner = Guid.NewGuid(); fixture.Directory.Set(owner, JournalOwnerStatus.Suspended);
            await using var db = fixture.Database.Context(owner);
            var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Suspended synthetic");
            account.Configure(null, "0x" + new string('a', 40), null);
            var row = new MonitorAccount { OwnerId = owner, AccountId = account.Id };
            row.Admit(Guid.NewGuid(), "manual", now.AddMinutes(-1));
            db.AddRange(account, row); await db.SaveChangesAsync();
        }
        var scheduler = fixture.Host.Services.GetServices<IHostedService>().OfType<MonitorScheduler>().Single();
        await scheduler.TickAsync(default);
        await fixture.Venue.Started(fixture.Second).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(fixture.Second, Assert.Single(fixture.Venue.Reads));
        fixture.Venue.Release.TrySetResult();
        await fixture.EventuallyAsync(async () =>
        {
            await using var db = fixture.Database.Context(fixture.Second);
            return await db.Set<MonitorAccount>().AllAsync(m => m.ManualState == "idle");
        });
    }

    [PostgresFact]
    public async Task Candidate_continuation_survives_ticks_so_earlier_due_work_cannot_jump_the_queue()
    {
        await using var fixture = await Fixture.CreateAsync(concurrency: 1);
        var scheduler = fixture.Host.Services.GetServices<IHostedService>().OfType<MonitorScheduler>().Single();
        await scheduler.TickAsync(default);
        await fixture.Venue.Started(fixture.First).WaitAsync(TimeSpan.FromSeconds(20));
        fixture.Venue.Release.TrySetResult();
        await fixture.EventuallyAsync(async () =>
        {
            await using var db = fixture.Database.Context(fixture.First);
            return await db.Set<MonitorAccount>().AllAsync(m => m.ManualState == "idle");
        });
        await using (var db = fixture.Database.Context(fixture.First))
        {
            (await db.Set<MonitorAccount>().SingleAsync()).NextRunAtUtc = fixture.Clock.GetUtcNow().AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        await scheduler.TickAsync(default);
        await fixture.Venue.Started(fixture.Second).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, fixture.Venue.Reads.Count(o => o == fixture.First));
        await fixture.EventuallyAsync(async () =>
        {
            await using var db = fixture.Database.Context(fixture.Second);
            return await db.Set<MonitorAccount>().AllAsync(m => m.ManualState == "idle");
        });
    }

    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Manual_check_is_accepted_as_pending_when_the_owner_or_venue_is_at_capacity(bool ownerCapacity)
    {
        await using var fixture = await Fixture.CreateAsync(firstAccounts: 3, venueConcurrency: ownerCapacity ? 4 : 1);
        var target = fixture.Accounts[fixture.First];
        var busyOwner = ownerCapacity ? fixture.First : fixture.Second;
        await using (var db = fixture.Database.Context(busyOwner))
        {
            var busyAccounts = await db.Accounts.Where(a => a.Id != target).ToListAsync();
            foreach (var account in busyAccounts)
            {
                var row = new MonitorAccount { OwnerId = busyOwner, AccountId = account.Id };
                var request = Guid.NewGuid();
                row.Admit(request, "manual", fixture.Clock.GetUtcNow());
                Assert.True(row.Claim(request, "another-process", fixture.Clock.GetUtcNow(), MonitorOptions.Lease));
                db.Add(row);
            }
            await db.SaveChangesAsync();
            Assert.Equal(ownerCapacity ? 2 : 1, busyAccounts.Count);
        }
        // Queue saturation does not replace main's separate requirement that a check worker is online.
        await fixture.Host.Services.GetRequiredService<IServerStateStore>()
            .RecordHeartbeatAsync("monitor", fixture.Clock.GetUtcNow(), default);
        await using var scope = fixture.Host.Services.GetRequiredService<IOwnerScopeFactory>().Create(fixture.First);
        var coordinator = scope.Services.GetRequiredService<MonitorCoordinator>();
        var accepted = await coordinator.RequestAsync(target, default);
        Assert.Equal("pending", accepted.Monitor.ManualState);
        await using var check = fixture.Database.Context(fixture.First);
        var pending = await check.Set<MonitorAccount>().SingleAsync(m => m.AccountId == target);
        Assert.Equal(accepted.RequestId, pending.RequestId);
        Assert.Equal("pending", pending.ManualState);
        // Queueing is allowed, not execution: claiming still enforces both capacities.
        Assert.Null(await scope.Services.GetRequiredService<IMonitorStore>().ClaimAsync(target, accepted.RequestId,
            "worker", fixture.Clock.GetUtcNow(), default));
        await Assert.ThrowsAsync<MonitorAdmissionException>(() => coordinator.RequestAsync(target, default));
    }

    [PostgresFact]
    public async Task A_held_account_row_is_skipped_so_another_owner_is_claimed_without_waiting()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var owner in new[] { fixture.First, fixture.Second })
        {
            await using var scope = fixture.Host.Services.GetRequiredService<IOwnerScopeFactory>().Create(owner);
            await scope.Services.GetRequiredService<MonitorCoordinator>().DiscoverAsync(["hyperliquid"], fixture.Clock.GetUtcNow(), default);
        }
        await using var held = fixture.Database.Context(fixture.First);
        await using var transaction = await held.Database.BeginTransactionAsync();
        var accountId = fixture.Accounts[fixture.First];
        await held.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1 FROM accounts WHERE "Id" = {accountId} FOR UPDATE
            """);
        var scheduler = fixture.Host.Services.GetServices<IHostedService>().OfType<MonitorScheduler>().Single();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await scheduler.TickAsync(timeout.Token);
        await fixture.Venue.Started(fixture.Second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(fixture.First, fixture.Venue.Reads);
        Assert.Contains(fixture.Second, fixture.Venue.Reads);
        // The held lock is still active when the other owner's work has been admitted.
        await transaction.RollbackAsync();
        fixture.Venue.Release.TrySetResult();
        await fixture.EventuallyAsync(async () =>
        {
            await using var db = fixture.Database.Context(fixture.Second);
            return (await db.Set<MonitorAccount>().SingleAsync()).ManualState == "idle";
        });
    }

    [PostgresFact]
    public async Task Venue_at_capacity_is_skipped_for_the_rest_of_the_tick_without_account_claim_locks()
    {
        await using var fixture = await Fixture.CreateAsync(firstAccounts: 3, secondAccounts: 3, venueConcurrency: 1);
        foreach (var owner in new[] { fixture.First, fixture.Second })
        {
            await using var scope = fixture.Host.Services.GetRequiredService<IOwnerScopeFactory>().Create(owner);
            await scope.Services.GetRequiredService<MonitorCoordinator>().DiscoverAsync(["hyperliquid"], fixture.Clock.GetUtcNow(), default);
        }
        await using (var db = fixture.Database.Context(fixture.Suspended))
        {
            var row = new MonitorAccount { OwnerId = fixture.Suspended, AccountId = fixture.Accounts[fixture.Suspended] };
            var request = Guid.NewGuid();
            row.Admit(request, "manual", fixture.Clock.GetUtcNow());
            Assert.True(row.Claim(request, "another-process", fixture.Clock.GetUtcNow(), MonitorOptions.Lease));
            db.Add(row); await db.SaveChangesAsync();
        }
        fixture.Failures.AccountLocks = 0;
        fixture.Failures.VenueCapacityReads = 0;
        var scheduler = fixture.Host.Services.GetServices<IHostedService>().OfType<MonitorScheduler>().Single();
        await scheduler.TickAsync(default);
        Assert.Empty(fixture.Venue.Reads);
        Assert.Equal(0, fixture.Failures.AccountLocks);
        Assert.Equal(1, fixture.Failures.VenueCapacityReads);
        Assert.True(File.Exists(fixture.Heartbeat));
    }

    [PostgresFact]
    public async Task An_active_owner_with_no_due_accounts_still_records_health()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = fixture.Clock.GetUtcNow();
        foreach (var owner in new[] { fixture.First, fixture.Second })
        {
            await using var db = fixture.Database.Context(owner);
            db.Add(new MonitorAccount { OwnerId = owner, AccountId = fixture.Accounts[owner], NextRunAtUtc = now.AddHours(1) });
            await db.SaveChangesAsync();
        }
        await fixture.Host.Services.GetServices<IHostedService>().OfType<MonitorScheduler>().Single().TickAsync(default);
        Assert.Empty(fixture.Venue.Reads);
        Assert.True(File.Exists(fixture.Heartbeat));
        Assert.Equal(now, (await fixture.Host.Services.GetRequiredService<IServerStateStore>().HeartbeatAsync(default))!.HeartbeatAtUtc);
    }

    [PostgresFact]
    public async Task A_once_skipped_account_is_revisited_when_the_bounded_scan_cycle_wraps()
    {
        await using var fixture = await Fixture.CreateAsync(secondAccounts: 4, concurrency: 1);
        fixture.Host.Services.GetRequiredService<MonitorOptions>().Interval = TimeSpan.FromMinutes(1);
        foreach (var owner in new[] { fixture.First, fixture.Second })
        {
            await using var scope = fixture.Host.Services.GetRequiredService<IOwnerScopeFactory>().Create(owner);
            await scope.Services.GetRequiredService<MonitorCoordinator>().DiscoverAsync(["hyperliquid"], fixture.Clock.GetUtcNow(), default);
        }
        fixture.Venue.Release.TrySetResult();
        var scheduler = fixture.Host.Services.GetServices<IHostedService>().OfType<MonitorScheduler>().Single();
        await using (var held = fixture.Database.Context(fixture.First))
        {
            await using var tx = await held.Database.BeginTransactionAsync();
            var account = fixture.Accounts[fixture.First];
            await held.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM accounts WHERE \"Id\" = {account} FOR UPDATE");
            await scheduler.TickAsync(default);
            await fixture.EventuallyAsync(async () =>
            {
                await using var db = fixture.Database.Context(fixture.Second);
                return await db.Set<MonitorAccount>().AllAsync(m => m.ManualState == "idle");
            });
            await tx.RollbackAsync();
        }
        for (var tick = 0; tick < 12; tick++)
        {
            await Task.Delay(50);
            fixture.Clock.Advance(TimeSpan.FromSeconds(30));
            await scheduler.TickAsync(default);
            await fixture.EventuallyAsync(async () =>
            {
                await using var db = fixture.Database.Context(fixture.Second);
                return await db.Set<MonitorAccount>().AllAsync(m => m.ManualState == "idle");
            });
        }
        Assert.True(fixture.Venue.Reads.Any(o => o == fixture.First),
            $"Once-locked owner has zero runs after six minutes; other owner has {fixture.Venue.Reads.Count(o => o == fixture.Second)} runs.");
    }

    [PostgresFact]
    public async Task An_owners_unlocked_third_due_account_and_its_manual_check_are_not_hidden_behind_two_locked_ones()
    {
        await using var fixture = await Fixture.CreateAsync(firstAccounts: 3, secondAccounts: 4, concurrency: 1);
        fixture.Host.Services.GetRequiredService<MonitorOptions>().Interval = TimeSpan.FromMinutes(1);
        foreach (var owner in new[] { fixture.First, fixture.Second })
        {
            await using var scope = fixture.Host.Services.GetRequiredService<IOwnerScopeFactory>().Create(owner);
            await scope.Services.GetRequiredService<MonitorCoordinator>().DiscoverAsync(["hyperliquid"], fixture.Clock.GetUtcNow(), default);
        }
        fixture.Venue.Release.TrySetResult();
        var scheduler = fixture.Host.Services.GetServices<IHostedService>().OfType<MonitorScheduler>().Single();
        await using var held = fixture.Database.Context(fixture.First);
        var ids = await held.Accounts.OrderBy(a => a.Id).Select(a => a.Id).ToArrayAsync();
        await using (var db = fixture.Database.Context(fixture.First))
        {
            (await db.Set<MonitorAccount>().SingleAsync(m => m.AccountId == ids[2])).Admit(Guid.NewGuid(), "manual", fixture.Clock.GetUtcNow());
            await db.SaveChangesAsync();
        }
        // The owner's two earliest-keyed accounts stay row-locked for the whole run, so each claim skips them.
        await using var transaction = await held.Database.BeginTransactionAsync();
        await held.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM accounts WHERE \"Id\" IN ({ids[0]}, {ids[1]}) FOR UPDATE");
        for (var tick = 0; tick < 10; tick++)
        {
            await scheduler.TickAsync(default);
            await fixture.EventuallyAsync(async () =>
            {
                await using var db = fixture.Database.Context(fixture.Second);
                return await db.Set<MonitorAccount>().AllAsync(m => m.ManualState == "idle");
            });
            fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        }
        Assert.True(fixture.Venue.Reads.Any(o => o == fixture.First),
            $"The unlocked third account never ran; the other owner has {fixture.Venue.Reads.Count(o => o == fixture.Second)} runs.");
        await fixture.EventuallyAsync(async () =>
        {
            await using var db = fixture.Database.Context(fixture.First);
            return (await db.Set<MonitorAccount>().SingleAsync(m => m.AccountId == ids[2])).ManualState == "idle";
        });
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Heartbeat { get; } = Path.Combine(Path.GetTempPath(), "vessel-owner-heartbeat-" + Guid.NewGuid().ToString("N"));
        private Fixture(CoreDatabase database, int concurrency, int venueConcurrency)
        {
            Database = database;
            Directory.Set(First, JournalOwnerStatus.Active);
            Directory.Set(Second, JournalOwnerStatus.Active);
            Directory.Set(Suspended, JournalOwnerStatus.Suspended);
            Venue = new(Clock);
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Vessel"] = database.ConnectionString,
                ["Vessel:Auth:OwnerId"] = First.ToString(),
                ["Vessel:Identity:Origins:0"] = TestAuthentication.Origin,
                ["Vessel:Messaging:RabbitMqUri"] = "amqp://unused:unused@127.0.0.1:1",
                ["Vessel:Monitor:HeartbeatFile"] = Heartbeat,
                ["Vessel:Monitor:WorkerConcurrency"] = concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Vessel:Monitor:VenueReadConcurrency"] = venueConcurrency.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(Logs);
            WorkerHost.Configure(builder);
            VesselEventTestHosting.ConfigureServices(builder.Services);
            builder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));
            builder.Services.Replace(ServiceDescriptor.Singleton<IJournalOwnerDirectory>(Directory));
            builder.Services.Replace(ServiceDescriptor.Singleton<IVenueRegistry>(TestVenues.With(Venue)));
            builder.Services.Replace(ServiceDescriptor.Singleton<IMonitorImporter>(new NoImporter()));
            builder.Services.Replace(ServiceDescriptor.Singleton<IMonitorEvaluator>(new NoEvaluator()));
            builder.Services.ConfigureDbContext<VesselDbContext>(options => options.AddInterceptors(Failures));
            Host = builder.Build();
        }
        public Guid First { get; } = Guid.Parse("11111111-0000-0000-0000-000000000001");
        public Guid Second { get; } = Guid.Parse("22222222-0000-0000-0000-000000000002");
        public Guid Suspended { get; } = Guid.Parse("33333333-0000-0000-0000-000000000003");
        public CoreDatabase Database { get; }
        public MutableOwnerDirectory Directory { get; } = new();
        public RenewalClock Clock { get; } = new();
        public GatedVenue Venue { get; }
        public IHost Host { get; }
        public BackgroundWorkLogs Logs { get; } = new();
        public OwnerFailure Failures { get; } = new();
        public Dictionary<Guid, Guid> Accounts { get; } = [];

        public static async Task<Fixture> CreateAsync(int firstAccounts = 1, int secondAccounts = 1, int concurrency = 2, int venueConcurrency = 4)
        {
            var fixture = new Fixture(await CoreDatabase.CreateAsync(), concurrency, venueConcurrency);
            foreach (var owner in new[] { fixture.First, fixture.Second, fixture.Suspended })
            {
                var count = owner == fixture.First ? firstAccounts : owner == fixture.Second ? secondAccounts : 1;
                for (var index = 0; index < count; index++)
                {
                    await using var db = fixture.Database.Context(owner);
                    var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic owner account");
                    account.Configure(null, "0x" + new string((char)('a' + index), 40), null);
                    var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"),
                        InstrumentSource.Venue, "{}", fixture.Clock.GetUtcNow().AddMinutes(-2));
                    play.MarkPlanned(fixture.Clock.GetUtcNow().AddMinutes(-1));
                    db.AddRange(account, play);
                    await db.SaveChangesAsync();
                    fixture.Accounts[owner] = account.Id;
                }
            }
            return fixture;
        }

        public async Task AssertSettledAsync(bool refresh = false)
        {
            foreach (var owner in new[] { First, Second })
                await EventuallyAsync(async () =>
                {
                    await using var db = Database.Context(owner);
                    var row = await db.Set<MonitorAccount>().SingleAsync();
                    return row.ManualState == "idle" && (refresh || row.LastSuccessAtUtc is not null) && await db.Snapshots.AnyAsync();
                });
        }

        public async Task EventuallyAsync(Func<Task<bool>> check)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!await check()) await Task.Delay(25, timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            Venue.Release.TrySetResult();
            Host.Dispose();
            await Database.DisposeAsync();
            File.Delete(Heartbeat);
        }
    }

    private sealed class GatedVenue(RenewalClock clock) : IPerpetualVenueReader, IVenueOrderReader
    {
        private int activeReads;
        public int PeakReads { get; private set; }
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> entered = new();
        public ConcurrentBag<Guid> Reads { get; } = [];
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started(Guid owner) => entered.GetOrAdd(owner, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        public string VenueId => "hyperliquid";
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VenueInstrument>>([]);
        public Task<PerpetualVenueReadResult> ReadAsync(string source, CancellationToken ct) => throw new NotSupportedException();
        public async Task<PerpetualVenueReadResult> ReadAsync(Account account, CancellationToken ct)
        {
            Reads.Add(account.OwnerId);
            lock (entered) PeakReads = Math.Max(PeakReads, ++activeReads);
            entered.GetOrAdd(account.OwnerId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            try { await Release.Task.WaitAsync(TimeSpan.FromSeconds(45), ct); }
            finally { lock (entered) activeReads--; }
            return new(new(clock.GetUtcNow(), "synthetic", 100, 100, 0, []), [], [], "Synthetic isolated read",
                PositionsComplete: true, FillHistoryComplete: true);
        }
        public Task<VenueOrderReadResult> ReadOrdersAsync(string source, CancellationToken ct) =>
            Task.FromResult(new VenueOrderReadResult([], clock.GetUtcNow(), "Synthetic isolated orders", OpenOrdersComplete: true));
    }

    private sealed class OwnerFailure : DbCommandInterceptor
    {
        public Guid Owner { get; set; }
        public string? Step { get; set; }
        public bool Triggered { get; private set; }
        public int AccountLocks { get; set; }
        public int VenueCapacityReads { get; set; }
        private void Check(CommandEventData data, string step)
        {
            if (Step != step || data.Context is not VesselDbContext db || db.CurrentOwnerId != Owner) return;
            Triggered = true;
            throw new InvalidOperationException("Synthetic private store failure");
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SELECT * FROM accounts", StringComparison.Ordinal))
            {
                AccountLocks++;
                Check(eventData, "claim");
            }
            if (command.CommandText.Contains("SELECT COUNT(*) AS \"Value\" FROM monitor_accounts", StringComparison.Ordinal))
                VenueCapacityReads++;
            Check(eventData, "discovery");
            return ValueTask.FromResult(result);
        }

    }

    private sealed class NoImporter : IMonitorImporter
    {
        public Task RetainAsync(Account account, VenueOrderReadResult orders, CancellationToken ct) => Task.CompletedTask;
        public Task ReconcileAsync(Account account, VenueOrderReadResult orders, IReadOnlyList<VenueInstrument> instruments, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class NoEvaluator : IMonitorEvaluator
    {
        public Task<MonitorEvaluationResult> EvaluateAsync(MonitorEvaluation observation, CancellationToken ct) => Task.FromResult(new MonitorEvaluationResult(null));
        public Task<IReadOnlyList<MonitorDiscrepancyDto>> ReadAsync(Guid accountId, Guid? playId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MonitorDiscrepancyDto>>([]);
        public Task<MonitorDiscrepancyList> ReadListAsync(Guid accountId, Guid? playId, CancellationToken ct) =>
            Task.FromResult(new MonitorDiscrepancyList([], false));
        public Task SettleAsync(Account account, Guid runId, string trigger, bool interrupted, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RenewalClock : TimeProvider
    {
        private long ticks = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Ticks;
        private readonly List<ClockTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(callback, state);
            lock (timers) timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref ticks, elapsed.Ticks);
            ClockTimer[] snapshot;
            lock (timers) snapshot = timers.ToArray();
            foreach (var timer in snapshot) timer.Tick();
        }
        private sealed class ClockTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public void Tick() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
