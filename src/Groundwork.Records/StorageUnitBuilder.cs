using Groundwork.Kernel;
using KernelStorageUnit = Groundwork.Kernel.StorageUnit;

namespace Groundwork.Records;

/// <summary>
/// Compatibility entry point for the original records package. The declaration implementation
/// belongs to Groundwork.Kernel; this type only forwards the legacy namespace.
/// </summary>
public static class StorageUnit
{
    public static StorageDeclarationBuilder Declare(string id, string name) =>
        new(KernelStorageUnit.Declare(id, name));
}

/// <summary>Compatibility wrapper around the kernel-owned neutral declaration builder.</summary>
public sealed class StorageDeclarationBuilder
{
    private readonly Groundwork.Kernel.StorageDeclarationBuilder inner;

    internal StorageDeclarationBuilder(Groundwork.Kernel.StorageDeclarationBuilder inner) => this.inner = inner;

    public StorageDeclarationBuilder String(string name, int maxLength, Action<ColumnBuilder>? configure = null) =>
        Wrap(inner.String(name, maxLength, configure));

    public StorageDeclarationBuilder String(string name, Action<ColumnBuilder>? configure = null) =>
        Wrap(inner.String(name, configure));

    public StorageDeclarationBuilder Int32(string name, Action<ColumnBuilder>? configure = null) =>
        Wrap(inner.Int32(name, configure));

    public StorageDeclarationBuilder Int64(string name, Action<ColumnBuilder>? configure = null) =>
        Wrap(inner.Int64(name, configure));

    public StorageDeclarationBuilder Decimal(string name, int precision, int scale, Action<ColumnBuilder>? configure = null) =>
        Wrap(inner.Decimal(name, precision, scale, configure));

    public StorageDeclarationBuilder Decimal(string name, Action<ColumnBuilder>? configure = null) =>
        Wrap(inner.Decimal(name, configure));

    public StorageDeclarationBuilder Boolean(string name, Action<ColumnBuilder>? configure = null) =>
        Wrap(inner.Boolean(name, configure));

    public StorageDeclarationBuilder Timestamp(string name, Action<ColumnBuilder>? configure = null) =>
        Wrap(inner.Timestamp(name, configure));

    public StorageDeclarationBuilder DateTimeOffset(string name, Action<ColumnBuilder>? configure = null) =>
        Wrap(inner.DateTimeOffset(name, configure));

    public StorageDeclarationBuilder Guid(string name, Action<ColumnBuilder>? configure = null) =>
        Wrap(inner.Guid(name, configure));

    public StorageDeclarationBuilder Binary(string name, int maxLength, Action<ColumnBuilder>? configure = null) =>
        Wrap(inner.Binary(name, maxLength, configure));

    public StorageDeclarationBuilder Binary(string name, Action<ColumnBuilder>? configure = null) =>
        Wrap(inner.Binary(name, configure));

    public StorageDeclarationBuilder Json(string name, Action<ColumnBuilder>? configure = null) =>
        Wrap(inner.Json(name, configure));

    public StorageDeclarationBuilder Column(string name, PortableType type, Action<ColumnBuilder>? configure = null) =>
        Wrap(inner.Column(name, type, configure));

    public StorageDeclarationBuilder Key(params string[] columns) => Wrap(inner.Key(columns));

    public StorageDeclarationBuilder Retention(RetentionDeclaration declaration) => Wrap(inner.Retention(declaration));

    public StorageDeclarationBuilder Retention(int keepNewest, string orderBy, RetentionTrigger trigger = RetentionTrigger.Explicit, params string[] partitionColumns) =>
        Wrap(inner.Retention(keepNewest, orderBy, trigger, partitionColumns));

    public StorageDeclarationBuilder KeepNewest(int keepNewest, string orderBy, RetentionTrigger trigger = RetentionTrigger.Explicit, params string[] partitionColumns) =>
        Wrap(inner.KeepNewest(keepNewest, orderBy, trigger, partitionColumns));

    public StorageDeclarationBuilder Retain(RetentionDeclaration declaration) => Wrap(inner.Retain(declaration));

    public StorageDeclarationBuilder Scoped() => Wrap(inner.Scoped());

    public StorageDeclarationBuilder UniqueIndex(string name, params string[] columns) => Wrap(inner.UniqueIndex(name, columns));

    public StorageDeclarationBuilder UniqueIndex(string name, Action<IndexBuilder> configure) =>
        Wrap(inner.UniqueIndex(name, configure));

    public StorageDeclarationBuilder Index(string name, params string[] columns) => Wrap(inner.Index(name, columns));

    public StorageDeclarationBuilder Index(string name, Action<IndexBuilder> configure) =>
        Wrap(inner.Index(name, configure));

    public StorageDeclarationBuilder AppendIdempotency(TimeSpan window, string ledgerName = "__groundwork_operations") =>
        Wrap(inner.AppendIdempotency(window, ledgerName));

    public StorageDeclarationBuilder Aggregate(string name, Action<AggregationBuilder> configure) =>
        Wrap(inner.Aggregate(name, configure));

    public KernelStorageUnit Build(PortabilityValidationContext? context = null)
    {
        try
        {
            return inner.Build(context);
        }
        catch (DeclarationBuildException exception)
        {
            throw DiagnosticsCompatibility.ToRecords(exception);
        }
    }

    private StorageDeclarationBuilder Wrap(Groundwork.Kernel.StorageDeclarationBuilder builder) => this;
}
