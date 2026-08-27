using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.Substrate.Relational.Tests;

public sealed class RelationalRuntimeAdmissionTests
{
    [Fact]
    public void Admission_is_cached_per_unit_until_invalidated()
    {
        var inspections = 0;
        var admission = new RelationalRuntimeAdmission(
            "stub.schema-admission",
            desired => Target(desired),
            _ =>
            {
                inspections++;
                return new PhysicalSchemaInspectionResult(PhysicalSchemaHistoryState.Empty, IsAppliedSchemaValid: true);
            });
        var unit = Unit("admission-cache");

        admission.EnsureAdmitted(unit, observer: null);
        admission.EnsureAdmitted(unit, observer: null);
        Assert.Equal(1, inspections);

        admission.EnsureAdmitted(Unit("admission-cache"), observer: null);
        Assert.Equal(1, inspections);

        admission.Invalidate(unit.Id);
        admission.EnsureAdmitted(unit, observer: null);
        Assert.Equal(2, inspections);
    }

    [Fact]
    public void Verdict_from_an_inspection_that_raced_an_apply_cannot_satisfy_later_opens()
    {
        var inspections = 0;
        RelationalRuntimeAdmission? admission = null;
        var unit = Unit("admission-race");
        admission = new RelationalRuntimeAdmission(
            "stub.schema-admission",
            desired => Target(desired),
            _ =>
            {
                inspections++;
                if (inspections == 1)
                    admission!.Invalidate(unit.Id);
                return new PhysicalSchemaInspectionResult(PhysicalSchemaHistoryState.Empty, IsAppliedSchemaValid: true);
            });

        admission.EnsureAdmitted(unit, observer: null);
        Assert.Equal(1, inspections);

        admission.EnsureAdmitted(unit, observer: null);
        Assert.Equal(2, inspections);

        admission.EnsureAdmitted(unit, observer: null);
        Assert.Equal(2, inspections);
    }

    private static PhysicalSchemaTarget Target(StorageUnit unit) =>
        new(new SchemaSubject(unit), new ProviderIdentity("Stub", "1.0"), []);

    private static StorageUnit Unit(string id) => new()
    {
        Id = new StorageUnitId(id),
        Name = id.Replace('-', '_'),
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
            new ColumnDefinition { Name = "payload", Type = PortableType.String }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };
}
