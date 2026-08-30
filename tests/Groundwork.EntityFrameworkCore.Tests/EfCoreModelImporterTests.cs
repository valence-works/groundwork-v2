using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Groundwork.EntityFrameworkCore.Tests;

public sealed class EfCoreModelImporterTests
{
    [Fact]
    public void Basic_relational_shape_becomes_a_valid_kernel_declaration()
    {
        var model = Model(builder => builder.Entity<Customer>(entity =>
        {
            entity.ToTable("customers");
            entity.HasKey(customer => customer.Id);
            entity.Property(customer => customer.Id).HasColumnName("customer_id");
            entity.Property(customer => customer.Name).HasColumnName("display_name").HasMaxLength(80).IsRequired();
            entity.HasIndex(customer => customer.Name).HasDatabaseName("ix_customers_name").IsUnique();
        }));

        var result = EfCoreModelImporter.Import(model);

        Assert.True(result.IsComplete);
        var unit = Assert.Single(result.Declarations);
        Assert.Equal("customers", unit.Name);
        Assert.Equal(["customer_id"], unit.Key.Columns);
        var name = Assert.Single(unit.Columns, column => column.Name == "display_name");
        Assert.Equal(PortableType.String, name.Type);
        Assert.False(name.IsNullable);
        Assert.Equal(80, name.MaxLength);
        Assert.True(Assert.Single(unit.Indexes).IsUnique);
        SchemaSubject.ValidateManifest(result.Declarations);
    }

    [Fact]
    public void Foreign_key_becomes_a_logical_reference_with_a_covering_index()
    {
        var model = Model(builder =>
        {
            builder.Entity<Customer>(entity =>
            {
                entity.ToTable("customers");
                entity.HasKey(customer => customer.Id);
            });
            builder.Entity<Order>(entity =>
            {
                entity.ToTable("orders");
                entity.HasKey(order => order.Id);
                entity.HasOne<Customer>().WithMany().HasForeignKey(order => order.CustomerId);
            });
        });

        var result = EfCoreModelImporter.Import(model);

        Assert.True(result.IsComplete);
        var order = Assert.Single(result.Declarations, declaration => declaration.Name == "orders");
        var reference = Assert.Single(order.References);
        Assert.Equal(new StorageUnitId("customers"), reference.TargetUnitId);
        Assert.Equal(["CustomerId"], reference.Columns);
        Assert.Contains(order.Indexes, index => index.Columns[0].Column == "CustomerId");
        SchemaSubject.ValidateManifest(result.Declarations);
    }

    [Fact]
    public void Floating_point_is_storage_only_and_names_the_queryable_alternative()
    {
        var model = Model(builder => builder.Entity<Measurement>(entity =>
        {
            entity.ToTable("measurements");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Value);
        }));

        var result = EfCoreModelImporter.Import(model);

