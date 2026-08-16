using System.Collections.ObjectModel;
using System.Globalization;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;

namespace Groundwork.Store;

/// <summary>Provider capability for an atomic equality-guarded delete.</summary>
public interface ICompareAndDeleteStorageSession
{
    /// <summary>
    /// Deletes the row identified by <paramref name="key"/> only when every declared value in
    /// <paramref name="expectedValues"/> still matches. The provider owns the atomic decision;
    /// this operation is never implemented as a read followed by an unconditional delete.
    /// </summary>
    WriteOutcome CompareAndDelete(
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions? options = null);
}

/// <summary>Public entry point for provider-owned compare-and-delete operations.</summary>
public static class CompareAndDeleteSessionExtensions
{
    public static WriteOutcome CompareAndDelete(
        this IStorageSession session,
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        StorageAccessValidation.EnsurePointOperation(session.Access, "compare-and-delete");
        if (session is not ICompareAndDeleteStorageSession compareAndDelete)
        {
            throw new NotSupportedException(
                "GW-COMPARE-DELETE-001: this provider does not advertise atomic compare-and-delete; " +
                "inspect ICompareAndDeleteStorageSession before using CompareAndDelete.");
        }

        var canonicalKey = CompareAndDeleteValidation.CanonicalizeKey(session.Unit, key);
        var validated = CompareAndDeleteValidation.Validate(session.Unit, canonicalKey, expectedValues, options);
        return compareAndDelete.CompareAndDelete(canonicalKey, validated, options);
    }
}

/// <summary>Fail-closed admission and value semantics shared by all compare-and-delete providers.</summary>
internal static class CompareAndDeleteValidation
{
    internal static IReadOnlyDictionary<string, object?> Validate(
        StorageUnit unit,
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions? options)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(expectedValues);
        WritePreconditionValidator.Validate(unit, WriteOperation.CompareAndDelete, options);

