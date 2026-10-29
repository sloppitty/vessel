
namespace Vessel.Tests;

internal static class Eventually
{
    public static async Task True(Func<bool> condition, string because, Action? step = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, because);
            step?.Invoke();
            await Task.Delay(5);
        }
    }

    public static async Task<T> Next<T>(IAsyncEnumerator<T> enumerator)
    {
        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)), "The stream ended early.");
        return enumerator.Current;
    }
}
