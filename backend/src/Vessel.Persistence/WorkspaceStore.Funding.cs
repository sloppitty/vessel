using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence;

public sealed partial class WorkspaceStore
{
    // The caller holds the account row lock and refresh transaction, including deduplication and coverage updates.
    private async Task SaveFundingAsync(Account account, VenueFundingReadResult result, CancellationToken ct)
    {
        VenueFundingChecks.Validate(result, result.FromUtc, result.ToUtc);
        var ids = result.Payments.Select(p => p.SourceId).Distinct().ToArray();
        var existing = await db.FundingPayments.Where(p => p.AccountId == account.Id && ids.Contains(p.SourceId))
            .ToDictionaryAsync(p => p.SourceId, ct);
        foreach (var payment in result.Payments.DistinctBy(p => p.SourceId))
        {
            if (existing.TryGetValue(payment.SourceId, out var retained))
            {
                // Canonical labels are derived from mutable catalogues; native identity is the retained fact.
                if ((retained.VenueContractId is null && retained.ContractId != payment.ContractId) ||
                    retained.VenueContractId != payment.VenueContractId ||
                    retained.Amount != payment.Amount || retained.Asset != payment.Asset || retained.OccurredAtUtc != payment.OccurredAtUtc ||
                    retained.OccurredAtUnixNanoseconds != payment.OccurredAtUnixNanoseconds)
                    throw new ProblemException(409, "A retained funding settlement conflicts with the venue response. No refresh changes were saved.");
                continue;
            }
            db.FundingPayments.Add(new()
            {
                Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id,
                SourceId = payment.SourceId, ContractId = payment.ContractId, VenueContractId = payment.VenueContractId,
                Amount = payment.Amount, Asset = payment.Asset, OccurredAtUtc = payment.OccurredAtUtc.ToUniversalTime(),
                OccurredAtUnixNanoseconds = payment.OccurredAtUnixNanoseconds
            });
        }
        var state = await db.FundingStates.SingleOrDefaultAsync(s => s.AccountId == account.Id, ct);
        if (state is null)
        {
            state = new() { OwnerId = account.OwnerId, AccountId = account.Id };
            db.FundingStates.Add(state);
        }
        if (result.Complete)
        {
            // Retain one contiguous verified interval. Never bridge an unread gap.
            var overlaps = state.CoveredFromUtc is { } oldFrom && state.CoveredToUtc is { } oldTo &&
                result.FromUtc <= oldTo && result.ToUtc >= oldFrom;
            state.CoveredFromUtc = overlaps && state.CoveredFromUtc < result.FromUtc ? state.CoveredFromUtc : result.FromUtc.ToUniversalTime();
            state.CoveredToUtc = overlaps && state.CoveredToUtc > result.ToUtc ? state.CoveredToUtc : result.ToUtc.ToUniversalTime();
        }
        // A windowed read records its widest window, so the next Refresh does not relearn the venue's budget.
        var window = result is { WindowFromUtc: { } wf, WindowToUtc: { } wt } && wf <= wt ? (From: wf, To: wt) : (From: result.FromUtc, To: result.ToUtc);
        state.LastAttemptFromUtc = window.From.ToUniversalTime();
        state.LastAttemptToUtc = window.To.ToUniversalTime();
        state.LastReadComplete = result.Complete;
        state.PnlBasis = result.PnlBasis;
        state.Notice = result.Notice;
        // Original adapter JSON may contain unrelated private facts. Store normalized settlements only.
    }
}
