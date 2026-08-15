using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Groundwork.Kernel;
using Groundwork.Records;
using Groundwork.Documents.Serialization;
using KernelStorageUnit = Groundwork.Kernel.StorageUnit;

namespace Groundwork.Documents;

/// <summary>Stable mapping from one document JSON path to one ordinary storage column.</summary>
public sealed record ColumnBinding(
    string Column,
    string Path,
    PortableType Type)
{
    public string ColumnName => Column;

    public string JsonPath => Path;
}

/// <summary>Structured diagnostics produced when a document declaration is unsafe or ambiguous.</summary>
public sealed class DocumentDiagnostic
{
    public DocumentDiagnostic(string code, string message, string path)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public string Code { get; }
    public string Message { get; }
    public string Path { get; }
}

/// <summary>Raised before a document contract can leak an ambiguous mapping into a provider.</summary>
public sealed class DocumentDeclarationException : Exception
{
    public DocumentDeclarationException(IEnumerable<DocumentDiagnostic> diagnostics)
        : this((diagnostics ?? throw new ArgumentNullException(nameof(diagnostics))).ToArray()) { }

    private DocumentDeclarationException(DocumentDiagnostic[] diagnostics)
        : base("The document declaration is invalid: " + string.Join("; ", diagnostics.Select(d => d.Code + ": " + d.Message))) =>
        Diagnostics = Array.AsReadOnly(diagnostics);

    public IReadOnlyList<DocumentDiagnostic> Diagnostics { get; }
}

/// <summary>Starts a typed document declaration without introducing a provider dependency.</summary>
public static class DocumentUnit
{
    public static DocumentUnitBuilder<T> For<T>(string documentKind, string name) =>
        new(documentKind, name);
}

/// <summary>Expression-based authoring state for one typed document contract.</summary>
public sealed class DocumentUnitBuilder<T>
{
    private readonly string documentKind;
    private readonly string name;
    private readonly List<ProjectedMember> projections = [];
    private readonly List<IndexMember> indexes = [];
    private Func<T, object?>? idSelector;
    private MemberInfo? idMember;
    private Action<ColumnBuilder>? idConfiguration;
    private bool optimistic;
    private string tokenColumn = "version";
    private bool scoped;
    private bool sharedKind;
    private int minimumReadableVersion = 1;
    private int currentVersion = 1;
    private JsonSerializerOptions? jsonOptions;

    internal DocumentUnitBuilder(string documentKind, string name)
    {
        this.documentKind = RequireText(documentKind, nameof(documentKind));
        this.name = RequireText(name, nameof(name));
    }

    /// <summary>Declares the native typed key. The value is also written to the JSON document.</summary>
    public DocumentUnitBuilder<T> Id<TKey>(Expression<Func<T, TKey>> selector, Action<ColumnBuilder>? configure = null)
    {
        idMember = SingleMember(selector);
        idSelector = ConvertSelector(selector).Compile();
        idConfiguration = configure;
        return this;
    }

    /// <summary>Declares a typed projection and its stable serialized JSON path.</summary>
    public DocumentUnitBuilder<T> Project<TValue>(
        Expression<Func<T, TValue>> selector,
        Action<ColumnBuilder>? configure = null)
    {
        var memberPath = MemberPath(selector);
        var path = string.Join('.', memberPath.Members.Select(JsonName));
        var column = memberPath.Column;
        if (projections.Any(existing => string.Equals(existing.Path, path, StringComparison.Ordinal)))
            throw Invalid("GW-DOC-DECL-002", $"JSON path '{path}' is projected more than once.", "projections");

        projections.Add(new ProjectedMember(
            path,
            column,
            selector.Compile(),
            ToPortableType(typeof(TValue)),
            configure));
        return this;
    }

