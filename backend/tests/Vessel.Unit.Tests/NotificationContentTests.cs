using System.Text.Json;
using Vessel.Application.Events;
using Vessel.Application.Notifications;

namespace Vessel.Tests;

/// <summary>Content, identity, rules and cursors of the Notification Hub, without a database.</summary>
public sealed class NotificationContentTests
{
    private static readonly Guid Account = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Play = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly NotificationContext Context = new(Account, "Main", "hyperliquid",
        new Dictionary<Guid, NotificationPlay> { [Play] = new(Play, "BTC Long", "BTC") });

    [Fact]
    public void A_closed_run_keeps_the_reported_basis_and_fees_and_folds_its_fills_into_lines()
    {
        var content = NotificationComposer.Compose([
            Event("execution.filled", 1, new { role = "entry", side = "buy", filledQuantity = "0.5", averagePrice = "84541", complete = true }, relevance: "superseded"),
            Event("execution.filled", 2, new { role = "stop", side = "sell", filledQuantity = "0.5", averagePrice = "83900", complete = true }, "warning"),
            Event("play.closed", 3, new { enteredQuantity = "0.5", exitedQuantity = "0.5", closedPnlUsd = "-320.5", closedPnlBasis = "gross",
                fees = new[] { new { token = "USDC", amount = "0.42" } } })
        ], Context);
        Assert.Equal("BTC Long closed", content.Title);
        Assert.Equal("Realized -320.5 USD before fees.", content.Summary);
        Assert.Equal(["Entry filled: Buy 0.5 at 84541", "Stop filled: Sell 0.5 at 83900", "Fees 0.42 USDC"], content.Lines);
        Assert.Equal(new NotificationLink("play", "Open Play", Play), Assert.Single(content.Links));
        Assert.Equal(new NotificationSubject(Account, "Main", "hyperliquid", Play, "BTC Long", "BTC"), content.Subject);
        Assert.False(content.Late);
    }

    [Theory]
    [InlineData("net-of-fee", "Realized 5 USD after fees.")]
    [InlineData("mixed", "Realized 5 USD (mixed fee basis).")]
    [InlineData("unknown", "Realized 5 USD.")]
    public void Closed_results_never_claim_a_basis_they_do_not_have(string basis, string summary) =>
        Assert.Equal(summary, NotificationComposer.Compose([Event("play.closed", 1, new { closedPnlUsd = "5", closedPnlBasis = basis })], Context).Summary);

    [Fact]
    public void Several_Plays_late_events_bounds_and_owner_text_are_handled()
    {
        var other = Guid.NewGuid();
        var context = Context with
        {
            Plays = new Dictionary<Guid, NotificationPlay>
            {
                [Play] = new(Play, "BTC\u0007 Long‮", "BTC"), [other] = new(other, new string('x', 300), "ETH")
            }
        };
        var events = Enumerable.Range(1, 12).Select(i => Event("execution.filled", i,
            new { role = "entry", side = "buy", filledQuantity = $"{i}", averagePrice = "1", complete = i % 2 == 0 },
            play: i % 2 == 0 ? Play : other, timeliness: i == 3 ? "late" : "timely")).ToList();
        var content = NotificationComposer.Compose(events, context);
        Assert.Equal("2 Plays updated on Main", content.Title);
        Assert.True(content.Late);
        Assert.Equal(NotificationValues.MaxLines, content.Lines.Count);
        Assert.Equal("and 4 more changes", content.Lines[^1]);
        Assert.All(content.Lines, line => Assert.True(line.Length <= NotificationValues.LineLength));
        Assert.All(content.Links, link => Assert.True(link.Label.Length <= 60));
        Assert.Equal(2, content.Links.Count);
        Assert.Contains("BTC Long: Entry filled: Buy 4 at 1", content.Lines);
        Assert.DoesNotContain(content.Lines, line => line.Any(char.IsControl) || line.Contains('‮'));
    }

    [Fact]
    public void Multi_Play_link_labels_clean_owner_text_too()
    {
        var other = Guid.NewGuid();
        var context = Context with { Plays = new Dictionary<Guid, NotificationPlay>
        {
            [Play] = new(Play, "BTC\u0007 Long\u202e", "BTC"),
            [other] = new(other, "ETH", "ETH")
        } };
        var content = NotificationComposer.Compose([
            Event("play.opened", 1, new { }, play: Play),
            Event("play.opened", 2, new { }, play: other)
        ], context);
        Assert.All(content.Links, link =>
            Assert.DoesNotContain(link.Label, c => char.IsControl(c) || c == '\u202e'));
    }

    [Fact]
    public void Standalone_kinds_link_to_their_subject_and_a_deleted_Play_keeps_a_neutral_name()
    {
        var missing = NotificationComposer.Compose([Event("discrepancy.opened", 1, new { kind = "protection.missing" }, "critical",
            play: Guid.NewGuid())], Context);
        Assert.Equal("BTC Play: no stop protects the open position", missing.Title);
        Assert.Equal("account", Assert.Single(missing.Links).Kind);

        var instrument = NotificationComposer.Compose([Event("discrepancy.opened", 1, new { kind = "position.opposite" }, "critical", playless: true)], Context);
        Assert.Equal("BTC on Main: the venue position is on the opposite side", instrument.Title);

        var token = NotificationComposer.Compose([Event("monitor.state-changed", 1, new { previous = "live", state = "credential-error" }, playless: true)], Context);
        Assert.Equal(("Read token problem on Main", "account-credential"), (token.Title, Assert.Single(token.Links).Kind));

        var expiring = NotificationComposer.Compose([Event("credential.expiring", 1, new { expiresAtUtc = "2026-10-30T12:00:00Z" }, playless: true)], Context);
        Assert.Equal("It expires on 30 Oct 2026 (UTC).", expiring.Summary);
    }

