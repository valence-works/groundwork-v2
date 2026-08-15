using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Query.Model;

namespace Groundwork.Testing;

/// <summary>A deterministic provider-neutral reference implementation for the testing package.</summary>
public sealed class InMemoryProviderFactory : IStorageProviderFactory
{
    private readonly object gate = new();
    private readonly Dictionary<string, InMemoryDatabase> databases = new(StringComparer.Ordinal);

    public IStorageProviderConnection Create(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        lock (gate)
        {
            if (!databases.TryGetValue(connectionString, out var database))
            {
                database = new InMemoryDatabase();
                databases.Add(connectionString, database);
            }

            return new InMemoryProviderConnection(database);
        }
    }
}

public sealed class SchemaConflictException : InvalidOperationException
{
    public SchemaConflictException(string message) : base(message)
    {
    }
}

public sealed class InMemoryProviderConnection : IStorageProviderConnection
{
    private readonly InMemoryDatabase database;
    private bool disposed;

    internal InMemoryProviderConnection(InMemoryDatabase database)
    {
        this.database = database;
        Catalog = new InMemoryProviderCatalog(database);
        Schema = new InMemorySchemaCoordinator(database);
    }

    public IProviderCatalog Catalog { get; }

    public ISchemaCoordinator Schema { get; }

    public IStorageSession OpenSession(StorageUnit unit, StorageAccess access)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(access);
        var state = database.GetState(unit, access);
        return new InMemoryStorageSession(database, state, access, liveState: true);
    }

    public IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(units);
        if (units.Length == 0)
            throw new ArgumentException("A unit of work must declare at least one storage unit.", nameof(units));

        var states = units.Select(unit => database.GetState(unit, access)).ToArray();
        if (states.Select(state => state.Unit.Id).Distinct().Count() != states.Length)
            throw new ArgumentException("A unit of work cannot list the same storage unit twice.", nameof(units));

        return new InMemoryUnitOfWork(database, states, access);
    }

    public void Dispose() => disposed = true;

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(InMemoryProviderConnection));
    }
}

internal sealed class InMemoryDatabase
{
    internal readonly object Gate = new();
    internal readonly Dictionary<StorageUnitId, InMemoryUnitState> Units = [];

    internal InMemoryUnitState GetState(StorageUnit requested, StorageAccess access)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(access);
        lock (Gate)
        {
            if (!Units.TryGetValue(requested.Id, out var state))
                throw new InvalidOperationException(
                    $"Storage unit '{requested.Id.Value}' has not been applied to this provider.");

            ValidateScope(state.Unit, access);
            return state;
        }
    }

    internal static void ValidateScope(StorageUnit unit, StorageAccess access)
    {
        if (unit.Scope != access.Policy)
            throw new InvalidOperationException(
                $"Storage unit '{unit.Name}' requires {unit.Scope} access, but {access.Policy} was supplied.");
    }
}

internal sealed class InMemoryUnitState
{
    internal InMemoryUnitState(StorageUnit unit)
    {
        Unit = StorageDeclaration.Clone(unit);
    }

    internal StorageUnit Unit { get; }

    // Compare-and-swap token used by unit-of-work commits.
    internal long Revision { get; set; }

    internal Dictionary<string, Dictionary<string, InMemoryEntry>> Partitions { get; } = [];

    internal List<ProviderIndex> PhysicalIndexes { get; } = [];

    internal InMemoryUnitState Clone()
    {
        var clone = new InMemoryUnitState(Unit);
        clone.Revision = Revision;
        foreach (var partition in Partitions)
        {
            clone.Partitions[partition.Key] = partition.Value.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.Ordinal);
        }

        clone.PhysicalIndexes.AddRange(PhysicalIndexes.Select(index => new ProviderIndex(
            index.Name,
            index.Columns.Select(column => new ProviderIndexColumn(column.Column, column.Direction)).ToArray(),
            index.IsUnique,
            index.MissingValues,
            index.SchemaVersion)));

        return clone;
    }
}

internal sealed class InMemoryEntry
{
    internal InMemoryEntry(IReadOnlyDictionary<string, object?> values, long? version)
    {
        Values = StorageValues.Snapshot(values);
        Version = version;
    }

    internal IReadOnlyDictionary<string, object?> Values { get; }

    internal long? Version { get; }

    internal InMemoryEntry With(
        IReadOnlyDictionary<string, object?> values,
        long? version) => new(values, version);

