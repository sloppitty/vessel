using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Common;

namespace Vessel.Infrastructure.Venues.Hyperliquid;

public sealed partial class HyperliquidPerpetualReader
{
    private const int FundingPageSize = 500;
    private const int MaxFundingPages = 20;
    private const int MaxFundingRows = 10000;

    /// <summary>Settled primary-perpetual USDC payments in an inclusive, millisecond-aligned window.</summary>
    public Task<VenueFundingReadResult> ReadFundingAsync(Account account, DateTimeOffset fromUtc,
        DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (account.VenueId != Id || !account.IsEnabled)
            throw new VenueReadException("An enabled Hyperliquid account is required.");
        var source = EvmAddress.Require(account.SourceId);
        fromUtc = fromUtc.ToUniversalTime();
        toUtc = toUtc.ToUniversalTime();
        if (fromUtc < DateTimeOffset.UnixEpoch || fromUtc > toUtc || toUtc - fromUtc > TimeSpan.FromDays(31) ||
            toUtc > timeProvider.GetUtcNow() ||
            fromUtc.Ticks % TimeSpan.TicksPerMillisecond != 0 || toUtc.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new VenueReadException("Funding requires an inclusive, millisecond-aligned past window of at most 31 days.");

        return ReadBoundedAsync(async token =>
        {
            var contracts = (await CatalogueAsync(token, refresh: true)).All
                .Select(i => i.VenueContractId ?? i.ContractId).ToHashSet(StringComparer.Ordinal);
            var payments = new Dictionary<string, VenueFundingPayment>(StringComparer.Ordinal);
            var cursor = fromUtc.ToUnixTimeMilliseconds();
            var end = toUtc.ToUnixTimeMilliseconds();
            var readRows = 0;
            var excluded = false;
            var exhausted = false;
            var budget = true; // Cleared when a non-advancing timestamp, not the page/row budget, stops the read.

            for (var page = 0; page < MaxFundingPages; page++)
            {
                token.ThrowIfCancellationRequested();
                using var document = await ReadJsonAsync(
                    new { type = "userFunding", user = source, startTime = cursor, endTime = end }, token);
                var rows = Array(document.RootElement);
                var count = rows.GetArrayLength();
                if (count > MaxFundingRows) throw new VenueReadException(InvalidResponse);
                if (readRows + count > MaxFundingRows) break;
                readRows += count;
                var last = cursor;
                foreach (var row in rows.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    var occurred = Timestamp(Property(row, "time"), end);
                    var milliseconds = occurred.ToUnixTimeMilliseconds();
                    // The documented last-timestamp cursor requires ascending, inclusive pages.
                    if (milliseconds < last) throw new VenueReadException(InvalidResponse);
                    last = milliseconds;
                    var hash = Text(Property(row, "hash"), 66);
                    if (hash.Length != 66 || !hash.StartsWith("0x", StringComparison.Ordinal) ||
                        !hash[2..].All(char.IsAsciiHexDigit) || hash[2..].All(c => c == '0'))
                        throw new VenueReadException(InvalidResponse);
                    var delta = Property(row, "delta");
                    if (Text(Property(delta, "type")) != "funding")
                        throw new VenueReadException(InvalidResponse);
                    var coin = Text(Property(delta, "coin"));
                    var amount = Number(Property(delta, "usdc"));
                    if (!contracts.Contains(coin))
                    {
                        excluded = true;
                        continue;
                    }
                    // Hash uniqueness is undocumented; include native contract/time, never the payment amount.
                    var identity = $"{hash.ToLowerInvariant()}:{coin}:{milliseconds.ToString(CultureInfo.InvariantCulture)}";
                    var id = "funding:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                    var payment = new VenueFundingPayment(id, HyperliquidInstruments.Canonical(coin), coin,
                        amount, PrimaryQuoteAsset, occurred, row.GetRawText());
                    if (payments.TryGetValue(id, out var known))
                    {
                        using var original = JsonDocument.Parse(known.RawJson);
                        if (!JsonElement.DeepEquals(original.RootElement, row))
                            throw new VenueReadException(InvalidResponse);
                    }
                    else
                        payments.Add(id, payment);
                }

                // Time-range replies cap at 500 elements OR distinct blocks. Fewer than 500 rows
                // establishes exhaustion; >=500 needs another read even when rows share timestamps.
                if (count < FundingPageSize)
                {
                    exhausted = true;
                    break;
                }
                // Never skip a possibly truncated same-millisecond group by incrementing the cursor.
                if (last == cursor) { budget = false; break; }
                cursor = last;
            }
            var complete = exhausted && !excluded;
            var notice = "Settled Hyperliquid primary-perpetual funding in USDC; inclusive requested window only.";
            if (!exhausted)
                notice += " Incomplete funding: the page/row bound or a non-advancing timestamp prevented exhaustive retrieval.";
            if (excluded)
                notice += " Incomplete funding: unrecognized or non-primary contracts were excluded.";
            return new VenueFundingReadResult(payments.Values.OrderBy(p => p.OccurredAtUtc)
                .ThenBy(p => p.SourceId, StringComparer.Ordinal).ToList(), fromUtc, toUtc, complete, notice,
                PnlBasis: FundingFacts.Unknown) // Official docs do not establish the API closedPnl funding basis.
            { Truncated = !exhausted && budget && !excluded };
        }, cancellationToken);
    }
}
