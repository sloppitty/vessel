using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Vessel.Domain.Numerics;

namespace Vessel.Application.Events;

// Every optional field is written as an explicit null. One serializer policy serves storage, broker and API.
public sealed record VesselEventSubject(Guid? AccountId = null, string? VenueId = null, string? ContractId = null,
    Guid? PlayId = null, string? EntryId = null, string? LevelId = null, Guid? DiscrepancyId = null);
public sealed record VesselEventSource(string Trigger, string[]? OrderIds = null, string[]? FillIds = null);

public sealed record VesselEventDraft(string Type, Guid RunId, string DedupKey, string Severity,
    DateTimeOffset OccurredAt, DateTimeOffset DetectedAt, VesselEventSubject Subject, VesselEventSource Source,
    JsonElement Data, string Timeliness = "timely", string Relevance = "current", DateTimeOffset? ExpiresAt = null,
    int Version = 1);

public sealed record VesselEvent(Guid Id, string Type, int Version, Guid OwnerId, string Sequence, Guid RunId,
    string DedupKey, string Severity, DateTimeOffset OccurredAt, DateTimeOffset DetectedAt,
    string Timeliness, string Relevance, DateTimeOffset? ExpiresAt, VesselEventSubject Subject,
    VesselEventSource Source, JsonElement Data);

/// <summary>A bounded page. <paramref name="NextCursor"/> carries the stream epoch with the sequence.</summary>
public sealed record VesselEventPage(IReadOnlyList<VesselEvent> Events, string NextCursor, bool ResetRequired);

public interface IVesselEventSink
{
    /// <summary>Buffer an actual mutation in the active unit of work, not a re-read observation.</summary>
    Task AppendAsync(VesselEventDraft draft, CancellationToken cancellationToken);
    /// <summary>Set the identity of the run or owner action for events captured in the active unit of work.</summary>
    Task SetRunAsync(Guid runId, string trigger, CancellationToken cancellationToken);
    /// <summary>Call and commit before the first venue read; already imported fills are historical.</summary>
    Task EnsureMonitoringBaselineAsync(Guid accountId, CancellationToken cancellationToken);
    /// <summary>
    /// Phase 2, item 6b: fills a backfill added, in the active unit, are history and never a fresh execution. They are
    /// claimed now, so no execution event is drafted for them, in this unit or when their order is linked later.
    /// </summary>
    Task ClaimHistoricalFillsAsync(IReadOnlyCollection<Vessel.Domain.Workspace.ImportedFill> fills, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public interface IVesselEventReader
{
    /// <summary>Committed events after <paramref name="cursor"/>; another epoch, a pruned or a future cursor resets.</summary>
    Task<VesselEventPage> ReadAsync(string? cursor, int limit, CancellationToken cancellationToken);
}

/// <summary>Browser cursor: <c>{epoch}.{sequence}</c>, or <c>0</c> for the start of a stream not yet seen.</summary>
public readonly record struct VesselEventCursor(Guid? Epoch, long Sequence)
{
    public static bool TryParse(string? value, out VesselEventCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(value) || value == "0") return true;
        var dot = value.IndexOf('.');
        if (dot != 32 || !Guid.TryParseExact(value[..dot], "N", out var epoch) || epoch == Guid.Empty ||
            !long.TryParse(value[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
        {
            // A bare sequence predates epochs; it parses so the reader can answer with a reset.
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var legacy)) return false;
            cursor = new(null, legacy);
            return true;
        }
        cursor = new(epoch, sequence);
        return true;
    }

    public static string Format(Guid? epoch, long sequence) => epoch is { } value
        ? value.ToString("N") + "." + sequence.ToString(CultureInfo.InvariantCulture)
        : sequence.ToString(CultureInfo.InvariantCulture);
}

public static class VesselEventValues
{
    // The shared exact rules; event payload text is byte-identical to every other amount the API emits.
    public static string Exact(decimal value) => ExactDecimal.Format(value);
    public static string Sum(IEnumerable<decimal> values) => ExactDecimal.Sum(values);
    public static string? AveragePrice(IEnumerable<(decimal Price, decimal Quantity)> values)
    {
        var rows = values.ToList();
        var quantity = rows.Aggregate(default(ExactAmount), (sum, row) => sum + ExactAmount.From(row.Quantity));
        var notional = rows.Aggregate(BigInteger.Zero, (sum, row) => sum + ExactAmount.Product(row.Price, row.Quantity));
        return ExactAmount.Average(notional, quantity);
    }
    public static readonly string[] Types =
    [
        "play.opened", "play.closed", "play.status-changed", "execution.filled", "link.needs-confirmation",
        "execution.link-changed", "discrepancy.opened", "discrepancy.resolved", "discrepancy.unverifiable",
        "monitor.state-changed", "monitor.run-completed", "credential.expiring", "notification.test-requested"
    ];
}
