using Groundwork.Kernel;

namespace Groundwork.Sqlite;

internal readonly record struct SqliteNativePlanRow(int Id, int ParentId, string Detail);

/// <summary>
/// Maps the small, deliberately closed subset of SQLite EXPLAIN QUERY PLAN details used by the
/// initial structured-evidence slice. Native text is parsed only here and never leaves the adapter.
/// </summary>
internal static class SqliteNativePlanMapper
{
    internal static ProviderPlanForest? Map(
        IReadOnlyList<SqliteNativePlanRow> rows,
        string physicalTarget,
        ProviderOpaqueIdentity targetId,
        Func<string, ProviderOpaqueIdentity> indexIdentity,
        IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalTarget);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(indexIdentity);
        ArgumentNullException.ThrowIfNull(logicalIndexesByPhysicalName);

        if (rows.Count == 0)
            return null;

        var normalizedIds = new Dictionary<int, int>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            // SQLite uses parent 0 as the root sentinel. A source id of 0 would make a real parent
            // indistinguishable from that sentinel, so the mapping fails closed.
            if (row.Id <= 0 || row.ParentId < 0 || !normalizedIds.TryAdd(row.Id, index))
                return null;
        }

        foreach (var row in rows)
        {
            if (row.ParentId != 0 && !normalizedIds.ContainsKey(row.ParentId))
                return null;
        }

        var nodes = new ProviderPlanNode[rows.Count];
        var accessCount = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.ParentId == row.Id)
                return null;

            int? parentId = row.ParentId == 0 ? null : normalizedIds[row.ParentId];
            if (!TryParseNode(
                    row.Detail,
                    physicalTarget,
                    targetId,
                    indexIdentity,
                    logicalIndexesByPhysicalName,
                    index,
                    parentId,
                    out var node,
                    out var isAccess))
                return null;

            if (isAccess && ++accessCount > 1)
                return null;

            nodes[index] = node;
        }

        // The first supported shape is one physical source, optionally accompanied by sort roots
        // or sort descendants. A plan without an access node is not a complete single-source map.
        if (accessCount != 1)
            return null;

        try
        {
            return new ProviderPlanForest(nodes);
        }
        catch (ArgumentException)
        {
            // Preserve the fail-closed contract for malformed parent graphs.
            return null;
        }
    }

    private static bool TryParseNode(
        string detail,
        string physicalTarget,
        ProviderOpaqueIdentity targetId,
        Func<string, ProviderOpaqueIdentity> indexIdentity,
        IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName,
        int id,
        int? parentId,
        out ProviderPlanNode node,
        out bool isAccess)
    {
        node = null!;
        isAccess = false;
        if (string.IsNullOrWhiteSpace(detail))
            return false;

        var position = 0;
        if (TryReadKeyword(detail, ref position, "USE"))
        {
            if (!TryReadKeyword(detail, ref position, "TEMP") ||
                !TryReadKeyword(detail, ref position, "B-TREE") ||
                !TryReadKeyword(detail, ref position, "FOR"))
                return false;

            ProviderPlanSortPurpose purpose;
            if (TryReadKeyword(detail, ref position, "ORDER"))
            {
                if (!TryReadKeyword(detail, ref position, "BY"))
                    return false;
                purpose = ProviderPlanSortPurpose.OrderBy;
            }
            else if (TryReadKeyword(detail, ref position, "GROUP"))
            {
                if (!TryReadKeyword(detail, ref position, "BY"))
                    return false;
                purpose = ProviderPlanSortPurpose.GroupBy;
            }
            else if (TryReadKeyword(detail, ref position, "DISTINCT"))
            {
                purpose = ProviderPlanSortPurpose.Distinct;
            }
            else
            {
                return false;
            }

            if (!AtEnd(detail, position))
                return false;

            node = new ProviderPlanNode(id, parentId, ProviderPlanOperator.Sort, sortPurpose: purpose);
            return true;
        }

        position = 0;
        var search = TryReadKeyword(detail, ref position, "SEARCH");
        if (!search && !TryReadKeyword(detail, ref position, "SCAN"))
            return false;

        if (!TryReadIdentifier(detail, ref position, out var actualTarget) ||
            !string.Equals(actualTarget, physicalTarget, StringComparison.Ordinal))
            return false;

        if (!search && AtEnd(detail, position))
        {
            node = new ProviderPlanNode(id, parentId, ProviderPlanOperator.TableScan, targetId: targetId);
            isAccess = true;
            return true;
        }

        if (!TryReadKeyword(detail, ref position, "USING"))
            return false;

        // INTEGER PRIMARY KEY is SQLite's rowid lookup and is not an index identity. Keep the
        // parenthesized native constraint opaque, while requiring its presence for this form.
        if (search && TryReadKeyword(detail, ref position, "INTEGER"))
        {
            if (!TryReadKeyword(detail, ref position, "PRIMARY") ||
                !TryReadKeyword(detail, ref position, "KEY") ||
                !TryReadRequiredParenthesizedTail(detail, ref position))
                return false;

            node = new ProviderPlanNode(id, parentId, ProviderPlanOperator.PrimaryKeySearch, targetId: targetId);
            isAccess = true;
            return true;
        }

        var covering = TryReadKeyword(detail, ref position, "COVERING");
        if (!TryReadKeyword(detail, ref position, "INDEX") ||
            !TryReadIdentifier(detail, ref position, out var searchIndex) ||
            !(search ? TryReadRequiredParenthesizedTail(detail, ref position) : TryReadOptionalParenthesizedTail(detail, ref position)) ||
            !TryResolveIndex(
                searchIndex,
                indexIdentity,
                logicalIndexesByPhysicalName,
                out var searchIndexId,
                out var searchLogicalIndexName))
            return false;

        node = new ProviderPlanNode(
            id,
            parentId,
            search ? ProviderPlanOperator.IndexSearch : ProviderPlanOperator.IndexScan,
            targetId,
            searchIndexId,
            searchLogicalIndexName,
            covering);
        isAccess = true;
        return true;
    }

    private static bool TryResolveIndex(
        string physicalIndex,
        Func<string, ProviderOpaqueIdentity> indexIdentity,
        IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName,
        out ProviderOpaqueIdentity indexId,
        out string logicalIndexName)
    {
        indexId = null!;
        logicalIndexName = null!;

        string? resolvedLogicalName = null;
        var found = false;
        foreach (var pair in logicalIndexesByPhysicalName)
        {
            if (!string.Equals(pair.Key, physicalIndex, StringComparison.Ordinal))
                continue;
            if (found || string.IsNullOrWhiteSpace(pair.Value))
                return false;
            found = true;
            resolvedLogicalName = pair.Value;
        }

        // A native index whose exact physical name is not in the provider's declared catalog is
        // not safe to expose as a complete mapped tree; in particular, do not treat a prefix match
        // as the requested index.
        if (!found)
            return false;

        indexId = indexIdentity(physicalIndex);
        if (indexId is null)
            return false;
        logicalIndexName = resolvedLogicalName!;
        return true;
    }

    private static bool TryReadKeyword(string text, ref int position, string keyword)
    {
        var saved = position;
        var start = position;
        SkipWhitespace(text, ref start);
        // Keywords are native grammar tokens, not SQL identifiers. Quoting is only meaningful
        // when resolving a target or index name; stripping it here would accept unknown syntax.
        if (start < text.Length && text[start] is '"' or '`' or '[')
            return false;
        if (!TryReadIdentifier(text, ref position, out var token) ||
            !string.Equals(token, keyword, StringComparison.OrdinalIgnoreCase))
        {
            position = saved;
            return false;
        }

        return true;
    }

    private static bool TryReadIdentifier(string text, ref int position, out string identifier)
    {
        SkipWhitespace(text, ref position);
        if (position >= text.Length)
        {
            identifier = string.Empty;
            return false;
        }

        var opener = text[position];
        if (opener is '"' or '`' or '[')
        {
            var closer = opener == '[' ? ']' : opener;
            var start = ++position;
            var value = new System.Text.StringBuilder();
            while (position < text.Length)
            {
                var current = text[position++];
                if (current == closer)
                {
                    // SQLite escapes quoted identifier delimiters by doubling them, except for
                    // bracket quoting where a closing bracket ends the identifier.
                    if (closer != ']' && position < text.Length && text[position] == closer)
                    {
                        value.Append(closer);
                        position++;
                        continue;
                    }

                    if (value.Length == 0 && position - 1 == start)
                    {
                        identifier = string.Empty;
                        return false;
                    }

                    identifier = value.ToString();
                    return true;
                }

                value.Append(current);
            }

            identifier = string.Empty;
            return false;
        }

        var tokenStart = position;
        while (position < text.Length &&
            !char.IsWhiteSpace(text[position]) &&
            text[position] is not '(' and not ')')
            position++;

        if (position == tokenStart)
        {
            identifier = string.Empty;
            return false;
        }

        identifier = text[tokenStart..position];
        return true;
    }

    private static bool TryReadOptionalParenthesizedTail(string text, ref int position)
    {
        SkipWhitespace(text, ref position);
        return position == text.Length || TryReadParenthesizedTail(text, ref position);
    }

    private static bool TryReadRequiredParenthesizedTail(string text, ref int position)
    {
        SkipWhitespace(text, ref position);
        return position < text.Length && TryReadParenthesizedTail(text, ref position);
    }

    private static bool TryReadParenthesizedTail(string text, ref int position)
    {
        if (position >= text.Length || text[position] != '(')
            return false;

        var bodyStart = position + 1;
        var depth = 0;
        while (position < text.Length)
        {
            var current = text[position++];
            if (current is '\'' or '"' or '`' or '[')
            {
                var quoted = current == '[' ? ']' : current;
                if (!TrySkipQuoted(text, ref position, quoted))
                    return false;
                continue;
            }

            if (current == '(')
            {
                depth++;
            }
            else if (current == ')' && --depth == 0)
            {
                if (string.IsNullOrWhiteSpace(text[bodyStart..(position - 1)]))
                    return false;
                SkipWhitespace(text, ref position);
                return position == text.Length;
            }
        }

        return false;
    }

    private static bool TrySkipQuoted(string text, ref int position, char closer)
    {
        while (position < text.Length)
        {
            if (text[position++] != closer)
                continue;
            if (closer != ']' && position < text.Length && text[position] == closer)
            {
                position++;
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool AtEnd(string text, int position)
    {
        SkipWhitespace(text, ref position);
        return position == text.Length;
    }

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
            position++;
    }
}
