using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Messaging;
using Vessel.Messaging.Modules.Verification;
using Vessel.Persistence.Events;
using Vessel.Persistence.Events.Subscriptions;

namespace Vessel.Tests;

[Collection("Monitor transport")]
public sealed class MultiOwnerMessagingTests
{
    [VesselEventPostgresFact]
    public async Task Provisioned_listener_dispatches_new_owners_ignores_unknown_and_skips_suspension_without_receipts()
    {
        await using var scope = await DeliveryScope.CreateAsync();
        var owners = new MutableOwnerDirectory();
        owners.Set(scope.Owner, JournalOwnerStatus.Active);
        var secondOwner = Guid.NewGuid();
        owners.Set(secondOwner, JournalOwnerStatus.Active);
        scope.Owners = owners;
        var provision = await scope.ProvisionAsync();
        Assert.Contains(VerificationModule.SubscriptionName, provision.Registered);
        Assert.Equal(0, await scope.CountAsync("SELECT count(*) FROM event_subscriptions"));
        var api = await scope.StartAsync(MessagingHosting.ApiDeployment);
        var worker = await scope.StartAsync(VerificationModule.ModuleName, VerificationModule.ModuleName);
        var first = await scope.AppendAsync(api, Draft("first"));
        var second = await scope.AppendAsync(api, Draft("second"), secondOwner);
        await scope.EventuallyAsync(async () => await scope.ReceiptAsync("verification", first.Id) == ("delivered", 1) &&
            await scope.ReceiptAsync("verification", second.Id) == ("delivered", 1));
        Assert.Equal(2, await scope.CountAsync("SELECT count(*) FROM event_subscriptions"));
        var unknown = second with { Id = Guid.CreateVersion7(), OwnerId = Guid.NewGuid() };
        await scope.NotifyAsync(VesselNotifications.Events, VesselNotifications.EventPayload(unknown));
        owners.Set(secondOwner, JournalOwnerStatus.Suspended);
        var skipped = await scope.AppendAsync(api, Draft("suspended"), secondOwner);
        // A later delivery is a barrier proving the listener passed the unknown and suspended notifications.
        var barrier = await scope.AppendAsync(api, Draft("barrier"));
        await scope.EventuallyAsync(async () => await scope.ReceiptAsync("verification", barrier.Id) == ("delivered", 1));
        await worker.StopAsync();
        Assert.Null(await scope.ReceiptAsync("verification", skipped.Id));
        Assert.Null(await scope.ReceiptAsync("verification", unknown.Id));
        Assert.Equal(0, await scope.CountAsync($"SELECT count(*) FROM event_subscriptions WHERE owner_id='{unknown.OwnerId}'"));
        owners.Set(secondOwner, JournalOwnerStatus.Active);
        await scope.ExecuteAsync("UPDATE events SET detected_at=detected_at-interval '1 minute'");
        await using (var owned = worker.Services.GetRequiredService<IOwnerScopeFactory>().Create(secondOwner))
            await owned.Services.GetRequiredService<EventCatchUp>().RunAsync(
                new("verification", "verification", ["#"]), default);
        Assert.Equal(("delivered", 1), await scope.ReceiptAsync("verification", skipped.Id));
        Assert.True(await MessagingProvisioning.DeregisterAsync(scope.Configuration, "verification", default));
        foreach (var table in new[] { "event_subscription_registrations", "event_subscriptions", "event_receipts" })
            Assert.Equal(0, await scope.CountAsync("SELECT count(*) FROM " + table));
    }

    private static VesselEventDraft Draft(string key) => new("monitor.state-changed", Guid.CreateVersion7(), key,
        "info", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new(), new("owner"),
        JsonSerializer.SerializeToElement(new { current = "live" }));
}
