using Vessel.Application.Credentials;
using Vessel.Domain.Credentials;

namespace Vessel.Tests;

internal sealed class SecurityCredentialStore : IAccountCredentialStore
{
    internal AccountCredential? Stored { get; set; }
    public Task<AccountCredential?> FindAsync(Guid accountId, string purpose, CancellationToken ct) => Task.FromResult(Stored?.AccountId == accountId && Stored.Purpose == purpose ? Stored : null);
    public Task SaveAsync(AccountCredential credential, CancellationToken ct) { Stored = credential; return Task.CompletedTask; }
    public Task DeleteAsync(Guid accountId, string purpose, CancellationToken ct) { Stored = null; return Task.CompletedTask; }
}
