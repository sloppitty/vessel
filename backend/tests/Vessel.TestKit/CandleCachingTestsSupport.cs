using Vessel.Application.MarketData;
using Vessel.Domain.Accounts;

namespace Vessel.Tests;

public partial class CandleCachingTestsSupport
{

    internal sealed class Reader : ICandleReader
    {
        public string VenueId => "hyperliquid";
        public int Reads;
        public string? Native;
        public CancellationToken LoadToken;
        public Func<CancellationToken, Task>? Wait;
        public Exception? Failure;
        public bool Empty;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<VenueCandle>> ReadCandlesAsync(string contractId, string interval,
            long fromMs, long toMs, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            Native = contractId;
            LoadToken = ct;
            Started.TrySetResult();
            if (Wait is not null) await Wait(ct);
            if (Failure is not null) throw Failure;
            return Empty ? [] : [new(toMs, toMs, "1.0000", "2", "0", "1", "10.00", null)];
        }
    }

    internal static (Guid Owner, MemoryWorkspaceStore Store, Account Account) Setup()
    {
        var owner = Guid.NewGuid();
        var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Test");
        store.Accounts.Add(account);
        return (owner, store, account);
    }
}
