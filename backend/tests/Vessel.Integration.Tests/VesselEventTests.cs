using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;
using Vessel.Persistence.Events;

namespace Vessel.Tests;

public sealed class VesselEventTests : VesselEventTestsValues
{
    [VesselEventPostgresFact]
    public async Task Paper_owner_and_simulated_lifecycle_history_does_not_emit_actual_account_events()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var scope = new EventScope(database, owner);
        await scope.ExecuteAsync(async () =>
        {
            var portfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: "paper");
            var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Virtual", "paper");
            account.Configure(portfolio.Id, null, null);
            scope.Db.Portfolios.Add(portfolio);
            scope.Db.Accounts.Add(account);
            scope.Db.PaperLedgerEntries.Add(Vessel.Domain.Paper.PaperLedgerEntry.Seed(owner, account.Id, 4000, DateTimeOffset.UtcNow));
            var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
            scope.Db.Plays.Add(play);
            var planned = play.MarkPlanned(DateTimeOffset.UtcNow);
            scope.Db.PlanRevisions.Add(planned.Revision);
            scope.Db.StatusChanges.Add(planned.Change);
            await scope.Db.SaveChangesAsync();
            scope.Db.StatusChanges.Add(play.Pause(DateTimeOffset.UtcNow));
            await scope.Db.SaveChangesAsync();
            scope.Db.StatusChanges.Add(play.Resume(DateTimeOffset.UtcNow));
            scope.Db.StatusChanges.Add(play.ApplyPaperExecutionState(true, false, DateTimeOffset.UtcNow)!);
            scope.Db.StatusChanges.Add(play.ApplyPaperExecutionState(true, true, DateTimeOffset.UtcNow)!);
            await scope.Db.SaveChangesAsync();
            var cancelled = TestPlays.Create(account, new("hyperliquid", "ETH"));
            scope.Db.Plays.Add(cancelled);
            scope.Db.StatusChanges.Add(cancelled.Cancel(CancelReason.ChangedMind, null, DateTimeOffset.UtcNow));
            return true;
        }, default);
        Assert.Equal(6, await scope.Db.StatusChanges.CountAsync());
        Assert.Empty((await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events);
        Assert.Empty(scope.Outbox.Staged);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(30)]
    public void Lateness_is_strictly_over_twice_the_configured_monitor_interval(int minutes)
    {
        var services = new ServiceCollection();
        services.AddVesselEvents(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vessel:Monitor:Interval"] = TimeSpan.FromMinutes(minutes).ToString(),
            // There is only one interval setting, even if an Events value was also supplied.
            ["Vessel:Events:MonitorInterval"] = "00:01:00"
        }).Build());
        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IOptions<VesselEventSettings>>().Value;
        var occurred = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
        Assert.Equal(TimeSpan.FromMinutes(minutes), settings.MonitorInterval);
        Assert.Equal("timely", settings.Timeliness(occurred, occurred.AddMinutes(minutes)));
        Assert.Equal("timely", settings.Timeliness(occurred, occurred.AddMinutes(minutes * 2)));
        Assert.Equal("late", settings.Timeliness(occurred, occurred.AddMinutes(minutes * 2).AddTicks(1)));
    }

    [VesselEventPostgresFact]
    public async Task Owner_cursor_is_commit_ordered_not_a_nontransactional_sequence()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var firstGate = new CommitGate();
        var secondGate = new CommitGate();
        await using var first = new EventScope(database, owner, afterStage: firstGate);
        await using var second = new EventScope(database, owner, afterStage: secondGate);
        await using var reader = new EventScope(database, owner);
        var commit1 = first.ExecuteAsync(async () => { await first.Store.AppendAsync(Draft("first"), default); return true; }, default);
        await firstGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var commit2 = second.ExecuteAsync(async () => { await second.Store.AppendAsync(Draft("second"), default); return true; }, default);
        try
        {
            Assert.Empty((await EventReads.ReadAsync(reader.Db, 0, 200, default)).Events);
            await Task.Delay(150);
            Assert.False(commit2.IsCompleted);
            Assert.False(secondGate.Entered.Task.IsCompleted);
            firstGate.Release.TrySetResult();
            await commit1.WaitAsync(TimeSpan.FromSeconds(10));
            await secondGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("1", Assert.Single((await EventReads.ReadAsync(reader.Db, 0, 200, default)).Events).Sequence);
        }
        finally
        {
            firstGate.Release.TrySetResult();
            secondGate.Release.TrySetResult();
            await Task.WhenAll(commit1, commit2).WaitAsync(TimeSpan.FromSeconds(10));
        }
        var page = await EventReads.ReadAsync(reader.Db, 1, 200, default);
        Assert.Equal("2", Assert.Single(page.Events).Sequence);
        Assert.Equal("second", page.Events[0].DedupKey);
        await using var foreign = new EventScope(database, Guid.NewGuid());
        Assert.Empty((await EventReads.ReadAsync(foreign.Db, 0, 200, default)).Events);
        Assert.True((await EventReads.ReadAsync(reader.Db, 999, 200, default)).ResetRequired);
    }

    [VesselEventPostgresFact]
    public async Task Rollback_removes_event_dedup_cursor_and_buffered_sends_and_flush_errors_do_not_fail_commit()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var scope = new EventScope(database, owner);
        scope.Outbox.FailStage = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ExecuteAsync(async () =>
        {
            await scope.Store.AppendAsync(Draft("rollback"), default);
            return true;
        }, default));
        Assert.Equal(0, scope.Outbox.Flushes);
        Assert.Empty(scope.Outbox.Staged);
        Assert.Empty((await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events);
        scope.Outbox.FailStage = false;
        scope.Outbox.FailFlush = true;
        await scope.ExecuteAsync(async () => { await scope.Store.AppendAsync(Draft("rollback"), default); return true; }, default);
        var message = Assert.Single((await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events);
        Assert.Equal("1", message.Sequence);
        await scope.ExecuteAsync(async () => { await scope.Store.AppendAsync(Draft("rollback"), default); return true; }, default);
        Assert.Single((await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events);
    }

    [VesselEventPostgresFact]
    public async Task Intermediate_status_saves_are_captured_once_and_rolled_back_mutations_do_not_replay()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "manual", "Synthetic");
        await using var scope = new EventScope(database, owner);
        await scope.ExecuteAsync(async () =>
        {
            scope.Db.Accounts.Add(account);
            var play = TestPlays.Create(account, new("manual", "BTC"));
            scope.Db.Plays.Add(play);
            var planned = play.MarkPlanned(DateTimeOffset.UtcNow);
            scope.Db.StatusChanges.Add(planned.Change);
            scope.Db.PlanRevisions.Add(planned.Revision);
            await scope.Db.SaveChangesAsync();
            scope.Db.StatusChanges.Add(play.Pause(DateTimeOffset.UtcNow));
            await scope.Db.SaveChangesAsync();
            return true;
        }, default);
        var page = await EventReads.ReadAsync(scope.Db, 0, 200, default);
        Assert.Equal(2, page.Events.Count);
        Assert.Equal(["planned", "paused"], page.Events.Select(e => e.Data.GetProperty("current").GetString()!).ToArray());
        var savedPlay = await scope.Db.Plays.SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ExecuteAsync(async () =>
        {
            scope.Db.StatusChanges.Add(savedPlay.Resume(DateTimeOffset.UtcNow));
            await scope.Db.SaveChangesAsync();
            throw new InvalidOperationException("Deliberate rollback.");
#pragma warning disable CS0162
            return true;
#pragma warning restore CS0162
        }, default));
        await scope.ExecuteAsync(() => Task.FromResult(true), default);
        Assert.Equal(2, (await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events.Count);
        Assert.Equal(PlayStatus.Paused, (await scope.Db.Plays.SingleAsync()).Status);
    }

    [VesselEventPostgresFact]
    public async Task Durable_baseline_and_fill_identity_prevent_pre_watch_reread_and_relink_replay()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic");
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account); seed.Plays.Add(play);
            var planned = play.MarkPlanned(DateTimeOffset.UtcNow.AddHours(-1));
            seed.StatusChanges.Add(planned.Change); seed.PlanRevisions.Add(planned.Revision);
            seed.Fills.Add(Fill(owner, account.Id, "old", DateTimeOffset.UtcNow.AddHours(-1)));
            await seed.SaveChangesAsync();
        }
        await using var scope = new EventScope(database, owner);
        await scope.ExecuteAsync(async () =>
        {
            await scope.Store.EnsureMonitoringBaselineAsync(account.Id, default);
            return true;
        }, default);
        PlayOrderLink? link = null;
        var order = TestOrders.For(play, "order", status: "open");
        await scope.ExecuteAsync(async () =>
        {
            var stored = await scope.Db.Plays.SingleAsync();
            scope.Db.Orders.Add(order);
            link = new(stored, order, OrderLinkRole.Entry, "entry-1", null,
                OrderLinkState.Linked, OrderLinkSource.Owner, DateTimeOffset.UtcNow);
            scope.Db.OrderLinks.Add(link);
            return true;
        }, default);
        Assert.DoesNotContain((await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events, e => e.Type == "execution.filled");
        await scope.ExecuteAsync(() =>
        {
            scope.Db.Fills.Add(Fill(owner, account.Id, "new", DateTimeOffset.UtcNow));
            return Task.FromResult(true);
        }, default);
        var emitted = Assert.Single((await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events, e => e.Type == "execution.filled");
        Assert.Equal(["new"], emitted.Source.FillIds!);
        Assert.EndsWith(":filled:1", emitted.DedupKey);
        await scope.ExecuteAsync(() => { scope.Db.OrderLinks.Remove(link!); return Task.FromResult(true); }, default);
        await scope.ExecuteAsync(async () =>
        {
            scope.Db.OrderLinks.Add(new(await scope.Db.Plays.SingleAsync(), order, OrderLinkRole.Entry, "entry-1", null,
                OrderLinkState.Linked, OrderLinkSource.Owner, DateTimeOffset.UtcNow));
            return true;
        }, default);
        Assert.Single((await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events, e => e.Type == "execution.filled");
    }

    [VesselEventPostgresFact]
    public async Task Manual_fact_corrections_do_not_masquerade_as_venue_fill_events()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var scope = new EventScope(database, owner);
        await scope.ExecuteAsync(() =>
        {
            var account = new Account(Guid.NewGuid(), owner, "manual", "Synthetic");
            var play = TestPlays.Create(account, new("manual", "BTC"));
            scope.Db.Accounts.Add(account); scope.Db.Plays.Add(play);
            scope.Db.ManualExecutions.Add(new()
            {
                Id = Guid.NewGuid(), OwnerId = owner, PlayId = play.Id, Role = "entry", Side = "buy",
                Quantity = "0.50", Price = "123.00", Source = "owner", OccurredAtUtc = DateTimeOffset.UtcNow,
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            return Task.FromResult(true);
        }, default);
        await scope.ExecuteAsync(async () =>
        {
            var fact = await scope.Db.ManualExecutions.SingleAsync();
            fact.Price = "124.00";
            return true;
        }, default);
        Assert.Empty((await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events);
    }

    [VesselEventPostgresFact]
    public async Task Read_path_resolves_without_any_broker_runtime_and_foreign_subject_writes_roll_back()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var foreignAccount = new Account(Guid.NewGuid(), foreign, "manual", "Foreign");
        await using (var seed = database.Context(foreign))
        {
            seed.Accounts.Add(foreignAccount);
            await seed.SaveChangesAsync();
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Vessel"] = database.ConnectionString
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IJournalOwnerContext>(new CoreOwner(owner));
        services.AddVesselPersistence(config);
        services.AddVesselEvents(config); // No Wolverine host/runtime or broker connection.
        await using var provider = services.BuildServiceProvider();
        await using var reading = provider.CreateAsyncScope();
        var reader = reading.ServiceProvider.GetRequiredService<IVesselEventReader>();
        Assert.IsType<VesselEventHistory>(reader);
        Assert.Empty((await reader.ReadAsync("0", 200, default)).Events);
        await using var scope = new EventScope(database, owner);
        var rejected = await Assert.ThrowsAsync<Vessel.Application.Common.ProblemException>(() =>
            scope.ExecuteAsync(async () =>
            {
                await scope.Store.AppendAsync(Draft("foreign") with { Subject = new(foreignAccount.Id) }, default);
                return true;
            }, default));
        Assert.Equal(404, rejected.StatusCode);
        Assert.Empty((await reader.ReadAsync("0", 200, default)).Events);
    }

    [VesselEventPostgresFact]
    public async Task Late_open_superseded_by_close_carries_exact_closed_data_and_paused_warning()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var scope = new EventScope(database, owner);
        await scope.ExecuteAsync(async () =>
        {
            var account = new Account(Guid.NewGuid(), owner, "manual", "Synthetic");
            var play = TestPlays.Create(account, new("manual", "BTC"));
            scope.Db.Accounts.Add(account); scope.Db.Plays.Add(play);
            var planned = play.MarkPlanned(DateTimeOffset.UtcNow.AddHours(-2));
            scope.Db.StatusChanges.Add(planned.Change); scope.Db.PlanRevisions.Add(planned.Revision);
            scope.Db.StatusChanges.Add(play.Pause(DateTimeOffset.UtcNow.AddHours(-2)));
            scope.Db.StatusChanges.Add(play.MarkOpen("Synthetic paused venue fill", DateTimeOffset.UtcNow.AddHours(-1)));
            await scope.Db.SaveChangesAsync();
            scope.Db.StatusChanges.Add(play.MarkClosed("Synthetic close", DateTimeOffset.UtcNow.AddMinutes(-30)));
            return true;
        }, default);
        var page = await EventReads.ReadAsync(scope.Db, 0, 200, default);
        var opened = Assert.Single(page.Events, e => e.Type == "play.opened");
        Assert.Equal(("warning", "late", "superseded"), (opened.Severity, opened.Timeliness, opened.Relevance));
        var closed = Assert.Single(page.Events, e => e.Type == "play.closed");
        Assert.Equal(("info", "late", "current"), (closed.Severity, closed.Timeliness, closed.Relevance));
        Assert.Equal("0", closed.Data.GetProperty("enteredQuantity").GetString());
        Assert.Equal("0", closed.Data.GetProperty("closedPnlUsd").GetString());
        Assert.Equal("gross", closed.Data.GetProperty("closedPnlBasis").GetString());
    }

    [VesselEventPostgresFact]
    public async Task Delayed_venue_open_and_close_in_one_run_use_first_entry_and_final_settling_exit()
    {
        foreach (var interval in new[] { 20, 30 })
        {
            await using var database = await CoreDatabase.CreateAsync();
            var owner = Guid.NewGuid();
            var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var first = now.AddMinutes(-interval * 4);
            var final = now.AddMinutes(-interval * 2 - 5);
            var run = Guid.CreateVersion7();
            await using var scope = new EventScope(database, owner, new() { MonitorInterval = TimeSpan.FromMinutes(interval) });
            await scope.ExecuteAsync(async () =>
            {
                await scope.Store.SetRunAsync(run, "monitor", default);
                var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic");
                var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
                scope.Db.Accounts.Add(account); scope.Db.Plays.Add(play);
                var planned = play.MarkPlanned(first.AddHours(-1));
                scope.Db.StatusChanges.Add(planned.Change); scope.Db.PlanRevisions.Add(planned.Revision);
                AddOrder(scope.Db, play, "entry-a", OrderLinkRole.Entry, 1, 0, "filled", now);
                AddOrder(scope.Db, play, "entry-b", OrderLinkRole.Entry, 1, 0, "filled", now);
                AddOrder(scope.Db, play, "exit", OrderLinkRole.Exit, 2, 0, "filled", now);
                scope.Db.Fills.AddRange(
                    VenueFill(play, "entry-a", "a1", 0.25m, first),
                    VenueFill(play, "entry-a", "a2", 0.75m, first.AddMinutes(1)),
                    VenueFill(play, "entry-b", "b1", 1, first.AddMinutes(2)),
                    VenueFill(play, "exit", "x1", 0.5m, final.AddMinutes(-1), exit: true),
                    VenueFill(play, "exit", "x2", 1.5m, final, exit: true));
                // The current service records both mutations at detection time, with an intermediate save.
                scope.Db.StatusChanges.Add(play.MarkOpen("Delayed entry", now));
                await scope.SaveChangesAsync(default);
                scope.Db.StatusChanges.Add(play.MarkClosed("Delayed settlement", now));
                return true;
            }, default);
            var events = (await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events;
            var opened = Assert.Single(events, e => e.Type == "play.opened");
            var closed = Assert.Single(events, e => e.Type == "play.closed");
            Assert.Equal(first, opened.OccurredAt);
            Assert.Equal(final, closed.OccurredAt);
            Assert.Equal(("late", "superseded"), (opened.Timeliness, opened.Relevance));
            Assert.Equal(("late", "current"), (closed.Timeliness, closed.Relevance));
            Assert.InRange(opened.DetectedAt, now, now.AddMinutes(1));
            Assert.Equal(opened.DetectedAt, closed.DetectedAt);
            Assert.Equal(run, opened.RunId); Assert.Equal(run, closed.RunId);
            Assert.Equal("2", closed.Data.GetProperty("enteredQuantity").GetString());
            Assert.Equal("2", closed.Data.GetProperty("exitedQuantity").GetString());
        }
    }

    [VesselEventPostgresFact]
    public async Task Closures_found_by_one_run_close_at_the_latest_valid_exit_their_outcomes_score()
    {
        // Phase 0 item 0.2: the play.closed source time and the sizing outcome share one helper. Three Plays exit at
        // different times and one check detects them together; an exit stamped after detection is clamped to it.
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var exits = new[] { now.AddHours(-2), now.AddHours(-1), now.AddMinutes(5) };
        await using (var scope = new EventScope(database, owner))
            await scope.ExecuteAsync(async () =>
            {
                var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic");
                scope.Db.Accounts.Add(account);
                var plays = new List<Play>();
                for (var i = 0; i < exits.Length; i++)
                {
                    var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
                    scope.Db.Plays.Add(play);
                    plays.Add(play);
                    var planned = play.MarkPlanned(now.AddHours(-4));
                    scope.Db.StatusChanges.Add(planned.Change); scope.Db.PlanRevisions.Add(planned.Revision);
                    AddOrder(scope.Db, play, $"entry-{i}", OrderLinkRole.Entry, 1, 0, "filled", now);
                    AddOrder(scope.Db, play, $"exit-{i}", OrderLinkRole.Exit, 1, 0, "filled", now);
                    var exit = VenueFill(play, $"exit-{i}", $"x{i}", 1, exits[i], exit: true);
                    exit.ClosedPnlUsd = i == 1 ? 5 : -5;
                    scope.Db.Fills.AddRange(VenueFill(play, $"entry-{i}", $"e{i}", 1, now.AddHours(-3)), exit);
                    scope.Db.StatusChanges.Add(play.MarkOpen("Synthetic entry", now));
                }
                await scope.SaveChangesAsync(default);
                foreach (var play in plays) scope.Db.StatusChanges.Add(play.MarkClosed("Synthetic settlement", now));
                return true;
            }, default);
        await using var db = database.Context(owner);
        var closed = (await EventReads.ReadAsync(db, 0, 200, default)).Events.Where(e => e.Type == "play.closed")
            .ToDictionary(e => e.Subject.PlayId!.Value, e => e.OccurredAt);
        Assert.Equal([exits[0], exits[1], now], closed.Values.Order());
        var sizing = await new SizingService(new SizingStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TimeProvider.System,
            TestUnitOfWork.For(db)).GetAsync(default);
        Assert.Equal(3, sizing.Record.Recent.Count);
        Assert.All(sizing.Record.Recent, row =>
        {
            Assert.Equal(closed[row.PlayId!.Value], row.ClosedAtUtc);
            Assert.Equal(now, row.DetectedAtUtc);
        });
        // The Plays themselves keep the detection time.
        Assert.All(await db.Plays.ToListAsync(), play => Assert.Equal(now, play.EndedAtUtc));
    }

    [VesselEventPostgresFact]
    public async Task Partial_entry_uses_known_source_time_but_incomplete_settlement_keeps_mutation_time()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var first = now.AddHours(-2);
        await using var scope = new EventScope(database, owner);
        await scope.ExecuteAsync(() =>
        {
            var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic");
            var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
            scope.Db.Accounts.Add(account); scope.Db.Plays.Add(play);
            var planned = play.MarkPlanned(first.AddHours(-1));
            scope.Db.StatusChanges.Add(planned.Change); scope.Db.PlanRevisions.Add(planned.Revision);
            AddOrder(scope.Db, play, "entry", OrderLinkRole.Entry, 1, 0.5m, "open", now);
            AddOrder(scope.Db, play, "exit", OrderLinkRole.Exit, 1, 0, "filled", now);
            scope.Db.Fills.AddRange(VenueFill(play, "entry", "a1", 0.5m, first),
                VenueFill(play, "exit", "x1", 0.5m, first.AddMinutes(5), exit: true));
            scope.Db.StatusChanges.Add(play.MarkOpen("Partial entry", now));
            return Task.FromResult(true);
        }, default);
        await scope.ExecuteAsync(async () =>
        {
            var play = await scope.Db.Plays.SingleAsync();
            (await scope.Db.Orders.SingleAsync(o => o.ExternalOrderId == "entry")).Status = "canceled";
            // A mutation with incomplete facts cannot justify inventing a source closure timestamp.
            scope.Db.StatusChanges.Add(play.MarkClosed("Synthetic mutation with missing source facts", now));
            return true;
        }, default);
        var events = (await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events;
        Assert.Equal(first, Assert.Single(events, e => e.Type == "play.opened").OccurredAt);
        var closed = Assert.Single(events, e => e.Type == "play.closed");
        Assert.Equal(now, closed.OccurredAt);
        Assert.Equal("timely", closed.Timeliness);
    }

    [VesselEventPostgresFact]
    public async Task Manual_lifecycle_keeps_action_time_not_backdated_manual_execution_time()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await using var scope = new EventScope(database, owner);
        await scope.ExecuteAsync(() =>
        {
            var account = new Account(Guid.NewGuid(), owner, "manual", "Synthetic");
            var play = TestPlays.Create(account, new("manual", "BTC"));
            scope.Db.Accounts.Add(account); scope.Db.Plays.Add(play);
            var planned = play.MarkPlanned(now.AddHours(-3));
            scope.Db.StatusChanges.Add(planned.Change); scope.Db.PlanRevisions.Add(planned.Revision);
            foreach (var role in new[] { "entry", "exit" })
                scope.Db.ManualExecutions.Add(new()
                {
                    Id = Guid.NewGuid(), OwnerId = owner, PlayId = play.Id, Role = role,
                    Side = role == "entry" ? "buy" : "sell", Quantity = "1", Price = "100", Source = "owner",
                    OccurredAtUtc = now.AddHours(role == "entry" ? -2 : -1), CreatedAtUtc = now, UpdatedAtUtc = now
                });
            scope.Db.StatusChanges.Add(play.RecordManualOpen(now));
            scope.Db.StatusChanges.Add(play.RecordManualClose(now.AddHours(-1), now));
            return Task.FromResult(true);
        }, default);
        var events = (await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events;
        var lifecycle = events.Where(e => e.Type is "play.opened" or "play.closed").ToList();
        Assert.Equal(2, lifecycle.Count);
        Assert.All(lifecycle, e =>
        {
            Assert.Equal(now, e.OccurredAt);
            Assert.Equal("timely", e.Timeliness);
        });
        Assert.DoesNotContain(events, e => e.Type == "execution.filled");
    }

    private static void AddOrder(VesselDbContext db, Play play, string id, OrderLinkRole role,
        decimal size, decimal remaining, string status, DateTimeOffset now)
    {
        var order = new ImportedOrder
        {
            Id = Guid.NewGuid(), OwnerId = play.OwnerId, AccountId = play.AccountId, ContractId = play.ContractId!,
            ExternalOrderId = id, Side = role == OrderLinkRole.Entry ? "buy" : "sell", OrderType = "limit",
            OriginalSize = size, RemainingSize = remaining, Status = status, VenueStatus = status,
            PlacedAtUtc = now.AddDays(-1), StatusAtUtc = now, ObservedAtUtc = now
        };
        db.Orders.Add(order);
        db.OrderLinks.Add(new(play, order, role, role == OrderLinkRole.Entry ? "entry-1" : null, null,
            OrderLinkState.Linked, OrderLinkSource.Automatic, now));
    }

    private static ImportedFill VenueFill(Play play, string order, string id, decimal quantity,
        DateTimeOffset occurred, bool exit = false)
    {
        var fill = Fill(play.OwnerId, play.AccountId, id, occurred);
        fill.ExternalOrderId = order; fill.Quantity = quantity;
        if (exit) { fill.Side = "sell"; fill.PositionEffect = "close"; fill.Direction = "Close Long"; }
        return fill;
    }

    private static ImportedFill Fill(Guid owner, Guid account, string id, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), OwnerId = owner, AccountId = account, ContractId = "BTC", SourceFillId = id,
        Side = "buy", Direction = "Open Long", PositionEffect = "open", Price = 100, Quantity = 0.5m,
        Fee = 0, FeeToken = "USDC", ClosedPnlUsd = 0, OccurredAtUtc = now, ExternalOrderId = "order", TransactionHash = "synthetic"
    };

    private sealed class FakeOutbox : IVesselEventOutbox
    {
        public List<VesselEvent> Staged { get; } = [];
        public int Flushes { get; private set; }
        public bool FailFlush { get; set; }
        public bool FailStage { get; set; }
        public Task StageAsync(VesselEvent message, CancellationToken ct)
        {
            Staged.Add(message);
            if (FailStage) throw new InvalidOperationException("Synthetic staging failure.");
            return Task.CompletedTask;
        }
        public Task StageAsync(MonitorAccountCommand command, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct)
        { Flushes++; if (FailFlush) throw new InvalidOperationException("Synthetic broker outage."); return Task.CompletedTask; }
        public void Reset() => Staged.Clear();
    }

    private sealed class EventScope : IUnitOfWork, IAsyncDisposable
    {
        public VesselDbContext Db { get; }
        public FakeOutbox Outbox { get; } = new();
        public VesselEventStore Store { get; }
        private readonly UnitOfWork work;
        public EventScope(CoreDatabase database, Guid owner, VesselEventSettings? settings = null,
            IUnitOfWorkParticipant? afterStage = null)
        {
            var capture = new VesselEventMutationCapture();
            Db = new(new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(database.ConnectionString)
                .AddInterceptors(capture).Options, new CoreOwner(owner));
            Store = new(Db, capture, Outbox, Options.Create(settings ?? new VesselEventSettings()), NullLogger<VesselEventStore>.Instance);
            work = new(Db, afterStage is null ? [Store] : [Store, afterStage]);
        }
        public Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken ct) => work.ExecuteAsync(operation, ct);
        public Task SaveChangesAsync(CancellationToken ct) => work.SaveChangesAsync(ct);
        public bool IsActive => work.IsActive;
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class CommitGate : IUnitOfWorkParticipant
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task BeforeCommitAsync(CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        public Task AfterCommitAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
