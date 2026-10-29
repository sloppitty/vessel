using Vessel.Application.Common;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

internal sealed class MemoryWorkspaceStore(Guid ownerId) : IWorkspaceStore
{
    public bool RejectOwnerWideReads { get; set; }
    public bool RejectActivityReads { get; set; }
    public int Saves { get; private set; }
    public List<Portfolio> Portfolios { get; } = [];
    public List<Account> Accounts { get; } = [];
    public List<AccountSnapshot> Snapshots { get; } = [];
    public List<ImportedFill> Fills { get; } = [];
    public Task<List<Portfolio>> PortfoliosAsync(CancellationToken ct) => Task.FromResult(Portfolios.Where(p => p.OwnerId == ownerId).ToList());
    public Task<List<Account>> AccountsAsync(CancellationToken ct) => Task.FromResult(Accounts.Where(a => a.OwnerId == ownerId).ToList());
    public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => Task.FromResult(Accounts.SingleOrDefault(a => a.Id == id && a.OwnerId == ownerId));
    public Task<MarketDataAccount?> MarketDataAccountAsync(Guid id, CancellationToken ct) => Task.FromResult(
        Accounts.Where(a => a.Id == id && a.OwnerId == ownerId).Select(a => new MarketDataAccount(a.IsEnabled, a.VenueId)).SingleOrDefault());
    public Task<bool> SourceExistsAsync(string venueId, string address, CancellationToken ct) =>
        Task.FromResult(Accounts.Any(a => a.OwnerId == ownerId && a.VenueId == venueId && a.Address == address));
    public Task<AccountSnapshot?> SnapshotAsync(Guid id, CancellationToken ct) => Task.FromResult(Snapshots.SingleOrDefault(s =>
        s.AccountId == id && s.OwnerId == ownerId && Accounts.Any(a => a.Id == s.AccountId && a.IsEnabled)));
    public Task<List<AccountSnapshot>> SnapshotsAsync(CancellationToken ct) => RejectOwnerWideReads
        ? throw new InvalidOperationException("Owner-wide snapshot read is not allowed in a detail operation.") : Task.FromResult(Snapshots.Where(s => s.OwnerId == ownerId && Accounts.Any(a => a.Id == s.AccountId && a.IsEnabled)).ToList());
    public Task<List<ImportedFill>> FillsAsync(Guid? accountId, int limit, CancellationToken ct) => RejectActivityReads
        ? throw new InvalidOperationException("Activity is not available for collection reads.") : Task.FromResult(Fills.Where(f => f.OwnerId == ownerId && Accounts.Any(a => a.Id == f.AccountId && a.IsEnabled) && (accountId == null || f.AccountId == accountId)).OrderByDescending(f => f.OccurredAtUtc).Take(limit).ToList());
    public Task<int> FillCountAsync(CancellationToken ct) => RejectActivityReads
        ? throw new InvalidOperationException("Activity is not available for collection reads.") : Task.FromResult(Fills.Count(f => f.OwnerId == ownerId && Accounts.Any(a => a.Id == f.AccountId && a.IsEnabled)));
    public Task AddPortfolioAsync(Portfolio portfolio, CancellationToken ct) { Portfolios.Add(portfolio); return Task.CompletedTask; }
    public Task AddAccountAsync(Account account, CancellationToken ct) { Accounts.Add(account); return Task.CompletedTask; }
    public async Task<T> WithAccountLockAsync<T>(Guid id, Func<Account, Task<T>> action, CancellationToken ct) =>
        await action(await AccountAsync(id, ct) ?? throw new ProblemException(404, "Account not found."));
    public Task SaveRefreshAsync(Account account, PerpetualVenueReadResult result, CancellationToken ct) => throw new NotSupportedException("Refresh tests use real PostgreSQL.");
    public Task<T> WithManagementLockAsync<T>(Func<Task<T>> action, CancellationToken ct) => action();
    public Task DeletePortfolioAsync(Guid id, CancellationToken ct)
    {
        var portfolio = Portfolios.SingleOrDefault(p => p.Id == id && p.OwnerId == ownerId)
            ?? throw new ProblemException(404, "Portfolio not found.");
        foreach (var account in Accounts.Where(a => a.OwnerId == ownerId && a.PortfolioId == id))
            account.UpdateSettings(account.Name, null, account.IsEnabled);
        Portfolios.Remove(portfolio);
        return Task.CompletedTask;
    }
    public Task DeleteAccountAsync(Account account, CancellationToken ct)
    {
        Accounts.Remove(account);
        Snapshots.RemoveAll(s => s.OwnerId == ownerId && s.AccountId == account.Id);
        Fills.RemoveAll(f => f.OwnerId == ownerId && f.AccountId == account.Id);
        return Task.CompletedTask;
    }
    public Task SaveAsync(CancellationToken ct) { Saves++; return Task.CompletedTask; }
}
