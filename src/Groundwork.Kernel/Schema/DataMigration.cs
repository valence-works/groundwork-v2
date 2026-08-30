using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Groundwork.Kernel.Schema;

/// <summary>Refusal codes raised by the data-migration facility.</summary>
public static class DataMigrationCodes
{
    /// <summary>The provider does not advertise a capability the facility requires.</summary>
    public const string MissingCapability = "GW-MIGRATION-001";

    /// <summary>A migration identity was recorded with a different request fingerprint.</summary>
    public const string RequestConflict = "GW-MIGRATION-002";

    /// <summary>The provider session offers no data-migration execution at all.</summary>
    public const string NotSupported = "GW-MIGRATION-003";

    /// <summary>The migration cannot be expressed against the subject it is attached to.</summary>
    public const string NotApplicable = "GW-MIGRATION-004";

    /// <summary>Durable ledger state is missing, malformed, or self-contradictory.</summary>
    public const string LedgerCorrupt = "GW-MIGRATION-005";

    /// <summary>A host transform produced a value it did not declare as a target column.</summary>
    public const string UndeclaredTargetColumn = "GW-MIGRATION-006";

    /// <summary>A migration stopped before its source was exhausted and can be resumed.</summary>
    public const string Incomplete = "GW-MIGRATION-007";

    /// <summary>A declaration names a semantic migration the running host does not supply.</summary>
    public const string MissingTransform = "GW-MIGRATION-008";
}

/// <summary>A refusal raised by the data-migration facility, naming what was refused and why.</summary>
public sealed class DataMigrationRefusedException : InvalidOperationException
{
    public DataMigrationRefusedException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}

/// <summary>
/// What a provider can actually do for a data migration. The kernel checks these rather than
/// assuming a relational shape; a provider that cannot honour one refuses instead of approximating.
/// </summary>
[Flags]
public enum DataMigrationCapabilities
{
    None = 0,

    /// <summary>Rows can be read in a stable total key order, resuming strictly after a key.</summary>
    KeysetScan = 1,

    /// <summary>One chunk's row writes and its progress record commit or roll back together.</summary>
    AtomicChunkProgress = 2,

    /// <summary>Applied and in-progress migrations are recorded in provider-owned durable state.</summary>
    AppliedLedger = 4,

    /// <summary>A chunk is written with set-based statements rather than one write per row.</summary>
    SetBasedBatchUpdate = 8
}

/// <summary>One source row handed to a host-process transform.</summary>
public sealed class DataMigrationRow
{
    public DataMigrationRow(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = values;
    }

    public IReadOnlyDictionary<string, object?> Values { get; }

    public object? this[string column] => Values.TryGetValue(column, out var value)
        ? value
        : throw new DataMigrationRefusedException(
            DataMigrationCodes.NotApplicable,
            $"the transform read column '{column}', which the scanned row does not carry.");

    public bool TryGetValue(string column, out object? value) => Values.TryGetValue(column, out value);
}

/// <summary>The values one row yields. A transform that changes nothing returns <see cref="Unchanged"/>.</summary>
public readonly struct DataMigrationValues
{
    private DataMigrationValues(IReadOnlyDictionary<string, object?>? values) => Values = values;

    /// <summary>Leaves the row exactly as it is; no write is issued for it.</summary>
    public static DataMigrationValues Unchanged => default;

    public static DataMigrationValues Set(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new DataMigrationValues(values);
    }

    public IReadOnlyDictionary<string, object?>? Values { get; }

    public bool HasValues => Values is { Count: > 0 };
}

/// <summary>
/// A host-process row transform attached to a semantic migration: row in, values out. It is
/// deliberately synchronous and returns no task, because a transform runs once per row inside a
/// provider chunk and may run again after an interruption — it must be a pure function of the row,
/// never a place to do I/O.
/// </summary>
public interface IDataMigrationTransform
{
    /// <summary>
    /// A stable identity for what this transform computes. It is part of the migration's request
    /// fingerprint, so replaying a recorded migration identity with changed logic is refused
    /// rather than silently producing different values.
    /// </summary>
    string Identity { get; }

    /// <summary>Columns the transform reads. The scan projects exactly these plus the key.</summary>
    ImmutableArray<string> SourceColumns { get; }

    /// <summary>Columns the transform may write. Producing any other column is refused.</summary>
    ImmutableArray<string> TargetColumns { get; }

    DataMigrationValues Transform(DataMigrationRow row);
}