    internal InMemoryEntry Clone() => new(Values, Version);
}

internal sealed class InMemoryProviderCatalog(InMemoryDatabase database) : IProviderCatalog
{
    public IReadOnlyList<ProviderIndex> ReadIndexes(StorageUnitId storageUnitId)
    {
        lock (database.Gate)
        {
            if (!database.Units.TryGetValue(storageUnitId, out var state))
                return [];

            return state.PhysicalIndexes
                .Select(index => new ProviderIndex(
                    index.Name,
                    index.Columns.Select(column => new ProviderIndexColumn(column.Column, column.Direction)).ToArray(),
                    index.IsUnique,
                    index.MissingValues,
                    index.SchemaVersion))
                .ToArray();
        }
    }
}

internal sealed class InMemorySchemaCoordinator(InMemoryDatabase database) : ISchemaCoordinator
{
    public SchemaDiff Diff(StorageUnit desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        lock (database.Gate)
        {
            return new SchemaDiff(BuildChanges(desired, database.Units.TryGetValue(desired.Id, out var current)
                ? current.Unit
                : null));
        }
    }

    public SchemaApplyResult Apply(StorageUnit desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        lock (database.Gate)
        {
            database.Units.TryGetValue(desired.Id, out var current);
            var changes = BuildChanges(desired, current?.Unit);
            var diff = new SchemaDiff(changes);
            if (diff.IsEmpty)
                return new SchemaApplyResult(diff, false);

            var merged = Merge(current?.Unit, desired);
            var physicalIndexes = current?.PhysicalIndexes.ToList() ?? [];
            foreach (var index in desired.Indexes.Where(index =>
                         physicalIndexes.All(existing => existing.Name != index.Name)))
            {
                physicalIndexes.Add(new ProviderIndex(
                    index.Name,
                    index.Columns.Select(column => new ProviderIndexColumn(column.Column, column.Direction)).ToArray(),
                    index.IsUnique,
                    index.MissingValues,
                    index.SchemaVersion));
            }

            InMemoryUnitState next;
            if (current is null)
                next = new InMemoryUnitState(merged);
            else
            {
                var replacement = new InMemoryUnitState(merged);
                foreach (var partition in current.Partitions)
                    replacement.Partitions[partition.Key] = partition.Value.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.Clone(),
                        StringComparer.Ordinal);
                replacement.Revision = checked(current.Revision + 1);
                next = replacement;
            }

            next.PhysicalIndexes.AddRange(physicalIndexes);

            database.Units[desired.Id] = next;
            return new SchemaApplyResult(diff, true);
        }
    }

    private static IReadOnlyList<SchemaChange> BuildChanges(StorageUnit desired, StorageUnit? current)
    {
        if (current is not null && !string.Equals(current.Name, desired.Name, StringComparison.Ordinal))
            throw new SchemaConflictException(
                $"Storage unit '{desired.Id.Value}' cannot change name from '{current.Name}' to '{desired.Name}'.");
        if (current is not null && !SchemaIdentity.KeyEquals(current.Key, desired.Key))
            throw new SchemaConflictException($"Storage unit '{desired.Name}' cannot change its key non-additively.");
        if (current is not null && current.Concurrency != desired.Concurrency)
            throw new SchemaConflictException(
                $"Storage unit '{desired.Name}' cannot change concurrency declaration non-additively.");
        if (current is not null && current.Timestamps != desired.Timestamps)
            throw new SchemaConflictException(
                $"Storage unit '{desired.Name}' cannot change timestamp declaration non-additively.");
        if (current is not null && current.Scope != desired.Scope)
            throw new SchemaConflictException(
                $"Storage unit '{desired.Name}' cannot change scope from {current.Scope} to {desired.Scope}.");
        if (current is not null && current.SchemaVersion != desired.SchemaVersion)
            throw new SchemaConflictException(
                $"Storage unit '{desired.Name}' cannot change schema version non-additively.");

        if (current is null)
        {
            return [
                new SchemaChange(SchemaChangeKind.CreateStorageUnit, desired.Name),
                .. desired.Columns.Select(column => new SchemaChange(SchemaChangeKind.AddColumn, column.Name)),
                .. desired.DerivedColumns.Select(column =>
                    new SchemaChange(SchemaChangeKind.AddDerivedColumn, column.Name)),
                .. desired.Indexes.Select(index => new SchemaChange(SchemaChangeKind.CreateIndex, index.Name))
            ];
        }

        var changes = new List<SchemaChange>();
        foreach (var column in desired.Columns)
        {
            var previous = current.Columns.FirstOrDefault(item => item.Name == column.Name);
            if (previous is null)
                changes.Add(new SchemaChange(SchemaChangeKind.AddColumn, column.Name));
            else if (!SchemaIdentity.ColumnEquals(previous, column))
                throw new SchemaConflictException($"Column '{column.Name}' changed non-additively.");
        }

        foreach (var previous in current.Columns)
        {
            if (!desired.Columns.Any(column => column.Name == previous.Name))
                throw new SchemaConflictException($"Column '{previous.Name}' was removed non-additively.");
        }

        foreach (var derived in desired.DerivedColumns)
        {
            var previous = current.DerivedColumns.FirstOrDefault(item => item.Name == derived.Name);
            if (previous is null)
                changes.Add(new SchemaChange(SchemaChangeKind.AddDerivedColumn, derived.Name));
            else if (previous != derived)
                throw new SchemaConflictException($"Derived column '{derived.Name}' changed non-additively.");
        }

        foreach (var previous in current.DerivedColumns)
        {
            if (!desired.DerivedColumns.Any(column => column.Name == previous.Name))
                throw new SchemaConflictException($"Derived column '{previous.Name}' was removed non-additively.");
        }

        foreach (var index in desired.Indexes)
        {
            var previous = current.Indexes.FirstOrDefault(item => item.Name == index.Name);
            if (previous is null)
                changes.Add(new SchemaChange(SchemaChangeKind.CreateIndex, index.Name));
            else if (!SchemaIdentity.IndexEquals(previous, index))
                throw new SchemaConflictException($"Index '{index.Name}' changed non-additively.");
        }

        foreach (var previous in current.Indexes)
        {
            if (!desired.Indexes.Any(index => index.Name == previous.Name))
                throw new SchemaConflictException($"Index '{previous.Name}' was removed non-additively.");
        }

        return changes;
    }

    private static StorageUnit Merge(StorageUnit? current, StorageUnit desired)
    {
        if (current is null)
            return desired;

        return desired with
        {
            Columns = desired.Columns.ToArray(),
            DerivedColumns = desired.DerivedColumns.ToArray(),
            Indexes = desired.Indexes.ToArray()
        };
    }
}

