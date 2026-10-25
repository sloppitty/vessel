using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vessel.Application.Ownership;
using Vessel.Identity;
using Vessel.Infrastructure;
using Vessel.Worker;

namespace Vessel.Tests;

public sealed class OwnerDirectoryIntegrationTests
{
    [Fact]
    public async Task Api_composition_resolves_the_identity_directory_and_explicit_owner_scope()
    {
        var owner = Guid.NewGuid();
        await using var factory = new CoreApiFactory(owner, settings: Settings(owner), testAuthentication: false);
        AssertDirectory(factory.Services);
        await AssertOwnerScopeAsync(factory.Services, owner);
    }

    [Fact]
    public async Task Worker_composition_resolves_the_identity_directory_and_explicit_owner_scope()
    {
        var owner = Guid.NewGuid();
        var builder = Builder(owner);
        WorkerHost.Configure(builder);
        // Resolve the real composition without starting database/broker background services.
        using var host = builder.Build();
        AssertDirectory(host.Services);
        await AssertOwnerScopeAsync(host.Services, owner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Directory_selection_is_independent_of_identity_infrastructure_registration_order(bool identityFirst)
    {
        var builder = Builder(Guid.NewGuid());
        if (identityFirst)
        {
            builder.Services.AddVesselIdentityDirectory(builder.Configuration);
            builder.Services.AddVesselInfrastructure(builder.Configuration);
        }
        else
        {
            builder.Services.AddVesselInfrastructure(builder.Configuration);
            builder.Services.AddVesselIdentityDirectory(builder.Configuration);
        }
        using var host = builder.Build();
        AssertDirectory(host.Services);
    }

    private static void AssertDirectory(IServiceProvider services)
    {
        var directory = services.GetRequiredService<IJournalOwnerDirectory>();
        Assert.Same(directory, services.GetRequiredService<IJournalOwnerDirectory>());
        Assert.Same(directory, Assert.Single(services.GetServices<IJournalOwnerDirectory>()));
        Assert.IsType<IdentityOwnerDirectory>(directory);
    }

    private static async Task AssertOwnerScopeAsync(IServiceProvider services, Guid owner)
    {
        using (var unassigned = services.CreateScope())
            Assert.Throws<UnauthorizedAccessException>(() =>
                unassigned.ServiceProvider.GetRequiredService<IJournalOwnerContext>().OwnerId);
        await using var assigned = services.GetRequiredService<IOwnerScopeFactory>().Create(owner);
        Assert.Equal(owner, assigned.Services.GetRequiredService<IJournalOwnerContext>().OwnerId);
        await using var other = services.GetRequiredService<IOwnerScopeFactory>().Create(Guid.NewGuid());
        Assert.Equal(other.OwnerId, other.Services.GetRequiredService<IJournalOwnerContext>().OwnerId);
        Assert.Equal(owner, assigned.Services.GetRequiredService<IJournalOwnerContext>().OwnerId);
    }

    private static HostApplicationBuilder Builder(Guid owner)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(Settings(owner));
        return builder;
    }

    private static Dictionary<string, string?> Settings(Guid owner) => new()
    {
        ["Vessel:Auth:OwnerId"] = owner.ToString(),
        ["Vessel:Identity:Origins:0"] = "https://vessel.test",
        ["Vessel:Messaging:RabbitMqUri"] = "amqp://unused:unused@127.0.0.1:1",
        ["ConnectionStrings:Vessel"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused"
    };
}
