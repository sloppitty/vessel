using Vessel.Application.Common;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Infrastructure.Venues.Pacifica;
using Vessel.Infrastructure.Venues;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;

namespace Vessel.Tests;

public sealed class PacificaIdentityTests
{
    [Theory]
    [InlineData("11111111111111111111111111111111", true)]
    [InlineData(PacificaAdapterTests.Source, true)]
    [InlineData("BrZp5bidJ3WUvceSq7X78bhjTfZXeezzGvGEV4hAYKTa", true)]
    [InlineData("11111111111111111111111111111112", true)]
    [InlineData("1111111111111111111111111111111", false)]
    [InlineData("111111111111111111111111111111111", false)]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", false)]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", false)]
    [InlineData("0x1111111111111111111111111111111111111111", false)]
    [InlineData("SO11111111111111111111111111111111111111112", false)]
    [InlineData("So11111111111111111111111111111111111111112\n", false)]
    [InlineData(null, false)]
    public void Decodes_exactly_32_bytes_without_a_solana_dependency(string? value, bool expected) =>
        Assert.Equal(expected, AccountSource.IsSolanaAddress(value));

    internal static VenueRegistry Registry() => new([VenueDescriptor.Manual, PacificaReader.Descriptor], [], [], [], [], [], [], []);

    [Fact]
    public async Task Creation_preserves_case_as_source_only_and_rejects_legacy_address_input()
    {
        var owner = Guid.NewGuid();
        var store = new MemoryWorkspaceStore(owner);
        var service = new WorkspaceService(store, new CoreOwner(owner), Registry(), TestUnitOfWork.None);
        var created = await service.CreateAccountAsync(new(null, "Solana", PacificaReader.Id, SourceId: PacificaAdapterTests.Source), default);
        Assert.Null(created.Address); Assert.Equal(PacificaAdapterTests.Source, created.SourceId);
        var invalid = await Assert.ThrowsAsync<ProblemException>(() => service.CreateAccountAsync(
            new(null, "Bad", PacificaReader.Id, Address: PacificaAdapterTests.Source), default));
        Assert.Equal(400, invalid.StatusCode);
        Assert.False(PacificaReader.Descriptor.Capabilities.PaperExecution);
        Assert.False(PacificaReader.Descriptor.Capabilities.ReadOnlyCredential);
        Assert.False(PacificaReader.Descriptor.Capabilities.AccountDiscovery);
    }

    [Fact]
    public async Task Composed_api_advertises_only_implemented_ports_and_preserves_source_in_the_creation_response()
    {
        var owner = Guid.NewGuid();
        await using var factory = new CoreApiFactory(owner, new MemoryWorkspaceStore(owner),
            settings: new() { ["Vessel:Venues:Pacifica:BaseUrl"] = "https://pacifica.example.test/" });
        using var client = factory.AuthorizedClient();
        using var response = await client.PostAsJsonAsync("/api/accounts",
            new CreateAccountRequest(null, "Solana", PacificaReader.Id, SourceId: PacificaAdapterTests.Source));
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.Null(created.Address); Assert.Equal(PacificaAdapterTests.Source, created.SourceId);
        using var scope = factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IVenueRegistry>();
        Assert.IsType<PacificaReader>(registry.Reader(PacificaReader.Id));
        Assert.IsType<PacificaReader>(registry.Orders(PacificaReader.Id));
        Assert.IsType<PacificaReader>(registry.Candles(PacificaReader.Id));
        Assert.Null(registry.MarketContext(PacificaReader.Id)); Assert.Null(registry.Stream(PacificaReader.Id));
        Assert.Null(registry.Discovery(PacificaReader.Id)); Assert.Null(registry.CredentialVerifier(PacificaReader.Id));
        Assert.Null(registry.Funding(PacificaReader.Id));
        Assert.Equal("https://pacifica.example.test/",
            factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(PacificaReader.Id).BaseAddress!.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://pacifica.example.test/")]
    [InlineData("https://pacifica.example.test/api/v1/")]
    [InlineData("https://pacifica.example.test/?private=not-a-real-secret")]
    public void Configured_host_validation_rejects_unsafe_origins_without_echoing_values(string url)
    {
        var options = new VenueHostOptions { Pacifica = new() { BaseUrl = url } };
        var failure = new VenueHostValidation().Validate(null, options);
        Assert.True(failure.Failed); Assert.Contains("Pacifica:BaseUrl", failure.FailureMessage);
        Assert.DoesNotContain(url, failure.FailureMessage);
    }
}