internal static class SchemaIdentity
{
    internal static bool ColumnEquals(ColumnDefinition left, ColumnDefinition right) =>
        string.Equals(Column(left), Column(right), StringComparison.Ordinal);

    internal static bool IndexEquals(IndexDefinition left, IndexDefinition right) =>
        string.Equals(Index(left), Index(right), StringComparison.Ordinal);

    internal static bool KeyEquals(KeyDefinition left, KeyDefinition right) =>
        left.Columns.SequenceEqual(right.Columns, StringComparer.Ordinal);

    private static string Column(ColumnDefinition column) => Encode(
        column.Name,
        column.Type,
        column.IsNullable,
        column.MaxLength,
        column.Precision,
        column.Scale,
        column.Collation,
        column.Default is null ? "default:absent" : $"default:present:{Value(column.Default.Value)}",
        column.Generation);

    private static string Index(IndexDefinition index) => Encode(
        index.Name,
        index.IsUnique,
        index.MissingValues,
        index.SchemaVersion,
        string.Join("", index.Columns.Select(column => Encode(column.Column, column.Direction))));

    private static string Encode(params object?[] parts) => string.Join(";", parts.Select(part =>
    {
        var value = part?.ToString() ?? "<null>";
        return $"{value.Length}:{value}";
    }));

    private static string Value(object? value) => value switch
    {
        null => "null",
        string text => $"s:{text.Length}:{text}",
        byte[] bytes => $"b:{Convert.ToBase64String(bytes)}",
        IReadOnlyDictionary<string, object?> dictionary =>
            $"dict:{string.Join("", dictionary.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => Encode(pair.Key, Value(pair.Value))))}",
        IDictionary dictionary =>
            $"dict:{string.Join("", dictionary.Cast<DictionaryEntry>()
                .OrderBy(pair => pair.Key?.ToString(), StringComparer.Ordinal)
                .Select(pair => Encode(pair.Key?.ToString(), Value(pair.Value))))}",
        IEnumerable sequence => $"seq:{string.Join("", sequence.Cast<object?>().Select(Value))}",
        IFormattable formattable => $"{value.GetType().FullName}:{formattable.ToString(null, CultureInfo.InvariantCulture)}",
        _ => $"{value.GetType().FullName}:{value}"
    };
}

