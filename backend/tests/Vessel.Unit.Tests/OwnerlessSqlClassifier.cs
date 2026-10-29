using System.Text;
using System.Text.RegularExpressions;

namespace Vessel.Tests;

/// <summary>
/// Decides which C# string literals are raw SQL that needs an entry in the reviewed ownerless inventory
/// (contract §9.1). Detection fails closed: a literal is SQL when, after leading whitespace, <c>;</c>, <c>(</c> and
/// (nested) comments, it or any statement after a <c>;</c> starts with a PostgreSQL command keyword in any case, or
/// when it contains a comment marker and a command keyword anywhere. Prose that starts with such a word ("Set …",
/// "Do not …", "refresh") is therefore reviewed too, as "Not SQL: …" entries; a missed statement is the danger.
/// Only a small, demonstrably owner-safe subset is exempt; everything else is reviewed:
/// <list type="bullet">
/// <item>a single-table SELECT, UPDATE or DELETE whose top-level WHERE has an exact owner-equality conjunct;</item>
/// <item>a single-row INSERT whose owner column receives the owner value (or an INSERT … SELECT from an owner-scoped
/// single table or from <c>unnest</c> of parameters), with ON CONFLICT DO UPDATE only on a target that includes the owner column.</item>
/// </list>
/// Neither may assign an owner column (UPDATE SET or ON CONFLICT DO UPDATE SET), which would move rows to another owner.
/// Nothing is exempt that contains a comment, a <c>U&amp;</c> escape, a quoted identifier other than <c>"OwnerId"</c>,
/// a <c>$</c> other than a positional parameter, a prefixed string (<c>E''</c>, <c>B''</c> …), a backslash in a string,
/// or anything else the normaliser cannot tokenise. CTEs, set operations, OR, NOT/IS around the owner predicate, joins,
/// subqueries, multi-row VALUES and text spliced into parameterised SQL are never exempt either.
/// Interpolation holes in SQL without @parameters are taken as EF Core parameters.
/// </summary>
internal static class OwnerlessSqlClassifier
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase;

    // Every PostgreSQL command keyword (the SQL Commands reference), matched in any case.
    private static readonly HashSet<string> Starters = new(StringComparer.OrdinalIgnoreCase)
    {
        "ABORT", "ALTER", "ANALYZE", "ANALYSE", "BEGIN", "CALL", "CHECKPOINT", "CLOSE", "CLUSTER", "COMMENT", "COMMIT",
        "COPY", "CREATE", "DEALLOCATE", "DECLARE", "DELETE", "DISCARD", "DO", "DROP", "END", "EXECUTE", "EXPLAIN",
        "FETCH", "GRANT", "IMPORT", "INSERT", "LISTEN", "LOAD", "LOCK", "MERGE", "MOVE", "NOTIFY", "PREPARE",
        "REASSIGN", "REFRESH", "REINDEX", "RELEASE", "RESET", "REVOKE", "ROLLBACK", "SAVEPOINT", "SECURITY", "SELECT",
        "SET", "SHOW", "START", "TABLE", "TRUNCATE", "UNLISTEN", "UPDATE", "VACUUM", "VALUES", "WITH",
    };
    private static readonly Regex AnyStarter = new(@"(?<![\w$])(?:" + string.Join('|', Starters) + @")(?![\w$])", Ci);
    private static readonly Regex UnicodeEscape = new(@"u&\s*[""']", Ci);

    private const string OwnerColumn = @"(?:\w+\.)?(?:owner_id|""OwnerId"")";
    private const string OwnerValue = @"(?:@owner(?:Id)?\b|\{\s*(?:[\w.]+\.)?(?:Current)?Owner(?:Id)?(?:\.Value)?\s*\})";
    private const string Table = @"(?:\w+\.)?\w+";
    // Any mention of an owner column, quoted or not, qualified or not, in any case: assigning one moves rows between owners.
    private static readonly Regex OwnerMention = new(@"(?<![\w""])(?:owner_?id|""owner_?id"")(?![\w""])", Ci);
    private static readonly Regex OwnerEquality = new($@"^(?:{OwnerColumn}\s*=\s*{OwnerValue}|{OwnerValue}\s*=\s*{OwnerColumn})$", Ci);
    private static readonly Regex OwnerColumnOnly = new($"^{OwnerColumn}$", Ci);
    private static readonly Regex OwnerValueOnly = new($"^{OwnerValue}$", Ci);
    private static readonly Regex Unsupported = new(
        @"\b(?:OR|UNION|INTERSECT|EXCEPT|JOIN|USING|LATERAL|BETWEEN)\b|\bWITH\b(?!\s+ORDINALITY\b)|\(\s*(?:SELECT|VALUES|TABLE|WITH)\b", Ci);
    private static readonly Regex SingleTable = new($@"^\s*{Table}(?:\s+(?:AS\s+)?\w+)?\s*$", Ci);
    private static readonly Regex InsertHead = new($@"^INSERT\s+INTO\s+{Table}(?:\s+AS\s+\w+)?\s*\(", Ci);
    private static readonly Regex InsertTail = new(
        @"^\s*(?:ON\s+CONFLICT\s*(?:\((?<target>[^()]*)\))?\s*DO\s+(?:NOTHING|(?<update>UPDATE\s+SET\b[\s\S]*?)))?\s*(?:RETURNING\b[\s\S]*)?$", Ci);
    private static readonly Regex UnnestSource = new(
        @"^\s*unnest\s*\(\s*(?:@\w+\s*,\s*)*@\w+\s*\)(?:\s+WITH\s+ORDINALITY)?(?:\s+(?:AS\s+)?\w+(?:\s*\(\s*\w+(?:\s*,\s*\w+)*\s*\))?)?" +
        @"(?:\s+ORDER\s+BY\s+[\w.]+(?:\s*,\s*[\w.]+)*)?\s*$", Ci);
    private static readonly Regex WhereEnd = new(@"\b(?:ORDER\s+BY|GROUP\s+BY|HAVING|WINDOW|LIMIT|OFFSET|FETCH|FOR|RETURNING)\b", Ci);

    /// <summary>
    /// C# string literal tokens in source: raw (three or more quotes, any $ prefix), verbatim and regular, each with
    /// an optional interpolation prefix.
    /// </summary>
    public static readonly Regex SourceLiteral =
        new(@"\$*(?<q>""{3,})[\s\S]*?\k<q>(?!"")|\$?@""(?:[^""]|"""")*""|\$?""(?:\\.|[^""\\])*""");

    /// <summary>True when a source literal token (including its quotes and $/@ prefix) needs a reviewed inventory entry.</summary>
    public static bool LiteralRequiresReview(string token)
    {
        var text = Decode(token, out var complete);
        // An escape that cannot be decoded leaves the runtime text unknown: any command keyword at all is reviewed.
        return complete ? RequiresReview(text) : IsSql(text) || AnyStarter.IsMatch(text);
    }

    /// <summary>The SQL text a source literal token (including its quotes and $/@ prefix) produces at runtime.</summary>
    public static string Text(string token) => Decode(token, out _);

    /// <summary>
    /// Decodes a literal with C#'s UTF-16 semantics: <c>\uXXXX</c> and <c>\xH…</c> are single code units (lone
    /// surrogates included) and <c>\UXXXXXXXX</c> is a code point. An invalid <c>\U</c> value becomes U+FFFD and
    /// clears <paramref name="complete"/> instead of throwing.
    /// </summary>
    public static string Decode(string token, out bool complete)
    {
        complete = true;
        var literal = token.TrimStart('$');
        var raw = literal.StartsWith("\"\"\"", StringComparison.Ordinal);
        var verbatim = !raw && literal.StartsWith('@');
        // A raw literal's delimiter is three or more quotes, the same number at each end.
        var quote = raw ? literal.Length - literal.TrimStart('"').Length : 1;
        var body = literal[((verbatim ? 1 : 0) + quote)..^quote];
        if (raw) return body;
        if (verbatim) return body.Replace("\"\"", "\"", StringComparison.Ordinal);
        var text = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\' || i + 1 == body.Length) { text.Append(body[i]); continue; }
            var next = body[++i];
            // \uXXXX, \UXXXXXXXX and \x with one to four hex digits; the compiler has already rejected malformed escapes.
            var digits = next switch { 'u' => 4, 'U' => 8, 'x' => HexRun(body, i + 1, 4), _ => 0 };
            if (digits > 0 && i + digits < body.Length && HexRun(body, i + 1, digits) == digits)
            {
                var value = Convert.ToInt64(body.Substring(i + 1, digits), 16);
                if (next != 'U') text.Append((char)value);
                else if (value <= 0x10FFFF && value is < 0xD800 or > 0xDFFF) text.Append(char.ConvertFromUtf32((int)value));
                else { text.Append('\uFFFD'); complete = false; }
                i += digits;
                continue;
            }
            text.Append(next switch
            {
                'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v', 'e' => '\u001b',
                _ => next,
            });
        }
        return text.ToString();
    }

    private static int HexRun(string text, int start, int max)
    {
        var count = 0;
        while (count < max && start + count < text.Length && Uri.IsHexDigit(text[start + count])) count++;
        return count;
    }

    /// <summary>
    /// True when the text or any statement after a <c>;</c> starts (after whitespace, <c>;</c>, <c>(</c> and comments)
    /// with a command keyword in any case, or when it holds a comment marker and a command keyword anywhere.
    /// </summary>
    public static bool IsSql(string text)
    {
        if (StartsWithCommand(text)) return true;
        for (var i = text.IndexOf(';'); i >= 0; i = text.IndexOf(';', i + 1))
            if (StartsWithCommand(text[(i + 1)..])) return true;
        return HasComment(text) && AnyStarter.IsMatch(text);
    }

    private static bool StartsWithCommand(string text)
    {
        var start = LeadingNoise(text);
        var end = start;
        while (end < text.Length && IsWord(text[end])) end++;
        return end > start && Starters.Contains(text[start..end]);
    }

    private static bool HasComment(string text) => text.Contains("--", StringComparison.Ordinal) || text.Contains("/*", StringComparison.Ordinal);

    // The length of the leading whitespace, semicolons, parentheses, line comments and (nested) block comments.
    private static int LeadingNoise(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]) || text[i] is '(' or ';') i++;
            else if (At(text, i, "--")) { while (i < text.Length && text[i] != '\n') i++; }
            else if (At(text, i, "/*"))
            {
                // PostgreSQL block comments nest; an unterminated one runs to the end.
                for (var depth = 0; i < text.Length;)
                {
                    if (At(text, i, "/*")) { depth++; i += 2; }
                    else if (At(text, i, "*/")) { i += 2; if (--depth == 0) break; }
                    else i++;
                }
            }
            else break;
        }
        return i;
    }

    /// <summary>True when the text is SQL and is not, statement by statement, inside the owner-safe subset.</summary>
    public static bool RequiresReview(string text) =>
        IsSql(text) && (HasComment(text) || UnicodeEscape.IsMatch(text) || Normalize(text) is not { } sql ||
                        sql.Split(';').Any(s => !string.IsNullOrWhiteSpace(s) && !OwnerScoped(s.Trim())));

    private static bool OwnerScoped(string statement)
    {
        if (Flatten(statement) is not { } flat || Unsupported.IsMatch(statement)) return false;
        // A {hole} in SQL that also binds @parameters is spliced text, not a parameter.
        if (statement.Contains('@') && statement.Contains('{')) return false;
        if (Regex.IsMatch(statement, @"^INSERT\b", Ci)) return InsertsOwner(statement);
        return Regex.IsMatch(statement, @"^(?:SELECT|UPDATE|DELETE)\b", Ci) && Filtered(statement, flat);
    }

    // A single-table SELECT/UPDATE/DELETE whose top-level WHERE has an exact owner-equality conjunct.
    private static bool Filtered(string statement, string flat)
    {
        var where = Regex.Match(flat, @"\bWHERE\b", Ci);
        if (!where.Success) return false;
        var head = flat[..where.Index];
        string target;
        if (Regex.IsMatch(head, @"^SELECT\b", Ci))
        {
            var from = Regex.Match(head, @"\bFROM\b", Ci);
            if (!from.Success) return false;
            target = head[(from.Index + from.Length)..];
        }
        else
        {
            var update = Regex.Match(head, @"^UPDATE\s+(?:ONLY\s+)?(?<t>[\s\S]*?)\s+SET\b(?<set>[\s\S]*)$", Ci);
            var delete = Regex.Match(head, @"^DELETE\s+FROM\s+(?:ONLY\s+)?(?<t>[\s\S]*)$", Ci);
            if (update.Success && Regex.IsMatch(update.Groups["set"].Value, @"\bFROM\b", Ci)) return false;
            var set = update.Groups["set"];
            if (update.Success && OwnerMention.IsMatch(statement.Substring(set.Index, set.Length))) return false;
            target = update.Success ? update.Groups["t"].Value : delete.Success ? delete.Groups["t"].Value : "";
        }
        if (!SingleTable.IsMatch(target)) return false;
        var start = where.Index + where.Length;
        var end = WhereEnd.Match(flat, start);
        var condition = statement[start..(end.Success ? end.Index : flat.Length)];
        // CASE … END and ARRAY[…] are not parenthesised, so an AND inside them would look like a top-level conjunct.
        if (Regex.IsMatch(condition, @"\bCASE\b|\[(?!\])", Ci)) return false;
        return Conjuncts(condition).Any(c => OwnerEquality.IsMatch(c));
    }

    // The top-level AND operands of a condition, with fully parenthesised AND groups expanded.
    private static IEnumerable<string> Conjuncts(string condition)
    {
        if (Flatten(condition) is not { } flat) yield break;
        var start = 0;
        foreach (var end in Regex.Matches(flat, @"\bAND\b", Ci).Select(m => m.Index).Append(flat.Length))
        {
            var part = condition[start..end].Trim();
            start = Math.Min(end + 3, flat.Length);
            if (part.StartsWith('(') && Closing(part, 0) == part.Length - 1)
                foreach (var inner in Conjuncts(part[1..^1])) yield return inner;
            else yield return part;
        }
    }

    // An INSERT writes its own owner when the value at the owner column's position is the owner parameter.
    private static bool InsertsOwner(string statement)
    {
        var head = InsertHead.Match(statement);
        if (!head.Success) return false;
        var columns = Items(statement, head.Length - 1, out var after);
        var owner = columns.FindIndex(c => OwnerColumnOnly.IsMatch(c));
        var rest = statement[after..];
        List<string> row;
        string tail;
        var values = Regex.Match(rest, @"^\s*VALUES\s*\(", Ci);
        if (values.Success)
        {
            row = Items(rest, values.Length - 1, out var rowEnd);
            tail = rest[rowEnd..];
            if (tail.TrimStart().StartsWith(',')) return false; // multi-row VALUES
        }
        else if (Regex.IsMatch(rest, @"^\s*SELECT\b", Ci) && Flatten(rest) is { } flat)
        {
            var split = Regex.Match(flat, @"\b(?:ON\s+CONFLICT|RETURNING)\b", Ci);
            var query = rest[..(split.Success ? split.Index : rest.Length)].Trim();
            tail = split.Success ? rest[split.Index..] : "";
            var from = Regex.Match(Flatten(query)!, @"\bFROM\b", Ci);
            if (from.Success && !UnnestSource.IsMatch(query[(from.Index + from.Length)..]) && !Filtered(query, Flatten(query)!))
                return false;
            var list = query[6..(from.Success ? from.Index : query.Length)];
            row = Split(list);
        }
        else return false;
        if (owner < 0 || owner >= row.Count || !OwnerValueOnly.IsMatch(row[owner])) return false;
        var conflict = InsertTail.Match(tail);
        if (!conflict.Success) return false;
        if (!conflict.Groups["update"].Success) return true;
        var assignments = conflict.Groups["update"].Value;
        var condition = Flatten(assignments) is { } flatAssignments ? Regex.Match(flatAssignments, @"\bWHERE\b", Ci) : null;
        if (condition is null || OwnerMention.IsMatch(condition.Success ? assignments[..condition.Index] : assignments)) return false;
        return conflict.Groups["target"].Value.Split(',').Any(c => OwnerColumnOnly.IsMatch(c.Trim()));
    }

    // Blanks string literals so that only SQL structure remains. Null, so that the statement is reviewed, whenever the
    // text holds anything whose meaning the classifier does not fully model: a comment, a quoted identifier other than
    // "OwnerId", a $ that is not a positional parameter (dollar quoting, $ in names), a prefixed string (E'', U&'', B''
    // …), a backslash in a string, or an unterminated quote.
    private static string? Normalize(string text)
    {
        var sql = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            if (At(text, i, "--") || At(text, i, "/*")) return null;
            if (text[i] == '\'')
            {
                if (i > 0 && (IsWord(text[i - 1]) || text[i - 1] is '&' or '$')) return null;
                for (i++; ; i++)
                {
                    if (i >= text.Length || text[i] == '\\') return null;
                    if (At(text, i, "''")) i++;
                    else if (text[i] == '\'') { i++; break; }
                }
                sql.Append("''");
            }
            else if (text[i] == '$')
            {
                if (i + 1 >= text.Length || !char.IsAsciiDigit(text[i + 1]) || (i > 0 && IsWord(text[i - 1]))) return null;
                sql.Append(text[i++]);
            }
            else if (text[i] == '"')
            {
                // Only the exact "OwnerId" column is understood; every other quoted name is reviewed.
                if (!At(text, i, "\"OwnerId\"") || (i > 0 && (IsWord(text[i - 1]) || text[i - 1] == '&'))) return null;
                sql.Append("\"OwnerId\"");
                i += "\"OwnerId\"".Length;
                if (i < text.Length && text[i] == '"') return null;
            }
            else sql.Append(text[i++]);
        }
        return sql.ToString();
    }

    // The text with everything inside parentheses and braces blanked, so regexes only see the top level; null when unbalanced.
    private static string? Flatten(string text)
    {
        var flat = new StringBuilder(text.Length);
        var depth = 0;
        foreach (var c in text)
        {
            if (c is ')' or '}' && --depth < 0) return null;
            flat.Append(depth > 0 ? ' ' : c);
            if (c is '(' or '{') depth++;
        }
        return depth == 0 ? flat.ToString() : null;
    }

    private static int Closing(string text, int open) =>
        Flatten(text[open..]) is { } flat ? open + flat.IndexOf(text[open] == '(' ? ')' : '}', 1) : -1;

    // The comma-separated items inside the parentheses that open at the given index.
    private static List<string> Items(string text, int open, out int end)
    {
        var close = Closing(text, open);
        if (close < 0) { end = text.Length; return []; }
        end = close + 1;
        return Split(text[(open + 1)..close]);
    }

    private static List<string> Split(string list)
    {
        var flat = Flatten(list);
        if (flat is null) return [];
        var items = new List<string>();
        var start = 0;
        for (var i = 0; i <= flat.Length; i++)
            if (i == flat.Length || flat[i] == ',') { items.Add(list[start..i].Trim()); start = i + 1; }
        return items;
    }

    private static bool At(string text, int index, string token) => string.CompareOrdinal(text, index, token, 0, token.Length) == 0;

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
}
