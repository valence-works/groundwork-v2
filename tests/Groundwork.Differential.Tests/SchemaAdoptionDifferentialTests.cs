using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.Differential.Tests;

/// <summary>
/// Adoption and the tolerant foreign-column policy against every relational provider, through the
/// same schema-tool session the deployment tool opens.
///
/// The adoption case is set up by applying the target normally and then deleting only Groundwork's
/// history row. What is left is exactly the situation adoption exists for — a real catalog on a
/// real server that Groundwork has no record of ever applying — and it gives the case an
/// independently produced reference row to compare the adopted one against, rather than comparing
/// adoption's output to itself.
///
/// These share one live SQL Server with every other differential class and create provider
/// infrastructure DDL on first use, so the class joins the collection that serializes them.
/// </summary>
[Collection(NativeProviderDifferentialCollection.Name)]
public sealed class SchemaAdoptionDifferentialTests
{
    [Fact]
    public void Sqlite_adopts_a_catalog_it_never_applied() =>
        RunAdoption(RelationalSchemaProvider.Sqlite("gw_adopt"));

    [SkippableFact]
    public void PostgreSql_adopts_a_catalog_it_never_applied() =>
        RunAdoption(RelationalSchemaProvider.PostgreSql("gw_adopt"));

    [SkippableFact]
    public void SqlServer_adopts_a_catalog_it_never_applied() =>
        RunAdoption(RelationalSchemaProvider.SqlServer("gw_adopt"));

    [Fact]
    public void Sqlite_refuses_to_adopt_a_catalog_that_differs() =>
        RunRefusal(RelationalSchemaProvider.Sqlite("gw_adopt"));

    [SkippableFact]
    public void PostgreSql_refuses_to_adopt_a_catalog_that_differs() =>
        RunRefusal(RelationalSchemaProvider.PostgreSql("gw_adopt"));

    [SkippableFact]
    public void SqlServer_refuses_to_adopt_a_catalog_that_differs() =>
        RunRefusal(RelationalSchemaProvider.SqlServer("gw_adopt"));

    [Fact]
    public void Sqlite_tolerates_a_foreign_column_only_where_the_declaration_opts_in() =>
        RunForeignColumns(RelationalSchemaProvider.Sqlite("gw_foreign"));

    [SkippableFact]
    public void PostgreSql_tolerates_a_foreign_column_only_where_the_declaration_opts_in() =>
        RunForeignColumns(RelationalSchemaProvider.PostgreSql("gw_foreign"));

    [SkippableFact]
    public void SqlServer_tolerates_a_foreign_column_only_where_the_declaration_opts_in() =>
        RunForeignColumns(RelationalSchemaProvider.SqlServer("gw_foreign"));

    /// <summary>
    /// The limitation the documentation states, proved rather than asserted. A derived search-key
    /// column's algorithm registration lives in Groundwork's own catalog; a database Groundwork
    /// never applied to has no such row, so the column's contents cannot be shown to have been
    /// produced by the declared algorithm. Adoption refuses instead of assuming.
    ///
    /// One provider is enough: the search-key catalog and the check over it are shared relational
    /// code, exercised on all three by the cases above.
    /// </summary>
    [Fact]
    public void A_folded_column_cannot_be_adopted_without_its_search_key_registration()
    {
        using var store = RelationalSchemaProvider.Sqlite("gw_adopt").Open();
        var folded = Orders(store.Table) with
        {
            Columns =
            [
                .. Orders(store.Table).Columns,
                new ColumnDefinition
                {
                    Name = "code",
                    Type = PortableType.String,
                    MaxLength = 32,
                    Collation = PortableCollation.OrdinalIgnoreCase
                }
            ]
        };
        var target = Target(store, folded);
        PhysicalSchemaApplication.Apply(target, store.Session.Executor);

        // A foreign database has neither Groundwork's history nor its search-key catalog.
        ForgetHistory(store);
        store.Execute($"DELETE FROM {store.Quote(RelationalDialect.SearchKeyAlgorithmsTable)};");

        var adoption = PhysicalSchemaAdoption.Adopt(target, store.Session.Executor);

        Assert.Equal(PhysicalSchemaAdoptionOutcome.Refused, adoption.Outcome);
        var refusal = Assert.Single(adoption.Refusals);
        Assert.Equal("GW-RUNTIME-001", refusal.Code);
        Assert.Equal("columns.__groundwork_search_code.searchKeyAlgorithm", refusal.Path);
        Assert.Contains("<missing>", refusal.Message, StringComparison.Ordinal);
    }

