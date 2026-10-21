using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Venues;

/// <summary>Settled account payments, never estimates derived from public funding rates.</summary>
public interface IVenueFundingReader
{
    string VenueId { get; }
    Task<VenueFundingReadResult> ReadFundingAsync(Account account, DateTimeOffset fromUtc,
        DateTimeOffset toUtc, CancellationToken cancellationToken);
}

/// <summary>Positive amounts are received; negative amounts are paid. Native identity and original facts are retained.</summary>
public sealed record VenueFundingPayment(string SourceId, string ContractId, string? VenueContractId,
    decimal Amount, string Asset, DateTimeOffset OccurredAtUtc, string RawJson)
{
    public long? OccurredAtUnixNanoseconds { get; init; }
}

/// <summary>Complete means every payment in this exact window was read, including a verified empty result.</summary>
public sealed record VenueFundingReadResult(IReadOnlyList<VenueFundingPayment> Payments,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, bool Complete, string Notice, string PnlBasis = FundingFacts.Unknown)
{
    /// <summary>
    /// Incomplete only because the reader's page or row budget ran out before the window was exhausted, so a
    /// narrower window may complete (Phase 2, item 9a). False for every other kind of incompleteness.
    /// </summary>
    public bool Truncated { get; init; }
}

public static class VenueFundingChecks
{
    public static void Validate(VenueFundingReadResult result, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
        if (result.FromUtc != fromUtc || result.ToUtc != toUtc || fromUtc > toUtc ||
            toUtc - fromUtc > TimeSpan.FromDays(31) || result.Payments.Count > 10_000 ||
            !FundingFacts.IsPnlBasis(result.PnlBasis) || !Text(result.Notice, 2000) ||
            result.Payments.Any(p => !Text(p.SourceId, 128) || !Text(p.ContractId, 128) ||
                p.VenueContractId is not null && !Text(p.VenueContractId, 128) || !Text(p.Asset, 64) ||
                p.OccurredAtUtc < fromUtc || p.OccurredAtUtc > toUtc ||
                p.OccurredAtUtc.Ticks % 10 != 0 ||
                p.OccurredAtUnixNanoseconds is { } ns && (ns < 0 ||
                    DateTimeOffset.UnixEpoch.AddTicks(ns / 1000 * 10) != p.OccurredAtUtc ||
                    (System.Numerics.BigInteger)ns > (toUtc - DateTimeOffset.UnixEpoch).Ticks * (System.Numerics.BigInteger)100)))
            throw new VenueReadException("The venue returned invalid funding facts or coverage.");
        foreach (var group in result.Payments.GroupBy(p => p.SourceId))
        {
            var first = group.First();
            if (group.Any(p => p.ContractId != first.ContractId || p.VenueContractId != first.VenueContractId ||
                p.Amount != first.Amount || p.Asset != first.Asset || p.OccurredAtUtc != first.OccurredAtUtc ||
                p.OccurredAtUnixNanoseconds != first.OccurredAtUnixNanoseconds))
                throw new VenueReadException("The venue returned conflicting funding identities.");
        }
    }
}