internal static class StorageDeclaration
{
    internal static StorageUnit Clone(StorageUnit unit) => unit with
    {
        Columns = unit.Columns.Select(column => column with
        {
            Default = column.Default is null ? null : new PortableDefault(
                StorageValues.CloneValue(column.Default.Value))
        }).ToArray(),
        Key = new KeyDefinition { Columns = unit.Key.Columns.ToArray() },
        DerivedColumns = unit.DerivedColumns.ToArray(),
        Indexes = unit.Indexes.Select(index => index with
        {
            Columns = index.Columns.ToArray()
        }).ToArray()
    };
}

internal sealed class InMemoryStorageSession : IStorageSession, IConcurrencyStorageSession
{
    private readonly InMemoryDatabase database;
    private readonly InMemoryUnitState state;
    private readonly bool liveState;
    private readonly string partition;
    private bool disposed;

    internal InMemoryStorageSession(
        InMemoryDatabase database,
        InMemoryUnitState state,
        StorageAccess access,
        bool liveState = false)
    {
        this.database = database;
        this.state = state;
        this.liveState = liveState;
        Access = access;
        Unit = StorageDeclaration.Clone(state.Unit);
        partition = access.Scope?.Value ?? "<global>";
    }

    public StorageUnit Unit { get; }

    public StorageAccess Access { get; }

    public StoredEntry? Read(StorageKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (database.Gate)
        {
            ThrowIfDisposed();
            return Mutation.Read(CurrentState(), partition, key);
        }
    }

