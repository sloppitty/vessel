using Vessel.Application.Venues;

namespace Vessel.Tests;

internal sealed class SecurityDiscovery(IReadOnlyList<VenueAccountCandidate> candidates) : IVenueAccountDiscovery
{
    public string VenueId => SecurityFixture.Venue.Id;
    public string? ReceivedToken { get; private set; }
    public Exception? Error { get; init; }
    public Task<IReadOnlyList<VenueAccountCandidate>> DiscoverAsync(string address, CancellationToken cancellationToken) => Task.FromResult(candidates);
    public Task<IReadOnlyList<VenueAccountCandidate>> DiscoverAsync(string address, string token, CancellationToken cancellationToken)
    {
        ReceivedToken = token;
        return Error is { } error ? Task.FromException<IReadOnlyList<VenueAccountCandidate>>(error) : Task.FromResult(candidates);
    }
}
