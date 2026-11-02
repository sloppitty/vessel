using Vessel.Application.Common;
using static Vessel.Tests.CsvTestRows;

namespace Vessel.Tests;

public sealed class CsvTests
{

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
        // Sol round 1 on #38: no trailing newline and no other scripts' digits.
        Assert.Throws<InvalidOperationException>(() => new Csv(["n"]).Number("1\n"));
        Assert.Throws<InvalidOperationException>(() => new Csv(["n"]).Number("\u0661"));
        // Unknown is an empty cell, never zero; a short row is refused.
        Assert.Equal(["", ""], Rows(new Csv(["a", "b"]).Number(null).Text(null).End().ToString())[1]);
        Assert.Throws<InvalidOperationException>(() => new Csv(["a", "b"]).Text("x").End());
    }
}
