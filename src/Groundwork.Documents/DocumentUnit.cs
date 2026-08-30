using System.Linq.Expressions;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Groundwork.Kernel;
using Groundwork.Records;
using Groundwork.Documents.Serialization;
using Groundwork.Store;
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

/// <summary>Raised when a persisted document row violates its typed identity or discriminator.</summary>
public sealed class DocumentMaterializationException : InvalidOperationException
{
    public DocumentMaterializationException(string code, string message)
        : base(message) => Code = code ?? throw new ArgumentNullException(nameof(code));

    public string Code { get; }
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
    private readonly List<IDocumentJsonUpcaster> upcasters = [];
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
    [RequiresDynamicCode("Compiles the document key selector at runtime.")]
    [RequiresUnreferencedCode("Inspects the selected document key member, which may be trimmed.")]
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
        projections.Add(new ProjectedMember(
            memberPath.Members,
            memberPath.Column,
            typeof(TValue),
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
        if (indexes.Any(existing => string.Equals(existing.Name, indexName, StringComparison.Ordinal)))
            throw Invalid("GW-DOC-DECL-004", $"Index '{indexName}' is declared more than once.", $"indexes.{indexName}");
        indexes.Add(new IndexMember(indexName, memberPath.Members, direction));
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

    /// <summary>Adds one contiguous historical JSON migration step for this document kind.</summary>
    public DocumentUnitBuilder<T> Upcaster(IDocumentJsonUpcaster upcaster)
    {
        ArgumentNullException.ThrowIfNull(upcaster);
        upcasters.Add(upcaster);
        return this;
    }

    /// <summary>Uses the supplied options for canonical JSON serialization and materialization.</summary>
    public DocumentUnitBuilder<T> JsonOptions(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        jsonOptions = new JsonSerializerOptions(options);
        return this;
    }

    [RequiresDynamicCode("Inspects configured JSON converters and freezes a reflection-based document contract.")]
    [RequiresUnreferencedCode("Resolves serialized CLR members and converters that may be trimmed.")]
    public DocumentUnit<T> Build()
    {
        var diagnostics = new List<DocumentDiagnostic>();
        if (idSelector is null || idMember is null)
            diagnostics.Add(new("GW-DOC-DECL-001", "A document declaration requires one native Id selector before Build().", "id"));

        var effectiveJsonOptions = jsonOptions is null
            ? new JsonSerializerOptions(JsonSerializerDefaults.Web)
            : new JsonSerializerOptions(jsonOptions);
        effectiveJsonOptions.MakeReadOnly(populateMissingResolver: true);
        var serializedNames = new Dictionary<MemberInfo, string>();
        if (idMember is not null)
            ResolveSerializableMember(idMember, "id", effectiveJsonOptions, serializedNames, diagnostics);
        foreach (var projection in projections)
            foreach (var member in projection.Members)
                ResolveSerializableMember(member, $"projections.{projection.Column}", effectiveJsonOptions, serializedNames, diagnostics);
        foreach (var index in indexes)
            foreach (var member in index.Members)
                ResolveSerializableMember(member, $"indexes.{index.Name}", effectiveJsonOptions, serializedNames, diagnostics);

        var idColumn = idMember is null ? null : LowerFirst(idMember.Name);
        var bindings = new List<ColumnBinding>();
        var resolvedProjections = projections.Select(projection => new ResolvedProjection(
            SerializedPath(projection.Members, serializedNames),
            projection.Column,
            ToPortableType(projection.ValueType, projection.Members),
            projection.Configure)).ToArray();
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

        foreach (var projection in resolvedProjections)
        {
            if (bindings.Any(binding => string.Equals(binding.Path, projection.Path, StringComparison.Ordinal)))
            {
                diagnostics.Add(new("GW-DOC-DECL-002", $"JSON path '{projection.Path}' is projected more than once.", "projections"));
                continue;
            }
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
            var indexPath = SerializedPath(index.Members, serializedNames);
            var projection = resolvedProjections.FirstOrDefault(candidate => string.Equals(candidate.Path, indexPath, StringComparison.Ordinal));
            if (projection is null)
                diagnostics.Add(new(
                    "GW-DOC-DECL-005",
                    $"Index '{index.Name}' targets path '{indexPath}', which has no projected column. Declare Project() first.",
                    $"indexes.{index.Name}"));
            else if (projection.Type is PortableType.Json or PortableType.Double)
            {
                var typeName = projection.Type == PortableType.Json ? "JSON" : "Double";
                diagnostics.Add(new(
                    "GW-DOC-DECL-006",
                    $"Index '{index.Name}' targets {typeName} path '{indexPath}', but {typeName} projections are not portable index keys. Project a portable comparable value instead.",
                    $"indexes.{index.Name}"));
            }
        }

        if (diagnostics.Count != 0)
            throw new DocumentDeclarationException(diagnostics);

        try
        {
            var declaration = Groundwork.Kernel.StorageUnit.Declare(name, name);
            var idType = ToPortableType(idMember!.GetMemberType(), new[] { idMember! });
            declaration.Column(idColumn!, idType, ConfigureColumn(idType, ConfigureRequired(idConfiguration)));
            declaration.Json("document", column => column.Required());
            declaration.String("schemaVersion", column => column.Required());
            if (sharedKind)
                declaration.String("kind", column => column.Required());

            foreach (var projection in resolvedProjections)
            {
                declaration.Column(projection.Column, projection.Type, ConfigureColumn(projection.Type, projection.Configure));
            }

            foreach (var index in indexes)
            {
                var indexPath = SerializedPath(index.Members, serializedNames);
                var projection = resolvedProjections.Single(projection => projection.Path == indexPath);
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

            declaration.Key(sharedKind ? ["kind", idColumn!] : [idColumn!]);

            var storageUnit = declaration.Build();
            var policy = new DocumentSchemaVersionPolicy(documentKind, minimumReadableVersion, currentVersion);
            var codec = DocumentCodecFactory.Create(policy, upcasters, effectiveJsonOptions);
            return new DocumentUnit<T>(
                documentKind,
                storageUnit,
                bindings,
                idColumn!,
                idSelector!,
                sharedKind,
                codec,
                effectiveJsonOptions);
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
        return new MemberPath(LowerFirst(members[^1].Name), members);
    }

    private static Expression Unwrap(Expression expression)
    {
        while (expression is UnaryExpression unary &&
            unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
            expression = unary.Operand;
        return expression;
    }

    [RequiresDynamicCode("Inspects configured JSON converters for enum projections.")]
    [RequiresUnreferencedCode("Inspects configured JSON converters and CLR members that may be trimmed.")]
    private PortableType ToPortableType(Type type, IReadOnlyList<MemberInfo>? members = null)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return PortableType.String;
        if (type == typeof(int)) return PortableType.Int32;
        if (type == typeof(long)) return PortableType.Int64;
        if (type == typeof(decimal)) return PortableType.Decimal;
        if (type == typeof(double)) return PortableType.Double;
        if (type == typeof(bool)) return PortableType.Boolean;
        if (type == typeof(DateTimeOffset)) return PortableType.DateTimeOffset;
        if (type == typeof(Guid)) return PortableType.Guid;
        if (type == typeof(byte[])) return PortableType.Binary;
        if (type == typeof(JsonElement) || type == typeof(JsonDocument) || type == typeof(object)) return PortableType.Json;
        if (type.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(type);
            if (underlying == typeof(uint) || underlying == typeof(ulong))
                throw Invalid("GW-DOC-DECL-007", $"Enum '{type.FullName}' uses unsupported unsigned underlying type '{underlying.Name}'. Use a signed enum or project it as JSON.", "projections");
            return EnumPortableType(type, members);
        }
        if (type.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            return PortableType.Json;
        return PortableType.Json;
    }

    [RequiresDynamicCode("Creates and inspects a configured JSON converter for an enum projection.")]
    [RequiresUnreferencedCode("Creates and inspects a configured JSON converter whose members may be trimmed.")]
    private PortableType EnumPortableType(Type enumType, IReadOnlyList<MemberInfo>? members)
    {
        JsonConverter? propertyConverter = null;
        var propertyConverterAttribute = members?.LastOrDefault()?.GetCustomAttribute<JsonConverterAttribute>();
        if (propertyConverterAttribute is not null)
        {
            try
            {
                propertyConverter = propertyConverterAttribute.CreateConverter(enumType) ??
                    (propertyConverterAttribute.ConverterType is { } converterType
                        ? Activator.CreateInstance(converterType) as JsonConverter
                        : null);
                if (propertyConverter is null)
                    throw new InvalidOperationException("The converter attribute did not create a JsonConverter instance.");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
            {
                throw Invalid("GW-DOC-DECL-008", $"The JSON converter on enum '{enumType.FullName}' could not be created: {exception.Message}", "projections");
            }
        }

        var options = jsonOptions is null
            ? new JsonSerializerOptions(JsonSerializerDefaults.Web)
            : new JsonSerializerOptions(jsonOptions);
        if (propertyConverter is not null)
            options.Converters.Insert(0, propertyConverter);

        JsonValueKind kind;
        try
        {
            var json = JsonSerializer.Serialize(Enum.ToObject(enumType, 0), enumType, options);
            using var document = JsonDocument.Parse(json);
            kind = document.RootElement.ValueKind;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw Invalid("GW-DOC-DECL-008", $"The JSON converter for enum '{enumType.FullName}' could not be inspected: {exception.Message}", "projections");
        }

        return kind switch
        {
            JsonValueKind.String => PortableType.String,
            JsonValueKind.Number => Enum.GetUnderlyingType(enumType) == typeof(long) ? PortableType.Int64 : PortableType.Int32,
            _ => throw Invalid("GW-DOC-DECL-008", $"The JSON converter for enum '{enumType.FullName}' emits {kind}, but document projections support only string or integral JSON values.", "projections")
        };
    }

    private static string LowerFirst(string value) => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static string SerializedPath(
        IReadOnlyList<MemberInfo> members,
        IReadOnlyDictionary<MemberInfo, string> serializedNames) =>
        string.Join('.', members.Select(member => serializedNames.GetValueOrDefault(member, member.Name)));

    private void ResolveSerializableMember(
        MemberInfo member,
        string path,
        JsonSerializerOptions options,
        IDictionary<MemberInfo, string> serializedNames,
        ICollection<DocumentDiagnostic> diagnostics)
    {
        if (serializedNames.ContainsKey(member))
            return;

        var ignore = member.GetCustomAttribute<JsonIgnoreAttribute>();
        var conditionallyIgnored = ignore?.Condition is JsonIgnoreCondition.WhenWritingDefault or JsonIgnoreCondition.WhenWritingNull ||
            ignore is null && options.DefaultIgnoreCondition != JsonIgnoreCondition.Never;
        var readOnlyIgnored = member switch
        {
            PropertyInfo property => options.IgnoreReadOnlyProperties && property.SetMethod is null,
            FieldInfo field => options.IgnoreReadOnlyFields && field.IsInitOnly,
            _ => false
        };

        JsonPropertyInfo? contractMember = null;
        string? contractFailure = null;
        try
        {
            var typeInfo = options.GetTypeInfo(member.DeclaringType!);
            contractMember = typeInfo.Properties.FirstOrDefault(property => Equals(property.AttributeProvider, member));
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
        {
            contractFailure = exception.Message;
        }

        var customConditional = contractMember?.ShouldSerialize is not null &&
            (ignore?.Condition != JsonIgnoreCondition.Never || jsonOptions?.TypeInfoResolver is not null);
        if (ignore?.Condition == JsonIgnoreCondition.Always || conditionallyIgnored || readOnlyIgnored ||
            contractMember is null || contractMember.Get is null || customConditional)
        {
            var correctiveAction = conditionallyIgnored || customConditional
                ? "Remove the conditional ignore/ShouldSerialize rule or mark the selected member with JsonIgnoreCondition.Never."
                : readOnlyIgnored
                    ? "Disable the matching IgnoreReadOnly option or make the selected member writable."
                    : contractFailure is not null
                        ? $"Fix the invalid JsonTypeInfo contract: {contractFailure}"
                        : "Include the member in the effective JsonTypeInfo contract (for fields, enable IncludeFields or add JsonInclude).";
            diagnostics.Add(new(
                "GW-DOC-DECL-009",
                $"Selected member '{member.Name}' can be omitted by the effective JSON contract. {correctiveAction}",
                path));
            return;
        }

        serializedNames[member] = contractMember.Name;
    }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value;

    private static DocumentDeclarationException Invalid(string code, string message, string path) =>
        new([new DocumentDiagnostic(code, message, path)]);

    private sealed record ProjectedMember(
        IReadOnlyList<MemberInfo> Members,
        string Column,
        Type ValueType,
        Action<ColumnBuilder>? Configure);

    private sealed record IndexMember(string Name, IReadOnlyList<MemberInfo> Members, SortDirection Direction);

    private sealed record ResolvedProjection(
        string Path,
        string Column,
        PortableType Type,
        Action<ColumnBuilder>? Configure);
}

/// <summary>Built document contract whose storage declaration is an ordinary kernel unit.</summary>
public sealed class DocumentUnit<T>
{
    private readonly Func<T, object?> idGetter;
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
    }

    public string DocumentKind { get; }
    public string IdColumn { get; }
    public KernelStorageUnit StorageUnit { get; }
    public KernelStorageUnit Definition => StorageUnit;
    public IReadOnlyList<ColumnBinding> Bindings { get; }
    public VersionedJsonDocumentCodec Codec => codec;

    [RequiresDynamicCode("Serializes the application document with its configured reflection-based JSON contract.")]
    [RequiresUnreferencedCode("Serializes an application document whose members may be trimmed.")]
    public VersionedJsonContent Serialize(T value) => codec.Serialize(DocumentKind, value);

    [RequiresDynamicCode("Serializes the application document with its configured reflection-based JSON contract.")]
    [RequiresUnreferencedCode("Serializes an application document whose members may be trimmed.")]
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

    [RequiresDynamicCode("Serializes the application document with its configured reflection-based JSON contract.")]
    [RequiresUnreferencedCode("Serializes an application document whose members may be trimmed.")]
    public RowValues Map(T value) => ToRowValues(value);

    [RequiresDynamicCode("Materializes the application document with its configured reflection-based JSON contract.")]
    [RequiresUnreferencedCode("Materializes an application document whose members may be trimmed.")]
    public T Materialize(RowValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!values.TryGetValue(IdColumn, out var persistedId))
            throw new DocumentMaterializationException(
                "GW-DOC-MAT-002",
                $"Document row for kind '{DocumentKind}' is missing required identity column '{IdColumn}'.");
        if (sharedKind && (!values.TryGetValue("kind", out var persistedKind) ||
            !string.Equals(Convert.ToString(persistedKind, System.Globalization.CultureInfo.InvariantCulture), DocumentKind, StringComparison.Ordinal)))
            throw new DocumentMaterializationException(
                "GW-DOC-MAT-004",
                $"Document row discriminator 'kind' does not match document kind '{DocumentKind}'.");
        if (!values.TryGetValue("document", out var content) || content is null)
            throw new DocumentMaterializationException(
                "GW-DOC-MAT-001",
                $"Document row for kind '{DocumentKind}' did not contain the required 'document' JSON column.");
        var contentJson = content switch
        {
            string text => text,
            JsonDocument document => document.RootElement.GetRawText(),
            JsonElement element => element.GetRawText(),
            _ => JsonSerializer.Serialize(content, jsonOptions)
        };
        if (!values.TryGetValue("schemaVersion", out var stamp) || stamp is null)
            throw new DocumentSchemaVersionException(
                DocumentSchemaVersionFailure.MalformedStamp,
                $"Document row for kind '{DocumentKind}' is missing required 'schemaVersion' metadata.",
                DocumentKind);
        var schemaVersion = Convert.ToString(stamp, System.Globalization.CultureInfo.InvariantCulture)!
            ?? throw new DocumentSchemaVersionException(
                DocumentSchemaVersionFailure.MalformedStamp,
                $"Document row for kind '{DocumentKind}' contains an invalid 'schemaVersion' metadata value.",
                DocumentKind);
        var materialized = codec.Deserialize<T>(new VersionedJsonPayload(DocumentKind, schemaVersion, contentJson));
        var materializedId = idGetter(materialized);
        if (!IdentityEquals(materializedId, persistedId))
            throw new DocumentMaterializationException(
                "GW-DOC-MAT-003",
                $"Document row for kind '{DocumentKind}' contains JSON identity '{materializedId}' that does not match its '{IdColumn}' column value '{persistedId}'.");
        return materialized;
    }

    [RequiresDynamicCode("Materializes the application document with its configured reflection-based JSON contract.")]
    [RequiresUnreferencedCode("Materializes an application document whose members may be trimmed.")]
    public T Read(RowValues values) => Materialize(values);

    [RequiresDynamicCode("Materializes the application document with its configured reflection-based JSON contract.")]
    [RequiresUnreferencedCode("Materializes an application document whose members may be trimmed.")]
    public DocumentReadResult<T> Read(RowValues values, long? version)
    {
        var materialized = Materialize(values);
        return new DocumentReadResult<T>(materialized, version);
    }

    /// <summary>Builds the ordinary Store row mutation for a typed document value.</summary>
    [RequiresDynamicCode("Serializes the application document with its configured reflection-based JSON contract.")]
    [RequiresUnreferencedCode("Serializes an application document whose members may be trimmed.")]
    public RowWrite ToRowWrite(T value, RowWriteMode mode, WriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var rowValues = mode == RowWriteMode.Delete
            ? null
            : new StorageValues(ToRowValues(value).Values);
        return mode switch
        {
            RowWriteMode.Insert => RowWrite.Insert(StorageUnit, rowValues!, options),
            RowWriteMode.Update => RowWrite.Update(StorageUnit, rowValues!, options),
            RowWriteMode.Upsert => RowWrite.Upsert(StorageUnit, rowValues!, options),
            RowWriteMode.ConditionalUpsert => RowWrite.ConditionalUpsert(StorageUnit, rowValues!, options),
            RowWriteMode.Delete => RowWrite.Delete(StorageUnit, KeyValues(value), options),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported document row-write mode.")
        };
    }

    [RequiresDynamicCode("Serializes the application document with its configured reflection-based JSON contract.")]
    [RequiresUnreferencedCode("Serializes an application document whose members may be trimmed.")]
    public RowWrite Insert(T value, WriteOptions? options = null) => ToRowWrite(value, RowWriteMode.Insert, options);

    [RequiresDynamicCode("Serializes the application document with its configured reflection-based JSON contract.")]
    [RequiresUnreferencedCode("Serializes an application document whose members may be trimmed.")]
    public RowWrite Update(T value, WriteOptions? options = null) => ToRowWrite(value, RowWriteMode.Update, options);

    [RequiresDynamicCode("Serializes the application document with its configured reflection-based JSON contract.")]
    [RequiresUnreferencedCode("Serializes an application document whose members may be trimmed.")]
    public RowWrite Upsert(T value, WriteOptions? options = null) => ToRowWrite(value, RowWriteMode.Upsert, options);

    [RequiresDynamicCode("Uses the document row-write compatibility path.")]
    [RequiresUnreferencedCode("Uses the document row-write compatibility path whose CLR members may be trimmed.")]
    public RowWrite Delete(T value, WriteOptions? options = null) => ToRowWrite(value, RowWriteMode.Delete, options);

    /// <summary>Executes a previously mapped row write through the provider-neutral Store seam.</summary>
    public WriteOutcome Execute(
        IStorageProviderConnection connection,
        RowWrite write,
        StorageAccess? access = null,
        IProviderCommandObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(write);
        if (!ReferenceEquals(write.Unit, StorageUnit))
            throw new ArgumentException("The row write belongs to a different storage unit.", nameof(write));

        var session = connection.OpenSession(StorageUnit, access ?? StorageAccess.Global, observer);
        return write.Mode switch
        {
            RowWriteMode.Insert => session.Insert(write.Values!, write.Options),
            RowWriteMode.Update => session.Update(write.Values!, write.Options),
            RowWriteMode.Upsert => session.Upsert(write.Values!, write.Options),
            RowWriteMode.ConditionalUpsert when session is IConcurrencyStorageSession conditional =>
                conditional.ConditionalUpsert(write.Values!, write.Options),
            RowWriteMode.ConditionalUpsert => throw new NotSupportedException(
                "The provider session does not support atomic conditional upsert."),
            RowWriteMode.Delete => session.Delete(write.Key!, write.Options),
            _ => throw new ArgumentOutOfRangeException(nameof(write.Mode), write.Mode, null)
        };
    }

    private StorageKey KeyValues(T value)
    {
        var key = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [IdColumn] = idGetter(value)
        };
        if (sharedKind)
            key["kind"] = DocumentKind;
        return new StorageKey(key);
    }

    private static bool IdentityEquals(object? materialized, object? persisted) =>
        materialized is byte[] materializedBytes && persisted is byte[] persistedBytes
            ? materializedBytes.AsSpan().SequenceEqual(persistedBytes)
            : Equals(materialized, persisted);

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
            PortableType.Double => current.GetDouble(),
            PortableType.Json => current.Clone(),
            _ => current.Clone()
        };
    }
}

/// <summary>Typed result used when a provider returns a materialized document and its version.</summary>
public sealed record DocumentReadResult<T>(T Value, long? Version);

internal static class DocumentCodecFactory
{
    internal static VersionedJsonDocumentCodec Create(
        DocumentSchemaVersionPolicy policy,
        IEnumerable<IDocumentJsonUpcaster> upcasters,
        JsonSerializerOptions? options) =>
        new(
            [policy],
            upcasters,
            new DocumentSchemaVersionFormat(
                (_, stamp) => stamp.StartsWith('v') && int.TryParse(stamp.AsSpan(1), out var version) ? version : null,
                (_, version) => "v" + version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

internal sealed record MemberPath(string Column, IReadOnlyList<MemberInfo> Members);

internal static class MemberInfoExtensions
{
    internal static Type GetMemberType(this MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => throw new ArgumentException("Only properties and fields are supported.", nameof(member))
    };
}
