using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Store;

namespace Groundwork.Testing.SelfTests;

public sealed class StorageUnitQueryRenderOptionsTests
{
    [Fact]
    public void Declared_indexes_become_typed_render_options_with_one_explicit_selection()
    {
        var unit = StorageUnit.Declare("tickets", "tickets")
            .String("id", 64, column => column.Required())
            .String("status", 32, column => column.Nullable())
            .Timestamp("createdAt", column => column.Required())
            .Key("id")
            .Index("by_status", "status")
            .Index("by_created", "createdAt")
            .Build();

        var options = unit.CreateQueryRenderOptions("by_status");

        Assert.Equal("by_status", options.SelectedIndex);
        Assert.Collection(
            options.Indexes,
            status =>
            {
                Assert.Equal("by_status", status.Name);
                var column = Assert.Single(status.Columns);
                Assert.Equal("status", column);
                Assert.Contains("status", status.NullableColumns);
                Assert.Equal(QueryType.String, status.ColumnTypes["status"]);
                Assert.True(status.IncludesNulls);
                Assert.Equal(QueryIndexPinning.ProviderDefault, status.Pinning);
            },
            created =>
            {
                Assert.Equal("by_created", created.Name);
                Assert.Equal(QueryType.DateTimeOffset, created.ColumnTypes["createdAt"]);
                Assert.Empty(created.NullableColumns);
            });
    }

    [Fact]
    public void Missing_value_and_unknown_selection_contracts_fail_closed()
    {
        var unit = StorageUnit.Declare("tickets", "tickets")
            .String("id", 64, column => column.Required())
            .String("status", 32, column => column.Nullable())
            .Key("id")
            .Index("by_status", index => index.Column("status").ExcludeMissingValues())
            .Build();

        var options = unit.CreateQueryRenderOptions("by_status");

        Assert.False(Assert.Single(options.Indexes).IncludesNulls);
        var exception = Assert.Throws<ArgumentException>(() =>
            unit.CreateQueryRenderOptions("not_declared"));
        Assert.Contains("not_declared", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manually_malformed_nonqueryable_index_is_refused_with_a_stable_code()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("malformed"),
            Name = "malformed",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new ColumnDefinition { Name = "payload", Type = PortableType.Json, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes =
            [
                new IndexDefinition { Name = "by_payload", Columns = [new IndexColumn("payload")] }
            ]
        };

        var exception = Assert.Throws<QueryRenderException>(() => unit.CreateQueryRenderOptions());

        Assert.Equal("GW-QUERY-018", exception.Code);
        Assert.Contains("payload", exception.Message, StringComparison.Ordinal);
    }
}
