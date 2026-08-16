using System.Reflection;
using Groundwork.Kernel;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class KernelDeclarationTests
{
    [Fact]
    public void Customer_is_a_direct_public_declaration()
    {
        var customer = new StorageUnit
        {
            Id = new StorageUnitId("customer"),
            Name = "Customer",
            Columns =
            [
                new() { Name = "id", Type = PortableType.Guid, IsNullable = false },
                new() { Name = "name", Type = PortableType.String, MaxLength = 200 },
                new() { Name = "email", Type = PortableType.String, MaxLength = 320 },
                new() { Name = "createdAt", Type = PortableType.DateTimeOffset, IsNullable = false },
                new() { Name = "isActive", Type = PortableType.Boolean, IsNullable = false },
                new() { Name = "balance", Type = PortableType.Decimal, Precision = 18, Scale = 2 }
            ],
            Key = new() { Columns = ["id"] }
        };

        Assert.Equal("customer", customer.Id.Value);
        Assert.Equal("Customer", customer.Name);
        Assert.Equal(["id"], customer.Key.Columns);
        Assert.Equal(6, customer.Columns.Count);
        Assert.Equal(ConcurrencyDeclaration.None, customer.Concurrency);
        Assert.Equal(ScopePolicy.Global, customer.Scope);
        Assert.Equal(TimestampDeclaration.None, customer.Timestamps);
        Assert.Equal(1, customer.SchemaVersion);
    }

    [Fact]
    public void Every_portable_type_is_reachable_from_a_column_declaration()
    {
        var columns = Enum.GetValues<PortableType>()
            .Select((type, index) => new ColumnDefinition { Name = $"column{index}", Type = type })
            .ToArray();

        Assert.Equal(9, columns.Length);
        Assert.Equal(Enum.GetValues<PortableType>(), columns.Select(column => column.Type));
    }

    [Fact]
    public void Optional_declaration_members_have_contract_defaults()
    {
        var column = new ColumnDefinition { Name = "value", Type = PortableType.String };
        var index = new IndexDefinition { Name = "by-value", Columns = [new("value")] };
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("values"),
            Name = "Values",
            Columns = [column],
            Key = new KeyDefinition { Columns = ["value"] }
        };

        Assert.True(column.IsNullable);
        Assert.Equal(ColumnGeneration.Supplied, column.Generation);
        Assert.Equal(SortDirection.Ascending, index.Columns.Single().Direction);
        Assert.False(index.IsUnique);
        Assert.Equal(MissingValueBehavior.Included, index.MissingValues);
        Assert.Equal(1, index.SchemaVersion);
        Assert.Empty(unit.DerivedColumns);
        Assert.Empty(unit.Indexes);
    }

    [Fact]
    public void Shared_concurrency_validation_rejects_an_index_over_the_system_owned_token()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("versioned"),
            Name = "versioned",
            Columns =
            [
                new() { Name = "id", Type = PortableType.Guid, IsNullable = false },
                new() { Name = "version", Type = PortableType.Int64, IsNullable = false, Default = new PortableDefault(0L) }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes = [new IndexDefinition { Name = "by-version", Columns = [new IndexColumn("version")] }],
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };

        var error = Assert.Throws<ArgumentException>(() =>
            ConcurrencyDeclaration.ValidateDeclaration(unit));

        Assert.Contains("index 'by-version'", error.Message, StringComparison.Ordinal);
        Assert.Contains("system-owned", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Supporting_projection_and_value_contracts_are_provider_neutral()
    {
        var definition = new DerivedColumnDefinition
        {
            Name = "name-fold",
            SourceColumn = "name",
            Projection = PortableProjection.UnicodeFold
        };
        var column = new ColumnDefinition
        {
            Name = "status",
            Type = PortableType.String,
            Collation = PortableCollation.OrdinalIgnoreCase,
            Default = new PortableDefault("active")
        };

        Assert.Equal("name", definition.SourceColumn);
        Assert.Equal(PortableProjection.UnicodeFold, definition.Projection);
        Assert.Equal("active", column.Default!.Value);
    }

    [Fact]
    public void Public_kernel_names_do_not_contain_contract_family_tokens()
    {
        var forbidden = new[] { "Document", "Envelope", "Record", "Stream", "Diagnostic" };
        var assembly = typeof(StorageUnit).Assembly;
        var publicNames = assembly.GetExportedTypes()
            .SelectMany(type => new[] { type.Name }.Concat(type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(member => member.Name)))
            .ToArray();

        Assert.All(publicNames, name => Assert.DoesNotContain(forbidden, token => name.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Kernel_assembly_references_only_the_BCL_and_query_model()
    {
        var externalReferences = typeof(StorageUnit).Assembly.GetReferencedAssemblies()
            .Where(reference => !reference.Name!.StartsWith("System", StringComparison.Ordinal) &&
                !reference.Name.Equals("netstandard", StringComparison.Ordinal) &&
                !reference.Name.Equals("Microsoft.Win32.Registry", StringComparison.Ordinal) &&
                !reference.Name.Equals("Groundwork.Query.Model", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(externalReferences);
    }
}