    [Theory]
    [InlineData("discrepancy.opened", "discrepancy:{id}:3:opened", "Discrepancy", "3", "opened")]
    [InlineData("discrepancy.resolved", "discrepancy:{id}:4:dismissed", "Discrepancy", "4", "dismissed")]
    [InlineData("discrepancy.unverifiable", "discrepancy:{id}:5:unverifiable", "Discrepancy", "5", "unverifiable")]
    [InlineData("monitor.state-changed", "monitor:{account}:7:degraded", "Monitor", "7", "degraded")]
    [InlineData("credential.expiring", "credential:{account}:A1B2:expiring", "Credential", "A1B2", "expiring")]
    public void Standalone_identities_parse_from_the_documented_dedup_keys(string type, string key, string kind, string revision, string transition)
    {
        var discrepancy = Guid.NewGuid();
        var message = Event(type, 42, new { }, playless: true, discrepancy: discrepancy) with
        {
            DedupKey = key.Replace("{id}", discrepancy.ToString()).Replace("{account}", Account.ToString())
        };
        var identity = NotificationIdentity.Parse(message)!;
        Assert.Equal((Enum.Parse<NotificationKind>(kind), 42L, revision, transition), (identity.Kind, identity.Generation, identity.Revision, identity.Transition));
    }

    [Theory]
    [InlineData("discrepancy.opened", "discrepancy:{id}:x:opened")]
    [InlineData("discrepancy.opened", "discrepancy:{id}:3:resolved")]
    [InlineData("discrepancy.resolved", "discrepancy:{other}:3:resolved")]
    [InlineData("monitor.state-changed", "monitor:{other}:7:degraded")]
    [InlineData("monitor.state-changed", "monitor:{account}:7:exploded")]
    [InlineData("credential.expiring", "credential:{account}:not-hex:expiring")]
    [InlineData("credential.expiring", "credential:{account}:A1:expiring:extra")]
    public void Malformed_or_mismatched_dedup_keys_do_not_parse(string type, string key)
    {
        var discrepancy = Guid.NewGuid();
        var message = Event(type, 1, new { }, playless: true, discrepancy: discrepancy) with
        {
            DedupKey = key.Replace("{id}", discrepancy.ToString()).Replace("{account}", Account.ToString()).Replace("{other}", Guid.NewGuid().ToString())
        };
        Assert.Null(NotificationIdentity.Parse(message));
    }

    [Fact]
    public void Rules_follow_trigger_age_category_and_severity()
    {
        var now = DateTimeOffset.UtcNow;
        var fill = Event("execution.filled", 1, new { }) with { DetectedAt = now };
        var defaults = NotificationPreferences.Defaults;
        Assert.True(NotificationRules.Eligible(fill, defaults, now, TimeSpan.FromDays(7)));
        Assert.False(NotificationRules.Eligible(fill with { Source = new("owner") }, defaults, now, TimeSpan.FromDays(7)));
        Assert.False(NotificationRules.Eligible(fill with { DetectedAt = now.AddDays(-8) }, defaults, now, TimeSpan.FromDays(7)));
        Assert.False(NotificationRules.Eligible(fill, defaults with { MinimumSeverity = "warning" }, now, TimeSpan.FromDays(7)));
        Assert.False(NotificationRules.Eligible(fill, defaults with { Categories = [NotificationValues.Monitor] }, now, TimeSpan.FromDays(7)));
        Assert.False(NotificationRules.Eligible(Event("monitor.run-completed", 1, new { }) with { DetectedAt = now }, defaults, now, TimeSpan.FromDays(7)));
        var test = Event(NotificationRules.TestType, 1, new { }) with { DetectedAt = now, Source = new("owner") };
        Assert.True(NotificationRules.Eligible(test, defaults with { MinimumSeverity = "critical", Categories = [] }, now, TimeSpan.FromDays(7)));
        Assert.False(NotificationRules.Eligible(test, defaults with { Enabled = false }, now, TimeSpan.FromDays(7)));
        Assert.False(new NotificationPreferences(true, "loud", [], 1).Valid());
        Assert.False(new NotificationPreferences(true, "info", ["executions", "executions"], 1).Valid());
    }

    [Fact]
    public void Notification_cursors_round_trip_and_reject_foreign_shapes()
    {
        var epoch = Guid.NewGuid();
        Assert.True(NotificationCursor.TryParse(NotificationCursor.Format(epoch, 3, 42), out var cursor));
        Assert.Equal(new NotificationCursor(epoch, 3, 42), cursor);
        Assert.True(NotificationCursor.TryParse("0.0.0", out var start));
        Assert.Equal(new NotificationCursor(null, 0, 0), start);
        foreach (var bad in new[] { null, "", "0", "0.1", $"{epoch:N}.42", $"{epoch:N}.-1.4", $"{epoch:N}.1.x", $"{epoch:D}.1.1", $"{epoch:N}".ToUpperInvariant() + ".1.1",
                     "00000000000000000000000000000000.1.1", "0.1.99999999999999999999" })
            Assert.False(NotificationCursor.TryParse(bad, out _), bad);
    }

    private static VesselEvent Event(string type, long sequence, object data, string severity = "info", Guid? play = null,
        string timeliness = "timely", string relevance = "current", Guid? discrepancy = null, bool playless = false) =>
        new(Guid.CreateVersion7(), type, 1, Guid.NewGuid(), sequence.ToString(), Guid.CreateVersion7(), $"{type}:{sequence}", severity,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, timeliness, relevance, null,
            new(Account, "hyperliquid", "BTC", playless ? null : play ?? Play, DiscrepancyId: discrepancy), new("monitor"),
            JsonSerializer.SerializeToElement(data));
}