    /// <summary>Declares an index over a previously declared typed projection.</summary>
    public DocumentUnitBuilder<T> Index<TValue>(
        string indexName,
        Expression<Func<T, TValue>> selector,
        SortDirection direction = SortDirection.Ascending)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        var memberPath = MemberPath(selector);
        var path = string.Join('.', memberPath.Members.Select(JsonName));
        if (indexes.Any(existing => string.Equals(existing.Name, indexName, StringComparison.Ordinal)))
            throw Invalid("GW-DOC-DECL-004", $"Index '{indexName}' is declared more than once.", $"indexes.{indexName}");
        indexes.Add(new IndexMember(indexName, path, direction));
        return this;
    }

    public DocumentUnitBuilder<T> OptimisticConcurrency(string tokenColumn = "version")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenColumn);
        optimistic = true;
        this.tokenColumn = tokenColumn;
        return this;
    }

    public DocumentUnitBuilder<T> Optimistic(string tokenColumn = "version") =>
        OptimisticConcurrency(tokenColumn);

    public DocumentUnitBuilder<T> Scoped()
    {
        scoped = true;
        return this;
    }

    /// <summary>Includes one constant kind column when several document kinds share this unit.</summary>
    public DocumentUnitBuilder<T> SharedKind()
    {
        sharedKind = true;
        return this;
    }

    /// <summary>Configures the persisted version window; stamps use the stable vN format.</summary>
    public DocumentUnitBuilder<T> SchemaVersion(int current, int minimumReadable = 1)
    {
        if (minimumReadable < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumReadable));
        if (current < minimumReadable)
            throw new ArgumentOutOfRangeException(nameof(current), "Current schema version must be at least the minimum readable version.");
        currentVersion = current;
        minimumReadableVersion = minimumReadable;
        return this;
    }

    /// <summary>Uses the supplied options for canonical JSON serialization and materialization.</summary>
    public DocumentUnitBuilder<T> JsonOptions(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        jsonOptions = new JsonSerializerOptions(options);
        return this;
    }

    public DocumentUnit<T> Build()
    {
        var diagnostics = new List<DocumentDiagnostic>();
        if (idSelector is null || idMember is null)
            diagnostics.Add(new("GW-DOC-DECL-001", "A document declaration requires one native Id selector before Build().", "id"));

        var idColumn = idMember is null ? null : LowerFirst(idMember.Name);
        var bindings = new List<ColumnBinding>();
        var occupiedColumns = new HashSet<string>(StringComparer.Ordinal)
        {
            "document",
            "schemaVersion"
        };
        if (sharedKind)
            occupiedColumns.Add("kind");
        if (optimistic)
            occupiedColumns.Add(tokenColumn);

        if (idColumn is not null && !occupiedColumns.Add(idColumn))
            diagnostics.Add(new("GW-DOC-DECL-003", $"The id column '{idColumn}' collides with a reserved document column.", "id"));

        foreach (var projection in projections)
        {
            if (!occupiedColumns.Add(projection.Column))
            {
                diagnostics.Add(new(
                    "GW-DOC-DECL-003",
                    $"Projection column '{projection.Column}' collides with a reserved or already declared column.",
                    $"projections.{projection.Path}"));
                continue;
            }

            bindings.Add(new ColumnBinding(projection.Column, projection.Path, projection.Type));
        }

        foreach (var index in indexes)
        {
            if (!projections.Any(projection => string.Equals(projection.Path, index.Path, StringComparison.Ordinal)))
                diagnostics.Add(new(
                    "GW-DOC-DECL-005",
                    $"Index '{index.Name}' targets path '{index.Path}', which has no projected column. Declare Project() first.",
                    $"indexes.{index.Name}"));
        }

        if (diagnostics.Count != 0)
            throw new DocumentDeclarationException(diagnostics);

        try
        {
            var declaration = Groundwork.Kernel.StorageUnit.Declare(name, name);
            var idType = ToPortableType(idMember!.GetMemberType());
            declaration.Column(idColumn!, idType, ConfigureColumn(idType, ConfigureRequired(idConfiguration)));
            declaration.Json("document", column => column.Required());
            declaration.String("schemaVersion", column => column.Required());
            if (sharedKind)
                declaration.String("kind", column => column.Required());

            foreach (var projection in projections)
            {
                declaration.Column(projection.Column, projection.Type, ConfigureColumn(projection.Type, projection.Configure));
            }

            foreach (var index in indexes)
            {
                var projection = projections.Single(projection => projection.Path == index.Path);
                if (projection.Type == PortableType.Json)
                    continue;
            declaration.Index(index.Name, builder =>
            {
                if (index.Direction == SortDirection.Descending)
                    builder.Descending(projection.Column);
                else
                    builder.Ascending(projection.Column);
            });
            }

            if (optimistic)
                declaration.OptimisticConcurrency(tokenColumn);
            if (scoped)
                declaration.Scoped();

            declaration.Key(idColumn!);

            var storageUnit = declaration.Build();
            var policy = new DocumentSchemaVersionPolicy(documentKind, minimumReadableVersion, currentVersion);
            var codec = DocumentCodecFactory.Create(policy, jsonOptions);
            return new DocumentUnit<T>(
                documentKind,
                storageUnit,
                bindings,
                idColumn!,
                idSelector!,
                sharedKind,
                codec,
                jsonOptions);
        }
        catch (DeclarationBuildException exception)
        {
            throw new DocumentDeclarationException(exception.Findings.Select(finding =>
                new DocumentDiagnostic(finding.Code, finding.Message, finding.Path)));
        }
    }

    private static Action<ColumnBuilder> ConfigureRequired(Action<ColumnBuilder>? configure) => builder =>
    {
        builder.Required();
        configure?.Invoke(builder);
    };

    private static Action<ColumnBuilder>? ConfigureColumn(PortableType type, Action<ColumnBuilder>? configure) =>
        type is PortableType.Decimal or PortableType.String or PortableType.Binary
            ? builder =>
            {
                if (type == PortableType.Decimal)
                    builder.Precision(38, 18);
                else if (type == PortableType.String)
                    builder.MaxLength(256);
                else
                    builder.MaxLength(4096);
                configure?.Invoke(builder);
            }
            : configure;

    private static Expression<Func<T, object?>> ConvertSelector<TKey>(Expression<Func<T, TKey>> selector)
    {
        var parameter = selector.Parameters[0];
        return Expression.Lambda<Func<T, object?>>(Expression.Convert(selector.Body, typeof(object)), parameter);
    }

    private static MemberInfo SingleMember<TKey>(Expression<Func<T, TKey>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var body = Unwrap(selector.Body);
        if (body is MemberExpression member && member.Expression is ParameterExpression)
            return member.Member;
        throw new ArgumentException("The Id selector must directly select a public property or field.", nameof(selector));
    }

    private static MemberPath MemberPath<TKey>(Expression<Func<T, TKey>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var members = new List<MemberInfo>();
        Expression? current = Unwrap(selector.Body);
        while (current is MemberExpression member)
        {
            members.Add(member.Member);
            current = Unwrap(member.Expression!);
        }
        if (current != selector.Parameters[0] || members.Count == 0)
            throw new ArgumentException("A document projection must select a property or field path rooted at its document parameter.", nameof(selector));

        members.Reverse();
        return new MemberPath(string.Empty, LowerFirst(members[^1].Name), members);
    }

    private static Expression Unwrap(Expression expression)
    {
        while (expression is UnaryExpression unary &&
            unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
            expression = unary.Operand;
        return expression;
    }

    private PortableType ToPortableType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return PortableType.String;
        if (type == typeof(int)) return PortableType.Int32;
        if (type == typeof(long)) return PortableType.Int64;
        if (type == typeof(decimal)) return PortableType.Decimal;
        if (type == typeof(bool)) return PortableType.Boolean;
        if (type == typeof(DateTimeOffset)) return PortableType.DateTimeOffset;
        if (type == typeof(Guid)) return PortableType.Guid;
        if (type == typeof(byte[])) return PortableType.Binary;
        if (type == typeof(JsonElement) || type == typeof(JsonDocument) || type == typeof(object)) return PortableType.Json;
        if (type.IsEnum)
            return jsonOptions?.Converters.Any(converter => converter is JsonStringEnumConverter) == true
                ? PortableType.String
                : Enum.GetUnderlyingType(type) == typeof(long) ? PortableType.Int64 : PortableType.Int32;
        if (type.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            return PortableType.Json;
        return PortableType.Json;
    }

    private static string LowerFirst(string value) => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private string JsonName(MemberInfo member) =>
        member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ??
        jsonOptions?.PropertyNamingPolicy?.ConvertName(member.Name) ??
        LowerFirst(member.Name);

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value;

    private static DocumentDeclarationException Invalid(string code, string message, string path) =>
        new([new DocumentDiagnostic(code, message, path)]);

    private sealed record ProjectedMember(
        string Path,
        string Column,
        Delegate Getter,
        PortableType Type,
        Action<ColumnBuilder>? Configure);

    private sealed record IndexMember(string Name, string Path, SortDirection Direction);
}

