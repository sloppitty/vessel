using Vessel.Application.Venues;

namespace Vessel.Tests;

internal sealed class SecurityVerifier : IVenueCredentialVerifier
{
    public string VenueId => SecurityFixture.Venue.Id;
    public Exception? Error { get; set; }
    public string? ReceivedToken { get; private set; }
    public DateTimeOffset Expiry { get; set; } = SecurityFixture.Now.AddDays(60);
    public Task<VerifiedVenueCredential> VerifyAsync(string sourceId, string token, CancellationToken cancellationToken)
    {
        ReceivedToken = token;
        return Error is { } error ? Task.FromException<VerifiedVenueCredential>(error) : Task.FromResult(new VerifiedVenueCredential("single", Expiry));
    }
}