/// <summary>One transform attached to a semantic migration identity for one subject.</summary>
public sealed class DataMigration
{
    public DataMigration(
        string semanticMigrationId,
        StorageUnitId subjectId,
        IDataMigrationTransform transform,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticMigrationId);
        ArgumentNullException.ThrowIfNull(transform);
        if (transform.TargetColumns.IsDefaultOrEmpty)
            throw new ArgumentException("A data migration transform must declare at least one target column.", nameof(transform));
        if (string.IsNullOrWhiteSpace(transform.Identity))
            throw new ArgumentException("A data migration transform must declare a stable identity.", nameof(transform));

        Id = semanticMigrationId;
        SubjectId = subjectId;
        Transform = transform;
        Description = description;
    }

    public string Id { get; }

    public StorageUnitId SubjectId { get; }

    public IDataMigrationTransform Transform { get; }

    public string? Description { get; }

    /// <summary>
    /// Validates that this migration can be expressed against <paramref name="unit"/>, and returns
    /// the columns the scan must project: the key columns first, then the declared sources.
    /// </summary>
    public ImmutableArray<string> ValidateAgainst(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var declared = unit.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        if (unit.Key.Columns.Count == 0)
            throw Refuse($"storage unit '{unit.Name}' declares no key, so migration '{Id}' has no resumable order.");
        foreach (var key in unit.Key.Columns)
        {
            if (!declared.TryGetValue(key, out var column))
                throw Refuse($"storage unit '{unit.Name}' names key column '{key}', which it does not declare.");
            if (column.Type == PortableType.Json)
                throw Refuse($"key column '{key}' of '{unit.Name}' is JSON, which carries no portable resume order.");
        }
        foreach (var source in Transform.SourceColumns.IsDefault ? [] : Transform.SourceColumns)
        {
            if (!declared.ContainsKey(source))
                throw Refuse($"migration '{Id}' reads column '{source}', which storage unit '{unit.Name}' does not declare.");
        }
        foreach (var target in Transform.TargetColumns)
        {
            if (!declared.ContainsKey(target))
                throw Refuse($"migration '{Id}' writes column '{target}', which storage unit '{unit.Name}' does not declare.");
            if (unit.Key.Columns.Contains(target, StringComparer.Ordinal))
                throw Refuse($"migration '{Id}' writes key column '{target}' of '{unit.Name}'; a data migration cannot move the key it resumes on.");
        }

        return
        [
            .. unit.Key.Columns,
            .. (Transform.SourceColumns.IsDefault ? [] : Transform.SourceColumns)
                .Where(source => !unit.Key.Columns.Contains(source, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// The request fingerprint recorded beside the migration identity. Replaying the identity with a
    /// changed transform, subject, or column set is a conflict rather than a no-op.
    /// </summary>
    public string RequestFingerprint(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return SchemaFingerprint.Create(
        [
            "data-migration-v1",
            Id,
            SubjectId.Value,
            unit.Name,
            Transform.Identity,
            .. (Transform.SourceColumns.IsDefault ? [] : Transform.SourceColumns).Select(column => "source:" + column),
            .. Transform.TargetColumns.Select(column => "target:" + column),
            .. unit.Key.Columns.Select(column => "key:" + column)
        ]);
    }

    private DataMigrationRefusedException Refuse(string message) =>
        new(DataMigrationCodes.NotApplicable, message);
}

/// <summary>Host-supplied transforms, looked up by the semantic migration identity that names them.</summary>
public sealed class DataMigrationCatalog
{
    private readonly ImmutableDictionary<string, DataMigration> byIdentity;

    public DataMigrationCatalog(IEnumerable<DataMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        var builder = ImmutableDictionary.CreateBuilder<string, DataMigration>(StringComparer.Ordinal);
        foreach (var migration in migrations)
        {
            ArgumentNullException.ThrowIfNull(migration);
            var key = Key(migration.Id, migration.SubjectId);
            if (builder.ContainsKey(key))
            {
                throw new ArgumentException(
                    $"Semantic migration '{migration.Id}' already has a transform for subject '{migration.SubjectId.Value}'.",
                    nameof(migrations));
            }
            builder.Add(key, migration);
        }
        byIdentity = builder.ToImmutable();
        Migrations = byIdentity.Values.OrderBy(migration => migration.Id, StringComparer.Ordinal).ToImmutableArray();
    }

    public static DataMigrationCatalog Empty { get; } = new([]);

    public ImmutableArray<DataMigration> Migrations { get; }

    public bool TryGet(string? semanticMigrationId, StorageUnitId subjectId, out DataMigration migration)
    {
        if (string.IsNullOrWhiteSpace(semanticMigrationId))
        {
            migration = null!;
            return false;
        }
        return byIdentity.TryGetValue(Key(semanticMigrationId, subjectId), out migration!);
    }

    /// <summary>
    /// Resolves a declaration's optional semantic migration, refusing an exact non-empty identity
    /// the host catalog does not supply.
    /// </summary>
    public DataMigration? ResolveDeclared(string? semanticMigrationId, StorageUnitId subjectId)
    {
        if (semanticMigrationId is null)
            return null;
        if (TryGet(semanticMigrationId, subjectId, out var migration))
            return migration;
        throw new DataMigrationRefusedException(
            DataMigrationCodes.MissingTransform,
            $"semantic migration '{semanticMigrationId}' for subject '{subjectId.Value}' has no host-supplied transform.");
    }

    private static string Key(string migrationId, StorageUnitId subjectId) =>
        SchemaFingerprint.Canonicalize([migrationId, subjectId.Value]);
}

/// <summary>Bounds one resumable data-migration pass.</summary>
public sealed record DataMigrationBudget
{
    public int MaxRowsPerBatch { get; init; } = 512;

    /// <summary>Batches this pass may run. Null runs until the source is exhausted.</summary>
    public int? MaxBatches { get; init; }

    /// <summary>Rows this pass may scan. Null scans until the source is exhausted.</summary>
    public long? MaxRows { get; init; }

    public static DataMigrationBudget Default { get; } = new();

    public DataMigrationBudget Validate()
    {
        if (MaxRowsPerBatch <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxRowsPerBatch), MaxRowsPerBatch, "A data-migration batch must hold at least one row.");
        if (MaxBatches is <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxBatches), MaxBatches, "A data-migration pass must allow at least one batch.");
        if (MaxRows is <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxRows), MaxRows, "A data-migration pass must allow at least one row.");
        return this;
    }
}

