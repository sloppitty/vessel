using System.Collections.Concurrent;
using Vessel.Application.MarketData;

namespace Vessel.Tests;

/// <summary>Application-level fake port: tests write events into each subscription.</summary>
internal sealed class FakeMarketStream : IMarketStream
{
    public string VenueId { get; init; } = "hyperliquid";
    public Exception? Failure { get; set; }
    public ConcurrentQueue<FakeSubscription> Subscriptions { get; } = new();
    public FakeSubscription Latest => Subscriptions.Last();

    public IMarketStreamSubscription Subscribe(string contractId, string interval)
    {
        if (Failure is not null) throw Failure;
        var subscription = new FakeSubscription(contractId, interval);
        Subscriptions.Enqueue(subscription);
        return subscription;
    }
}
