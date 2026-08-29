using System.Text;

namespace Groundwork.Sqlite;

/// <summary>V1-compatible SQL-aware editor for SQLite sqlite_master CREATE TABLE text.</summary>
internal static class SqliteCreateTableSql
{
    public static string ExtractColumnDeclaration(string sql, string column)
    {
        var parsed = Parse(sql);
        var item = parsed.FindColumn(column);
        return sql[item.Identifier.End..item.End];
    }

    public static string ExtractCollation(string declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        var keyword = FindTopLevelKeyword(declaration, "COLLATE");
        return keyword < 0
            ? "BINARY"
            : ReadIdentifier(declaration[(keyword + "COLLATE".Length)..]);
    }

    public static string ReplaceTableAndColumn(string sql, string expectedTable, string replacementTable, string column, string replacementColumn)
    {
        var parsed = Parse(sql);
        if (!string.Equals(parsed.Table.Value, expectedTable, StringComparison.Ordinal))
            throw new InvalidOperationException($"Physical table creation SQL declares '{parsed.Table.Value}' instead of expected table '{expectedTable}'.");
        var item = parsed.FindColumn(column);
        return ApplyReplacements(sql, new Replacement(item.Start, item.End, replacementColumn), new Replacement(parsed.Table.Start, parsed.Table.End, replacementTable));
    }