/// <summary>Built document contract whose storage declaration is an ordinary kernel unit.</summary>
public sealed class DocumentUnit<T>
{
    private readonly Func<T, object?> idGetter;
    private readonly IReadOnlyDictionary<string, ColumnBinding> bindingsByColumn;
    private readonly bool sharedKind;
    private readonly VersionedJsonDocumentCodec codec;
    private readonly JsonSerializerOptions? jsonOptions;

    internal DocumentUnit(
        string documentKind,
        KernelStorageUnit storageUnit,
        IReadOnlyList<ColumnBinding> bindings,
        string idColumn,
        Func<T, object?> idGetter,
        bool sharedKind,
        VersionedJsonDocumentCodec codec,
        JsonSerializerOptions? jsonOptions)
    {
        DocumentKind = documentKind;
        StorageUnit = storageUnit;
        Bindings = Array.AsReadOnly(bindings.ToArray());
        IdColumn = idColumn;
        this.idGetter = idGetter;
        this.sharedKind = sharedKind;
        this.codec = codec;
        this.jsonOptions = jsonOptions;
        bindingsByColumn = Bindings.ToDictionary(binding => binding.Column, StringComparer.Ordinal);
    }

    public string DocumentKind { get; }
    public string IdColumn { get; }
    public KernelStorageUnit StorageUnit { get; }
    public KernelStorageUnit Definition => StorageUnit;
    public IReadOnlyList<ColumnBinding> Bindings { get; }
    public VersionedJsonDocumentCodec Codec => codec;