    public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Table.Value, Unit.Name, StringComparison.Ordinal))
            throw new ArgumentException($"Query table '{request.Table.Value}' does not match session unit '{Unit.Name}'.", nameof(request));
        var suppliedOptions = options ?? QueryRenderOptions.Default;
        var renderOptions = suppliedOptions.WithIdentityTieBreaks(Unit.Key.Columns
            .Select(name => QueryColumn(name))
            .Where(column => column is not null)
            .Select(column => column!));
        var validation = PortableQuerySemantics.Validate(request);
        if (!validation.IsPortable)
        {
            var refusal = validation.Refusals[0];
            throw new QueryRenderException(refusal.Code, refusal.Message + " (" + refusal.Path + ").");
        }
        ValidateInBudget(request.Where, suppliedOptions.InValueLimit, request.Table.Value);
        lock (database.Gate)
        {
            ThrowIfDisposed();
            var rows = CurrentState().Partitions.TryGetValue(partition, out var entries)
                ? entries.Values
                    .Where(entry => PortableQuerySemantics.Evaluate(request.Where, entry.Values))
                    .Select(entry => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(entry.Values, StringComparer.Ordinal))
                    .ToList()
                : new List<IReadOnlyDictionary<string, object?>>();

            if (request.LatestPerKey is not null)
                rows = LatestPerKeyRows(rows, request.LatestPerKey, renderOptions.TieBreakColumns);

            var order = renderOptions.GetEffectiveOrder(request);
            if (order.Length != 0)
                rows.Sort(new MemoryRowComparer(order));

            var deferContinuation = request.Result.IncludesTotalCount && request.Paging.ContinuationToken is not null;
            if (!deferContinuation && request.Paging.ContinuationToken is { } token)
            {
                IReadOnlyList<QueryConstant> cursor;
                try
                {
                    cursor = QueryContinuationToken.Decode(token, request, renderOptions);
                }
                catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
                {
                    throw new QueryRenderException("GW-QUERY-013", "The keyset continuation token is invalid: " + exception.Message);
                }
                rows = rows.Where(row => IsAfter(row, order, cursor)).ToList();
            }

            var selectedIndex = renderOptions.FindPinnedIndex()?.Name;
            if (!request.Result.IncludesTotalCount)
                return QueryResultMaterializer.Materialize(request, renderOptions, rows, selectedIndex, sourceIncludesRequestedOffset: false);

            if (rows.Count == 0)
                return new QueryMaterializedResult(Array.Empty<IReadOnlyDictionary<string, object?>>(), 0L, null, selectedIndex);

            var counted = rows.Select((row, index) => index == 0
                ? (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(row)
                {
                    ["__groundwork_total_count"] = (long)rows.Count
                }
                : row).ToArray();
            return QueryResultMaterializer.Materialize(request, renderOptions, counted, selectedIndex,
                sourceIncludesRequestedOffset: false,
                sourceIncludesContinuation: !deferContinuation);
        }
    }

    private ColumnRef? QueryColumn(string name)
    {
        var column = Unit.Columns.Single(item => item.Name == name);
        return column.Type switch
        {
            PortableType.Boolean => new ColumnRef(new TableId(Unit.Name), name, QueryType.Boolean, column.IsNullable),
            PortableType.Int32 => new ColumnRef(new TableId(Unit.Name), name, QueryType.Int32, column.IsNullable),
            PortableType.Int64 => new ColumnRef(new TableId(Unit.Name), name, QueryType.Int64, column.IsNullable),
            PortableType.Decimal => new ColumnRef(new TableId(Unit.Name), name, QueryType.Decimal, column.IsNullable),
            PortableType.String => new ColumnRef(new TableId(Unit.Name), name, QueryType.String, column.IsNullable),
            PortableType.DateTimeOffset => new ColumnRef(new TableId(Unit.Name), name, QueryType.DateTimeOffset, column.IsNullable),
            PortableType.Guid => new ColumnRef(new TableId(Unit.Name), name, QueryType.Guid, column.IsNullable),
            PortableType.Binary => new ColumnRef(new TableId(Unit.Name), name, QueryType.Binary, column.IsNullable),
            _ => null
        };
    }

    private static void ValidateInBudget(Predicate predicate, int limit, string table)
    {
        switch (predicate)
        {
            case Predicate.In membership when membership.Values.Distinct().Count() > limit:
                throw new QueryRenderException(
                    "GW-QUERY-015",
                    $"Query on '{table}' has an In predicate on '{membership.Column.Name}' with {membership.Values.Distinct().Count()} distinct values, exceeding the configured maximum of {limit}.");
            case Predicate.And and:
                foreach (var term in and.Terms)
                    ValidateInBudget(term, limit, table);
                break;
            case Predicate.Or or:
                foreach (var term in or.Terms)
                    ValidateInBudget(term, limit, table);
                break;
            case Predicate.Not not:
                ValidateInBudget(not.Inner, limit, table);
                break;
        }
    }

    private static List<IReadOnlyDictionary<string, object?>> LatestPerKeyRows(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        LatestPerKey latest,
        IReadOnlyList<ColumnRef> tieBreakColumns)
    {
        return rows
            .Where(row => row.TryGetValue(latest.Timestamp.Name, out var timestamp) && timestamp is DateTimeOffset)
            .GroupBy(row => ValueIdentity(row.TryGetValue(latest.Key.Name, out var key) ? key : null), StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(row => ((DateTimeOffset)row[latest.Timestamp.Name]!).UtcTicks)
                .Aggregate((best, candidate) => CompareTie(candidate, best, tieBreakColumns) < 0 ? candidate : best))
            .ToList();
    }

    private static int CompareTie(
        IReadOnlyDictionary<string, object?> left,
        IReadOnlyDictionary<string, object?> right,
        IReadOnlyList<ColumnRef> columns)
    {
        foreach (var column in columns)
        {
            left.TryGetValue(column.Name, out var leftValue);
            right.TryGetValue(column.Name, out var rightValue);
            var comparison = CompareForOrder(leftValue, rightValue,
                new OrderTerm(column, OrderDirection.Ascending, NullOrder.First));
            if (comparison != 0)
                return comparison;
        }
        return 0;
    }

    private static bool IsAfter(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<OrderTerm> order,
        IReadOnlyList<QueryConstant> cursor)
    {
        for (var index = 0; index < order.Count; index++)
        {
            var term = order[index];
            row.TryGetValue(term.Column.Name, out var actual);
            var boundary = cursor[index].Kind == QueryConstantKind.Null ? null : cursor[index].Value;
            var comparison = CompareForOrder(actual, boundary, term);
            if (comparison > 0)
                return true;
            if (comparison < 0)
                return false;
        }
        return false;
    }

    private sealed class MemoryRowComparer(IReadOnlyList<OrderTerm> order) : IComparer<IReadOnlyDictionary<string, object?>>
    {
        public int Compare(IReadOnlyDictionary<string, object?>? left, IReadOnlyDictionary<string, object?>? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            foreach (var term in order)
            {
                left.TryGetValue(term.Column.Name, out var leftValue);
                right.TryGetValue(term.Column.Name, out var rightValue);
                var comparison = CompareForOrder(leftValue, rightValue, term);
                if (comparison != 0)
                    return comparison;
            }
            return 0;
        }
    }

    private static int CompareForOrder(object? left, object? right, OrderTerm term)
    {
        if (left is null || right is null)
        {
            var nullComparison = left is null && right is null ? 0 : left is null
                ? term.NullOrder == NullOrder.First ? -1 : 1
                : term.NullOrder == NullOrder.First ? 1 : -1;
            return nullComparison;
        }

        var comparison = CompareValues(left, right);
        return term.Direction == OrderDirection.Descending ? -comparison : comparison;
    }

    private static int CompareValues(object left, object right)
    {
        if (left is string leftText && right is string rightText)
            return string.CompareOrdinal(leftText, rightText);
        if (left is DateTimeOffset leftInstant && right is DateTimeOffset rightInstant)
            return leftInstant.UtcTicks.CompareTo(rightInstant.UtcTicks);
        if (left is Guid leftGuid && right is Guid rightGuid)
            return CompareBytes(GuidBytes(leftGuid), GuidBytes(rightGuid));
        if (left is byte[] leftBytes && right is byte[] rightBytes)
            return CompareBytes(leftBytes, rightBytes);
        return ((IComparable)left).CompareTo(right);
    }

    private static string ValueIdentity(object? value) => value switch
    {
        null => "null",
        byte[] bytes => "b:" + Convert.ToBase64String(bytes),
        DateTimeOffset instant => "t:" + instant.UtcTicks.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static byte[] GuidBytes(Guid value)
    {
        var text = value.ToString("N");
        var bytes = new byte[16];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = byte.Parse(text.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes;
    }

    private static int CompareBytes(byte[] left, byte[] right)
    {
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var comparison = left[index].CompareTo(right[index]);
            if (comparison != 0) return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }

    public WriteOutcome Insert(StorageValues values, WriteOptions? options = null) =>
        Mutate(values, options, MutationKind.Insert);

    public WriteOutcome Update(StorageValues values, WriteOptions? options = null) =>
        Mutate(values, options, MutationKind.Update);

    public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null) =>
        Mutate(values, options, MutationKind.Upsert);

    public WriteOutcome ConditionalUpsert(StorageValues values, WriteOptions? options = null)
    {
        options?.Observer?.Observe(new WritePathEvent("in-memory.conditional-upsert", null, IsProbe: false));
        return Mutate(values, options, MutationKind.Upsert, exactOutcome: true);
    }

    public WriteOutcome Delete(StorageKey key, WriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (database.Gate)
        {
            ThrowIfDisposed();
            return Mutation.Delete(CurrentState(), partition, key, options);
        }
    }

    internal void Close() => disposed = true;

    private WriteOutcome Mutate(
        StorageValues values,
        WriteOptions? options,
        MutationKind kind,
        bool exactOutcome = false)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (database.Gate)
        {
            ThrowIfDisposed();
            return Mutation.Apply(CurrentState(), partition, values, options, kind, exactOutcome);
        }
    }

    private InMemoryUnitState CurrentState() =>
        liveState ? database.Units[Unit.Id] : state;

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(InMemoryStorageSession));
    }
}

