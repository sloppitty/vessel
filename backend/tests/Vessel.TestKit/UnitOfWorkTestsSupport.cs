using Vessel.Application.Ownership;

namespace Vessel.Tests;

public partial class UnitOfWorkTestsSupport
{

    internal static async Task CancelledParticipantsFail(IUnitOfWork work, Action write)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var ran = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(async () =>
        {
            write();
            try
            {
                await work.ExecuteAsync(() =>
                {
                    ran = true;
                    return Task.FromResult(true);
                }, cancelled.Token);
            }
            catch (OperationCanceledException) { }
            return true;
        }, default));
        Assert.False(ran);
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(async () =>
        {
            write();
            try { await work.SaveChangesAsync(cancelled.Token); }
            catch (OperationCanceledException) { }
            return true;
        }, default));
        Assert.False(work.IsActive);
    }
}
