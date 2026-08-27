using System.Collections.Immutable;
using Groundwork.Query.Model;
using Groundwork.Query.Planning;
using Xunit;

namespace Groundwork.Query.Planning.Tests;

/// <summary>
/// The declared key as a coverage candidate. Every relational coordinator emits the key as the
/// table's PRIMARY KEY, which the engine backs with a unique index, so a key-bounded read is a seek
/// rather than a scan and must not be refused.
/// </summary>
public sealed class CoverageCandidatesTests
{
    private static readonly TableId Table = new("tickets");
    private static readonly ColumnRef Tenant = new(Table, "tenant", QueryType.String, isNullable: false);
    private static readonly ColumnRef Id = new(Table, "id", QueryType.String, isNullable: false);
    private static readonly ColumnRef Status = new(Table, "status", QueryType.String);

    [Fact]
    public void A_single_column_key_covers_its_equality_without_a_declared_index()
    {
        var result = Check(["id"], [], new Predicate.Equal(Id, QueryConstant.Of(Id, "a")));

        Assert.True(result.IsCovered, result.Refusal?.Message);
        Assert.Equal("(declared key)", result.Index!.Name);
    }

    [Fact]
    public void A_composite_key_covers_its_leading_column_and_the_whole_key()
    {
        var leading = Check(["tenant", "id"], [], new Predicate.Equal(Tenant, QueryConstant.Of(Tenant, "t1")));
        var whole = Check(["tenant", "id"], [], new Predicate.And([
            new Predicate.Equal(Tenant, QueryConstant.Of(Tenant, "t1")),
            new Predicate.Equal(Id, QueryConstant.Of(Id, "a"))]));

        Assert.True(leading.IsCovered, leading.Refusal?.Message);
        Assert.True(whole.IsCovered, whole.Refusal?.Message);
        Assert.Equal("(declared key)", leading.Index!.Name);
        Assert.Equal("(declared key)", whole.Index!.Name);
    }

    /// <summary>
    /// The trailing column of a composite key is not a leading-column bound, exactly as it would
    /// not be for a declared compound index. A key is an ordered index, not a set of columns.
    /// </summary>
    [Fact]
    public void A_composite_key_does_not_cover_its_trailing_column_alone()
    {
        var result = Check(["tenant", "id"], [], new Predicate.Equal(Id, QueryConstant.Of(Id, "a")));

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-006", result.Refusal!.Code);
        Assert.Equal("[GwIndex(\"ix_tickets\", \"id ASC\")]", result.Refusal.SuggestedDeclaration);
    }

