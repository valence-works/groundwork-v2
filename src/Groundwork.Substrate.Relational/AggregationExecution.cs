using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Groundwork.Kernel;

namespace Groundwork.Substrate.Relational;

/// <summary>Reads and enforces the hidden budget evidence emitted by a native reducer command.</summary>
public static class RelationalAggregationExecutor
{
    public static AggregationResult Execute(
        DbConnection connection,
        DbTransaction? transaction,
        RelationalDialect dialect,
        StorageUnit unit,
        AggregationProfile profile,
        AggregationQuery query,
        Func<string, object?, object?> decode)
    {
        try
        {
            return ExecuteCore(connection, transaction, dialect, unit, profile, query, decode);
        }
        catch (AggregationBudgetExceededException)
        {
            throw;
        }
        catch (OverflowException exception)
        {
            throw SumOverflow(profile, exception);
        }
        catch (DbException exception) when (
            profile.Aggregates.Any(aggregate => aggregate is Aggregate.Sum) &&
            (exception.Message.Contains("overflow", StringComparison.OrdinalIgnoreCase) ||
             exception.Message.Contains("too large or too small", StringComparison.OrdinalIgnoreCase)))
        {
            throw SumOverflow(profile, exception);
        }
    }

    private static AggregationResult ExecuteCore(
        DbConnection connection,
        DbTransaction? transaction,
        RelationalDialect dialect,
        StorageUnit unit,
        AggregationProfile profile,
        AggregationQuery query,
        Func<string, object?, object?> decode)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(decode);
        VerifyBudgets(connection, transaction, dialect, unit, profile);
        var command = dialect.RenderAggregation(unit, profile, query);
        using var native = connection.CreateCommand();
        native.Transaction = transaction;
        native.CommandText = command.CommandText;
        using var reader = native.ExecuteReader();
        var rows = new List<AggregationRow>();
        while (reader.Read())
        {
            var raw = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < reader.FieldCount; index++)
                raw[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);

            if (Convert.ToInt64(raw.GetValueOrDefault(RelationalAggregationRenderer.InputCount) ?? 0, CultureInfo.InvariantCulture) > profile.MaxInputRows)
                throw new AggregationBudgetExceededException("GW-AGG-BOUND-004", $"Aggregation profile '{profile.Name}' refused more than MaxInputRows={profile.MaxInputRows}; input was not truncated.");
            if (Convert.ToInt64(raw.GetValueOrDefault(RelationalAggregationRenderer.GroupCount) ?? 0, CultureInfo.InvariantCulture) > profile.MaxGroups)
                throw new AggregationBudgetExceededException("GW-AGG-BOUND-005", $"Aggregation profile '{profile.Name}' refused more than MaxGroups={profile.MaxGroups}; groups were not truncated.");
            foreach (var set in profile.Aggregates.OfType<Aggregate.SetUnion>())
            {
                var count = Convert.ToInt64(raw.GetValueOrDefault(RelationalAggregationRenderer.SetCountAlias(set.Alias)) ?? 0, CultureInfo.InvariantCulture);
                if (count > set.MaxValues)
                    throw new AggregationBudgetExceededException("GW-AGG-BOUND-007", $"SetUnion '{set.Alias}' refused more than MaxValues={set.MaxValues}; values were not truncated.");
            }

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var groupColumn in profile.GroupByColumns)
                values[groupColumn] = decode(groupColumn, raw.GetValueOrDefault(groupColumn));
            foreach (var aggregate in profile.Aggregates)
            {
                var value = raw.GetValueOrDefault(aggregate.Alias);
                values[aggregate.Alias] = aggregate is Aggregate.SetUnion set
                    ? ParseSet(value)
                    : DecodeAggregateValue(aggregate, value, unit, decode);
            }
            rows.Add(new AggregationRow(values));
        }

        return new AggregationResult(rows);
    }

    private static AggregationBudgetExceededException SumOverflow(
        AggregationProfile profile,
        Exception exception) => new(
            "GW-AGG-SUM-001",
            $"Sum in aggregation profile '{profile.Name}' overflowed the declared portable result type.")
        {
            Source = exception.Source
        };

    private static void VerifyBudgets(
        DbConnection connection,
        DbTransaction? transaction,
        RelationalDialect dialect,
        StorageUnit unit,
        AggregationProfile profile)
    {
        var probe = RelationalAggregationRenderer.RenderBudgetProbe(dialect, unit, profile);
        using var native = connection.CreateCommand();
        native.Transaction = transaction;
        native.CommandText = probe.CommandText;
        using var reader = native.ExecuteReader();
        var groups = 0;
        while (reader.Read())
        {
            groups++;
            if (Convert.ToInt64(reader[RelationalAggregationRenderer.InputCount], CultureInfo.InvariantCulture) > profile.MaxInputRows)
                throw new AggregationBudgetExceededException("GW-AGG-BOUND-004", $"Aggregation profile '{profile.Name}' refused more than MaxInputRows={profile.MaxInputRows}; input was not truncated.");
            foreach (var set in profile.Aggregates.OfType<Aggregate.SetUnion>())
            {
                if (Convert.ToInt64(reader[RelationalAggregationRenderer.SetCountAlias(set.Alias)], CultureInfo.InvariantCulture) > set.MaxValues)
                    throw new AggregationBudgetExceededException("GW-AGG-BOUND-007", $"SetUnion '{set.Alias}' refused more than MaxValues={set.MaxValues}; values were not truncated.");
            }
        }
        if (groups > profile.MaxGroups)
            throw new AggregationBudgetExceededException("GW-AGG-BOUND-005", $"Aggregation profile '{profile.Name}' refused more than MaxGroups={profile.MaxGroups}; groups were not truncated.");
    }

    private static object? DecodeAggregateValue(
        Aggregate aggregate,
        object? value,
        StorageUnit unit,
        Func<string, object?, object?> decode)
    {
        if (value is null) return null;
        var column = aggregate switch
        {
            Aggregate.Min min => min.Column,
            Aggregate.Max max => max.Column,
            Aggregate.Sum sum => sum.Column,
            Aggregate.FirstBy first => first.Column,
            _ => null
        };
        if (aggregate is Aggregate.Sum sumAggregate)
        {
            var type = unit.Columns.Single(columnDefinition => columnDefinition.Name == sumAggregate.Column).Type;
            object decoded = type switch
            {
                PortableType.Int32 or PortableType.Int64 => (object)Convert.ToInt64(value, CultureInfo.InvariantCulture),
                PortableType.Decimal => (object)Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException("Sum was not validated for this column type.")
            };
            return decoded;
        }
        return column is null ? value : decode(column, value);
    }

    private static IReadOnlyList<string> ParseSet(object? value)
    {
        if (value is null) return [];
        if (value is IEnumerable<string> strings)
            return strings.Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (value is JsonDocument document)
            return ParseJsonSet(document.RootElement);
        if (value is JsonElement element)
            return ParseJsonSet(element);
        using var parsed = JsonDocument.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
        return ParseJsonSet(parsed.RootElement);
    }

    private static IReadOnlyList<string> ParseJsonSet(JsonElement array) => array
        .EnumerateArray()
        .Where(item => item.ValueKind != JsonValueKind.Null)
        .Select(item => item.GetString() ?? throw new InvalidOperationException("SetUnion returned a non-string JSON value."))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(item => item, StringComparer.Ordinal)
        .ToArray();
}
