using Vessel.Application.Monitoring;
using Vessel.Application.Common;
using Vessel.Application.Ownership;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Credentials;

namespace Vessel.Application.Credentials;

public sealed class AccountCredentialService(
    IWorkspaceStore accounts, IAccountCredentialStore credentials, IJournalOwnerContext owner,
    IVenueRegistry venues, ICredentialVault vault, TimeProvider time, IUnitOfWork unitOfWork, AccountReadPolicy reads)
{
    // Read-only: no row lock, so opening Manage account does not wait behind a running refresh.
    public async Task<AccountCredentialDto> GetAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.AccountAsync(id, ct) ?? throw new ProblemException(404, "Account not found.");
        Guard(account, requireEnabled: false);
        var credential = await credentials.FindAsync(id, Purpose(account), ct);
        return Metadata(account, credential);
    }

    public const string ChangedWhileVerifying = "The account or its credential changed while verifying; try again.";

    /// <summary>
    /// Read, verify, then a short locked recheck. The venue call runs with no unit of work, transaction or account lock
    /// open, so it never holds the account row (blocking refreshes and settings changes) for the length of venue HTTP.
    /// The save then requires the account and its stored credential to be exactly as they were when verification
    /// started; otherwise it refuses with 409 and the owner retries. It never re-verifies by itself.
    /// </summary>
    public async Task<AccountCredentialDto> PutAsync(Guid id, SaveAccountCredentialRequest request, CancellationToken ct)
    {
        // 1. Owner-scoped read with every validation, outside any unit.
        var account = await accounts.AccountAsync(id, ct) ?? throw new ProblemException(404, "Account not found.");
        var token = Validate(account, request);
        // Copy now: the locked reload below refreshes this same tracked instance.
        var (revision, sourceId, venueId) = (account.SettingsRevision, account.SourceId!, account.VenueId);
        var purpose = Purpose(account);
        var existing = await credentials.FindAsync(id, purpose, ct);
        var expected = existing is null ? null : CredentialAttempt.RevisionOf(existing);
        var verifier = venues.CredentialVerifier(venueId) ?? throw new ProblemException(503, "Credential verification is unavailable.");

        // 2. Venue verification, with nothing open.
        if (unitOfWork.IsActive)
            throw new InvalidOperationException("Credential verification calls the venue and cannot run inside a unit of work or transaction.");
        await reads.AdmitVerificationAsync(owner.OwnerId, account.VenueId, ct);
        VerifiedVenueCredential verified;
        try
        {
            verified = await verifier.VerifyAsync(sourceId, token, ct);
        }
        catch (VenueRateLimitException limited) { throw await reads.LimitedAsync(account.VenueId, limited); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Adapters are an external boundary. Never forward even a ProblemException from them.
            throw new ProblemException(400, "The venue did not accept this read-only token.");
        }
        if (verified.Scope is not ("single" or "all"))
            throw new ProblemException(400, "The venue did not accept this read-only token.");

        // 3. Short locked recheck and save.
        return await unitOfWork.ExecuteAsync(() => accounts.WithAccountLockAsync(id, async current =>
        {
            if (current.OwnerId != owner.OwnerId) throw new ProblemException(404, "Account not found.");
            if (!current.IsEnabled || current.SettingsRevision != revision || current.SourceId != sourceId || current.VenueId != venueId)
                throw new ProblemException(409, ChangedWhileVerifying);
            Validate(current, request);
            var previous = await credentials.FindAsync(id, purpose, ct);
            // Absent must still be absent. A refusal mark (LastError) is not part of the revision, so it does not conflict.
            if ((previous is null ? null : CredentialAttempt.RevisionOf(previous)) != expected)
                throw new ProblemException(409, ChangedWhileVerifying);
            var now = time.GetUtcNow();
            if (verified.ExpiresAt <= now) throw new ProblemException(400, "The venue did not accept this read-only token.");
            SealedCredential sealedCredential;
            try { sealedCredential = vault.Seal(owner.OwnerId, id, purpose, token); }
            catch (CredentialStorageException) { throw new ProblemException(503, "Credential storage is unavailable."); }
            var credential = new AccountCredential
            {
                OwnerId = owner.OwnerId, AccountId = id, Purpose = purpose,
                KeyId = sealedCredential.KeyId, Nonce = sealedCredential.Nonce,
                Ciphertext = sealedCredential.Ciphertext, Tag = sealedCredential.Tag,
                Scope = verified.Scope, ExpiresAt = verified.ExpiresAt.ToUniversalTime(),
                CreatedAt = previous?.CreatedAt ?? now, LastVerifiedAt = now
            };
            await credentials.SaveAsync(credential, ct);
            return Metadata(current, credential);
        }, ct), ct);
    }

    /// <returns>The trimmed token.</returns>
    private string Validate(Account account, SaveAccountCredentialRequest request)
    {
        Guard(account, requireEnabled: true);
        if (!vault.IsConfigured) throw new ProblemException(503, "Credential storage is not configured.");
        var token = request.Token?.Trim();
        if (string.IsNullOrEmpty(token) || request.Token!.Length > 512)
            throw new ProblemException(400, "Enter a read-only token of at most 512 characters.");
        return token;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct) =>
        await unitOfWork.ExecuteAsync(() => accounts.WithAccountLockAsync(id, async account =>
        {
            Guard(account, requireEnabled: false);
            await credentials.DeleteAsync(id, Purpose(account), ct);
            return true;
        }, ct), ct);

    private void Guard(Account account, bool requireEnabled)
    {
        if (account.TradingMode != "real") throw new ProblemException(400, "Paper accounts do not accept venue credentials.");
        if (account.OwnerId != owner.OwnerId) throw new ProblemException(404, "Account not found.");
        if (requireEnabled && !account.IsEnabled) throw new ProblemException(409, "Enable the account before reading or changing its credential.");
        if (venues.Descriptor(account.VenueId)?.Capabilities.ReadOnlyCredential != true || account.SourceId is null)
            throw new ProblemException(400, "This account does not support read-only credentials.");
    }

    private AccountCredentialDto Metadata(Account account, AccountCredential? credential)
    {
        if (credential is null) return new(vault.IsConfigured, null);
        var status = credential.ExpiresAt <= time.GetUtcNow() ? "expired"
            : credential.ExpiresAt <= time.GetUtcNow().AddDays(14) ? "expiring" : "valid";
        if (!vault.IsConfigured || credential.LastError is not null) status = "unavailable";
        else
        {
            try { _ = vault.Open(owner.OwnerId, account.Id, Purpose(account), credential.Sealed()); }
            catch (CredentialStorageException) { status = "unavailable"; }
        }
        return new(vault.IsConfigured, new(credential.Scope, credential.ExpiresAt, credential.LastVerifiedAt, status));
    }

    private static string Purpose(Account account) => VenueCredentialPurpose.ReadToken(account.VenueId);
}
