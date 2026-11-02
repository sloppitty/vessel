using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Review;
using Vessel.Domain.Common;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 3: one active if-then rule per trading mode, acknowledged or excepted at Plan it, answered in the review, and
/// counted in Review with drill-down only.
/// </summary>
public sealed class IfThenRulePostgresTests
{

    private sealed class PauseActiveRead : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public readonly TaskCompletionSource Selected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData, System.Data.Common.DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM play_rules", StringComparison.Ordinal) && command.CommandText.Contains("IS NULL", StringComparison.Ordinal))
            {
                Selected.TrySetResult();
                await Resume.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }

    /// <summary>
    /// Sol round 1 on item 3: an archive and a replacement that both read version 1 as active cannot both succeed. The one
    /// that writes second finds version 1 already archived and gets a 409, in either order.
    /// </summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Overlapping_archive_and_replacement_of_one_version_let_only_the_first_succeed(bool staleIsArchive)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var first = database.Context(owner);
        var work = TestUnitOfWork.For(first);
        var service = new PlayService(new Vessel.Persistence.PlayStore(first, work), null!, TimeProvider.System, work);
        await service.SetRuleAsync("real", new("the stop is hit", "I wait a day"), default);
        var pause = new PauseActiveRead();
        await using var second = new Vessel.Persistence.VesselDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Vessel.Persistence.VesselDbContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(pause).Options, new CoreOwner(owner));
        var secondWork = TestUnitOfWork.For(second);
        var stale = new PlayService(new Vessel.Persistence.PlayStore(second, secondWork), null!, TimeProvider.System, secondWork);
        var late = staleIsArchive ? stale.ArchiveRuleAsync("real", 1, default)
            : stale.SetRuleAsync("real", new("the stop is hit twice", "I stop for the week", ExpectedVersion: 1), default);
        await pause.Selected.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (staleIsArchive) await service.SetRuleAsync("real", new("a new setup", "I size down", ExpectedVersion: 1), default);
        else await service.ArchiveRuleAsync("real", 1, default);
        pause.Resume.TrySetResult();
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => late)).StatusCode);
        var state = await service.RuleAsync("real", default);
        Assert.Equal(staleIsArchive ? 2 : null, state.Active?.Version);
    }
}
