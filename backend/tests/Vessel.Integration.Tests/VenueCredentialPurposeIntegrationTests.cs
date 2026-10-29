using Vessel.Application.Credentials;

namespace Vessel.Tests;

/// <summary>
/// The purpose is AES-GCM associated data: the shared helper must produce exactly the literal every existing
/// envelope was sealed with.
/// </summary>
public sealed class VenueCredentialPurposeIntegrationTests
{

    [Theory]
    [InlineData("lighter")]
    [InlineData("lighter-rh")]
    [InlineData("index-fixture")]
    public void Envelope_sealed_with_the_old_literal_opens_with_the_helper(string venueId)
    {
        var vault = SecurityFixture.Vault();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        var envelope = vault.Seal(owner, account, venueId + "-read-token", SecurityFixture.Token);
        Assert.Equal(SecurityFixture.Token, vault.Open(owner, account, VenueCredentialPurpose.ReadToken(venueId), envelope));
        // And the reverse: what the helper seals, the historical literal opens.
        var sealedNow = vault.Seal(owner, account, VenueCredentialPurpose.ReadToken(venueId), SecurityFixture.Token);
        Assert.Equal(SecurityFixture.Token, vault.Open(owner, account, venueId + "-read-token", sealedNow));
    }
}
