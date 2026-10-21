using Vessel.Application.Common;
using Vessel.Application.Ownership;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;

namespace Vessel.Application.Credentials;

// Kept separate from the management service: venue verifiers may themselves depend on this reader.
public sealed class AccountCredentialReader(IWorkspaceStore accounts, IAccountCredentialStore credentials,
    IJournalOwnerContext owner, ICredentialVault vault, TimeProvider time, IUnitOfWork unitOfWork) : IAccountCredentialReader
{
    public async Task<string?> ReadAsync(Guid accountId, CancellationToken cancellationToken) =>
        (await ReadAttemptAsync(accountId, cancellationToken))?.Token;

    public Task<CredentialAttempt?> ReadAttemptAsync(Guid accountId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(() => accounts.WithAccountLockAsync<CredentialAttempt?>(accountId, async account =>
        {
            if (account.TradingMode != "real") throw new ProblemException(409, "Paper accounts cannot use venue credentials.");
            if (account.OwnerId != owner.OwnerId) throw new ProblemException(404, "Account not found.");
            if (!account.IsEnabled) throw new ProblemException(409, "Enable the account before reading its credential.");
            var purpose = VenueCredentialPurpose.ReadToken(account.VenueId);
            var credential = await credentials.FindAsync(accountId, purpose, cancellationToken);
            if (credential is null || credential.ExpiresAt <= time.GetUtcNow() || credential.LastError is not null || !vault.IsConfigured)
                return null;
            try { return new(vault.Open(owner.OwnerId, accountId, purpose, credential.Sealed()), CredentialAttempt.RevisionOf(credential)); }
            catch (CredentialStorageException) { return null; }
        }, cancellationToken), cancellationToken);

    public const string RefusedError = "refused";

    public Task MarkRefusedAsync(Guid accountId, string attemptedRevision, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(() => accounts.WithAccountLockAsync(accountId, async account =>
        {
            if (account.TradingMode != "real") throw new ProblemException(409, "Paper accounts cannot use venue credentials.");
            if (account.OwnerId != owner.OwnerId) throw new ProblemException(404, "Account not found.");
            var credential = await credentials.FindAsync(accountId, VenueCredentialPurpose.ReadToken(account.VenueId), cancellationToken);
            if (credential is null || credential.LastError is not null || string.IsNullOrEmpty(attemptedRevision) ||
                CredentialAttempt.RevisionOf(credential) != attemptedRevision) return false;
            // A fixed application status; the venue response is never persisted.
            credential.LastError = RefusedError;
            await credentials.SaveAsync(credential, cancellationToken);
            return true;
        }, cancellationToken), cancellationToken);

    // Play execution status is polled while a play is open. Taking the account row lock here would queue
    // every poll behind a running refresh, and decrypting would expose the token for no reason.
    public async Task<bool> IsUsableAsync(Account account, CancellationToken cancellationToken)
    {
        if (account.TradingMode != "real" || account.OwnerId != owner.OwnerId || !account.IsEnabled) return false;
        var credential = await credentials.FindAsync(account.Id, VenueCredentialPurpose.ReadToken(account.VenueId), cancellationToken);
        return credential is not null && credential.ExpiresAt > time.GetUtcNow() && credential.LastError is null &&
            vault.HasKey(credential.KeyId);
    }
}