internal sealed class InMemoryUnitOfWork : IUnitOfWork
{
    private readonly InMemoryDatabase database;
    private readonly StorageAccess access;
    private readonly Dictionary<StorageUnitId, InMemoryUnitState> staged;
    private readonly Dictionary<StorageUnitId, long> baseRevisions;
    private readonly List<InMemoryStorageSession> sessions = [];
    private bool terminal;

    internal InMemoryUnitOfWork(
        InMemoryDatabase database,
        IReadOnlyList<InMemoryUnitState> states,
        StorageAccess access)
    {
        this.database = database;
        this.access = access;
        lock (database.Gate)
        {
            baseRevisions = states.ToDictionary(state => state.Unit.Id, state => state.Revision);
            staged = states.ToDictionary(state => state.Unit.Id, state => state.Clone());
        }
    }

    public IStorageSession OpenSession(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ThrowIfTerminal();
        if (!staged.TryGetValue(unit.Id, out var state))
            throw new InvalidOperationException(
                $"Storage unit '{unit.Id.Value}' was not declared for this unit of work.");

        var session = new InMemoryStorageSession(database, state, access);
        sessions.Add(session);
        return session;
    }

    public void Commit()
    {
        ThrowIfTerminal();
        lock (database.Gate)
        {
            foreach (var pair in staged)
            {
                if (!database.Units.TryGetValue(pair.Key, out var current) ||
                    current.Revision != baseRevisions[pair.Key])
                {
                    throw new InvalidOperationException(
                        $"Storage unit '{pair.Key.Value}' changed while the unit of work was active.");
                }
            }

            foreach (var pair in staged)
                database.Units[pair.Key] = pair.Value;
        }

        terminal = true;
        CloseSessions();
    }

