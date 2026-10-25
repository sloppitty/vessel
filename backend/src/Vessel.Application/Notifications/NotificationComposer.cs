using System.Globalization;
using System.Text;
using System.Text.Json;
using Vessel.Application.Events;

namespace Vessel.Application.Notifications;

/// <summary>A Play named in a notification, as it was when the notification was composed.</summary>
public sealed record NotificationPlay(Guid Id, string Title, string? Instrument);

/// <summary>Names the composer needs; it reads nothing itself.</summary>
public sealed record NotificationContext(Guid? AccountId, string? AccountName, string? VenueId,
    IReadOnlyDictionary<Guid, NotificationPlay> Plays);

/// <summary>
/// Composes channel-neutral content from the events one notification covers. Text is server-authored; values are
/// the events' exact decimal strings; owner-entered names are cleaned of control characters and bounded. It never
/// copies venue messages, notes, tokens or raw payloads.
/// </summary>
public static class NotificationComposer
{
    private static readonly string[] Priority =
    [
        "notification.test-requested", "discrepancy.opened", "discrepancy.unverifiable", "discrepancy.resolved",
        "play.closed", "play.opened", "play.status-changed", "execution.filled", "link.needs-confirmation",
        "execution.link-changed", "monitor.state-changed", "credential.expiring"
    ];

    public static NotificationContent Compose(IReadOnlyList<VesselEvent> events, NotificationContext context)
    {
        if (events.Count == 0) throw new ArgumentException("A notification covers at least one event.");
        var ordered = events.OrderBy(e => long.Parse(e.Sequence, CultureInfo.InvariantCulture)).ToList();
        var primary = ordered.OrderBy(e => Rank(e.Type)).ThenByDescending(e => NotificationValues.Rank(e.Severity)).First();
        var plays = ordered.Where(e => e.Subject.PlayId is not null).Select(e => e.Subject.PlayId!.Value).Distinct().ToList();
        var account = Clean(context.AccountName) ?? "the account";
        var single = plays.Count == 1 ? plays[0] : (Guid?)null;
        var subject = new NotificationSubject(context.AccountId, Clean(context.AccountName), context.VenueId, single,
            single is { } id ? PlayName(context, id, primary) : null,
            primary.Subject.ContractId ?? (single is { } p && context.Plays.TryGetValue(p, out var known) ? known.Instrument : null));

        var (title, summary) = primary.Type switch
        {
            "notification.test-requested" => ("Test notification", "In-app notifications are working."),
            "discrepancy.opened" => ($"{DiscrepancySubject(context, primary, account)}: {DiscrepancyText(Text(primary, "kind"))}", null),
            "discrepancy.unverifiable" => ($"{DiscrepancySubject(context, primary, account)}: could not be verified",
                $"Vessel could not confirm whether {DiscrepancyText(Text(primary, "kind"))} was resolved."),
            "discrepancy.resolved" => ($"{DiscrepancySubject(context, primary, account)}: resolved",
                $"Cleared: {DiscrepancyText(Text(primary, "kind"))}."),
            "monitor.state-changed" => MonitorState(Text(primary, "state"), account),
            "credential.expiring" => ($"Read token for {account} expires soon", ExpiresSummary(primary)),
            _ when plays.Count > 1 => ($"{plays.Count} Plays updated on {account}", null),
            "play.closed" => ($"{PlayName(context, primary.Subject.PlayId, primary)} closed", ClosedSummary(primary)),
            "play.opened" => ($"{PlayName(context, primary.Subject.PlayId, primary)} opened",
                ordered.FirstOrDefault(e => e.Type == "execution.filled" && Text(e, "role") == "entry") is { } entry ? FillText(entry) : null),
            "play.status-changed" => ($"{PlayName(context, primary.Subject.PlayId, primary)} is now {Text(primary, "current") ?? "changed"}", null),
            "execution.filled" => ($"{PlayName(context, primary.Subject.PlayId, primary)}: {Role(primary)} {(Bool(primary, "complete") == false ? "partly filled" : "filled")}",
                FillText(primary)),
            "link.needs-confirmation" => ($"{PlayName(context, primary.Subject.PlayId, primary)}: confirm an order link",
                $"An {Text(primary, "role") ?? "order"} order could belong to this Play."),
            _ => ($"Update on {account}", null)
        };

        var lines = new List<string>();
        foreach (var item in ordered.Where(e => e != primary))
        {
            if (Line(item) is not { } line) continue;
            lines.Add(plays.Count > 1 && item.Subject.PlayId is { } playId ? $"{PlayName(context, playId, item)}: {line}" : line);
        }
        if (primary.Type == "play.opened") lines.RemoveAll(line => summary is not null && line.EndsWith(summary, StringComparison.Ordinal));
        if (ordered.FirstOrDefault(e => e.Type == "play.closed") is { } close && FeesLine(close) is { } fees) lines.Add(fees);
        if (lines.Count > NotificationValues.MaxLines)
        {
            var hidden = lines.Count - (NotificationValues.MaxLines - 1);
            lines = [.. lines.Take(NotificationValues.MaxLines - 1), $"and {hidden} more changes"];
        }

        return new(Bound(title, NotificationValues.TitleLength), summary is null ? null : Bound(summary, NotificationValues.LineLength),
            lines.Select(line => Bound(line, NotificationValues.LineLength)).ToArray(),
            ordered.Any(e => e.Timeliness == "late"), Links(ordered, context, plays), subject);
    }