    private static void RunAdoption(RelationalSchemaProvider provider)
    {
        using var store = provider.Open();
        var target = Target(store, Orders(store.Table));

        // Apply normally, then keep the row it published as the reference and forget it.
        var reference = PhysicalSchemaApplication.Apply(target, store.Session.Executor).AppliedState!;
        ForgetHistory(store);
        Assert.Null(store.Session.Inspector.InspectHistory(target).History.AppliedState);

        var adoption = PhysicalSchemaAdoption.Adopt(target, store.Session.Executor);

        Assert.Equal(PhysicalSchemaAdoptionOutcome.Adopted, adoption.Outcome);
        Assert.Empty(adoption.Refusals);

        // What adoption published is what apply published: same target fingerprint, same snapshot,
        // and the same ledger row for row. The two were produced by different code paths against
        // the same catalog, so this is not the plan being compared to itself.
        var adopted = adoption.AppliedState!;
        Assert.Equal(reference.TargetFingerprint, adopted.TargetFingerprint);
        Assert.Equal(reference.Snapshot.Fingerprint, adopted.Snapshot.Fingerprint);
        Assert.Equal(reference.Snapshot.CanonicalPayload, adopted.Snapshot.CanonicalPayload);
        Assert.Equal(Ledger(reference), Ledger(adopted));

        // It survives the round trip through the provider's own history catalog — the CAS publish
        // really wrote it, and the serializer accepts it as canonical on the way back.
        var reread = store.Session.Inspector.InspectHistory(target);
        Assert.True(reread.IsAppliedSchemaValid);
        Assert.False(reread.HasColumnDrift);
        Assert.False(reread.HasIndexDrift);
        Assert.Equal(Ledger(reference), Ledger(reread.History.AppliedState!));

        // And the point of the whole feature: the next diff finds nothing to do.
        Assert.Empty(PhysicalSchemaDiffPlanner
            .Plan(target, reread.History, DateTimeOffset.UnixEpoch)
            .Operations);

        // Adopting again is reported rather than republished.
        Assert.Equal(
            PhysicalSchemaAdoptionOutcome.AlreadyAdopted,
            PhysicalSchemaAdoption.Adopt(target, store.Session.Executor).Outcome);
    }

    private static void RunRefusal(RelationalSchemaProvider provider)
    {
        using var store = provider.Open();
        var declared = Orders(store.Table);
        PhysicalSchemaApplication.Apply(Target(store, declared), store.Session.Executor);
        ForgetHistory(store);

        // A declaration that names a column the catalog does not have.
        var wider = declared with
        {
            Columns = [.. declared.Columns, new ColumnDefinition { Name = "note", Type = PortableType.String, MaxLength = 128 }]
        };
        var missingColumn = PhysicalSchemaAdoption.Adopt(Target(store, wider), store.Session.Executor);
        Assert.Equal(PhysicalSchemaAdoptionOutcome.Refused, missingColumn.Outcome);
        Assert.Contains(missingColumn.Refusals, refusal =>
            refusal.Code == "GW-RUNTIME-001" && refusal.Path == "columns.note");
        Assert.Null(store.Session.Inspector.InspectHistory(Target(store, declared)).History.AppliedState);

        // A catalog that carries a column the declaration does not name. Under the default policy
        // that is drift, and adoption would otherwise be recording a catalog it cannot account for.
        store.AddForeignColumn("audit_id", required: false);
        var foreignColumn = PhysicalSchemaAdoption.Adopt(Target(store, declared), store.Session.Executor);
        Assert.Equal(PhysicalSchemaAdoptionOutcome.Refused, foreignColumn.Outcome);
        var refused = Assert.Single(foreignColumn.Refusals);
        Assert.Equal("GW-RUNTIME-001", refused.Code);
        Assert.Equal("columns.audit_id", refused.Path);
        Assert.Contains("is not declared by this schema", refused.Message, StringComparison.Ordinal);

        // Nothing was published by either refusal.
        Assert.Null(store.Session.Inspector.InspectHistory(Target(store, declared)).History.AppliedState);

        // The same catalog under a declaration that opts into tolerating it adopts, and says so.
        var tolerant = declared with { ForeignColumns = ForeignColumnPolicy.TolerateDatabaseSupplied };
        var adopted = PhysicalSchemaAdoption.Adopt(Target(store, tolerant), store.Session.Executor);
        Assert.Equal(PhysicalSchemaAdoptionOutcome.Adopted, adopted.Outcome);
        var tolerated = Assert.Single(adopted.ToleratedDrift);
        Assert.Equal("GW-RUNTIME-003", tolerated.Code);
        Assert.Equal("columns.audit_id", tolerated.Path);

        // Tolerance is not part of the target, so the row it published is the one the strict
        // declaration would have published for the same catalog.
        Assert.Equal(Target(store, declared).Fingerprint, adopted.AppliedState!.TargetFingerprint);
    }

