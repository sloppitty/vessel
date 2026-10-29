using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Messaging;
using Vessel.Messaging.Modules.Verification;
using Vessel.Persistence.Events.Subscriptions;
using Wolverine;

namespace Vessel.Tests;

[Collection("Monitor transport")]
public sealed class MultiOwnerMessagingTests
{
    [VesselEventRabbitFact]
    public async Task Provisioned_listener_dispatches_new_owners_dead_letters_unknown_and_acks_suspension_without_receipts()
    {
        await using var broker = await BrokerScope.CreateAsync();
        var owners = new MutableOwnerDirectory();
        owners.Set(broker.Owner, JournalOwnerStatus.Active);
        var secondOwner = Guid.NewGuid();
        owners.Set(secondOwner, JournalOwnerStatus.Active);
        broker.Owners = owners;
        var provision = await broker.ProvisionAsync();
        Assert.Contains(VerificationModule.SubscriptionName, provision.Registered);
        Assert.Equal(0, await broker.CountAsync("SELECT count(*) FROM event_subscriptions"));
        var api = await broker.StartAsync(MessagingHosting.ApiDeployment);
        var worker = await broker.StartAsync(VerificationModule.ModuleName, VerificationModule.ModuleName);
        var first = await broker.AppendAsync(api, Draft("first"));
        var second = await broker.AppendAsync(api, Draft("second"), secondOwner);
        await broker.EventuallyAsync(async () => await broker.ReceiptAsync("verification", first.Id) == ("delivered", 1) &&
            await broker.ReceiptAsync("verification", second.Id) == ("delivered", 1));
        Assert.Equal(2, await broker.CountAsync("SELECT count(*) FROM event_subscriptions"));
        var bus = api.Services.GetRequiredService<IMessageBus>();
        var queue = broker.Topology.SubscriptionQueue("verification");
        var unknown = second with { Id = Guid.CreateVersion7(), OwnerId = Guid.NewGuid() };
        await bus.BroadcastToTopicAsync(unknown.Type, unknown);
        await broker.EventuallyAsync(async () => await broker.ReadyAsync(MessagingTopology.DeadLetterQueue(queue)) == 1);
        owners.Set(secondOwner, JournalOwnerStatus.Suspended);
        var skipped = await broker.AppendAsync(api, Draft("suspended"), secondOwner);
        // A later delivery is a barrier proving the inline consumer passed the suspended delivery.
        var barrier = await broker.AppendAsync(api, Draft("barrier"));
        await broker.EventuallyAsync(async () => await broker.ReceiptAsync("verification", barrier.Id) == ("delivered", 1));
        await worker.StopAsync();
        Assert.Null(await broker.ReceiptAsync("verification", skipped.Id));
        Assert.Equal(0u, await broker.ReadyAsync(queue));
        Assert.Equal(1u, await broker.ReadyAsync(MessagingTopology.DeadLetterQueue(queue)));
        owners.Set(secondOwner, JournalOwnerStatus.Active);
        await broker.ExecuteAsync("UPDATE events SET detected_at=detected_at-interval '1 minute'");
        await using (var scope = worker.Services.GetRequiredService<IOwnerScopeFactory>().Create(secondOwner))
            await scope.Services.GetRequiredService<EventCatchUp>().RunAsync(
                new("verification", "verification", ["#"]), default);
        Assert.Equal(("delivered", 1), await broker.ReceiptAsync("verification", skipped.Id));
        Assert.True(await MessagingProvisioning.DeregisterAsync(broker.Configuration, "verification", default));
        foreach (var table in new[] { "event_subscription_registrations", "event_subscriptions", "event_receipts" })
            Assert.Equal(0, await broker.CountAsync("SELECT count(*) FROM " + table));
    }

    private static VesselEventDraft Draft(string key) => new("monitor.state-changed", Guid.CreateVersion7(), key,
        "info", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new(), new("owner"),
        JsonSerializer.SerializeToElement(new { current = "live" }));
}