    private static int Rank(string type) => Array.IndexOf(Priority, type) is var index and >= 0 ? index : Priority.Length;

    private static string? Line(VesselEvent e) => e.Type switch
    {
        "execution.filled" => $"{Capital(Role(e))} {(Bool(e, "complete") == false ? "partly filled" : "filled")}: {FillText(e)}",
        "play.opened" => "Play opened",
        "play.closed" => ClosedSummary(e) is { } closed ? $"Play closed. {closed}" : "Play closed",
        "play.status-changed" => $"Status {Text(e, "previous") ?? "?"} to {Text(e, "current") ?? "?"}",
        "link.needs-confirmation" => $"An {Text(e, "role") ?? "order"} order needs your confirmation",
        "execution.link-changed" => Text(e, "current") switch
        {
            "linked" => $"{Capital(Role(e))} order linked",
            "unlinked" or "dismissed" => $"{Capital(Role(e))} order unlinked",
            _ => null
        },
        _ => null
    };

    private static List<NotificationLink> Links(List<VesselEvent> events, NotificationContext context, List<Guid> plays)
    {
        var links = new List<NotificationLink>();
        var execution = events.Any(e => e.Type is "link.needs-confirmation" || e.Type.StartsWith("discrepancy.", StringComparison.Ordinal));
        foreach (var play in plays.Where(context.Plays.ContainsKey).Take(NotificationValues.MaxLinks))
            links.Add(new(execution ? "play-execution" : "play",
                plays.Count == 1 ? "Open Play" : Bound($"Open {Clean(context.Plays[play].Title) ?? "Play"}", 60), PlayId: play));
        if (links.Count == 0 && context.AccountId is { } account)
        {
            var credential = events.Any(e => e.Type == "credential.expiring" ||
                e.Type == "monitor.state-changed" && Text(e, "state") == "credential-error");
            links.Add(new(credential ? "account-credential" : "account", credential ? "Manage token" : "Open account", AccountId: account));
        }
        return links;
    }

    private static (string, string?) MonitorState(string? state, string account) => state switch
    {
        "degraded" => ($"Monitoring degraded on {account}", "Some venue reads were incomplete; fills may be missing."),
        "stale" => ($"Monitoring stale on {account}", "No successful check for longer than twice the interval."),
        "credential-error" => ($"Read token problem on {account}", "Checks are paused until the token is replaced."),
        "live" or "idle" => ($"Monitoring restored on {account}", null),
        _ => ($"Monitoring changed on {account}", null)
    };