    public static string AddTableConstraint(
        string sql,
        string expectedTable,
        string replacementTable,
        string constraint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraint);
        var parsed = Parse(sql);
        if (!string.Equals(parsed.Table.Value, expectedTable, StringComparison.Ordinal))
            throw new InvalidOperationException($"Physical table creation SQL declares '{parsed.Table.Value}' instead of expected table '{expectedTable}'.");
        return ApplyReplacements(
            sql,
            new Replacement(parsed.BodyEnd, parsed.BodyEnd, $", {constraint}"),
            new Replacement(parsed.Table.Start, parsed.Table.End, replacementTable));
    }

    public static string? ExtractNamedConstraint(string sql, string constraint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraint);
        var parsed = Parse(sql);
        foreach (var item in parsed.Items)
        {
            if (!string.Equals(item.Identifier.Value, "CONSTRAINT", StringComparison.OrdinalIgnoreCase))
                continue;
            var index = item.Identifier.End;
            SkipTrivia(sql, ref index, item.End);
            var name = ReadIdentifierToken(sql, ref index, item.End);
            if (string.Equals(name.Value, constraint, StringComparison.Ordinal))
                return sql[item.Start..item.End];
        }
        return null;
    }

    public static int FindKeyword(string value, string keyword)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        for (var index = 0; index <= value.Length - keyword.Length;)
        {
            if (TrySkipSyntax(value, ref index)) continue;
            if (value.AsSpan(index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase) &&
                IsKeywordBoundary(value, index - 1) && IsKeywordBoundary(value, index + keyword.Length)) return index;
            index++;
        }
        return -1;
    }

    private static int FindTopLevelKeyword(string value, string keyword)
    {
        var depth = 0;
        for (var index = 0; index < value.Length;)
        {
            if (TrySkipSyntax(value, ref index)) continue;
            if (value[index] == '(')
            {
                depth++;
                index++;
                continue;
            }
            if (value[index] == ')')
            {
                if (depth == 0)
                    throw new InvalidOperationException("Physical column definition has an unmatched closing parenthesis.");
                depth--;
                index++;
                continue;
            }
            if (depth == 0 && index <= value.Length - keyword.Length &&
                value.AsSpan(index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase) &&
                IsKeywordBoundary(value, index - 1) && IsKeywordBoundary(value, index + keyword.Length))
            {
                return index;
            }
            index++;
        }
        if (depth != 0)
            throw new InvalidOperationException("Physical column definition has an unmatched opening parenthesis.");
        return -1;
    }

    public static string ReadIdentifier(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var index = 0;
        SkipTrivia(value, ref index);
        return ReadIdentifierToken(value, ref index).Value;
    }

    private static ParsedCreateTable Parse(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var index = 0;
        ConsumeKeyword(sql, ref index, "CREATE");
        if (!TryConsumeKeyword(sql, ref index, "TEMP")) TryConsumeKeyword(sql, ref index, "TEMPORARY");
        ConsumeKeyword(sql, ref index, "TABLE");
        if (TryConsumeKeyword(sql, ref index, "IF")) { ConsumeKeyword(sql, ref index, "NOT"); ConsumeKeyword(sql, ref index, "EXISTS"); }
        SkipTrivia(sql, ref index);
        var table = ReadIdentifierToken(sql, ref index);
        SkipTrivia(sql, ref index);
        if (index >= sql.Length || sql[index] != '(') throw new InvalidOperationException("Physical table definition is missing its column-list opening parenthesis.");
        var items = ReadTopLevelItems(sql, index, out var bodyEnd);
        return new ParsedCreateTable(table, items, bodyEnd);
    }

    private static IReadOnlyList<SqlItem> ReadTopLevelItems(string sql, int bodyStart, out int bodyEnd)
    {
        var items = new List<SqlItem>();
        var itemStart = bodyStart + 1;
        var depth = 0;
        for (var index = itemStart; index < sql.Length;)
        {
            if (TrySkipSyntax(sql, ref index)) continue;
            switch (sql[index])
            {
                case '(': depth++; index++; break;
                case ')' when depth > 0: depth--; index++; break;
                case ')' when depth == 0:
                    AddItem(sql, itemStart, index, items);
                    bodyEnd = index;
                    return items;
                case ',' when depth == 0: AddItem(sql, itemStart, index, items); itemStart = ++index; break;
                default: index++; break;
            }
        }
        throw new InvalidOperationException("Physical table definition is missing its closing parenthesis.");
    }

    private static void AddItem(string sql, int start, int end, ICollection<SqlItem> items)
    {
        SkipTrivia(sql, ref start, end);
        while (end > start && char.IsWhiteSpace(sql[end - 1])) end--;
        if (start >= end) return;
        var identifierIndex = start;
        var identifier = ReadIdentifierToken(sql, ref identifierIndex, end);
        items.Add(new SqlItem(start, end, identifier));
    }

    private static bool TrySkipSyntax(string sql, ref int index)
    {
        if (index >= sql.Length) return false;
        if (sql[index] is '\'' or '"' or '`' or '[') { SkipQuoted(sql, ref index); return true; }
        if (index + 1 < sql.Length && sql[index] == '-' && sql[index + 1] == '-')
        {
            index += 2; while (index < sql.Length && sql[index] is not '\r' and not '\n') index++; return true;
        }
        if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
        {
            var end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
            if (end < 0) throw new InvalidOperationException("Physical table definition contains an unterminated block comment.");
            index = end + 2; return true;
        }
        return false;
    }

    private static void SkipQuoted(string sql, ref int index)
    {
        var opening = sql[index++];
        var closing = opening == '[' ? ']' : opening;
        while (index < sql.Length)
        {
            if (sql[index] != closing) { index++; continue; }
            if (opening != '[' && index + 1 < sql.Length && sql[index + 1] == closing) { index += 2; continue; }
            index++; return;
        }
        throw new InvalidOperationException("Physical table definition contains an unterminated quoted token.");
    }

    private static IdentifierToken ReadIdentifierToken(string sql, ref int index, int? limit = null)
    {
        var endLimit = limit ?? sql.Length;
        if (index >= endLimit) throw new InvalidOperationException("Physical table definition is missing an identifier.");
        var start = index;
        var opening = sql[index];
        if (opening is '"' or '\'' or '`' or '[')
        {
            var closing = opening == '[' ? ']' : opening;
            var value = new StringBuilder(); index++;
            while (index < endLimit)
            {
                if (sql[index] != closing) { value.Append(sql[index++]); continue; }
                if (opening != '[' && index + 1 < endLimit && sql[index + 1] == closing) { value.Append(closing); index += 2; continue; }
                index++; return new IdentifierToken(start, index, value.ToString());
            }
            throw new InvalidOperationException("Physical table definition contains an unterminated quoted identifier.");
        }
        while (index < endLimit && !char.IsWhiteSpace(sql[index]) && sql[index] is not '(' and not ')' and not ',') index++;
        if (index == start) throw new InvalidOperationException("Physical table definition is missing an identifier.");
        return new IdentifierToken(start, index, sql[start..index]);
    }

    private static void ConsumeKeyword(string sql, ref int index, string keyword)
    {
        if (!TryConsumeKeyword(sql, ref index, keyword)) throw new InvalidOperationException($"Physical table definition is missing expected keyword '{keyword}'.");
    }

    private static bool TryConsumeKeyword(string sql, ref int index, string keyword)
    {
        SkipTrivia(sql, ref index);
        if (index + keyword.Length > sql.Length || !sql.AsSpan(index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase) || !IsKeywordBoundary(sql, index + keyword.Length)) return false;
        index += keyword.Length; return true;
    }

    private static void SkipTrivia(string sql, ref int index, int? limit = null)
    {
        var end = limit ?? sql.Length;
        while (index < end)
        {
            if (char.IsWhiteSpace(sql[index])) { index++; continue; }
            if (index + 1 < end && sql[index] == '-' && sql[index + 1] == '-') { index += 2; while (index < end && sql[index] is not '\r' and not '\n') index++; continue; }
            if (index + 1 < end && sql[index] == '/' && sql[index + 1] == '*')
            {
                var commentEnd = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (commentEnd < 0 || commentEnd + 2 > end) throw new InvalidOperationException("Physical table definition contains an unterminated block comment.");
                index = commentEnd + 2; continue;
            }
            break;
        }
    }

    private static bool IsKeywordBoundary(string value, int index) => index < 0 || index >= value.Length || !(char.IsLetterOrDigit(value[index]) || value[index] == '_');

    private static string ApplyReplacements(string value, params Replacement[] replacements)
    {
        foreach (var replacement in replacements.OrderByDescending(item => item.Start))
            value = string.Concat(value.AsSpan(0, replacement.Start), replacement.Value, value.AsSpan(replacement.End));
        return value;
    }

    private sealed record ParsedCreateTable(IdentifierToken Table, IReadOnlyList<SqlItem> Items, int BodyEnd)
    {
        public SqlItem FindColumn(string column)
        {
            var matches = Items.Where(item => string.Equals(item.Identifier.Value, column, StringComparison.Ordinal)).ToArray();
            return matches.Length switch
            {
                1 => matches[0],
                0 => throw new InvalidOperationException($"Physical table definition does not declare column '{column}'."),
                _ => throw new InvalidOperationException($"Physical table definition declares column '{column}' more than once.")
            };
        }
    }

    private sealed record SqlItem(int Start, int End, IdentifierToken Identifier);
    private sealed record IdentifierToken(int Start, int End, string Value);
    private sealed record Replacement(int Start, int End, string Value);
}