        Assert.True(result.IsComplete);
        Assert.Equal(PortableType.Double, Assert.Single(result.Declarations).Columns.Single(
            column => column.Name == nameof(Measurement.Value)).Type);
        var finding = Assert.Single(result.Findings, finding => finding.Code == "GW-EF-003");
        Assert.Equal(EfCoreImportSeverity.Warning, finding.Severity);
        Assert.Contains("Decimal", finding.Alternative, StringComparison.Ordinal);
        Assert.Contains("Int64", finding.Alternative, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_collation_requires_an_explicit_locale_sort_key_mapping()
    {
        var model = Model(builder => builder.Entity<Customer>(entity =>
        {
            entity.ToTable("customers");
            entity.HasKey(customer => customer.Id);
            entity.Property(customer => customer.Name).HasMaxLength(80).UseCollation("sv_SE_provider");
        }));

        var unresolved = EfCoreModelImporter.Import(model);
        Assert.False(unresolved.IsComplete);
        Assert.Contains(unresolved.Findings, finding => finding.Code == "GW-EF-005" &&
            finding.Alternative.Contains("LocaleOrderings", StringComparison.Ordinal));

        var resolved = EfCoreModelImporter.Import(model, new EfCoreImportOptions
        {
            LocaleOrderings = new Dictionary<string, EfCoreLocaleOrdering>
            {
                ["sv_SE_provider"] = new("sv-SE", 12)
            }
        });

        Assert.True(resolved.IsComplete);
        var locale = resolved.Declarations.Single().Columns.Single(column => column.Name == nameof(Customer.Name)).LocaleSortKey;
        Assert.NotNull(locale);
        Assert.Equal("sv-SE", locale.CultureName);
        Assert.Equal(12, locale.MaximumExpansionFactor);
    }

    [Fact]
    public void Global_query_filter_requires_an_explicit_scope_decision()
    {
        var model = Model(builder => builder.Entity<TenantRow>(entity =>
        {
            entity.ToTable("tenant_rows");
            entity.HasKey(row => row.Id);
            entity.HasQueryFilter(row => row.TenantId != Guid.Empty);
        }));
        var entityName = model.FindEntityType(typeof(TenantRow))!.Name;

        var unresolved = EfCoreModelImporter.Import(model);
        Assert.False(unresolved.IsComplete);
        Assert.Contains(unresolved.Findings, finding => finding.Code == "GW-EF-006");

        var resolved = EfCoreModelImporter.Import(model, new EfCoreImportOptions
        {
            ScopePolicies = new Dictionary<string, ScopePolicy> { [entityName] = ScopePolicy.Scoped }
        });

        Assert.True(resolved.IsComplete);
        Assert.Equal(ScopePolicy.Scoped, Assert.Single(resolved.Declarations).Scope);
    }

    [Fact]
    public void Unsupported_datetime_is_refused_with_the_datetimeoffset_alternative()
    {
        var model = Model(builder => builder.Entity<LegacyTime>(entity =>
        {
            entity.ToTable("legacy_times");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.OccurredAt);
        }));

        var result = EfCoreModelImporter.Import(model);

        Assert.False(result.IsComplete);
        var finding = Assert.Single(result.Findings, finding => finding.Code == "GW-EF-002");
        Assert.Contains("DateTimeOffset", finding.Alternative, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_qualified_tables_are_refused_instead_of_losing_the_schema()
    {
        var model = Model(builder => builder.Entity<Customer>(entity =>
        {
            entity.ToTable("customers", "sales");
            entity.HasKey(customer => customer.Id);
        }));

        var result = EfCoreModelImporter.Import(model);

        Assert.False(result.IsComplete);
        var finding = Assert.Single(result.Findings, finding => finding.Code == "GW-EF-001");
        Assert.Contains("sales.customers", finding.Message, StringComparison.Ordinal);
        Assert.Contains("schema-independent", finding.Alternative, StringComparison.Ordinal);
    }

    [Fact]
    public void Inheritance_is_refused_instead_of_emitting_independent_tables()
    {
        var model = Model(builder =>
        {
            builder.Entity<Animal>(entity =>
            {
                entity.UseTptMappingStrategy();
                entity.ToTable("animals");
                entity.HasKey(animal => animal.Id);
            });
            builder.Entity<Dog>().HasBaseType<Animal>();
            builder.Entity<Dog>().ToTable("dogs");
        });

        var result = EfCoreModelImporter.Import(model);

        Assert.False(result.IsComplete);
        Assert.NotEmpty(result.Findings.Where(finding => finding.Code == "GW-EF-001" &&
            finding.Message.Contains("inheritance hierarchy", StringComparison.Ordinal)));
        Assert.Empty(result.Declarations);
    }

    [Fact]
    public void Value_converters_are_refused_instead_of_guessing_the_persisted_type()
    {
        var model = Model(builder => builder.Entity<ConvertedValue>(entity =>
        {
            entity.ToTable("converted_values");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Score).HasConversion<string>();
        }));

        var result = EfCoreModelImporter.Import(model);

        Assert.False(result.IsComplete);
        var finding = Assert.Single(result.Findings, finding => finding.Code == "GW-EF-002");
        Assert.Contains("value converter", finding.Message, StringComparison.Ordinal);
        Assert.Contains("application layer", finding.Alternative, StringComparison.Ordinal);
    }

    [Fact]
    public void Portable_constant_defaults_are_preserved_without_inventing_generation()
    {
        var model = Model(builder => builder.Entity<DefaultedValue>(entity =>
        {
            entity.ToTable("defaulted_values");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.RetryCount).HasDefaultValue(3);
        }));

        var result = EfCoreModelImporter.Import(model);

        Assert.True(result.IsComplete);
        var column = Assert.Single(result.Declarations).Columns.Single(
            column => column.Name == nameof(DefaultedValue.RetryCount));
        Assert.Equal(3, column.Default?.Value);
        Assert.Equal(ColumnGeneration.Supplied, column.Generation);
    }

    [Fact]
    public void Int32_identity_is_refused_instead_of_becoming_application_supplied()
    {
        var model = Model(builder => builder.Entity<IntIdentity>(entity =>
        {
            entity.ToTable("int_identities");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).ValueGeneratedOnAdd();
        }));

        var result = EfCoreModelImporter.Import(model);

