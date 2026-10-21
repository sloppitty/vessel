using Vessel.Application.Common;
using Vessel.Application.Review;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

/// <summary>
/// Phase 2 item 5a (G-07): Review's own rows as CSV. Exact decimal strings, unknown as an empty cell, text neutralized
/// against spreadsheet formulas, and the scored rows and exclusions of the report in one snapshot. Not a tax report.
/// </summary>
public sealed class ReviewExportTests
{
    private readonly ReviewFixture f = new();

    /// <summary>The rows as cells, parsing RFC 4180 quoting.</summary>
    private static List<string[]> Rows(string csv)
    {
        var rows = new List<string[]>();
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') { cells.Add(cell.ToString()); cell.Clear(); rows.Add([.. cells]); cells.Clear(); i++; }
            else cell.Append(c);
        }
        return rows;
    }

    private static Dictionary<string, string> Named(string[] header, string[] row) =>
        header.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => row[x.i]);

    [Fact]
    public void Text_that_would_be_a_formula_is_neutralized_and_numbers_stay_exact()
    {
        var csv = new Csv(["text", "number"]);
        foreach (var dangerous in new[] { "=HYPERLINK(\"x\")", "+1", "-1", "@SUM(A1)", "\tx", "\rx" })
            csv.Text(dangerous).Number("-12.5").End();
        var rows = Rows(csv.ToString());
        Assert.All(rows.Skip(1), row => Assert.StartsWith("'", row[0]));
        Assert.All(rows.Skip(1), row => Assert.Equal("-12.5", row[1]));
        Assert.Equal("'=HYPERLINK(\"x\")", rows[1][0]);
        // A plain decimal is written as is; anything else is refused, never rewritten.
        Assert.Throws<InvalidOperationException>(() => new Csv(["n"]).Number("1e2"));
        Assert.Throws<InvalidOperationException>(() => new Csv(["n"]).Number("=1"));
        // Unknown is an empty cell, never zero; a short row is refused.
        Assert.Equal(["", ""], Rows(new Csv(["a", "b"]).Number(null).Text(null).End().ToString())[1]);
        Assert.Throws<InvalidOperationException>(() => new Csv(["a", "b"]).Text("x").End());
    }

    [Fact]
    public async Task The_export_holds_the_reports_scored_rows_then_its_exclusions()
    {
        // An owner-written name a spreadsheet would run as a formula.
        var account = f.Account("=cmd|' /C calc'!A0");
        f.Add(20, ReviewClock.Now.AddDays(-2), account);
        var loss = f.Add(-5.25m, ReviewClock.Now.AddDays(-1), account, risk: false);
        var report = await f.Service.GetAsync(new(), default);
        var export = await f.Service.ExportAsync(new(), default);
        Assert.Equal("vessel-review-month-20261009.csv", export.FileName);
        var rows = Rows(export.Content);
        Assert.Equal(ReviewService.ExportColumns, rows[0]);
        Assert.Equal("notice", rows[1][0]);
        Assert.Contains("Not a tax report.", rows[1][3]);
        var scored = rows.Skip(2).Select(r => Named(rows[0], r)).ToList();
        // The report's rows, newest first, with the same exact strings.
        Assert.Equal(report.Plays.Select(p => p.Id!.Value.ToString()), scored.Select(r => r["play_id"]));
        Assert.Equal(report.Plays.Select(p => p.Net), scored.Select(r => r["net_usd"]));
        Assert.Equal("-5.25", scored[0]["net_usd"]);
        Assert.Equal(("scored", "closed", "planned", "imported", "USDC=0"), (scored[0]["kind"], scored[0]["status"], scored[0]["origin"],
            scored[0]["execution_source"], scored[0]["fees"]));
        // R is unknown without a stop: an empty cell with its reason, never zero.
        Assert.Equal("", scored[0]["estimated_r"]);
        Assert.NotEqual("", scored[0]["estimated_r_reason"]);
        Assert.Equal("4", scored[1]["estimated_r"]);
        Assert.Equal("'=cmd|' /C calc'!A0", scored[1]["account"]);
        Assert.Equal(loss.Play.Id.ToString(), scored[0]["play_id"]);
    }

    [Fact]
    public async Task Exclusions_follow_with_their_reasons_and_no_amounts()
    {
        var account = f.Account();
        var scored = f.Add(10, ReviewClock.Now.AddDays(-1), account);
        var unscored = f.Add(10, ReviewClock.Now.AddDays(-1), account);
        // A missing entry fill: Review excludes it and the export says why, with no amount.
        f.Closed.Remove(unscored);
        f.Closed.Add(unscored with { Fills = unscored.Fills.Where(x => x.Role != OrderLinkRole.Entry).ToList() });
        var exclusions = await f.Service.ExclusionsAsync(new(), default);
        var rows = Rows((await f.Service.ExportAsync(new(), default)).Content);
        var named = rows.Skip(2).Select(r => Named(rows[0], r)).ToList();
        Assert.Equal(["scored", "excluded"], named.Select(r => r["kind"]));
        Assert.Equal(scored.Play.Id.ToString(), named[0]["play_id"]);
        var excluded = named[1];
        Assert.Equal((unscored.Play.Id.ToString(), "", "", "closed"), (excluded["play_id"], excluded["net_usd"], excluded["return_percent"], excluded["status"]));
        Assert.Equal(string.Join("; ", exclusions.Items.Single().Reasons), excluded["exclusion_reasons"]);
    }
}
