using System.Net;
using Vessel.Application.Plays;
using Vessel.Application.Reporting;

namespace Vessel.Tests.Isolation;

/// <summary>Each owner's reporting setting is their own, and so is the week Review computes from it (Phase 2, item 2).</summary>
public abstract class ReportingIsolationCases(IsolationHost api)
{
    [PostgresFact]
    [Covers("GET", "/api/settings/reporting")]
    [Covers("PUT", "/api/settings/reporting")]
    public async Task The_reporting_setting_and_its_weeks_stay_with_their_owner()
    {
        var (a, b) = await api.Owners();
        var initial = await a.GetJson("/api/settings/reporting");
        Assert.Equal(("UTC", "monday", true), (initial.GetProperty("timeZone").GetString(), initial.GetProperty("weekStart").GetString(),
            initial.GetProperty("isDefault").GetBoolean()));
        await a.Send(HttpMethod.Put, "/api/settings/reporting", new UpdateReportingSettingsRequest("Europe/Madrid", "sunday"));
        foreach (var (owner, zone, start, isDefault) in new[] { (a, "Europe/Madrid", "sunday", false), (b, "UTC", "monday", true) })
        {
            var settings = await owner.GetJson("/api/settings/reporting");
            Assert.Equal((zone, start, isDefault), (settings.GetProperty("timeZone").GetString(), settings.GetProperty("weekStart").GetString(),
                settings.GetProperty("isDefault").GetBoolean()));
            var week = (await owner.GetJson("/api/review?week=2026-01-07")).GetProperty("week");
            Assert.Equal((zone, start), (week.GetProperty("timeZone").GetString(), week.GetProperty("weekStart").GetString()));
            // The setting belongs to neither trading mode, so a paper Review uses it too.
            var paper = (await owner.GetJson("/api/review?week=2026-01-07&tradingMode=paper")).GetProperty("week");
            Assert.Equal(zone, paper.GetProperty("timeZone").GetString());
        }
        // A Draft with no fills is data for its week (Sol round 1 on item 2), and only for its owner.
        var account = (await a.ManualAccount(Isolated.Marker("week"))).GetProperty("id").GetGuid();
        await a.Post("/api/plays", new CreatePlayRequest(account, "BTC", "manual", Isolated.Marker("draft"), TestPlays.Plan()), HttpStatusCode.Created);
        Assert.Equal("empty", (await a.GetJson("/api/review?week=current")).GetProperty("week").GetProperty("data").GetString());
        Assert.Equal("none", (await b.GetJson("/api/review?week=current")).GetProperty("week").GetProperty("data").GetString());
        await a.Send(HttpMethod.Put, "/api/settings/reporting", new UpdateReportingSettingsRequest("Romance Standard Time", "monday"),
            HttpStatusCode.BadRequest);
        await a.Send(HttpMethod.Put, "/api/settings/reporting", new UpdateReportingSettingsRequest("UTC", "tuesday"), HttpStatusCode.BadRequest);
        Assert.Equal("Europe/Madrid", (await a.GetJson("/api/settings/reporting")).GetProperty("timeZone").GetString());
    }
}

/// <summary>The cases above in Token mode.</summary>
public sealed class ReportingIsolationTests(IsolationApi api) : ReportingIsolationCases(api), IClassFixture<IsolationApi>;

/// <summary>The same cases in Wallet mode.</summary>
public sealed class WalletReportingIsolationTests(WalletIsolationApi api) : ReportingIsolationCases(api), IClassFixture<WalletIsolationApi>;