    public void Rollback()
    {
        ThrowIfTerminal();
        terminal = true;
        CloseSessions();
    }

    public void Dispose()
    {
        if (!terminal)
            Rollback();
    }

    private void ThrowIfTerminal()
    {
        if (terminal)
            throw new InvalidOperationException("The unit of work is already terminal.");
    }

    private void CloseSessions()
    {
        foreach (var session in sessions)
            session.Close();
    }
}

internal enum MutationKind
{
    Insert,
    Update,
    Upsert
}

internal static class Mutation
{
    internal static StoredEntry? Read(InMemoryUnitState state, string partition, StorageKey key)
    {
        var identity = Key(state.Unit, key.Values);
        return state.Partitions.TryGetValue(partition, out var entries) &&
               entries.TryGetValue(identity, out var entry)
            ? new StoredEntry(new StorageValues(entry.Values), entry.Version)
            : null;
    }

    internal static WriteOutcome Apply(
        InMemoryUnitState state,
        string partition,
        StorageValues values,
        WriteOptions? options,
        MutationKind kind,
        bool exactOutcome = false)
    {
        ValidateValues(state.Unit, values.Values);
        var identity = Key(state.Unit, values.Values);
        var entries = GetEntries(state, partition);
        entries.TryGetValue(identity, out var existing);

        if (kind == MutationKind.Insert && existing is not null)
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing.Version);
        if (kind == MutationKind.Update && existing is null)
            return new WriteOutcome(WriteOutcomeStatus.NotFound);

        var expected = options?.ExpectedVersion;
        if (expected is not null && state.Unit.Concurrency == ConcurrencyDeclaration.None)
            throw new InvalidOperationException(
                $"Storage unit '{state.Unit.Name}' does not declare version machinery.");