    public VersionedJsonContent Serialize(T value) => codec.Serialize(DocumentKind, value);

    public RowValues ToRowValues(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var serialized = Serialize(value);
        using var json = JsonDocument.Parse(serialized.ContentJson);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [IdColumn] = idGetter(value),
            ["document"] = serialized.ContentJson,
            ["schemaVersion"] = serialized.SchemaVersion
        };
        if (sharedKind)
            values["kind"] = DocumentKind;

        foreach (var binding in Bindings)
            values[binding.Column] = Extract(json.RootElement, binding.Path, binding.Type);

        return new RowValues(values);
    }

    public RowValues Map(T value) => ToRowValues(value);

    public T Materialize(RowValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!values.TryGetValue("document", out var content) || content is null)
            throw new KeyNotFoundException("The document row did not contain the required 'document' JSON column.");
        var contentJson = content switch
        {
            string text => text,
            JsonDocument document => document.RootElement.GetRawText(),
            JsonElement element => element.GetRawText(),
            _ => JsonSerializer.Serialize(content, jsonOptions)
        };
        var schemaVersion = values.TryGetValue("schemaVersion", out var stamp) && stamp is not null
            ? Convert.ToString(stamp, System.Globalization.CultureInfo.InvariantCulture)!
            : "v1";
        return codec.Deserialize<T>(new VersionedJsonPayload(DocumentKind, schemaVersion, contentJson));
    }

    public T Read(RowValues values) => Materialize(values);

    public DocumentReadResult<T> Read(RowValues values, long? version)
    {
        var materialized = Materialize(values);
        return new DocumentReadResult<T>(materialized, version);
    }

    public DocumentUnitSession<T> Open(IRecordStore store) =>
        new(this, store ?? throw new ArgumentNullException(nameof(store)));

    public DocumentUnitSession<T> Use(IRecordStore store) => Open(store);

    internal RowValues KeyValues(T value)
    {
        var row = ToRowValues(value);
        return new RowValues(StorageUnit.Key.Columns.ToDictionary(column => column, column => row[column], StringComparer.Ordinal));
    }

    private static object? Extract(JsonElement root, string path, PortableType type)
    {
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }
        if (current.ValueKind == JsonValueKind.Null)
            return null;
        return type switch
        {
            PortableType.String => current.GetString(),
            PortableType.Int32 => current.GetInt32(),
            PortableType.Int64 => current.GetInt64(),
            PortableType.Decimal => current.GetDecimal(),
            PortableType.Boolean => current.GetBoolean(),
            PortableType.DateTimeOffset => current.GetDateTimeOffset(),
            PortableType.Guid => current.GetGuid(),
            PortableType.Binary => current.GetBytesFromBase64(),
            PortableType.Json => current.Clone(),
            _ => current.Clone()
        };
    }
}

/// <summary>Typed result used when a provider returns a materialized document and its version.</summary>
public sealed record DocumentReadResult<T>(T Value, long? Version);

/// <summary>Provider-neutral typed mutations over a document unit.</summary>
public sealed class DocumentUnitSession<T>
{
    private readonly DocumentUnit<T> unit;
    private readonly IRecordStore store;

    internal DocumentUnitSession(DocumentUnit<T> unit, IRecordStore store)
    {
        this.unit = unit;
        this.store = store;
    }

    public RecordWriteResult Insert(T value, RecordWriteOptions? options = null) =>
        store.Insert(unit.StorageUnit, unit.ToRowValues(value), options);

    public RecordWriteResult Update(T value, RecordWriteOptions? options = null) =>
        store.Update(unit.StorageUnit, unit.ToRowValues(value), options);

    public RecordWriteResult Upsert(T value, RecordWriteOptions? options = null) =>
        store.Upsert(unit.StorageUnit, unit.ToRowValues(value), options);

    public RecordWriteResult Delete(T value, RecordWriteOptions? options = null) =>
        store.Delete(unit.StorageUnit, unit.KeyValues(value), options);
}

internal static class DocumentCodecFactory
{
    internal static VersionedJsonDocumentCodec Create(
        DocumentSchemaVersionPolicy policy,
        JsonSerializerOptions? options) =>
        new(
            [policy],
            [],
            new DocumentSchemaVersionFormat(
                (_, stamp) => stamp.StartsWith('v') && int.TryParse(stamp.AsSpan(1), out var version) ? version : null,
                (_, version) => "v" + version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

internal sealed record MemberPath(string Path, string Column, IReadOnlyList<MemberInfo> Members);

internal static class MemberInfoExtensions
{
    internal static Type GetMemberType(this MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => throw new ArgumentException("Only properties and fields are supported.", nameof(member))
    };
}