    /// <summary>
    /// The point-read remedy is true of exactly one shape: a conjunction of single-value equalities
    /// over every key column, which matches at most one row. There the suggestion is withheld —
    /// declaring an index would duplicate the primary key and would not fix the refusal — and the
    /// point-read path is named instead.
    /// </summary>
    [Fact]
    public void A_complete_key_equality_names_the_point_read_instead_of_an_index()
    {
        var result = Check(["tenant", "id"], [], new Predicate.And([
            new Predicate.Equal(Tenant, QueryConstant.Of(Tenant, "t1")),
            new Predicate.Equal(Id, QueryConstant.Of(Id, "a")),
            new Predicate.Substring(Status, "pen", Anchor.Contains)]));

        Assert.False(result.IsCovered);
        Assert.Null(result.Refusal!.SuggestedIndex);
        Assert.Equal(string.Empty, result.Refusal.SuggestedDeclaration);
        Assert.Contains("session.Read(key)", result.Refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Add: ", result.Refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A disjunction over the key's columns is not a point read — one key lookup cannot preserve an
    /// Or — so it keeps the ordinary index suggestion rather than being handed a remedy that cannot
    /// be followed.
    /// </summary>
    [Fact]
    public void A_disjunction_over_key_columns_keeps_the_index_suggestion()
    {
        var result = Check(["tenant", "id"], [], new Predicate.Or([
            new Predicate.Equal(Tenant, QueryConstant.Of(Tenant, "t1")),
            new Predicate.Equal(Id, QueryConstant.Of(Id, "a"))]));

        Assert.False(result.IsCovered);
        // Or canonicalizes its terms, so the composed suggestion is in canonical column order
        // rather than the order the disjunction was written in.
        Assert.Equal("[GwIndex(\"ix_tickets\", \"id ASC, tenant ASC\")]", result.Refusal!.SuggestedDeclaration);
        Assert.DoesNotContain("session.Read(key)", result.Refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A range over the key is not a point read either: it can match many rows, so a key lookup is
    /// not the remedy and the suggestion stands.
    /// </summary>
    [Fact]
    public void A_range_over_the_key_keeps_the_index_suggestion()
    {
        var result = Check(["id"], [], new Predicate.And([
            new Predicate.Range(Id, Bound.Inclusive(QueryConstant.Of(Id, "a")), null),
            new Predicate.Substring(Status, "pen", Anchor.Contains)]));

        Assert.False(result.IsCovered);
        Assert.Equal("[GwIndex(\"ix_tickets\", \"id ASC, status ASC\")]", result.Refusal!.SuggestedDeclaration);
        Assert.DoesNotContain("session.Read(key)", result.Refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An equality over only part of a composite key leaves more than one row reachable, so it is
    /// not a point read and keeps its suggestion.
    /// </summary>
    [Fact]
    public void A_partial_key_equality_keeps_the_index_suggestion()
    {
        var result = Check(["tenant", "id"], [], new Predicate.And([
            new Predicate.Equal(Tenant, QueryConstant.Of(Tenant, "t1")),
            new Predicate.Substring(Status, "pen", Anchor.Contains)]));

        Assert.False(result.IsCovered);
        Assert.Equal("[GwIndex(\"ix_tickets\", \"tenant ASC, status ASC\")]", result.Refusal!.SuggestedDeclaration);
        Assert.DoesNotContain("session.Read(key)", result.Refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A suggestion over columns the key does not mention is still offered.</summary>
    [Fact]
    public void A_refusal_over_non_key_columns_still_suggests_an_index()
    {
        var result = Check(["id"], [], new Predicate.Or([
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
            new Predicate.Equal(Tenant, QueryConstant.Of(Tenant, "t1"))]));

        Assert.False(result.IsCovered);
        Assert.Equal("[GwIndex(\"ix_tickets\", \"status ASC, tenant ASC\")]", result.Refusal!.SuggestedDeclaration);
    }

    [Fact]
    public void The_key_leads_the_derived_candidates_and_the_declared_indexes_follow()
    {
        var derived = CoverageCandidates.Derive(
            ["tenant", "id"],
            [new CoverageIndex("ix_status", ["status"])]);

        Assert.Equal(["(declared key)", "ix_status"], derived.Select(index => index.Name));
        Assert.Equal(["tenant", "id"], derived[0].Columns.Select(column => column.Column));
        Assert.True(derived[0].IsDeclaredKey);
        Assert.False(derived[1].IsDeclaredKey);
    }

    /// <summary>Key columns are non-null on every provider, so the sparse rule cannot exclude them.</summary>
    [Fact]
    public void Key_candidate_columns_are_ascending_non_nullable_and_include_missing_values()
    {
        var key = CoverageCandidates.Derive(["id"], [])[0];

        Assert.Equal(OrderDirection.Ascending, key.Columns[0].Direction);
        Assert.False(key.Columns[0].IsNullable);
        Assert.Equal(IndexMissingValueBehavior.Included, key.MissingValues);
    }

    [Fact]
    public void A_unit_without_key_columns_contributes_no_key_candidate()
    {
        var derived = CoverageCandidates.Derive([], [new CoverageIndex("ix_status", ["status"])]);

        Assert.Equal(["ix_status"], derived.Select(index => index.Name));
    }

    [Fact]
    public void Derive_names_the_null_argument_it_refuses()
    {
        Assert.Equal("keyColumns",
            Assert.Throws<ArgumentNullException>(() => CoverageCandidates.Derive(null!, [])).ParamName);
        Assert.Equal("declaredIndexes",
            Assert.Throws<ArgumentNullException>(() => CoverageCandidates.Derive(["id"], null!)).ParamName);
    }

    private static QueryCoverageResult Check(
        ImmutableArray<string> keyColumns,
        ImmutableArray<CoverageIndex> indexes,
        Predicate predicate) =>
        QueryCoverageChecker.Check(
            new QueryRequest(Table, predicate, [], Projection.All, Paging.None, ResultShape.Rows.Instance),
            CoverageCandidates.Derive(keyColumns, indexes));
}