        _ = CanonicalizeKey(unit, key);
        var declared = unit.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);

        if (expectedValues.Count == 0)
            throw new ArgumentException("A compare-and-delete requires at least one expected column value.", nameof(expectedValues));

        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in expectedValues)
        {
            if (!declared.TryGetValue(pair.Key, out var definition) ||
                IsProviderOwnedColumn(pair.Key) ||
                string.Equals(unit.Concurrency.TokenColumn, pair.Key, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Comparison column '{pair.Key}' is not an application-declared physical column of '{unit.Name}'.",
                    nameof(expectedValues));
            }

            if (definition.Type == PortableType.Json)
            {
                throw new ArgumentException(
                    $"Comparison column '{pair.Key}' uses PortableType.Json, which is not supported by compare-and-delete; compare a portable scalar or binary column instead.",
                    nameof(expectedValues));
            }

            snapshot.Add(pair.Key, CanonicalizeValue(definition, pair.Value, pair.Key, nameof(expectedValues)));
        }

        return new ReadOnlyDictionary<string, object?>(snapshot);
    }

    internal static StorageKey CanonicalizeKey(StorageUnit unit, StorageKey key)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(key);

        var declared = unit.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var keyColumns = unit.Key.Columns
            .Where(column => !IsProviderOwnedColumn(column))
            .ToArray();
        var keyColumnSet = keyColumns.ToHashSet(StringComparer.Ordinal);
        if (key.Values.Count != keyColumns.Length || key.Values.Keys.Any(column => !keyColumnSet.Contains(column)))
        {
            throw new ArgumentException(
                $"A compare-and-delete key for '{unit.Name}' must contain exactly the declared key columns.",
                nameof(key));
        }

        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var keyColumn in keyColumns)
        {
            if (!declared.TryGetValue(keyColumn, out var definition))
                throw new ArgumentException($"Key column '{keyColumn}' is not declared by '{unit.Name}'.", nameof(key));
            snapshot.Add(keyColumn, CanonicalizeValue(definition, key.Values[keyColumn], keyColumn, nameof(key)));
        }

        return new StorageKey(snapshot);
    }

    internal static bool IsProviderOwnedColumn(string column) =>
        column.StartsWith("__groundwork_", StringComparison.Ordinal);

    internal static bool ValuesEqual(
        object? left,
        object? right,
        PortableType type) => type switch
        {
            PortableType.Binary => left is byte[] leftBytes && right is byte[] rightBytes && leftBytes.SequenceEqual(rightBytes),
            PortableType.DateTimeOffset => left is DateTimeOffset leftDate && right is DateTimeOffset rightDate && leftDate.UtcTicks == rightDate.UtcTicks,
            PortableType.Int64 => left is int or long && right is int or long && Convert.ToInt64(left, CultureInfo.InvariantCulture) == Convert.ToInt64(right, CultureInfo.InvariantCulture),
            PortableType.Decimal => IsDecimal(left) && IsDecimal(right) && Convert.ToDecimal(left, CultureInfo.InvariantCulture) == Convert.ToDecimal(right, CultureInfo.InvariantCulture),
            _ => Equals(left, right)
        };

    private static void ValidateValue(ColumnDefinition definition, object? value, string column, string parameter)
    {
        if (value is null)
        {
            if (!definition.IsNullable)
                throw new ArgumentException($"Comparison value for non-nullable column '{column}' cannot be null.", parameter);
            return;
        }

        var compatible = definition.Type switch
        {
            PortableType.String => value is string,
            PortableType.Int32 => value is int,
            PortableType.Int64 => value is int or long,
            PortableType.Decimal => IsDecimal(value),
            PortableType.Boolean => value is bool,
            PortableType.DateTimeOffset => value is DateTimeOffset,
            PortableType.Guid => value is Guid,
            PortableType.Binary => value is byte[],
            _ => false
        };
        if (!compatible)
            throw new ArgumentException(
                $"Comparison value for column '{column}' is not compatible with declared type {definition.Type}.",
                parameter);
        if (definition.MaxLength is { } maxLength && value is string text && text.Length > maxLength)
            throw new ArgumentException($"Comparison value for column '{column}' exceeds its declared length of {maxLength}.", parameter);
        if (definition.MaxLength is { } binaryLength && value is byte[] bytes && bytes.Length > binaryLength)
            throw new ArgumentException($"Comparison value for column '{column}' exceeds its declared length of {binaryLength}.", parameter);
    }

    private static object? CanonicalizeValue(
        ColumnDefinition definition,
        object? value,
        string column,
        string parameter)
    {
        ValidateValue(definition, value, column, parameter);
        if (value is null)
            return null;

        try
        {
            return definition.Type switch
            {
                PortableType.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                PortableType.Decimal => CanonicalizeDecimal(
                    Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                    definition,
                    column,
                    parameter),
                PortableType.DateTimeOffset => ((DateTimeOffset)value).ToUniversalTime(),
                PortableType.Binary => ((byte[])value).ToArray(),
                _ => StorageValues.CloneValue(value)
            };
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException(
                $"Comparison value for column '{column}' cannot be represented by declared type {definition.Type}.",
                parameter,
                exception);
        }
    }

    private static bool IsDecimal(object? value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or decimal;

    private static decimal CanonicalizeDecimal(
        decimal value,
        ColumnDefinition definition,
        string column,
        string parameter)
    {
        if (definition is not { Precision: int precision, Scale: int scale })
            return value;

        if (decimal.Round(value, scale, MidpointRounding.ToEven) != value)
        {
            throw new ArgumentException(
                $"Comparison value for column '{column}' cannot be represented exactly by Decimal({precision},{scale}).",
                parameter);
        }

        var integral = decimal.Truncate(decimal.Abs(value));
        var integerDigits = integral == 0m
            ? 0
            : integral.ToString("0", CultureInfo.InvariantCulture).Length;
        if (integerDigits > precision - scale)
        {
            throw new ArgumentException(
                $"Comparison value for column '{column}' exceeds Decimal({precision},{scale}).",
                parameter);
        }

        return value;
    }

}

internal static class RowWriteFingerprint
{
    internal static string Create(
        StorageUnit unit,
        RowWriteMode mode,
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions options)
    {
        return ExactAppendCodec.FingerprintRowWrite(unit, mode, key, expectedValues, options);
    }
}

internal static class ImmutableExpectedValues
{
    internal static IReadOnlyDictionary<string, object?> Empty { get; } =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal));
}
