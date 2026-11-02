using Vessel.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Vessel.Application.Events;
using Vessel.Application.Monitoring;
using Vessel.Application.Plays;
using Vessel.Domain.Markets;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;
using Vessel.Persistence.Events;

namespace Vessel.Tests;

public sealed class MonitorDiscrepancyPostgresTests
{
    [PostgresFact]
    public async Task Retrospective_order_links_are_not_reported_as_unlinked_fills()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture();
        var retro = AddRetrospectivePlay(f);
        f.Fill("retro-in-fill", "retro-in", 1, f.Clock.Now.AddMinutes(-4));
        f.Fill("retro-out-fill", "retro-out", 1, f.Clock.Now.AddMinutes(-2), "sell", ExecutionFacts.Close);
        foreach (var orderId in new[] { "retro-in", "retro-out" })
            f.Store.Orders.Add(new ImportedOrder { Id = Guid.NewGuid(), OwnerId = f.Account.OwnerId,
                AccountId = f.Account.Id, ContractId = "BTC", ExternalOrderId = orderId,
                Side = orderId == "retro-in" ? "buy" : "sell", OrderType = "Limit", LimitPrice = 100,
                OriginalSize = 1, RemainingSize = 0, Status = "filled", VenueStatus = "filled",
                PlacedAtUtc = f.Clock.Now.AddMinutes(-5), StatusAtUtc = f.Clock.Now.AddMinutes(-2),
                ObservedAtUtc = f.Clock.Now });
        f.Store.Links.Add(f.Link(retro, "retro-in", OrderLinkRole.Entry, "retrospective", null, OrderLinkState.Linked, OrderLinkSource.Owner, f.Clock.Now));
        f.Store.Links.Add(f.Link(retro, "retro-out", OrderLinkRole.Exit, null, null, OrderLinkState.Linked, OrderLinkSource.Owner, f.Clock.Now));
        await database.Seed(f);
        await using (var first = database.Scope(f)) await first.Evaluate(f.Observation());
        f.Advance(60);
        await using var second = database.Scope(f);
        await second.Evaluate(f.Observation());
        Assert.Empty(await second.Service.ReadAsync(f.Account.Id, null, default));
        await second.Work.ExecuteAsync(async () =>
        {
            var facts = await second.Store.LoadAsync(f.Account.Id, f.Clock.Now, [], default);
            Assert.True(facts.AssignmentLinksComplete);
            Assert.DoesNotContain(facts.Plays, x => x.Id == retro.Id);
            Assert.DoesNotContain(facts.Links, x => x.PlayId == retro.Id);
            Assert.Equal(2, facts.AssignmentLinks!.Count(x => x.PlayId == retro.Id));
            return true;
        }, default);
        Assert.Equal(3, await second.Db.Fills.CountAsync());
    }

    [PostgresFact]
    public async Task Account_wide_suggestion_prevents_unlinked_warning_but_dismissal_does_not()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture();
        var retro = AddRetrospectivePlay(f);
        f.Fill("extra", "extra-order", 1, f.Clock.Now.AddMinutes(-2));
        f.Store.Links.Add(f.Link(retro, "extra-order", OrderLinkRole.Entry, "retrospective", null,
            OrderLinkState.Suggested, OrderLinkSource.Automatic, f.Clock.Now));
        await database.Seed(f);
        await using (var first = database.Scope(f)) await first.Evaluate(f.Observation());
        f.Advance(60);
        await using (var second = database.Scope(f))
        {
            await second.Evaluate(f.Observation());
            Assert.Empty(await second.Service.ReadAsync(f.Account.Id, null, default));
            await second.Work.ExecuteAsync(async () =>
            {
                await second.Store.LockAsync(f.Account.Id, default);
                var extra = await second.Db.Orders.SingleAsync(x => x.ExternalOrderId == "extra-order");
                (await second.Db.OrderLinks.SingleAsync(x => x.ImportedOrderId == extra.Id)).Dismiss(f.Clock.Now);
                return true;
            }, default);
        }
        f.Advance(60);
        await using (var third = database.Scope(f)) await third.Evaluate(f.Observation());
        f.Advance(60);
        await using var fourth = database.Scope(f);
        await fourth.Evaluate(f.Observation());
        Assert.Contains(await fourth.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == "fill.unlinked" && x.State == "open");
    }

    [PostgresFact]
    public async Task Unrelated_historical_assignments_neither_cap_nor_block_unlinked_fill_resolution()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture();
        var retro = AddRetrospectivePlay(f);
        f.Fill("extra", "extra-order", 1, f.Clock.Now.AddMinutes(-2));
        await database.Seed(f);
        await using (var first = database.Scope(f)) await first.Evaluate(f.Observation());
        f.Advance(60);
        await using (var second = database.Scope(f))
        {
            await second.Evaluate(f.Observation());
            Assert.Contains(await second.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == "fill.unlinked" && x.State == "open");
            // Seed historical assignments directly, as in Seed, without generating 10,000 fixture events.
            var historical = Enumerable.Range(0, 10000).Select(i => TestOrders.For(retro, "historical-" + i)).ToList();
            second.Db.Orders.AddRange(historical);
            second.Db.OrderLinks.AddRange(historical.Select(order => new PlayOrderLink(
                retro, order, OrderLinkRole.Entry, "retrospective", null,
                OrderLinkState.Suggested, OrderLinkSource.Automatic, f.Clock.Now)));
            var extra = TestOrders.For(retro, "extra-order");
            second.Db.Orders.Add(extra);
            second.Db.OrderLinks.Add(new(retro, extra, OrderLinkRole.Entry, "retrospective", null,
                OrderLinkState.Suggested, OrderLinkSource.Automatic, f.Clock.Now));
            await second.Db.SaveChangesAsync();
        }
        f.Position = 2;
        f.Advance(60);
        await using (var third = database.Scope(f))
        {
            await third.Work.ExecuteAsync(async () =>
            {
                // Only the loaded fills' orders need assignment coverage, so history cannot grow into a cap.
                var facts = await third.Store.LoadAsync(f.Account.Id, f.Clock.Now, [], default);
                Assert.True(facts.Complete);
                Assert.True(facts.AssignmentLinksComplete);
                var external = facts.Orders.ToDictionary(x => x.Id, x => x.ExternalOrderId);
                Assert.Contains(facts.AssignmentLinks!, x => external[x.ImportedOrderId] == "extra-order");
                Assert.DoesNotContain(facts.AssignmentLinks!, x => !external.ContainsKey(x.ImportedOrderId) || external[x.ImportedOrderId].StartsWith("historical-"));
                return true;
            }, default);
            Assert.False((await third.Evaluate(f.Observation())).Incomplete);
        }
        f.Advance(60);
        await using var fourth = database.Scope(f);
        Assert.False((await fourth.Evaluate(f.Observation())).Incomplete);
        var rows = await fourth.Service.ReadAsync(f.Account.Id, null, default);
        Assert.Equal("resolved", Assert.Single(rows, x => x.Kind == "fill.unlinked").State);
        Assert.Contains(rows, x => x.Kind == "position.excess" && x.State == "open");
    }

    private static Play AddRetrospectivePlay(DiscrepancyFixture f)
    {
        var play = Play.Retrospective(Guid.NewGuid(), f.Account, new PerpetualInstrument("hyperliquid", "BTC"),
            PlayDocuments.Serialize(new PlayPlanDocument("long", "margin", "", "0", null, [], new("", "", "", ""))),
            f.Clock.Now.AddMinutes(-2), f.Clock.Now, "Already journalled");
        f.Store.Plays.Add(play);
        return play;
    }

    [PostgresFact]
    public async Task Step_four_migration_upgrades_layer_three_without_changing_imported_facts()
    {
        await using var database = await DiscrepancyDatabase.Create("20261016110200_MessagingDeployments");
        var f = new DiscrepancyFixture();
        var fill = f.Store.Fills[0];
        await using var scope = database.Scope(f);
        // This deliberately predates paper columns. Seed/read through that historical shape, not today's EF model.
        await scope.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO accounts ("Id","OwnerId","VenueId","Name","Address","SourceId","SyncStatus")
            VALUES ({f.Account.Id},{f.Account.OwnerId},{f.Account.VenueId},{f.Account.Name},
                {f.Account.Address},{f.Account.SourceId},{f.Account.SyncStatus})
            """);
        await scope.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO plays ("Id","OwnerId","AccountId","VenueId","ContractId","InstrumentSource","Status","Plan","Direction")
            VALUES ({f.Play.Id},{f.Play.OwnerId},{f.Play.AccountId},{f.Play.VenueId},{f.Play.ContractId},
                {f.Play.InstrumentSource.ToString()},{f.Play.Status.ToString()},{f.Play.Plan}::jsonb,{f.Play.Direction})
            """);
        // Imported facts in this historical schema's columns ("OrderId" before the order identity change).
        await scope.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO imported_fills ("Id", "OwnerId", "AccountId", "ContractId", "SourceFillId", "Side", "Direction", "PositionEffect",
                "Price", "Quantity", "Fee", "FeeToken", "FeeBasis", "ClosedPnlUsd", "PnlBasis", "OccurredAtUtc", "OrderId", "TransactionHash")
            VALUES ({fill.Id}, {fill.OwnerId}, {fill.AccountId}, {fill.ContractId}, {fill.SourceFillId}, {fill.Side}, {fill.Direction},
                {fill.PositionEffect}, {fill.Price}, {fill.Quantity}, 0, 'USDC', 'reported', 0, 'gross', {fill.OccurredAtUtc}, {fill.ExternalOrderId}, 'synthetic')
            """);
        Assert.Equal("20261016110300_PlayDiscrepancies", (await scope.Db.Database.GetPendingMigrationsAsync()).First());
        // Step four: the discrepancy table and its unverifiable outcome column.
        await scope.Db.GetService<IMigrator>().MigrateAsync("20261016110400_PlayDiscrepancyUnverifiable");
        Assert.Empty(await scope.Db.Set<PlayDiscrepancy>().ToListAsync());
        Assert.Equal(fill.Quantity, await scope.Db.Database.SqlQueryRaw<decimal>("""SELECT "Quantity" AS "Value" FROM imported_fills""").SingleAsync());
        Assert.Equal(f.Play.Status.ToString(), await scope.Db.Database.SqlQueryRaw<string>(
            """SELECT "Status" AS "Value" FROM plays""").SingleAsync());
        await scope.Db.GetService<IMigrator>().MigrateAsync("20261016174509_FundingPayments");
        // The order identity change has no backfill: it refuses a database that still holds imported facts.
        Assert.Equal("20261017091636_OrderIdentity", (await scope.Db.Database.GetPendingMigrationsAsync()).First());
        var refused = await Assert.ThrowsAsync<PostgresException>(() => scope.Db.Database.MigrateAsync());
        Assert.Equal("P0001", refused.SqlState);
    }

    [PostgresFact]
    public async Task DbContext_rejects_writing_another_owners_discrepancy()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture(); await database.Seed(f);
        await using var scope = database.Scope(f);
        scope.Db.Add(new PlayDiscrepancy { OwnerId = Guid.NewGuid(), AccountId = f.Account.Id,
            Kind = "position.excess", Severity = "warning", SubjectKey = "foreign" });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scope.Db.SaveChangesAsync());
    }

    [PostgresFact]
    public async Task Actual_evaluator_persists_candidates_confirms_once_and_dismisses_in_the_event_transaction()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture { Position = 2 };
        await database.Seed(f);
        await using (var first = database.Scope(f))
        {
            await first.Evaluate(f.Observation());
            Assert.Empty(await first.Service.ReadAsync(f.Account.Id, null, default));
            Assert.Empty((await EventReads.ReadAsync(first.Db, 0, 100, default)).Events);
        }
        f.Advance(60);
        await using (var second = database.Scope(f))
        {
            await second.Evaluate(f.Observation());
            var row = Assert.Single(await second.Service.ReadAsync(f.Account.Id, f.Play.Id, default));
            Assert.Equal("position.excess", row.Kind);
            Assert.Equal("open", row.State);
            Assert.Equal("1", row.Details.GetProperty("expectedQuantity").GetString());
            Assert.Single((await EventReads.ReadAsync(second.Db, 0, 100, default)).Events);
            await second.Service.DismissAsync(f.Account.Id, f.Play.Id, row.Id, "accepted exposure", default);
            var events = (await EventReads.ReadAsync(second.Db, 0, 100, default)).Events;
            Assert.Equal(2, events.Count);
            Assert.Equal("monitor", events[0].Source.Trigger);
            Assert.Equal("info", events[1].Severity);
            Assert.Equal("dismissed", events[1].Data.GetProperty("resolution").GetString());
        }
        f.Advance(600);
        await using (var restart = database.Scope(f))
        {
            await restart.Evaluate(f.Observation());
            Assert.Equal("dismissed", Assert.Single(await restart.Service.ReadAsync(f.Account.Id, f.Play.Id, default)).State);
            Assert.Equal(2, (await EventReads.ReadAsync(restart.Db, 0, 100, default)).Events.Count);
            var retained = await restart.Db.Fills.SingleAsync();
            Assert.Equal(f.Store.Fills[0].Quantity, retained.Quantity);
            Assert.Equal(f.Store.Fills[0].Side, retained.Side);
            Assert.Equal(f.Store.Fills[0].Fee, retained.Fee);
        }
        await using var foreign = database.Scope(f, Guid.NewGuid());
        Assert.Empty(await foreign.Db.Set<PlayDiscrepancy>().ToListAsync());
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => foreign.Service.ReadAsync(f.Account.Id, f.Play.Id, default))).StatusCode);
    }

    [PostgresFact]
    public async Task Rollback_leaves_neither_confirmation_nor_event_and_retry_confirms_the_same_candidate()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture { Position = 0 }; await database.Seed(f);
        await using (var first = database.Scope(f)) await first.Evaluate(f.Observation());
        f.Advance(60);
        await using (var failed = database.Scope(f))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => failed.Work.ExecuteAsync<bool>(async () =>
            {
                await failed.Store.LockAsync(f.Account.Id, default);
                await failed.Service.EvaluateAsync(f.Observation(), default);
                await failed.Work.SaveChangesAsync(default);
                throw new InvalidOperationException("synthetic fence failure");
            }, default));
        }
        await using var retry = database.Scope(f);
        Assert.Empty((await EventReads.ReadAsync(retry.Db, 0, 100, default)).Events);
        Assert.Equal("candidate", (await retry.Db.Set<PlayDiscrepancy>().SingleAsync()).State);
        await retry.Evaluate(f.Observation());
        Assert.Single((await EventReads.ReadAsync(retry.Db, 0, 100, default)).Events);
        Assert.Equal("open", (await retry.Db.Set<PlayDiscrepancy>().SingleAsync()).State);
    }

    [PostgresFact]
    public async Task Fenced_overlapping_confirmations_emit_one_event_and_one_subject_row()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture { Position = 2 }; await database.Seed(f);
        await using (var first = database.Scope(f)) await first.Evaluate(f.Observation());
        f.Advance(60);
        await using var a = database.Scope(f); await using var b = database.Scope(f);
        await Task.WhenAll(a.Evaluate(f.Observation()), b.Evaluate(f.Observation()));
        await using var read = database.Scope(f);
        Assert.Single(await read.Db.Set<PlayDiscrepancy>().ToListAsync());
        Assert.Single((await EventReads.ReadAsync(read.Db, 0, 100, default)).Events);
        var original = await read.Db.Set<PlayDiscrepancy>().SingleAsync();
        read.Db.Add(new PlayDiscrepancy { OwnerId = original.OwnerId, AccountId = original.AccountId,
            Kind = original.Kind, SubjectKey = original.SubjectKey, ContractId = original.ContractId,
            NativeContractId = original.NativeContractId, Severity = "warning" });
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => read.Db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
    }

    [PostgresFact]
    public async Task Dismissal_checks_account_and_involved_play_and_rolled_back_dismissal_has_no_event()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture { Position = 2 }; await database.Seed(f);
        await using (var first = database.Scope(f)) await first.Evaluate(f.Observation());
        f.Advance(60);
        await using (var second = database.Scope(f)) await second.Evaluate(f.Observation());
        Guid id;
        await using (var read = database.Scope(f)) id = (await read.Db.Set<PlayDiscrepancy>().SingleAsync()).Id;
        await using (var failed = database.Scope(f))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => failed.Work.ExecuteAsync<bool>(async () =>
            {
                await failed.Service.DismissAsync(f.Account.Id, f.Play.Id, id, "not committed", default);
                throw new InvalidOperationException("synthetic outer failure");
            }, default));
        }
        await using (var read = database.Scope(f))
        {
            Assert.Equal("open", (await read.Db.Set<PlayDiscrepancy>().SingleAsync()).State);
            Assert.Single((await EventReads.ReadAsync(read.Db, 0, 100, default)).Events);
        }
        await using (var invalidAccount = database.Scope(f))
            Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => invalidAccount.Service.DismissAsync(Guid.NewGuid(), null, id, null, default))).StatusCode);
        await using (var invalidPlay = database.Scope(f))
            Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => invalidPlay.Service.DismissAsync(f.Account.Id, Guid.NewGuid(), id, null, default))).StatusCode);
    }

    [PostgresFact]
    public async Task Persisted_contradictory_fills_and_partial_reads_never_prove_absence()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture { Position = 0, OrdersComplete = false };
        f.Store.Fills[0].PositionEffect = "flip";
        f.VenueOrders.Clear(); await database.Seed(f);
        await using (var first = database.Scope(f)) Assert.True((await first.Evaluate(f.Observation())).Incomplete);
        f.Advance(60);
        await using var second = database.Scope(f);
        Assert.True((await second.Evaluate(f.Observation())).Incomplete);
        Assert.Empty(await second.Service.ReadAsync(f.Account.Id, null, default));
        Assert.Empty((await EventReads.ReadAsync(second.Db, 0, 100, default)).Events);
    }
}

internal sealed class DiscrepancyDatabase(string admin, string schema, string connectionString) : IAsyncDisposable
{
    public string ConnectionString => connectionString;
    public DiscrepancyDatabaseScope Scope(DiscrepancyFixture fixture, Guid? owner = null) => new(connectionString, fixture, owner ?? fixture.Account.OwnerId);
    public async Task Seed(DiscrepancyFixture f)
    {
        await using var scope = Scope(f);
        scope.Db.Add(f.Account); scope.Db.AddRange(f.Store.Plays); scope.Db.AddRange(f.Store.Links);
        scope.Db.AddRange(f.Store.Orders); scope.Db.AddRange(f.Store.Fills);
        await scope.Db.SaveChangesAsync();
    }
    public static async Task<DiscrepancyDatabase> Create(string? targetMigration = null)
    {
        var admin = Environment.GetEnvironmentVariable("Vessel_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(admin))
            throw new InvalidOperationException("Set Vessel_TEST_POSTGRES to opt in to isolated-schema PostgreSQL tests.");
        var settings = new NpgsqlConnectionStringBuilder(admin);
        var schema = "vessel_discrepancy_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(admin); await connection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", connection)) await create.ExecuteNonQueryAsync();
        settings.SearchPath = schema; settings.Pooling = false;
        var database = new DiscrepancyDatabase(admin, schema, settings.ConnectionString);
        try
        {
            await using var scope = database.Scope(new());
            await scope.Db.GetService<IMigrator>().MigrateAsync(targetMigration);
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(admin); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection); await command.ExecuteNonQueryAsync();
    }
}

internal sealed class DiscrepancyDatabaseScope : IAsyncDisposable
{
    public VesselDbContext Db { get; }
    public UnitOfWork Work { get; }
    public VesselEventStore Events { get; }
    public MonitorDiscrepancyStore Store { get; }
    public MonitorDiscrepancyService Service { get; }
    public DiscrepancyDatabaseScope(string connection, DiscrepancyFixture f, Guid owner)
    {
        var capture = new VesselEventMutationCapture();
        Db = new(new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(connection)
            .AddInterceptors(capture).Options, new CoreOwner(owner));
        Events = new(Db, capture, new DiscrepancyOutbox(), Options.Create(new VesselEventSettings()), NullLogger<VesselEventStore>.Instance);
        Work = new(Db, [Events]); Store = new(Db, Work);
        Service = new(Store, Work, new CoreOwner(owner), Events, new(), f.Clock);
    }
    public Task<MonitorEvaluationResult> Evaluate(MonitorEvaluation evaluation) => Work.ExecuteAsync(async () =>
    {
        await Store.LockAsync(evaluation.Account.Id, default);
        return await Service.EvaluateAsync(evaluation, default);
    }, default);
    public ValueTask DisposeAsync() => Db.DisposeAsync();
    private sealed class DiscrepancyOutbox : IVesselEventOutbox
    {
        public Task StageAsync(VesselEvent message, CancellationToken ct) => Task.CompletedTask;
        public Task StageAsync(MonitorAccountCommand command, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public void Reset() { }
    }
}