        if (existing is not null && state.Unit.Concurrency == ConcurrencyDeclaration.Optimistic)
        {
            if (expected is null || expected != existing.Version)
                return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);
        }

        if (existing is null && expected is not null && state.Unit.Concurrency == ConcurrencyDeclaration.Optimistic)
            return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict);

        if (!UniqueIndexesAllow(state.Unit, entries, identity, values.Values))
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing?.Version);

        long? version = state.Unit.Concurrency == ConcurrencyDeclaration.Optimistic
            ? existing?.Version + 1 ?? 1
            : null;
        var status = kind switch
        {
            MutationKind.Insert => WriteOutcomeStatus.Inserted,
            MutationKind.Update => WriteOutcomeStatus.Updated,
            MutationKind.Upsert when exactOutcome && existing is null => WriteOutcomeStatus.Inserted,
            MutationKind.Upsert when exactOutcome => WriteOutcomeStatus.Updated,
            _ => WriteOutcomeStatus.Upserted
        };
        var storedValues = values.Values;
        if (exactOutcome && existing is not null &&
            existing.Values.TryGetValue("createdAt", out var existingCreatedAt))
        {
            var preserved = values.Values.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            preserved["createdAt"] = existingCreatedAt;
            storedValues = new StorageValues(preserved).Values;
        }
        entries[identity] = new InMemoryEntry(storedValues, version);
        state.Revision = checked(state.Revision + 1);
        return new WriteOutcome(status, version);
    }

    internal static WriteOutcome Delete(
        InMemoryUnitState state,
        string partition,
        StorageKey key,
        WriteOptions? options)
    {
        var identity = Key(state.Unit, key.Values);
        var entries = GetEntries(state, partition);
        if (!entries.TryGetValue(identity, out var existing))
            return new WriteOutcome(WriteOutcomeStatus.NotFound);

        if (options?.ExpectedVersion is not null && state.Unit.Concurrency == ConcurrencyDeclaration.None)
            throw new InvalidOperationException(
                $"Storage unit '{state.Unit.Name}' does not declare version machinery.");
        if (state.Unit.Concurrency == ConcurrencyDeclaration.Optimistic &&
            (options?.ExpectedVersion is null || options.ExpectedVersion != existing.Version))
            return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);

        entries.Remove(identity);
        state.Revision = checked(state.Revision + 1);
        return new WriteOutcome(WriteOutcomeStatus.Deleted,
            state.Unit.Concurrency == ConcurrencyDeclaration.Optimistic ? existing.Version : null);
    }

    private static Dictionary<string, InMemoryEntry> GetEntries(
        InMemoryUnitState state,
        string partition)
    {
        if (!state.Partitions.TryGetValue(partition, out var entries))
        {
            entries = new Dictionary<string, InMemoryEntry>(StringComparer.Ordinal);
            state.Partitions.Add(partition, entries);
        }

        return entries;
    }

    private static bool UniqueIndexesAllow(
        StorageUnit unit,
        IReadOnlyDictionary<string, InMemoryEntry> entries,
        string identity,
        IReadOnlyDictionary<string, object?> values)
    {
        foreach (var index in unit.Indexes.Where(index => index.IsUnique))
        {
            var candidate = IndexKey(index, values);
            if (candidate is null)
                continue;
            foreach (var pair in entries)
            {
                if (pair.Key == identity)
                    continue;
                if (IndexKey(index, pair.Value.Values) == candidate)
                    return false;
            }
        }

        return true;
    }

    private static string? IndexKey(
        IndexDefinition index,
        IReadOnlyDictionary<string, object?> values)
    {
        var parts = new List<string>(index.Columns.Count);
        foreach (var column in index.Columns)
        {
            if (!values.TryGetValue(column.Column, out var value))
            {
                if (index.MissingValues == MissingValueBehavior.Excluded)
                    return null;
                parts.Add("<missing>");
            }
            else
                parts.Add(ValueCanonicalizer.Canonical(value));
        }

        return string.Join("|", parts);
    }

    private static string Key(StorageUnit unit, IReadOnlyDictionary<string, object?> values)
    {
        var parts = unit.Key.Columns.Select(column =>
        {
            if (!values.TryGetValue(column, out var value))
                throw new ArgumentException($"Key column '{column}' is required.", nameof(values));
            return ValueCanonicalizer.Canonical(value);
        });
        return string.Join("|", parts);
    }

    private static void ValidateValues(StorageUnit unit, IReadOnlyDictionary<string, object?> values)
    {
        var known = unit.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = values.Keys.Where(key => !known.Contains(key)).OrderBy(key => key, StringComparer.Ordinal).FirstOrDefault();
        if (unknown is not null)
            throw new ArgumentException($"Column '{unknown}' is not declared by '{unit.Name}'.", nameof(values));

        foreach (var column in unit.Columns.Where(column => !column.IsNullable))
        {
            if (!values.TryGetValue(column.Name, out var value) || value is null)
                throw new ArgumentException($"Non-nullable column '{column.Name}' is required.", nameof(values));
        }
    }
}

internal static class ValueCanonicalizer
{
    internal static string Canonical(object? value) => value switch
    {
        null => "null",
        string text => $"string:{text.Length}:{text}",
        bool boolean => $"bool:{boolean}",
        int number => $"int:{number.ToString(CultureInfo.InvariantCulture)}",
        long number => $"long:{number.ToString(CultureInfo.InvariantCulture)}",
        decimal number => $"decimal:{number.ToString(CultureInfo.InvariantCulture)}",
        double number => $"double:{number.ToString("R", CultureInfo.InvariantCulture)}",
        float number => $"float:{number.ToString("R", CultureInfo.InvariantCulture)}",
        Guid guid => $"guid:{guid:D}",
        DateTimeOffset timestamp => $"timestamp:{timestamp:O}",
        byte[] bytes => $"binary:{Convert.ToBase64String(bytes)}",
        JsonElement json => $"json:{json.GetRawText()}",
        _ => $"{value.GetType().FullName}:{value}"
    };
}