/// <summary>
/// Keyset progress: the last key a chunk committed, in the subject's declared key order. It is
/// produced from a scanned row and encoded portably, so a provider resumes strictly after it
/// rather than counting rows it has already rewritten.
/// </summary>
public sealed class DataMigrationCursor
{
    private DataMigrationCursor(ImmutableArray<object?> values, string canonical)
    {
        Values = values;
        Canonical = canonical;
    }

    /// <summary>Key values in the subject's declared key order.</summary>
    public ImmutableArray<object?> Values { get; }

    /// <summary>The portable ledger encoding of <see cref="Values"/>.</summary>
    public string Canonical { get; }

    public static DataMigrationCursor After(StorageUnit unit, IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(row);
        var values = ImmutableArray.CreateBuilder<object?>(unit.Key.Columns.Count);
        var parts = new List<string?>(unit.Key.Columns.Count);
        foreach (var column in unit.Key.Columns)
        {
            if (!row.TryGetValue(column, out var value) || value is null)
            {
                throw new DataMigrationRefusedException(
                    DataMigrationCodes.NotApplicable,
                    $"key column '{column}' of '{unit.Name}' is absent or null in a scanned row, so it carries no resume position.");
            }
            values.Add(value);
            parts.Add(Encode(value));
        }
        return new DataMigrationCursor(values.MoveToImmutable(), SchemaFingerprint.Canonicalize(parts));
    }

    public static bool TryDecode(StorageUnit unit, string canonical, out DataMigrationCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(unit);
        cursor = null!;
        if (canonical is null || !SchemaFingerprint.TryParseCanonical(canonical, out var parts) ||
            parts.Length != unit.Key.Columns.Count)
        {
            return false;
        }

        var declared = unit.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var values = ImmutableArray.CreateBuilder<object?>(parts.Length);
        for (var index = 0; index < parts.Length; index++)
        {
            if (!declared.TryGetValue(unit.Key.Columns[index], out var column) ||
                parts[index] is not { } encoded ||
                !TryDecode(encoded, column.Type, out var value))
            {
                return false;
            }
            values.Add(value);
        }

        cursor = new DataMigrationCursor(values.MoveToImmutable(), canonical);
        return true;
    }

    public override string ToString() => Canonical;

