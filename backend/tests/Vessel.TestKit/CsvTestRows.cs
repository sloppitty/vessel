namespace Vessel.Tests;

internal static class CsvTestRows
{

    /// <summary>The rows as cells, parsing RFC 4180 quoting.</summary>
    internal static List<string[]> Rows(string csv)
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
}
