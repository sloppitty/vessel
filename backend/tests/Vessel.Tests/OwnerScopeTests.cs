using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vessel.Application.Ownership;
using Vessel.Infrastructure;
using Vessel.Infrastructure.Auth;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class OwnerScopeTests
{
    [Fact]
    public async Task Factory_assigns_fresh_scopes_without_consulting_the_directory_or_mutating_the_request()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddLogging();
        services.AddVesselInfrastructure(configuration);
        services.AddVesselPersistence(configuration);
        services.Replace(ServiceDescriptor.Singleton<IJournalOwnerDirectory>(new NoDirectory()));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var request = provider.CreateAsyncScope();
        var owner = Guid.NewGuid();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = TestAuthentication.Principal(owner, "Owner")
        };
        var factory = provider.GetRequiredService<IOwnerScopeFactory>();
        Assert.Throws<ArgumentException>(() => factory.Create(Guid.Empty));
        await using var first = factory.Create(owner);
        await using var second = factory.Create(owner);
        Assert.Equal(owner, first.OwnerId);
        Assert.Equal(owner, first.Services.GetRequiredService<IJournalOwnerContext>().OwnerId);
        Assert.NotSame(first.Services.GetRequiredService<OwnerScopeAssignment>(),
            second.Services.GetRequiredService<OwnerScopeAssignment>());
        Assert.Null(request.ServiceProvider.GetRequiredService<OwnerScopeAssignment>().OwnerId);
        await using var mismatch = factory.Create(Guid.NewGuid());
        Assert.Throws<UnauthorizedAccessException>(() => mismatch.Services.GetRequiredService<IJournalOwnerContext>().OwnerId);
        accessor.HttpContext = null;
        Assert.Equal(first.OwnerId, first.Services.GetRequiredService<IJournalOwnerContext>().OwnerId);
        Assert.Throws<UnauthorizedAccessException>(() => request.ServiceProvider.GetRequiredService<IJournalOwnerContext>().OwnerId);
        Assert.Throws<InvalidOperationException>(() => first.Services.GetRequiredService<OwnerScopeAssignment>().Assign(owner));
    }

    [Fact]
    public async Task Non_http_context_requires_an_assignment_and_the_directory_registration_is_order_independent()
    {
        var assignment = new OwnerScopeAssignment();
        var context = new AssignedOwnerContext(assignment);
        Assert.Throws<UnauthorizedAccessException>(() => context.OwnerId);
        var owner = Guid.NewGuid();
        assignment.Assign(owner);
        Assert.Equal(owner, context.OwnerId);
        foreach (var directoryFirst in new[] { true, false })
        {
            var services = new ServiceCollection();
            var custom = new ConfiguredOwnerDirectory(owner);
            if (directoryFirst) services.AddSingleton<IJournalOwnerDirectory>(custom);
            services.AddVesselInfrastructure(new ConfigurationBuilder().Build());
            if (!directoryFirst) services.Replace(ServiceDescriptor.Singleton<IJournalOwnerDirectory>(custom));
            await using var provider = services.BuildServiceProvider();
            Assert.Same(custom, provider.GetRequiredService<IJournalOwnerDirectory>());
        }
    }

    private sealed class NoDirectory : IJournalOwnerDirectory
    {
        public Task<JournalOwnerStatus> StatusAsync(Guid ownerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> ActiveOwnersAsync(Guid? after, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
