using Groundwork.Kernel.Schema;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class RetentionDeclarationTests
{
    [Fact]
    public void Schema_subject_rejects_retention_idempotency_without_retention()
    {
        var unit = Unit() with
        {
            RetentionIdempotency = new RetentionIdempotencyDeclaration
            {
                Window = TimeSpan.FromMinutes(1)
            }
        };

        var refusal = Assert.Throws<ArgumentException>(() => new SchemaSubject(unit));

        Assert.StartsWith("GW-RETENTION-004", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Retention", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("RetentionIdempotency", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_manifest_rejects_retention_idempotency_without_retention()
    {
        var refusal = Assert.Throws<ArgumentException>(() => SchemaSubject.ValidateManifest([
            Unit() with
            {
                RetentionIdempotency = new RetentionIdempotencyDeclaration
                {
                    Window = TimeSpan.FromMinutes(1)
                }
            }
        ]));

        Assert.StartsWith("GW-RETENTION-004", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Kernel_builder_reports_retention_idempotency_without_retention_as_a_declaration_finding()
    {
        var refusal = Assert.Throws<DeclarationBuildException>(() =>
            Groundwork.Kernel.StorageUnit
                .Declare("retention-idempotency-without-retention", "retention_idempotency_without_retention")
                .String("id", column => column.Required())
                .Key("id")
                .RetentionIdempotency(TimeSpan.FromMinutes(1))
                .Build());

        var finding = Assert.Single(refusal.Findings, finding => finding.Code == "GW-RETENTION-004");
        Assert.Contains("Declare Retention", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Records_builder_reports_the_same_retention_declaration_diagnostic()
    {
        var refusal = Assert.Throws<Groundwork.Records.StorageDeclarationException>(() =>
            Groundwork.Records.StorageUnit
                .Declare("retention-idempotency-without-retention-records", "retention_idempotency_without_retention_records")
                .String("id", column => column.Required())
                .Key("id")
                .RetentionIdempotency(TimeSpan.FromMinutes(1))
                .Build());

        var finding = Assert.Single(refusal.Diagnostics, finding => finding.Code == "GW-RETENTION-004");
        Assert.Contains("Declare Retention", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Retention_without_retention_idempotency_remains_a_valid_status_only_declaration()
    {
        var unit = Unit() with
        {
            Retention = new RetentionDeclaration
            {
                KeepNewest = 1,
                OrderColumn = "id"
            }
        };

        var subject = new SchemaSubject(unit);

        Assert.NotNull(subject.Retention);
        Assert.Null(subject.RetentionIdempotency);
    }

    private static StorageUnit Unit() => new()
    {
        Id = new StorageUnitId("retention-declaration-test"),
        Name = "retention_declaration_test",
        Columns = [new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 100, IsNullable = false }],
        Key = new KeyDefinition { Columns = ["id"] },
        Concurrency = ConcurrencyDeclaration.None
    };
}
