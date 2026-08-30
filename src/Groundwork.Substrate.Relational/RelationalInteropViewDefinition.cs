using System.Globalization;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;

namespace Groundwork.Substrate.Relational;

/// <summary>Canonical provider definition for one opt-in relational reporting view.</summary>
public static class RelationalInteropViewDefinition
{
    public const string Kind = ProviderPhysicalSchemaDefinitionKinds.InteropView;
    private const int FormatVersion = 1;
    private const char Separator = '\u001f';

    public static ProviderPhysicalSchemaDefinition? Create(string providerName, StorageUnit physical)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(physical);
        if (physical.InteropView is null)
            return null;

        var columns = physical.Columns
            .Where(column => !column.Name.StartsWith("__groundwork_", StringComparison.Ordinal))
            .Concat(physical.Scope == ScopePolicy.Scoped
                ? [physical.Columns.Single(column => column.Name == ProviderOwnedColumns.Scope)]
                : [])
            .Select(column => new InteropViewColumn(
                column.Name,
                column.Type,
                column.IsNullable,
                column.MaxLength,
                column.Precision,
                column.Scale))
            .ToArray();
        var model = new InteropViewModel(
            FormatVersion,
            physical.InteropView.Name,
            physical.Name,
            physical.Scope,
            columns);
        return new ProviderPhysicalSchemaDefinition(
            providerName,
            physical.Id,
            Kind,
            model.ViewName,
            Serialize(model));
    }

    public static IReadOnlyList<ProviderPhysicalSchemaDefinition> AppendTo(
        string providerName,
        StorageUnit physical,
        IEnumerable<ProviderPhysicalSchemaDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var result = definitions.ToList();
        if (Create(providerName, physical) is { } interopView)
            result.Add(interopView);
        return result;
    }

    internal static InteropViewModel Parse(ProviderPhysicalSchemaDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.Equals(definition.Kind, Kind, StringComparison.Ordinal))
            throw new ArgumentException($"Provider definition '{definition.Kind}' is not an interop view.", nameof(definition));
        var model = Deserialize(definition.CanonicalDefinition);
        if (model.FormatVersion != FormatVersion ||
            string.IsNullOrWhiteSpace(model.ViewName) ||
            string.IsNullOrWhiteSpace(model.SourceName) ||
            model.Columns is null ||
            model.Columns.Length == 0 ||
            model.Columns.Any(column => string.IsNullOrWhiteSpace(column.Name)) ||
            !string.Equals(model.ViewName, definition.SubjectIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The interop view definition is malformed or uses an unsupported format.");
        }
        return model;
    }

    private static string Serialize(InteropViewModel model)
    {
        var parts = new List<string>
        {
            model.FormatVersion.ToString(CultureInfo.InvariantCulture),
            model.ViewName,
            model.SourceName,
            model.Scope.ToString(),
            model.Columns.Length.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var column in model.Columns)
        {
            parts.Add(column.Name);
            parts.Add(column.Type.ToString());
            parts.Add(column.IsNullable ? "1" : "0");
            parts.Add(column.MaxLength?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            parts.Add(column.Precision?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            parts.Add(column.Scale?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        }
        return string.Join(Separator, parts);
    }

    private static InteropViewModel Deserialize(string canonical)
    {
        var parts = canonical.Split(Separator);
        if (parts.Length < 5 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            !Enum.TryParse<ScopePolicy>(parts[3], ignoreCase: false, out var scope) ||
            !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
            count <= 0 || parts.Length != 5 + count * 6)
        {
            throw new InvalidOperationException("The interop view definition is malformed.");
        }

        var columns = new InteropViewColumn[count];
        for (var index = 0; index < count; index++)
        {
            var offset = 5 + index * 6;
            if (!Enum.TryParse<PortableType>(parts[offset + 1], ignoreCase: false, out var type))
                throw new InvalidOperationException("The interop view definition contains an unknown portable type.");
            columns[index] = new InteropViewColumn(
                parts[offset],
                type,
                parts[offset + 2] == "1",
                NullableInt(parts[offset + 3]),
                NullableInt(parts[offset + 4]),
                NullableInt(parts[offset + 5]));
        }
        return new InteropViewModel(version, parts[1], parts[2], scope, columns);
    }

    private static int? NullableInt(string value) => string.IsNullOrEmpty(value)
        ? null
        : int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    internal sealed record InteropViewModel(
        int FormatVersion,
        string ViewName,
        string SourceName,
        ScopePolicy Scope,
        InteropViewColumn[] Columns);

    internal sealed record InteropViewColumn(
        string Name,
        PortableType Type,
        bool IsNullable,
        int? MaxLength,
        int? Precision,
        int? Scale)
    {
        public ColumnDefinition ToColumn() => new()
        {
            Name = Name,
            Type = Type,
            IsNullable = IsNullable,
            MaxLength = MaxLength,
            Precision = Precision,
            Scale = Scale
        };
    }
}