    private static string Encode(object value) => value switch
    {
        string text => "s" + text,
        bool boolean => boolean ? "b1" : "b0",
        byte or sbyte or short or ushort or int or uint or long or ulong =>
            "i" + Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        decimal number => "d" + number.ToString(CultureInfo.InvariantCulture),
        double number => "d" + number.ToString("R", CultureInfo.InvariantCulture),
        float number => "d" + number.ToString("R", CultureInfo.InvariantCulture),
        DateTimeOffset instant => "t" + instant.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
        DateTime instant => "t" + instant.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
        Guid guid => "g" + guid.ToString("D"),
        byte[] bytes => "x" + Convert.ToHexString(bytes),
        _ => throw new DataMigrationRefusedException(
            DataMigrationCodes.NotApplicable,
            $"key value type '{value.GetType()}' has no portable data-migration resume encoding.")
    };

    private static bool TryDecode(string encoded, PortableType type, out object? value)
    {
        value = null;
        if (encoded.Length == 0)
            return false;
        var payload = encoded[1..];
        switch (encoded[0])
        {
            case 's' when type == PortableType.String:
                value = payload;
                return true;
            case 'b' when type == PortableType.Boolean && payload is "0" or "1":
                value = payload == "1";
                return true;
            case 'i' when type is PortableType.Int32 or PortableType.Int64:
                if (!long.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    return false;
                if (type == PortableType.Int32 && integer is < int.MinValue or > int.MaxValue)
                    return false;
                value = type == PortableType.Int32 ? (object)(int)integer : integer;
                return true;
            case 'd' when type == PortableType.Decimal:
                if (!decimal.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    return false;
                value = number;
                return true;
            case 't' when type == PortableType.DateTimeOffset:
                if (!long.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) ||
                    ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
                {
                    return false;
                }
                value = new DateTimeOffset(ticks, TimeSpan.Zero);
                return true;
            case 'g' when type == PortableType.Guid:
                if (!Guid.TryParseExact(payload, "D", out var guid))
                    return false;
                value = guid;
                return true;
            case 'x' when type == PortableType.Binary:
                try
                {
                    value = Convert.FromHexString(payload);
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }
            default:
                return false;
        }
    }
}

/// <summary>Whether a recorded data migration is still running or is durably finished.</summary>
public enum DataMigrationRunState
{
    Running,
    Completed
}

/// <summary>
/// Evidence, obtainable only from a chunk outcome the provider marked exhausted, that a migration
/// has no rows left. It cannot be constructed outside this assembly, so no caller can turn "the
/// cursor reached the last chunk" into "the migration finished" — the two stay distinguishable.
/// </summary>
public readonly struct DataMigrationExhaustion
{
    internal DataMigrationExhaustion(string migrationId) => MigrationId = migrationId;

    internal string? MigrationId { get; }

    internal bool IsEvidence => MigrationId is not null;
}

/// <summary>
/// One durable data-migration record in provider-owned state. <see cref="DataMigrationRunState.Completed"/>
/// carries a completion instant and no cursor; <see cref="DataMigrationRunState.Running"/> carries a
/// cursor and no completion instant. The constructor refuses any other combination, so an interrupted
/// migration cannot be read back as a finished one.
///
/// It is a class with one validating constructor rather than a record: a record's <c>with</c>
/// expression bypasses constructor validation through its init setters, which would let a caller
/// write "completed, and here is where to resume from" — the exact state this type exists to make
/// unrepresentable.
/// </summary>
public sealed class DataMigrationLedgerEntry
{
    public DataMigrationLedgerEntry(
        PhysicalSchemaTargetIdentity target,
        string migrationId,
        string unitName,
        string requestFingerprint,
        DataMigrationRunState state,
        string? cursor,
        long rowsScanned,
        long rowsChanged,
        int batches,
        DateTimeOffset startedAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? completedAt)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(unitName);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestFingerprint);
        if (rowsScanned < 0 || rowsChanged < 0 || batches < 0)
            throw Corrupt(migrationId, "records a negative row or batch count.");
        if (state == DataMigrationRunState.Completed)
        {
            if (completedAt is null)
                throw Corrupt(migrationId, "is marked completed without a completion instant.");
            if (cursor is not null)
                throw Corrupt(migrationId, "is marked completed while still carrying a resume cursor.");
        }
        else
        {
            if (completedAt is not null)
                throw Corrupt(migrationId, "is marked running while carrying a completion instant.");
        }

        Target = target;
        MigrationId = migrationId;
        UnitName = unitName;
        RequestFingerprint = requestFingerprint;
        State = state;
        Cursor = cursor;
        RowsScanned = rowsScanned;
        RowsChanged = rowsChanged;
        Batches = batches;
        StartedAt = startedAt;
        UpdatedAt = updatedAt;
        CompletedAt = completedAt;
    }

