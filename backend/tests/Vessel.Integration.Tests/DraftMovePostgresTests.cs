using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class DraftMovePostgresTests
{
    private static PlayService Service(VesselDbContext db) =>
        new(new PlayStore(db, TestUnitOfWork.For(db)), new MemoryObjectStore(), new ManualTime(DateTimeOffset.UnixEpoch), TestUnitOfWork.For(db));

    private static UpdatePlayRequest Update(PlayDto play, Guid accountId, string? title = null, string? review = null) =>
        new(play.Summary.Version, accountId, play.Summary.Instrument, play.Summary.InstrumentSource,
            title ?? play.Summary.Title, play.Plan, play.Drawings, review ?? play.Review,
            StructuredReview: play.StructuredReview);

    private static async Task<(PlayDto First, PlayDto Second)> Seed(CoreDatabase database, Guid owner)
    {
        await using var db = database.Context(owner);
        var first = new Account(Guid.NewGuid(), owner, "manual", "First");
        var second = new Account(Guid.NewGuid(), owner, "manual", "Second");
        db.Accounts.AddRange(first, second);
        await db.SaveChangesAsync();
        var service = Service(db);
        return (await service.CreateAsync(new(first.Id, "BTC", "manual", "First draft", TestPlays.Plan(),
                Review: "First review"), default),
            await service.CreateAsync(new(second.Id, "ETH", "manual", "Second draft", TestPlays.Plan(),
                Review: "Second review"), default));
    }

    [PostgresFact]
    public async Task Opposite_moves_serialize_before_source_locks_without_deadlocking_or_losing_history()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var (first, second) = await Seed(database, owner);
        var gate = new BeforePlayLock();
        await using var movingFirst = Context(database, owner, gate);
        await using var movingSecond = database.Context(owner);
        await movingSecond.Database.OpenConnectionAsync();
        var firstMove = Service(movingFirst).UpdateAsync(first.Summary.Id, Update(first, second.Summary.AccountId), default);
        Task<PlayDto>? secondMove = null;
        string? waitEvent = null;
        try
        {
            // The first move holds its account locks but has not yet locked or updated the Play.
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            secondMove = Service(movingSecond).UpdateAsync(second.Summary.Id, Update(second, first.Summary.AccountId), default);
            waitEvent = await WaitForLock(database, movingSecond);
        }
        finally { gate.Release.TrySetResult(); }
        var results = await Task.WhenAll(firstMove, secondMove!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("advisory", waitEvent);
        Assert.Equal(second.Summary.AccountId, results[0].Summary.AccountId);
        Assert.Equal(first.Summary.AccountId, results[1].Summary.AccountId);
        for (var i = 0; i < results.Length; i++)
        {
            var original = i == 0 ? first : second;
            Assert.Equal("draft", results[i].Summary.Status);
            Assert.Equal(original.Summary.Version + 1, results[i].Summary.Version);
            Assert.Equal(original.Review, results[i].Review);
            Assert.Equal(original.StructuredReview, results[i].StructuredReview);
            Assert.Equal(original.Summary.ReviewedAtUtc, results[i].Summary.ReviewedAtUtc);
        }
        await using var verify = database.Context(owner);
        Assert.Equal(2, await verify.ReviewRevisions.CountAsync());
        Assert.Empty(await verify.PlanRevisions.ToListAsync());
        Assert.Empty(await verify.StatusChanges.ToListAsync());
    }

    [PostgresFact]
    public async Task Foreign_or_missing_destination_and_foreign_play_are_rejected_without_writes()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var (first, _) = await Seed(database, owner);
        var other = Guid.NewGuid();
        var foreign = new Account(Guid.NewGuid(), other, "manual", "Foreign");
        await using (var db = database.Context(other))
        {
            db.Accounts.Add(foreign);
            await db.SaveChangesAsync();
        }
        foreach (var target in new[] { foreign.Id, Guid.NewGuid() })
        {
            await using var db = database.Context(owner);
            var error = await Assert.ThrowsAsync<ProblemException>(() => Service(db).UpdateAsync(first.Summary.Id,
                Update(first, target, "Rejected title", "Rejected review"), default));
            Assert.Equal(404, error.StatusCode);
        }
        await using (var db = database.Context(other))
            Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => Service(db).UpdateAsync(first.Summary.Id,
                Update(first, foreign.Id), default))).StatusCode);
        await AssertUnchanged(database, owner, first);
    }

    [PostgresFact]
    public async Task Disabled_destination_is_reloaded_even_when_the_context_tracks_its_enabled_state()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var (first, second) = await Seed(database, owner);
        await using var moving = database.Context(owner);
        Assert.True((await moving.Accounts.SingleAsync(a => a.Id == second.Summary.AccountId)).IsEnabled);
        await using (var disabling = database.Context(owner))
        {
            var account = await disabling.Accounts.SingleAsync(a => a.Id == second.Summary.AccountId);
            account.UpdateSettings(account.Name, account.PortfolioId, false);
            await disabling.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<ProblemException>(() => Service(moving).UpdateAsync(first.Summary.Id,
            Update(first, second.Summary.AccountId, "Rejected title", "Rejected review"), default));
        Assert.Equal(409, error.StatusCode);
        await AssertUnchanged(database, owner, first);
    }

    [PostgresFact]
    public async Task Concurrent_disable_commits_before_destination_validation()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var (first, second) = await Seed(database, owner);
        await using var disabling = database.Context(owner);
        await using var moving = database.Context(owner);
        // Retain a stale tracked target to prove validation uses its locked, committed state.
        await moving.Accounts.SingleAsync(a => a.Id == second.Summary.AccountId);
        await moving.Database.OpenConnectionAsync();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspace = new WorkspaceStore(disabling, TestUnitOfWork.For(disabling));
        var disable = TestUnitOfWork.For(disabling).ExecuteAsync(() => workspace.WithManagementLockAsync(() => workspace.WithAccountLockAsync(second.Summary.AccountId, async account =>
        {
            reached.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            account.UpdateSettings(account.Name, account.PortfolioId, false);
            await workspace.SaveAsync(default);
            return true;
        }, default), default), default);
        Task<PlayDto>? move = null;
        string? waitEvent = null;
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            move = Service(moving).UpdateAsync(first.Summary.Id, Update(first, second.Summary.AccountId), default);
            waitEvent = await WaitForLock(database, moving);
        }
        finally { release.TrySetResult(); }
        await disable.WaitAsync(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<ProblemException>(() => move!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal("advisory", waitEvent);
        await AssertUnchanged(database, owner, first);
    }

    [PostgresFact]
    public async Task A_second_move_with_a_stale_source_and_version_returns_conflict()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var (first, second) = await Seed(database, owner);
        var gate = new BeforePlayLock();
        await using var winner = Context(database, owner, gate);
        await using var stale = database.Context(owner);
        // The lock must also reload this previously tracked Play.
        await stale.Plays.SingleAsync(p => p.Id == first.Summary.Id);
        await stale.Database.OpenConnectionAsync();
        var winning = Service(winner).UpdateAsync(first.Summary.Id, Update(first, second.Summary.AccountId, "Winner"), default);
        Task<PlayDto>? losing = null;
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            losing = Service(stale).UpdateAsync(first.Summary.Id, Update(first, second.Summary.AccountId, "Loser"), default);
            await WaitForLock(database, stale);
        }
        finally { gate.Release.TrySetResult(); }
        var saved = await winning.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => losing!.WaitAsync(TimeSpan.FromSeconds(10)))).StatusCode);
        await AssertUnchanged(database, owner, saved);
    }

    [PostgresFact]
    public async Task Nonmove_updates_keep_account_locking_and_reject_a_stale_version()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var (first, _) = await Seed(database, owner);
        var gate = new BeforePlayLock();
        await using var winner = Context(database, owner, gate);
        await using var stale = database.Context(owner);
        await stale.Plays.SingleAsync(p => p.Id == first.Summary.Id);
        await stale.Database.OpenConnectionAsync();
        var winning = Service(winner).UpdateAsync(first.Summary.Id, Update(first, first.Summary.AccountId, "Winner"), default);
        Task<PlayDto>? losing = null;
        string? waitEvent = null;
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            losing = Service(stale).UpdateAsync(first.Summary.Id, Update(first, first.Summary.AccountId, "Loser"), default);
            waitEvent = await WaitForLock(database, stale);
        }
        finally { gate.Release.TrySetResult(); }
        var saved = await winning.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual("advisory", waitEvent);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => losing!.WaitAsync(TimeSpan.FromSeconds(10)))).StatusCode);
        await AssertUnchanged(database, owner, saved);
    }

    [PostgresFact]
    public async Task A_stale_move_cannot_overwrite_annotations_or_append_history()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var (first, second) = await Seed(database, owner);
        PlayDto saved;
        await using (var editing = database.Context(owner))
            saved = await Service(editing).UpdateAsync(first.Summary.Id, Update(first, first.Summary.AccountId, "Winner"), default);
        await using var moving = database.Context(owner);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => Service(moving).UpdateAsync(first.Summary.Id,
            Update(first, second.Summary.AccountId, "Loser", "Rejected review"), default))).StatusCode);
        await AssertUnchanged(database, owner, saved);
    }

    [PostgresFact]
    public async Task Move_participates_in_outer_unit_of_work_and_rolls_back_account_annotations_and_history_together()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var (first, second) = await Seed(database, owner);
        await using var moving = database.Context(owner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => TestUnitOfWork.For(moving).ExecuteAsync<bool>(async () =>
        {
            var result = await Service(moving).UpdateAsync(first.Summary.Id,
                Update(first, second.Summary.AccountId, "Tentative", "Tentative review"), default);
            Assert.Equal(second.Summary.AccountId, result.Summary.AccountId);
            Assert.Equal("Tentative review", result.Review);
            // The nested update reached the database inside the outer transaction, not only the change tracker.
            var written = await moving.Plays.AsNoTracking().SingleAsync(p => p.Id == first.Summary.Id);
            Assert.Equal((second.Summary.AccountId, "Tentative", result.Summary.Version), (written.AccountId, written.Title, written.Version));
            Assert.Equal(3, await moving.ReviewRevisions.CountAsync());
            await AssertUnchanged(database, owner, first);
            throw new InvalidOperationException("Roll back the outer operation.");
        }, default));
        Assert.Empty(moving.ChangeTracker.Entries());
        await AssertUnchanged(database, owner, first);
    }

    private static async Task AssertUnchanged(CoreDatabase database, Guid owner, PlayDto expected)
    {
        await using var db = database.Context(owner);
        var actual = await Service(db).GetAsync(expected.Summary.Id, default);
        Assert.Equal(expected.Summary, actual.Summary);
        Assert.Equal(JsonSerializer.Serialize(expected.Plan), JsonSerializer.Serialize(actual.Plan));
        Assert.Equal(expected.Drawings.GetRawText(), actual.Drawings.GetRawText());
        Assert.Equal(expected.Review, actual.Review);
        Assert.Equal(expected.StructuredReview, actual.StructuredReview);
        Assert.Equal(2, await db.ReviewRevisions.CountAsync());
        Assert.Empty(await db.PlanRevisions.ToListAsync());
        Assert.Empty(await db.StatusChanges.ToListAsync());
    }

    private static VesselDbContext Context(CoreDatabase database, Guid owner, BeforePlayLock gate) =>
        new(new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(gate).Options,
            new CoreOwner(owner));

    private static async Task<string> WaitForLock(CoreDatabase database, VesselDbContext waiting)
    {
        var pid = ((NpgsqlConnection)waiting.Database.GetDbConnection()).ProcessID;
        await using var inspect = new NpgsqlConnection(database.ConnectionString);
        await inspect.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var command = new NpgsqlCommand(
                "SELECT wait_event FROM pg_stat_activity WHERE pid = @pid AND wait_event_type = 'Lock'", inspect);
            command.Parameters.AddWithValue("pid", pid);
            if (await command.ExecuteScalarAsync(timeout.Token) is string waitEvent) return waitEvent;
            await Task.Delay(10, timeout.Token);
        }
    }

    // Gate after account/execution locks, immediately before the Play row lock. No timing-dependent overlap.
    private sealed class BeforePlayLock : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR NO KEY UPDATE", StringComparison.Ordinal))
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }
}