        Assert.False(result.IsComplete);
        var finding = Assert.Single(result.Findings, finding => finding.Code == "GW-EF-002");
        Assert.Contains("OnAdd generation", finding.Message, StringComparison.Ordinal);
        Assert.Contains("Int64 ProviderSequence", finding.Alternative, StringComparison.Ordinal);
    }

    [Fact]
    public void Ef_concurrency_tokens_are_refused_instead_of_becoming_ordinary_columns()
    {
        var model = Model(builder => builder.Entity<VersionedValue>(entity =>
        {
            entity.ToTable("versioned_values");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Revision).IsRowVersion();
        }));

        var result = EfCoreModelImporter.Import(model);

        Assert.False(result.IsComplete);
        var finding = Assert.Single(result.Findings, finding => finding.Code == "GW-EF-002");
        Assert.Contains("concurrency token", finding.Message, StringComparison.Ordinal);
        Assert.Contains("optimistic concurrency", finding.Alternative, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_default_collation_requires_the_same_explicit_locale_mapping()
    {
        var model = Model(builder =>
        {
            builder.UseCollation("sv_SE_model_default");
            builder.Entity<Customer>(entity =>
            {
                entity.ToTable("customers");
                entity.HasKey(customer => customer.Id);
                entity.Property(customer => customer.Name).HasMaxLength(80);
            });
        });

        var unresolved = EfCoreModelImporter.Import(model);
        Assert.False(unresolved.IsComplete);
        Assert.Contains(unresolved.Findings, finding => finding.Code == "GW-EF-005");

        var resolved = EfCoreModelImporter.Import(model, new EfCoreImportOptions
        {
            LocaleOrderings = new Dictionary<string, EfCoreLocaleOrdering>
            {
                ["sv_SE_model_default"] = new("sv-SE", 12)
            }
        });
        Assert.True(resolved.IsComplete);
        Assert.NotNull(resolved.Declarations.Single().Columns.Single(
            column => column.Name == nameof(Customer.Name)).LocaleSortKey);
    }

    [Fact]
    public void View_only_entities_are_reported_instead_of_disappearing()
    {
        var model = Model(builder => builder.Entity<ReadOnlyReport>(entity =>
        {
            entity.HasNoKey();
            entity.ToView("report_view");
            entity.Property(report => report.Label);
        }));

        var result = EfCoreModelImporter.Import(model);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Declarations);
        var finding = Assert.Single(result.Findings, finding => finding.Code == "GW-EF-001");
        Assert.Contains("report_view", finding.Message, StringComparison.Ordinal);
        Assert.Contains("read-only views", finding.Alternative, StringComparison.Ordinal);
    }

    [Fact]
    public void Inferred_units_run_the_kernel_portability_gate()
    {
        var model = Model(builder =>
        {
            builder.Entity<DoubleKey>(entity =>
            {
                entity.ToTable("double_keys");
                entity.HasKey(value => value.Id);
            });
            builder.Entity<UnboundedDecimal>(entity =>
            {
                entity.ToTable("unbounded_decimals");
                entity.HasKey(value => value.Id);
                entity.Property(value => value.Amount);
            });
            builder.Entity<NullableUnique>(entity =>
            {
                entity.ToTable("nullable_uniques");
                entity.HasKey(value => value.Id);
                entity.Property(value => value.Code).HasMaxLength(32);
                entity.HasIndex(value => value.Code).IsUnique();
            });
        });

        var result = EfCoreModelImporter.Import(model);

        Assert.False(result.IsComplete);
        Assert.Contains(result.Findings, finding => finding.Message.Contains("GW-PORT-012", StringComparison.Ordinal));
        Assert.Contains(result.Findings, finding => finding.Message.Contains("GW-PORT-002", StringComparison.Ordinal));
        Assert.Contains(result.Findings, finding => finding.Message.Contains("GW-PORT-001", StringComparison.Ordinal));
    }

    private static Microsoft.EntityFrameworkCore.Metadata.IModel Model(Action<ModelBuilder> configure)
    {
        var builder = new ModelBuilder();
        configure(builder);
        return builder.FinalizeModel();
    }

    private sealed class Customer
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class Order
    {
        public Guid Id { get; set; }
        public Guid CustomerId { get; set; }
    }

    private sealed class Measurement
    {
        public Guid Id { get; set; }
        public double Value { get; set; }
    }

    private sealed class TenantRow
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
    }

    private sealed class LegacyTime
    {
        public Guid Id { get; set; }
        public DateTime OccurredAt { get; set; }
    }

    private class Animal
    {
        public Guid Id { get; set; }
    }

    private sealed class Dog : Animal;

    private sealed class ConvertedValue
    {
        public Guid Id { get; set; }
        public int Score { get; set; }
    }

    private sealed class DefaultedValue
    {
        public Guid Id { get; set; }
        public int RetryCount { get; set; }
    }

    private sealed class IntIdentity
    {
        public int Id { get; set; }
    }

    private sealed class VersionedValue
    {
        public Guid Id { get; set; }
        public byte[] Revision { get; set; } = [];
    }

    private sealed class ReadOnlyReport
    {
        public string Label { get; set; } = string.Empty;
    }

    private sealed class DoubleKey
    {
        public double Id { get; set; }
    }

    private sealed class UnboundedDecimal
    {
        public Guid Id { get; set; }
        public decimal Amount { get; set; }
    }

    private sealed class NullableUnique
    {
        public Guid Id { get; set; }
        public string? Code { get; set; }
    }
}