    private static void RunForeignColumns(RelationalSchemaProvider provider)
    {
        using var store = provider.Open();
        var declared = Orders(store.Table);
        var executor = (RelationalSchemaExecutor)store.Session.Executor;
        PhysicalSchemaApplication.Apply(Target(store, declared), executor);

        // A clean catalog reports nothing either way.
        Assert.False(executor.InspectDeployedHistory(Target(store, declared)).HasToleratedDrift);

        // Another tool adds a column of its own. The database will fill it in for a writer that
        // omits it, which is the whole of the tolerance question.
        store.AddForeignColumn("audit_id", required: false);

        var strict = executor.InspectDeployedHistory(Target(store, declared));
        Assert.False(strict.IsAppliedSchemaValid);
        Assert.False(strict.HasToleratedDrift);
        var fatal = Assert.Single(strict.ColumnDrift);
        Assert.Equal("GW-RUNTIME-001", fatal.Code);
        Assert.Equal("columns.audit_id", fatal.Path);

        var tolerantTarget = Target(store, declared with { ForeignColumns = ForeignColumnPolicy.TolerateDatabaseSupplied });
        var tolerant = executor.InspectDeployedHistory(tolerantTarget);
        Assert.True(tolerant.IsAppliedSchemaValid);
        Assert.False(tolerant.HasColumnDrift);
        var warning = Assert.Single(tolerant.ToleratedDrift);
        Assert.Equal("GW-RUNTIME-003", warning.Code);
        Assert.Equal("columns.audit_id", warning.Path);

        // The opt-in stops exactly where the database stops supplying a value. The column is added
        // with the table still empty, because a server will not otherwise accept it.
        store.AddForeignColumn("tenant_ref", required: true);
        var stillFatal = executor.InspectDeployedHistory(tolerantTarget);
        Assert.False(stillFatal.IsAppliedSchemaValid);
        Assert.Equal("columns.tenant_ref", Assert.Single(stillFatal.ColumnDrift).Path);
        Assert.Equal("columns.audit_id", Assert.Single(stillFatal.ToleratedDrift).Path);
    }

    /// <summary>
    /// Removes only Groundwork's record that this subject was ever applied, leaving the catalog
    /// exactly as the apply left it.
    /// </summary>
    private static void ForgetHistory(RelationalSchemaStore store) => store.Execute(
        $"DELETE FROM {store.Quote(RelationalDialect.SchemaHistoryTable)} " +
        $"WHERE {store.Quote("subject_id")}='{store.Table}';");

    private static string Ledger(PhysicalSchemaAppliedState state) => string.Join(
        Environment.NewLine,
        state.AppliedOperations
            .Select(operation =>
                $"{operation.Identity}|{operation.Fingerprint}|{operation.Kind}|{operation.SubjectIdentity}|" +
                $"{operation.SlotIdentity}|{operation.CanonicalPayload}")
            .Order(StringComparer.Ordinal));

    private static PhysicalSchemaTarget Target(RelationalSchemaStore store, StorageUnit unit) =>
        store.Session.Targets.Compile(unit);

    /// <summary>
    /// Deliberately carries no folded column. A derived search-key column's registration lives in
    /// Groundwork's own catalog, which a foreign database does not have, so a subject with one
    /// cannot be adopted — that case is covered separately rather than smuggled in here.
    /// </summary>
    private static StorageUnit Orders(string table) => new()
    {
        Id = new StorageUnitId(table),
        Name = table,
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new() { Name = "customer", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new() { Name = "total", Type = PortableType.Decimal, Precision = 18, Scale = 4 }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes = [new IndexDefinition { Name = "by_total", Columns = [new IndexColumn("total")] }]
    };
}
