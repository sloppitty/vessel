
namespace Vessel.Tests;

public sealed class UnitOfWorkUseCasesTests : UnitOfWorkTestsSupport
{

    // A5: the database-free double enforces the same participation rules as UnitOfWork.
    [Fact]
    public async Task Memory_unit_of_work_rejects_commit_after_a_caught_nested_failure()
    {
        var saves = 0;
        var work = TestUnitOfWork.Saving(_ => { saves++; return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(async () =>
        {
            try { await work.ExecuteAsync<bool>(() => throw new InvalidOperationException("participant"), default); }
            catch (InvalidOperationException) { }
            return true;
        }, default));
        Assert.Equal(0, saves);
        await work.ExecuteAsync(() => Task.FromResult(true), default);
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task Memory_unit_of_work_requires_an_active_unit_and_flushes_nested_units()
    {
        var saves = 0;
        var work = TestUnitOfWork.Saving(_ => { saves++; return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.SaveChangesAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => TestUnitOfWork.None.SaveChangesAsync(default));
        var seen = await work.ExecuteAsync(async () =>
        {
            await work.ExecuteAsync(() => Task.FromResult(true), default);
            return saves;
        }, default);
        Assert.Equal((1, 2), (seen, saves));
        var failing = TestUnitOfWork.Saving(_ => throw new IOException("Save failed."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ExecuteAsync(async () =>
        {
            try { await failing.SaveChangesAsync(default); }
            catch (IOException) { }
            return true;
        }, default));
    }

    [Fact]
    public async Task Memory_unit_of_work_matches_cancelled_participant_rules()
    {
        var saves = 0;
        await CancelledParticipantsFail(TestUnitOfWork.Saving(_ => { saves++; return Task.CompletedTask; }), () => { });
        Assert.Equal(0, saves);
    }
}
