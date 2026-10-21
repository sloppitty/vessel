using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Vessel.Application.Common;

/// <summary>
/// RFC 4180 CSV for owner exports (Phase 2, item 5). Text cells that a spreadsheet would read as a formula (a leading
/// <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or carriage return) get a leading <c>'</c>. Numeric cells must be plain
/// decimal strings and are written exactly as they are, so a negative amount is never altered. Unknown is an empty cell,
/// never <c>0</c>. Times are UTC ISO-8601.
/// </summary>
public sealed partial class Csv
{
    private readonly StringBuilder text = new();
    private readonly int columns;
    private int cells;

    public Csv(IReadOnlyList<string> header)
    {
        columns = header.Count;
        foreach (var name in header) Text(name);
        End();
    }

    public Csv Text(string? value)
    {
        var cell = value ?? "";
        if (cell.Length > 0 && cell[0] is '=' or '+' or '-' or '@' or '\t' or '\r') cell = "'" + cell;
        return Cell(cell);
    }

    /// <summary>A plain decimal string, written exactly; null is an empty cell.</summary>
    public Csv Number(string? value)
    {
        if (value is not null && !PlainDecimal().IsMatch(value))
            throw new InvalidOperationException("Numeric export cells must be plain decimal strings.");
        return Cell(value ?? "");
    }

    public Csv Count(int value) => Cell(value.ToString(CultureInfo.InvariantCulture));
    public Csv Flag(bool? value) => Cell(value switch { true => "true", false => "false", null => "" });
    public Csv Time(DateTimeOffset? value) => Cell(value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture) ?? "");

    /// <summary>Ends the row; every row has the header's number of cells.</summary>
    public Csv End()
    {
        if (cells != columns) throw new InvalidOperationException($"A CSV row has {cells} cells; the header has {columns}.");
        text.Append("\r\n");
        cells = 0;
        return this;
    }

    public override string ToString() => text.ToString();

    private Csv Cell(string value)
    {
        if (cells++ > 0) text.Append(',');
        if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0) text.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
        else text.Append(value);
        return this;
    }

    [GeneratedRegex(@"^-?\d+(\.\d+)?$")]
    private static partial Regex PlainDecimal();
}