    public PhysicalSchemaTargetIdentity Target { get; }

    public string MigrationId { get; }

    public string UnitName { get; }

    public string RequestFingerprint { get; }

    public DataMigrationRunState State { get; }

    /// <summary>The last committed keyset position. Always null on a completed entry.</summary>
    public string? Cursor { get; }

    public long RowsScanned { get; }

    public long RowsChanged { get; }

    public int Batches { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    /// <summary>Always set on a completed entry, never on a running one.</summary>
    public DateTimeOffset? CompletedAt { get; }

    public bool IsComplete => State == DataMigrationRunState.Completed;

    public static DataMigrationLedgerEntry Start(
        PhysicalSchemaTargetIdentity target,
        DataMigration migration,
        StorageUnit unit,
        DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentNullException.ThrowIfNull(unit);
        return new DataMigrationLedgerEntry(
            target,
            migration.Id,
            unit.Name,
            migration.RequestFingerprint(unit),
            DataMigrationRunState.Running,
            cursor: null,
            rowsScanned: 0,
            rowsChanged: 0,
            batches: 0,
            startedAt,
            startedAt,
            completedAt: null);
    }

    /// <summary>Records one committed chunk. The provider persists the result inside that chunk.</summary>
    public DataMigrationLedgerEntry Advance(
        DataMigrationCursor cursor,
        long rowsScanned,
        long rowsChanged,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (State != DataMigrationRunState.Running)
            throw Corrupt(MigrationId, "cannot advance after it was recorded as completed.");
        if (rowsScanned <= 0)
            throw Corrupt(MigrationId, "cannot advance its resume cursor without scanning a row.");
        return new DataMigrationLedgerEntry(
            Target,
            MigrationId,
            UnitName,
            RequestFingerprint,
            DataMigrationRunState.Running,
            cursor.Canonical,
            RowsScanned + rowsScanned,
            RowsChanged + rowsChanged,
            Batches + 1,
            StartedAt,
            at,
            completedAt: null);
    }

    /// <summary>
    /// Seals the migration as durably finished. It takes exhaustion evidence rather than a boolean,
    /// so only a chunk the provider actually reported exhausted can end a migration.
    /// </summary>
    public DataMigrationLedgerEntry Complete(DataMigrationExhaustion exhaustion, DateTimeOffset at)
    {
        if (!exhaustion.IsEvidence)
        {
            throw new DataMigrationRefusedException(
                DataMigrationCodes.Incomplete,
                $"data migration '{MigrationId}' cannot be completed without evidence that its source was exhausted.");
        }
        if (!string.Equals(exhaustion.MigrationId, MigrationId, StringComparison.Ordinal))
        {
            throw new DataMigrationRefusedException(
                DataMigrationCodes.Incomplete,
                $"data migration '{MigrationId}' cannot be completed with exhaustion evidence from '{exhaustion.MigrationId}'.");
        }
        if (State == DataMigrationRunState.Completed)
            return this;
        return new DataMigrationLedgerEntry(
            Target,
            MigrationId,
            UnitName,
            RequestFingerprint,
            DataMigrationRunState.Completed,
            cursor: null,
            RowsScanned,
            RowsChanged,
            Batches,
            StartedAt,
            at,
            completedAt: at);
    }

    private static DataMigrationRefusedException Corrupt(string migrationId, string detail) =>
        new(DataMigrationCodes.LedgerCorrupt, $"the ledger entry for data migration '{migrationId}' {detail}");
}

/// <summary>One chunk of work handed to a provider.</summary>
public sealed class DataMigrationChunkRequest
{
    public DataMigrationChunkRequest(
        DataMigration migration,
        StorageUnit unit,
        DataMigrationLedgerEntry entry,
        DataMigrationCursor? cursor,
        ImmutableArray<string> projection,
        int maxRows)
    {
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(entry);
        if (maxRows <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRows), maxRows, "A chunk must admit at least one row.");
        if (entry.State != DataMigrationRunState.Running)
            throw new ArgumentException("A completed data migration has no further chunks.", nameof(entry));
        if (cursor is not null && !string.Equals(cursor.Canonical, entry.Cursor, StringComparison.Ordinal))
            throw new ArgumentException("The chunk cursor does not match the ledger entry it resumes.", nameof(cursor));