    private static string? ExpiresSummary(VesselEvent e) =>
        e.Data.TryGetProperty("expiresAtUtc", out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires)
            ? $"It expires on {expires.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} (UTC)." : null;

    /// <summary>The reported closed result with its basis; it never claims a fee- or funding-complete figure.</summary>
    private static string? ClosedSummary(VesselEvent e)
    {
        if (Text(e, "closedPnlUsd") is not { } pnl) return null;
        var basis = Text(e, "closedPnlBasis") switch
        {
            "gross" => " before fees",
            "net-of-fee" => " after fees",
            "mixed" => " (mixed fee basis)",
            _ => ""
        };
        return $"Realized {pnl} USD{basis}.";
    }

    private static string? FeesLine(VesselEvent e)
    {
        if (e.Data.ValueKind != JsonValueKind.Object || !e.Data.TryGetProperty("fees", out var fees) || fees.ValueKind != JsonValueKind.Array)
            return null;
        var parts = fees.EnumerateArray().Take(4)
            .Select(fee => fee.ValueKind == JsonValueKind.Object && fee.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.String &&
                fee.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String
                    ? $"{Clean(amount.GetString())} {Clean(token.GetString())}" : null)
            .OfType<string>().ToList();
        return parts.Count == 0 ? null : "Fees " + string.Join(", ", parts);
    }

    private static string FillText(VesselEvent e)
    {
        var side = Text(e, "side") is { } value ? Capital(value) + " " : "";
        var quantity = Text(e, "filledQuantity") ?? "?";
        return Text(e, "averagePrice") is { } price ? $"{side}{quantity} at {price}" : $"{side}{quantity}";
    }

    private static string DiscrepancySubject(NotificationContext context, VesselEvent e, string account) => e.Subject.PlayId is { } play
        ? PlayName(context, play, e)
        : $"{Clean(e.Subject.ContractId) ?? "Position"} on {account}";

    private static string DiscrepancyText(string? kind) => kind switch
    {
        "protection.missing" => "no stop protects the open position",
        "protection.cancelled" => "its stop was cancelled",
        "protection.short" => "its stop covers less than the position",
        "protection.excess" => "resting exits exceed the position",
        "protection.beyond-liquidation" => "its stop sits beyond the venue's liquidation price",
        "protection.orphaned" => "a stop or target still rests after the Play ended",
        "entry.orphaned" => "an entry order still rests after the Play ended",
        "level.drift" => "a linked order no longer matches its plan level",
        "position.missing" => "the venue shows no position",
        "position.opposite" => "the venue position is on the opposite side",
        "position.short" => "the venue position is smaller than expected",
        "position.excess" => "the venue position is larger than expected",
        "fill.unlinked" => "a fill is not linked to a Play",
        _ => "a discrepancy"
    };

    private static string PlayName(NotificationContext context, Guid? id, VesselEvent e) =>
        id is { } play && context.Plays.TryGetValue(play, out var known) && Clean(known.Title) is { } title ? title
        : Clean(e.Subject.ContractId) is { } instrument ? $"{instrument} Play" : "A Play";

    private static string Role(VesselEvent e) => Text(e, "role") ?? "order";

    private static string Capital(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string? Text(VesselEvent e, string name) =>
        e.Data.ValueKind == JsonValueKind.Object && e.Data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Clean(value.GetString()) : null;

    private static bool? Bool(VesselEvent e, string name) =>
        e.Data.ValueKind == JsonValueKind.Object && e.Data.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>Owner and venue text becomes one plain line: no control or format characters, trimmed, bounded.</summary>
    internal static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var builder = new StringBuilder(Math.Min(value.Length, 200));
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            builder.Append(category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                ? " " : rune.ToString());
            if (builder.Length >= 200) break;
        }
        var text = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? null : Bound(text, 120);
    }

    private static string Bound(string value, int length)
    {
        if (value.Length <= length) return value;
        var cut = value[..(length - 1)];
        // Never split a surrogate pair.
        if (char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        return cut + "…";
    }
}
