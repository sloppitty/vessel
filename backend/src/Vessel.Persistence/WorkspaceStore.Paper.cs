using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Workspace;
using Vessel.Domain.Numerics;

namespace Vessel.Persistence;

public sealed partial class WorkspaceStore
{
    public async Task<IReadOnlyDictionary<Guid, PaperAccountBalance>> PaperBalancesAsync(
        IReadOnlyList<Guid> accountIds, CancellationToken ct)
    {
        if (accountIds.Count == 0) return new Dictionary<Guid, PaperAccountBalance>();
        var ids = accountIds.ToArray();
        // One statement shares one MVCC snapshot, including inside an existing read-committed unit of work.
        // Independent reads can otherwise combine an old open margin with a newly released reservation.
        var rows = await db.Database.SqlQuery<PaperBalanceRow>($"""
            SELECT a."Id", ledger."CashUsd", ledger."HasSeed", executions."MarginUsd",
              reservations."ReservedUsd", positions."OpenPositionCount",
              EXISTS (SELECT 1 FROM paper_simulations s WHERE s."OwnerId"=a."OwnerId"
                AND s."AccountId"=a."Id"
                AND (s."State" IN ('gap','ambiguous') OR s."IssuesJson" <> '[]'::jsonb
                  OR s."SuspensionsJson" <> '[]'::jsonb AND EXISTS (
                    SELECT 1 FROM paper_executions se WHERE se."OwnerId"=s."OwnerId" AND se."PlayId"=s."PlayId"
                    GROUP BY se."EntryId"
                    HAVING SUM(CASE WHEN se."Role"='entry' THEN se."Quantity" ELSE -se."Quantity" END)>0
                      AND MIN(se."OccurredAtUtc") <= (
                        SELECT MAX((h->>'SuspendedAtUtc')::timestamptz)
                        FROM jsonb_array_elements(s."SuspensionsJson") h)))) AS "Unresolved"
            FROM accounts a
            CROSS JOIN LATERAL (
              SELECT COALESCE(SUM(l."AmountUsd"),0)::text AS "CashUsd",
                COALESCE(bool_or(l."Kind"='seed'),false) AS "HasSeed"
              FROM paper_ledger_entries l WHERE l."OwnerId"=a."OwnerId" AND l."AccountId"=a."Id"
            ) ledger
            CROSS JOIN LATERAL (
              SELECT COALESCE(SUM(e."MarginUsd"),0)::text AS "MarginUsd"
              FROM paper_executions e WHERE e."OwnerId"=a."OwnerId" AND e."AccountId"=a."Id"
            ) executions
            CROSS JOIN LATERAL (
              SELECT COALESCE(SUM(r."AmountUsd"),0)::text AS "ReservedUsd"
              FROM paper_reservations r WHERE r."OwnerId"=a."OwnerId" AND r."AccountId"=a."Id"
            ) reservations
            CROSS JOIN LATERAL (
              SELECT COUNT(*)::integer AS "OpenPositionCount" FROM (
                SELECT e."PlayId",e."EntryId" FROM paper_executions e
                WHERE e."OwnerId"=a."OwnerId" AND e."AccountId"=a."Id"
                GROUP BY e."PlayId",e."EntryId"
                HAVING SUM(CASE WHEN e."Role"='entry' THEN e."Quantity" ELSE -e."Quantity" END)>0
              ) opened
            ) positions
            WHERE a."OwnerId"={db.CurrentOwnerId} AND a."TradingMode"='paper' AND a."Id"=ANY({ids})
            """).ToListAsync(ct);
        var result = new Dictionary<Guid, PaperAccountBalance>();
        foreach (var row in rows)
        {
            if (!row.HasSeed) continue; // Missing capital is unknown, never a guessed seed.
            var cash = ExactAmount.Parse(row.CashUsd).ToString();
            var available = ExactAmount.Parse(row.CashUsd) - ExactAmount.Parse(row.MarginUsd) - ExactAmount.Parse(row.ReservedUsd);
            result[row.Id] = new(cash, !row.Unresolved && row.OpenPositionCount == 0 ? cash : null,
                row.Unresolved ? null : available.Units.Sign < 0 ? "0" : available.ToString(), row.OpenPositionCount);
        }
        return result;
    }

    private sealed class PaperBalanceRow
    {
        public Guid Id { get; set; }
        public string CashUsd { get; set; } = null!;
        public bool HasSeed { get; set; }
        public string MarginUsd { get; set; } = null!;
        public string ReservedUsd { get; set; } = null!;
        public int OpenPositionCount { get; set; }
        public bool Unresolved { get; set; }
    }
}