        Migration = migration;
        Unit = unit;
        Entry = entry;
        Cursor = cursor;
        Projection = projection;
        MaxRows = maxRows;
    }

    public DataMigration Migration { get; }

    public StorageUnit Unit { get; }

    /// <summary>The running ledger entry this chunk advances. The provider persists the advanced entry.</summary>
    public DataMigrationLedgerEntry Entry { get; }

    /// <summary>Resume strictly after this key, or from the start when null.</summary>
    public DataMigrationCursor? Cursor { get; }

    /// <summary>Columns the scan must project: key columns first, then the transform's sources.</summary>
    public ImmutableArray<string> Projection { get; }

    public int MaxRows { get; }

    /// <summary>
    /// Applies the transform to one scanned row, refusing any produced column the transform did not
    /// declare as a target. Providers call this so the declared write set is enforced once.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Apply(IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var produced = Migration.Transform.Transform(new DataMigrationRow(row));
        if (!produced.HasValues)
            return null;
        foreach (var column in produced.Values!.Keys)
        {
            if (!Migration.Transform.TargetColumns.Contains(column, StringComparer.Ordinal))
            {
                throw new DataMigrationRefusedException(
                    DataMigrationCodes.UndeclaredTargetColumn,
                    $"data migration '{Migration.Id}' produced column '{column}', which its transform does not declare as a target.");
            }
        }
        return new ReadOnlyDictionary<string, object?>(
            produced.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }
}

/// <summary>What one committed chunk did, and whether the source is exhausted.</summary>
public sealed class DataMigrationChunkOutcome
{
    private DataMigrationChunkOutcome(DataMigrationLedgerEntry entry, bool exhausted)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        IsExhausted = exhausted;
        Evidence = exhausted ? new DataMigrationExhaustion(entry.MigrationId) : default;
    }

    /// <summary>The chunk committed rows and its advanced progress; more rows remain.</summary>
    public static DataMigrationChunkOutcome Advanced(DataMigrationLedgerEntry entry) => new(entry, false);

    /// <summary>The chunk observed the source exhausted. This is the only source of completion evidence.</summary>
    public static DataMigrationChunkOutcome Exhausted(DataMigrationLedgerEntry entry) => new(entry, true);

    /// <summary>The ledger entry as durably committed by this chunk.</summary>
    public DataMigrationLedgerEntry Entry { get; }

    public bool IsExhausted { get; }

    internal DataMigrationExhaustion Evidence { get; }
}

/// <summary>
/// Provider execution boundary for data migrations. Every member that talks to a store is declared
/// twice — synchronous, and asynchronous with a cancellation token — matching the storage session.
/// </summary>
public interface IDataMigrationExecutor
{
    /// <summary>What this provider can actually honour. The kernel checks it before any row moves.</summary>
    DataMigrationCapabilities Capabilities { get; }

    DataMigrationLedgerEntry? ReadLedgerEntry(PhysicalSchemaTargetIdentity target, string migrationId);

    ValueTask<DataMigrationLedgerEntry?> ReadLedgerEntryAsync(
        PhysicalSchemaTargetIdentity target,
        string migrationId,
        CancellationToken cancellationToken = default);

    /// <summary>All ledger entries recorded for one target, used by inspection and status reporting.</summary>
    IReadOnlyList<DataMigrationLedgerEntry> ReadLedgerEntries(PhysicalSchemaTargetIdentity target);

    ValueTask<IReadOnlyList<DataMigrationLedgerEntry>> ReadLedgerEntriesAsync(
        PhysicalSchemaTargetIdentity target,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes one ledger entry on its own. The runner uses it only for the first record of a pass and
    /// for the terminal completion; chunk progress is written by <see cref="ExecuteChunk"/> instead,
    /// inside the same durable unit as the rows it describes.
    /// </summary>
    void WriteLedgerEntry(DataMigrationLedgerEntry entry);

    ValueTask WriteLedgerEntryAsync(DataMigrationLedgerEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads, transforms, writes, and records progress for one chunk as a single durable unit.
    /// </summary>
    DataMigrationChunkOutcome ExecuteChunk(DataMigrationChunkRequest request);

    ValueTask<DataMigrationChunkOutcome> ExecuteChunkAsync(
        DataMigrationChunkRequest request,
        CancellationToken cancellationToken = default);
}
