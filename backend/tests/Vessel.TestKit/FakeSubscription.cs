using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Vessel.Application.MarketData;

namespace Vessel.Tests;

internal sealed class FakeSubscription(string contractId, string interval) : IMarketStreamSubscription
{
    private readonly Channel<MarketStreamEvent> events = Channel.CreateUnbounded<MarketStreamEvent>();
    public string ContractId { get; } = contractId;
    public string Interval { get; } = interval;
    public volatile bool Disposed;

    public void Write(MarketStreamEvent value) => events.Writer.TryWrite(value);
    public void Complete() => events.Writer.TryComplete();

    public async IAsyncEnumerable<MarketStreamEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var value in events.Reader.ReadAllAsync(cancellationToken))
            yield return value;
    }

    public void Dispose() { Disposed = true; events.Writer.TryComplete(); }
}
