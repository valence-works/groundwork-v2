using System.Collections.Immutable;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Store;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Centralizes provider-neutral session admission, query shaping, and write declaration policy.
/// Provider sessions retain only rendering, command execution, and driver value conversion.
/// </summary>
internal static class RelationalSessionPolicy
{
    internal static RelationalPreparedQuery PrepareQuery(
        StorageUnit unit,
        StorageAccess access,
        QueryRequest request,
        QueryRenderOptions? options,
        IReadOnlyDictionary<string, string> physicalIndexNames)
    {
        ArgumentNullException.ThrowIfNull(request);
        StorageAccessValidation.EnsureOrdinaryQuery(access);
        EnsureTable(unit, request);

        var suppliedOptions = options ?? QueryRenderOptions.Default;
        var executionSource = BindScope(unit, access, request);
        var renderOptions = CompleteOptions(unit, suppliedOptions, physicalIndexNames, crossScope: false);
        return new RelationalPreparedQuery(
            executionSource,
            renderOptions,
            QueryRequestExecution.ForPage(executionSource, renderOptions));
    }

    internal static RelationalPreparedQuery PrepareCrossScopeQuery(
        StorageUnit unit,
        StorageAccess access,
        QueryRequest request,
        QueryRenderOptions? options,
        IReadOnlyDictionary<string, string> physicalIndexNames)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!access.IsPrivilegedAcrossScopes)
            throw new InvalidOperationException(
                "GW-ACCESS-001: cross-scope queries require explicit privileged across-scope access.");
        EnsureTable(unit, request);

        var suppliedOptions = options ?? QueryRenderOptions.Default;
        var renderOptions = CompleteOptions(unit, suppliedOptions, physicalIndexNames, crossScope: true);
        var executionSource = QueryRequestExecution.WithProviderPredicate(
            request,
            request.Where,
            CrossScopeQueryMaterializer.BindingDiscriminator(access));
        return new RelationalPreparedQuery(
            executionSource,
            renderOptions,
            EnsureScopeProjection(unit, QueryRequestExecution.ForPage(executionSource, renderOptions)));
    }

    internal static StoredEntry? PublicEntry(StoredEntry? entry) => entry is null
        ? null
        : new StoredEntry(
            new StorageValues(SearchKeyProjection.PublicValues(entry.Values.Values)),
            entry.Version);

    internal static bool MatchesExpected(
        StorageUnit unit,
        StoredEntry existing,
        IReadOnlyDictionary<string, object?> expected) =>
        expected.All(pair =>
        {
            var definition = unit.Columns.Single(column => column.Name == pair.Key);
            return existing.Values.Values.TryGetValue(pair.Key, out var actual) &&
                CompareAndDeleteValidation.ValuesEqual(actual, pair.Value, definition.Type);
        });

    internal static void ValidateValues(
        StorageUnit unit,
        IReadOnlyList<ColumnDefinition> userColumns,
        string providerName,
        IReadOnlyDictionary<string, object?> values,
        bool requireAllNonNullable,
        bool allowGeneratedLocator = false)
    {
        var known = userColumns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = values.Keys.FirstOrDefault(key => !known.Contains(key));
        if (unknown is not null)
            throw new ArgumentException($"Column '{unknown}' is not declared by '{unit.Name}'.", nameof(values));
        foreach (var generated in userColumns.Where(column => column.Generation == ColumnGeneration.ProviderSequence))
        {
            if (values.ContainsKey(generated.Name) && !allowGeneratedLocator)
                throw new ArgumentException(
                    $"ProviderSequence column '{generated.Name}' is assigned by {providerName}; it may only be supplied as the locator for Update or Upsert.",
                    nameof(values));
        }
        if (!requireAllNonNullable)
            return;
        foreach (var column in userColumns.Where(column =>
                     !column.IsNullable && column.Default is null &&
                     column.Generation != ColumnGeneration.ProviderSequence))
        {
            if (!values.TryGetValue(column.Name, out var value) || value is null)
                throw new ArgumentException($"Non-nullable column '{column.Name}' is required.", nameof(values));
        }
    }

    internal static bool HasSecondaryUniqueIndex(StorageUnit logicalUnit) =>
        logicalUnit.Indexes.Any(index => index.IsUnique &&
            !index.Columns.Select(column => column.Column)
                .SequenceEqual(logicalUnit.Key.Columns, StringComparer.Ordinal));

    internal static QueryType? QueryTypeOf(PortableType type) => type switch
    {
        PortableType.Boolean => QueryType.Boolean,
        PortableType.Int32 => QueryType.Int32,
        PortableType.Int64 => QueryType.Int64,
        PortableType.Decimal => QueryType.Decimal,
        PortableType.String => QueryType.String,
        PortableType.DateTimeOffset => QueryType.DateTimeOffset,
        PortableType.Guid => QueryType.Guid,
        PortableType.Binary => QueryType.Binary,
        _ => null
    };

    private static QueryRenderOptions CompleteOptions(
        StorageUnit unit,
        QueryRenderOptions suppliedOptions,
        IReadOnlyDictionary<string, string> physicalIndexNames,
        bool crossScope)
    {
        var scopeToken = new ColumnRef(
            new TableId(unit.Name),
            CrossScopeQueryMaterializer.ScopeTokenColumn,
            QueryType.String,
            isNullable: false);
        var identity = unit.Key.Columns
            .Where(name => name != ProviderOwnedColumns.Scope)
            .Select(name => QueryColumn(unit, name))
            .Where(column => column is not null)
            .Select(column => column!);
        if (crossScope)
            identity = new[] { scopeToken }.Concat(identity);

        return suppliedOptions.WithIdentityTieBreaks(identity) with
        {
            Indexes = SearchKeyQueryMappings.RetargetIndexes(unit, suppliedOptions.Indexes)
                .Select(index => index.WithColumnTypes(unit.Columns.ToDictionary(
                    column => column.Name,
                    column => QueryTypeOf(column.Type),
                    StringComparer.Ordinal)))
                .ToImmutableArray(),
            PhysicalIndexNames = suppliedOptions.PhysicalIndexNames
                .Concat(physicalIndexNames.Where(pair => !suppliedOptions.PhysicalIndexNames.ContainsKey(pair.Key)))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            SearchKeyColumns = SearchKeyQueryMappings.For(unit),
            ElementSearchKeyColumns = SearchKeyQueryMappings.ElementFor(unit),
            LatestPartitionColumns = crossScope ? [scopeToken] : suppliedOptions.LatestPartitionColumns
        };
    }

    private static QueryRequest BindScope(StorageUnit unit, StorageAccess access, QueryRequest request) =>
        unit.Scope != ScopePolicy.Scoped
            ? request
            : RelationalQueryExecution.BindScope(request, ProviderOwnedColumns.Scope, access.Scope!.Value);

    private static QueryRequest EnsureScopeProjection(StorageUnit unit, QueryRequest request)
    {
        if (request.Projection.AllColumns || request.Projection.Columns.Any(column =>
                string.Equals(column.Name, ProviderOwnedColumns.Scope, StringComparison.Ordinal)))
            return request;
        var scope = new ColumnRef(
            new TableId(unit.Name),
            ProviderOwnedColumns.Scope,
            QueryType.String,
            isNullable: false);
        return QueryRequestExecution.WithProjection(
            request,
            Projection.ColumnsOnly([.. request.Projection.Columns, scope]));
    }

    private static ColumnRef? QueryColumn(StorageUnit unit, string name)
    {
        var column = unit.Columns.Single(item => item.Name == name);
        return column.Type switch
        {
            PortableType.Boolean => new ColumnRef(new TableId(unit.Name), name, QueryType.Boolean, column.IsNullable),
            PortableType.Int32 => new ColumnRef(new TableId(unit.Name), name, QueryType.Int32, column.IsNullable),
            PortableType.Int64 => new ColumnRef(new TableId(unit.Name), name, QueryType.Int64, column.IsNullable),
            PortableType.Decimal => new ColumnRef(new TableId(unit.Name), name, QueryType.Decimal, column.IsNullable, null,
                column.Precision is int precision ? checked((byte)precision) : null,
                column.Scale is int scale ? checked((byte)scale) : null),
            PortableType.String => new ColumnRef(new TableId(unit.Name), name, QueryType.String, column.IsNullable, column.MaxLength),
            PortableType.DateTimeOffset => new ColumnRef(new TableId(unit.Name), name, QueryType.DateTimeOffset, column.IsNullable),
            PortableType.Guid => new ColumnRef(new TableId(unit.Name), name, QueryType.Guid, column.IsNullable),
            PortableType.Binary => new ColumnRef(new TableId(unit.Name), name, QueryType.Binary, column.IsNullable, column.MaxLength),
            _ => null
        };
    }

    private static void EnsureTable(StorageUnit unit, QueryRequest request)
    {
        if (!string.Equals(request.Table.Value, unit.Name, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Query table '{request.Table.Value}' does not match session unit '{unit.Name}'.",
                nameof(request));
    }
}

internal sealed record RelationalPreparedQuery(
    QueryRequest ExecutionSource,
    QueryRenderOptions RenderOptions,
    QueryRequest ExecutionRequest);
