using System.Globalization;
using System.Text;
using Vessel.Application.Common;
using Vessel.Application.Venues;
using Vessel.Domain.Common;
using Vessel.Domain.Numerics;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Export;

/// <summary>
/// Raw retained facts of one real account as CSV (Phase 2, item 5b): its executions, imported or owner-entered, or its
/// funding settlements. Native IDs, exact decimal strings and separate fees and funding, so other tools can read them.
/// Not a tax report: no lots, no valuation, no inferred fill. Files carry no notice row, so tools can ingest them; the
/// Settings panel states the limits.
/// </summary>
public sealed class FactsExportService(IFactsExportStore store, IVenueRegistry venues, TimeProvider time)
{
    public static readonly IReadOnlyList<string> ExecutionColumns = ["provenance", "account", "venue", "account_source", "instrument",
        "venue_contract_id", "source_fill_id", "order_id", "transaction_hash", "occurred_at_utc", "side", "venue_direction", "position_effect",
        "price", "quantity", "fee", "fee_token", "fee_basis", "venue_pnl", "pnl_basis", "leverage", "play_id", "role", "source_label"];

    public static readonly IReadOnlyList<string> FundingColumns = ["account", "venue", "account_source", "instrument", "venue_contract_id",
        "source_id", "occurred_at_utc", "occurred_at_unix_ns", "amount", "asset"];

    public async Task<ExportFile> PrepareAsync(Guid accountId, string? kind, string? from, string? to, CancellationToken ct)
    {
        if (kind is not ("executions" or "funding")) throw new ProblemException(400, "Choose executions or funding.");
        var (start, end) = (Day(from, "from"), Day(to, "to"));
        if (start is not null && end is not null && start > end) throw new ProblemException(400, "The start date is after the end date.");
        // Half-open UTC days: the end date is included in full. The last representable day has no next day.
        DateTimeOffset? until = end is null ? null : end.Value.Date == DateTime.MaxValue.Date
            ? throw new ProblemException(400, "The end date is out of range.") : end.Value.AddDays(1);
        var account = await store.AccountAsync(accountId, ct) ?? throw new ProblemException(404, "Account not found.");
        if (account.TradingMode != TradingMode.Real) throw new ProblemException(409, "Paper accounts have no retained venue facts to export.");
        if (!account.IsEnabled) throw new ProblemException(409, "Enable the account to export its facts.");
        var venue = venues.Descriptor(account.VenueId)?.Name ?? account.VenueId;
        var source = account.SourceId ?? account.Address;
        var name = $"vessel-{kind}-{account.Id.ToString("N")[..8]}-{(start ?? time.GetUtcNow()).UtcDateTime:yyyyMMdd}" +
            (end is null ? "" : $"-{end.Value.UtcDateTime:yyyyMMdd}") + ".csv";
        return new(name, "text/csv; charset=utf-8", async (stream, token) =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 65_536, leaveOpen: true);
            if (kind == "funding")
            {
                var csv = new Csv(FundingColumns, writer);
                await foreach (var payment in store.FundingAsync(account.Id, start, until, token))
                {
                    csv.Text(account.Name).Text(venue).Text(source).Text(payment.ContractId).Text(payment.VenueContractId).Text(payment.SourceId)
                        .Time(payment.OccurredAtUtc).Number(payment.OccurredAtUnixNanoseconds?.ToString(CultureInfo.InvariantCulture))
                        .Number(ExactDecimal.Format(payment.Amount)).Text(payment.Asset).End();
                    await writer.FlushAsync(token);
                }
                return;
            }
            var rows = new Csv(ExecutionColumns, writer);
            await foreach (var fill in store.FillsAsync(account.Id, start, until, token))
            {
                rows.Text("imported").Text(account.Name).Text(venue).Text(source).Text(fill.ContractId).Text(fill.VenueContractId).Text(fill.SourceFillId)
                    .Text(fill.ExternalOrderId).Text(fill.TransactionHash).Time(fill.OccurredAtUtc).Text(fill.Side).Text(fill.Direction)
                    .Text(fill.PositionEffect).Number(ExactDecimal.Format(fill.Price)).Number(ExactDecimal.Format(fill.Quantity))
                    .Number(ExactDecimal.Format(fill.Fee)).Text(fill.FeeToken).Text(fill.FeeBasis).Number(ExactDecimal.Format(fill.ClosedPnlUsd))
                    .Text(fill.PnlBasis).Number(fill.Leverage?.ToString(CultureInfo.InvariantCulture)).Text(null).Text(null).Text(null).End();
                await writer.FlushAsync(token);
            }
            await foreach (var (row, instrument) in store.ManualExecutionsAsync(account.Id, start, until, token))
            {
                rows.Text("manual").Text(account.Name).Text(venue).Text(source).Text(instrument).Text(null).Text(null).Text(null).Text(null)
                    .Time(row.OccurredAtUtc).Text(row.Side).Text(null).Text(row.Role == "entry" ? ExecutionFacts.Open : ExecutionFacts.Close).Number(Manual(row.Price))
                    .Number(Manual(row.Quantity)).Number(row.Fee is null ? null : Manual(row.Fee)).Text(row.FeeCurrency).Text(null).Text(null).Text(null)
                    .Number(null).Text(row.PlayId.ToString()).Text(row.Role).Text(row.Source).End();
                await writer.FlushAsync(token);
            }
        });
    }

    /// <summary>An owner-entered number as an exact plain decimal; facts were validated when they were entered.</summary>
    private static string? Manual(string text) =>
        ExactDecimal.ParseManualFact(text, positive: false, out var value) == ManualFactError.None ? ExactDecimal.Format(value) : null;

    private static DateTimeOffset? Day(string? text, string name)
    {
        if (text is null) return null;
        return DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var day)
            ? new DateTimeOffset(DateTime.SpecifyKind(day, DateTimeKind.Utc))
            : throw new ProblemException(400, $"The {name} date is a UTC day, yyyy-MM-dd.");
    }
}
