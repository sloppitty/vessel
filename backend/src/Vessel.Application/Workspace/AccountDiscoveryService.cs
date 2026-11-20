using Vessel.Application.Monitoring;
using Vessel.Application.Common;
using Vessel.Application.Ownership;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Numerics;

namespace Vessel.Application.Workspace;

public sealed record DiscoveredAccountDto(string SourceId, string Name, string AccountType, string? AccountValueUsd,
    Guid? ExistingAccountId, bool? IsEnabled, string? CollateralUsd = null, string? AvailableBalanceUsd = null);
public sealed record AccountDiscoveryDto(string VenueId, string Address, IReadOnlyList<DiscoveredAccountDto> Accounts, string Notice);
public sealed class CredentialDiscoveryRequest
{
    public string? Address { get; init; }
    public string? Token { get; init; }
}

public sealed class AccountDiscoveryService(IWorkspaceStore accounts, IJournalOwnerContext owner, IVenueRegistry venues, AccountReadPolicy reads)
{
    public Task<AccountDiscoveryDto> DiscoverAsync(string venueId, string? address, CancellationToken ct) =>
        DiscoverCoreAsync(venueId, address, null, ct);

    public Task<AccountDiscoveryDto> DiscoverAsync(string venueId, CredentialDiscoveryRequest request, CancellationToken ct)
    {
        var token = request.Token?.Trim();
        if (string.IsNullOrEmpty(token) || request.Token!.Length > 512)
            throw new ProblemException(400, "Enter a read-only token of at most 512 characters.");
        if (venues.Descriptor(venueId)?.Capabilities.ReadOnlyCredential != true)
            throw new ProblemException(400, "This venue does not support read-only credentials.");
        return DiscoverCoreAsync(venueId, request.Address, token, ct);
    }

    private async Task<AccountDiscoveryDto> DiscoverCoreAsync(string venueId, string? address, string? token, CancellationToken ct)
    {
        if (venues.Descriptor(venueId)?.Capabilities.AccountDiscovery != true)
            throw new ProblemException(400, "This venue does not support account discovery.");
        if (!AccountSource.IsEvmAddress(address)) throw new ProblemException(400, "A 42-character public hexadecimal address is required.");
        var discovery = venues.Discovery(venueId) ?? throw new ProblemException(503, "Venue discovery is unavailable.");
        await reads.AdmitDiscoveryAsync(owner.OwnerId, venueId, ct);
        IReadOnlyList<VenueAccountCandidate> found;
        try
        {
            found = token is null ? await discovery.DiscoverAsync(address!.ToLowerInvariant(), ct)
                : await discovery.DiscoverAsync(address!.ToLowerInvariant(), token, ct);
            if (found.Count > 1000 || found.Any(c => !AccountSource.IsAccountIndex(c.SourceId) ||
                    string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 200 || c.AccountType is not ("main" or "subaccount") ||
                    c.AccountValueUsd is { } value && !ExactDecimal.IsSignedAmountText(value) ||
                    c.CollateralUsd is { } collateral && !ExactDecimal.IsSignedAmountText(collateral) ||
                    c.AvailableBalanceUsd is { } available && !ExactDecimal.IsSignedAmountText(available)) ||
                found.Select(c => c.SourceId).Distinct().Count() != found.Count)
                throw new VenueReadException("Invalid discovery response.");
        }
        catch (VenueRateLimitException limited) { throw await reads.LimitedAsync(venueId, limited); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (VenueCredentialRejectedException) when (token is not null)
        {
            throw new ProblemException(400, "The read-only token is not valid for this wallet. Check its format, wallet, scope and expiry.");
        }
        catch (Exception) { throw new ProblemException(502, token is null
            ? "Venue accounts could not be discovered. Try again later."
            : "Venue names could not be loaded. Check the read-only token's wallet, scope and expiry."); }
        // Include disabled accounts so discovery cannot suggest importing the same source again. Discovery reads a real
        // venue, so it matches real accounts only.
        var existing = (await accounts.AccountsAsync(TradingMode.Real, ct)).Where(a => a.OwnerId == owner.OwnerId && a.VenueId == venueId && a.SourceId is not null)
            .ToDictionary(a => a.SourceId!);
        return new(venueId, address!.ToLowerInvariant(), found.Select(c =>
        {
            existing.TryGetValue(c.SourceId, out var account);
            return new DiscoveredAccountDto(c.SourceId, c.Name, c.AccountType, c.AccountValueUsd, account?.Id, account?.IsEnabled,
                c.CollateralUsd, c.AvailableBalanceUsd);
        }).ToList(), "Discovery does not import history. Add selected accounts and refresh them explicitly.");
    }
}
