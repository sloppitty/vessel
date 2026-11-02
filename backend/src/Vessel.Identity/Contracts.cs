using System.Text.Json.Serialization;

namespace Vessel.Identity;

public sealed record ChallengeRequest([property: JsonRequired] string? Address);
public sealed record VerifyRequest([property: JsonRequired] Guid ChallengeId, [property: JsonRequired] string? Signature,
    [property: JsonRequired] bool Remember);
public sealed record RegisterRequest([property: JsonRequired] string? Registration, [property: JsonRequired] string? DisplayName,
    string? Invitation, [property: JsonRequired] bool Remember);
public sealed record SignOutRequest([property: JsonRequired] Guid ExpectedSessionId);
public sealed record ChallengeDto(Guid ChallengeId, string Message, DateTimeOffset ExpiresAt);
public sealed record UserDto(Guid Id, string DisplayName, string Role, int Revision);
public sealed record WalletDto(Guid Id, string Address, string? Label, DateTimeOffset AddedAt, DateTimeOffset? LastUsedAt, bool Current);
public sealed record SessionDetails(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset IdleExpiresAt, DateTimeOffset AbsoluteExpiresAt, bool Remember);
public sealed record SessionDto(UserDto User, WalletDto Wallet, SessionDetails Session, bool AdoptsLegacyPreferences);
public sealed record IssuedSession(string Token, SessionDto Session);
public sealed record VerificationResult(string Status, IssuedSession? SignedIn = null, string? Registration = null,
    bool? RequiresInvitation = null, DateTimeOffset? ExpiresAt = null);
public sealed record AuthenticatedSession(IdentitySession Session, IdentityUser User, UserWallet Wallet)
{
    public SessionDto ToDto(IdentitySettings settings) => new(
        new(User.Id, User.DisplayName, User.Role, User.Revision),
        new(Wallet.Id, WalletSignatures.Checksum(Wallet.Address), Wallet.Label, Wallet.AddedAt, Wallet.LastUsedAt, true),
        new(Session.Id, Session.CreatedAt, Session.IdleExpiresAt, Session.AbsoluteExpiresAt, Session.Remember),
        User.Id == settings.OwnerId);
}

public sealed record ProfileRequest([property: JsonRequired] string? DisplayName, [property: JsonRequired] int ExpectedRevision);
public sealed record WalletLabelRequest([property: JsonRequired] string? Label);
public sealed record StepUpChallengeRequest([property: JsonRequired] string? Action, [property: JsonRequired] string? Address, Guid? Target = null);
public sealed record StepUpRequest([property: JsonRequired] Guid ChallengeId, [property: JsonRequired] string? Signature);
public sealed record LinkWalletRequest([property: JsonRequired] Guid ChallengeId, [property: JsonRequired] string? Signature, string? Label = null);
public sealed record SessionRowDto(Guid Id, bool Current, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt,
    bool Remember, string? UserAgent, string WalletAddress, string Source);
public sealed record ClaimPairingRequest(string? Secret = null, string? TypedCode = null);
public sealed record ApprovePairingRequest([property: JsonRequired] string? Code);
public sealed record CompletePairingRequest([property: JsonRequired] bool Remember);
public sealed record PairingCreatedDto(Guid Id, string Secret, string TypedCode, DateTimeOffset ExpiresAt);
public sealed record PairingStatusDto(string Status, DateTimeOffset ExpiresAt, string? Device);
public sealed record PairingJourneyDto(string DisplayName, string Wallet);
public sealed record PairingClaimedDto(string Code, DateTimeOffset ExpiresAt, PairingJourneyDto Journal);
public sealed record PairingCompletionResult(string Status, IssuedSession? SignedIn = null);
public sealed record IdentityVerificationUser(Guid Id, bool HasWallet);
