using Groundwork.Kernel;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class ProviderPlanForestTests
{
    [Theory]
    [InlineData("Aggregate")]
    [InlineData("FunctionScan")]
    [InlineData("Compute")]
    [InlineData("Projection")]
    [InlineData("Offset")]
    public void Computational_operators_preserve_structure_without_fabricating_a_storage_target(string name)
    {
        Assert.True(Enum.TryParse<ProviderPlanOperator>(name, out var operation));
        var node = new ProviderPlanNode(1, 0, operation);
        Assert.Null(node.TargetId);
        Assert.Null(node.IndexId);
        Assert.Null(node.IsCovering);
        Assert.Null(node.SortPurpose);
        Assert.Throws<ArgumentException>(() => new ProviderPlanNode(1, 0, operation, targetId: Target));
    }

    private static readonly ProviderOpaqueIdentity Target = new(Guid.NewGuid());
    private static readonly ProviderOpaqueIdentity Index = new(Guid.NewGuid());

    [Fact]
    public void Native_top_n_sort_retains_fused_semantics_in_one_node()
    {
        Assert.True(Enum.TryParse<ProviderPlanOperator>("TopNSort", out var operation));
        var forest = new ProviderPlanForest([
            new(0, null, operation, sortPurpose: ProviderPlanSortPurpose.OrderBy),
            new(1, 0, ProviderPlanOperator.TableScan, targetId: Target)
        ]);

        Assert.Equal(2, forest.Nodes.Length);
        Assert.Equal(operation, forest.Nodes[0].Operation);
        Assert.Equal(ProviderPlanSortPurpose.OrderBy, forest.Nodes[0].SortPurpose);
        Assert.Equal(0, forest.Nodes[1].ParentId);
        Assert.Null(forest.Nodes[0].TargetId);
        Assert.Null(forest.Nodes[0].IndexId);
        Assert.Throws<ArgumentException>(() => new ProviderPlanNode(0, null, operation, targetId: Target));
    }

    [Fact]
    public void Native_limit_preserves_parentage_without_claiming_access_or_sort_facts()
    {
        Assert.True(Enum.TryParse<ProviderPlanOperator>("Limit", out var limit));
        var forest = new ProviderPlanForest([
            new(0, null, limit),
            new(1, 0, ProviderPlanOperator.Sort),
            new(2, 1, ProviderPlanOperator.TableScan, targetId: Target)
        ]);

        Assert.Equal(limit, forest.Nodes[0].Operation);
        Assert.Null(forest.Nodes[0].ParentId);
        Assert.Equal(0, forest.Nodes[1].ParentId);
        Assert.Equal(1, forest.Nodes[2].ParentId);
        Assert.Throws<ArgumentException>(() => new ProviderPlanNode(0, null, limit, targetId: Target));
        Assert.Throws<ArgumentException>(() => new ProviderPlanNode(0, null, limit, indexId: Index));
        Assert.Throws<ArgumentException>(() => new ProviderPlanNode(0, null, limit, isCovering: true));
        Assert.Throws<ArgumentException>(() => new ProviderPlanNode(0, null, limit, sortPurpose: ProviderPlanSortPurpose.OrderBy));
    }

    [Fact]
    public void Complete_scan_and_sort_forest_is_a_typed_fact_without_an_index_choice()
    {
        var nodes = new List<ProviderPlanNode>
        {
            new(0, null, ProviderPlanOperator.TableScan, targetId: Target),
            new(1, null, ProviderPlanOperator.Sort, sortPurpose: ProviderPlanSortPurpose.OrderBy)
        };
        var forest = new ProviderPlanForest(nodes);
        var evidence = new ProviderPlanEvidence(ProviderEvidenceAvailability.Collected,
            ProviderPlanProvenance.EstimatedExplain, winningPlan: forest);
        nodes.Clear();

        Assert.Same(forest, evidence.WinningPlan);
        Assert.Equal(2, forest.Nodes.Length);
        Assert.All(forest.Nodes, node => Assert.Null(node.ParentId));
        Assert.Null(evidence.ChoseExpectedIndex);
        Assert.Null(evidence.ChosenPhysicalIndexId);
    }

    [Fact]
    public void Index_fact_does_not_claim_a_completely_mapped_winning_plan()
    {
        var evidence = new ProviderPlanEvidence(ProviderEvidenceAvailability.Collected,
            ProviderPlanProvenance.EstimatedExplain, choseExpectedIndex: true, expectedLogicalIndex: "by_value");

        Assert.Null(evidence.WinningPlan);
    }

    [Fact]
    public void Child_identity_and_observed_index_attributes_are_preserved()
    {
        var forest = new ProviderPlanForest([
            new(4, null, ProviderPlanOperator.Materialize),
            new(9, 4, ProviderPlanOperator.IndexSearch, Target, Index, "by_value", isCovering: true)
        ]);

        Assert.Equal(4, forest.Nodes[1].ParentId);
        Assert.Equal(Index, forest.Nodes[1].IndexId);
        Assert.Equal("by_value", forest.Nodes[1].LogicalIndexName);
        Assert.True(forest.Nodes[1].IsCovering);
    }

    [Fact]
    public void Empty_null_duplicate_or_orphaned_nodes_cannot_attest_to_a_complete_plan()
    {
        Assert.Throws<ArgumentException>(() => new ProviderPlanForest([]));
        Assert.Throws<ArgumentNullException>(() => new ProviderPlanForest(null!));
        Assert.Throws<ArgumentException>(() => new ProviderPlanForest([null!]));
        Assert.Throws<ArgumentException>(() => new ProviderPlanForest([Scan(0), Scan(0)]));
        Assert.Throws<ArgumentException>(() => new ProviderPlanForest([Scan(0, 2)]));
    }

    [Fact]
    public void Cycles_are_rejected_even_when_an_unrelated_root_exists()
    {
        Assert.Throws<ArgumentException>(() => new ProviderPlanForest([Scan(0, 1), Scan(1, 0)]));
        Assert.Throws<ArgumentException>(() => new ProviderPlanForest([Scan(0), Scan(1, 2), Scan(2, 1)]));
    }

    [Theory]
    [InlineData(ProviderPlanOperator.Unknown)]
    [InlineData((ProviderPlanOperator)999)]
    public void Unknown_operators_cannot_be_silently_omitted_from_a_complete_plan(ProviderPlanOperator kind) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderPlanNode(0, null, kind));

    [Theory]
    [InlineData(ProviderPlanOperator.TableScan)]
    [InlineData(ProviderPlanOperator.PrimaryKeySearch)]
    [InlineData(ProviderPlanOperator.IndexScan)]
    [InlineData(ProviderPlanOperator.IndexSearch)]
    public void Access_nodes_require_the_actual_target(ProviderPlanOperator kind) =>
        Assert.Throws<ArgumentException>(() => new ProviderPlanNode(0, null, kind, indexId: Index));

    [Theory]
    [InlineData(ProviderPlanOperator.IndexScan)]
    [InlineData(ProviderPlanOperator.IndexSearch)]
    public void Index_access_requires_a_physical_index_identity(ProviderPlanOperator kind) =>
        Assert.Throws<ArgumentException>(() => new ProviderPlanNode(0, null, kind, targetId: Target));

    [Fact]
    public void Contradictory_fields_and_invalid_identifiers_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Scan(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Scan(0, -1));
        Assert.Throws<ArgumentException>(() => Scan(0, 0));
        Assert.Throws<ArgumentException>(() => new ProviderPlanNode(0, null, ProviderPlanOperator.Sort, targetId: Target));
        Assert.Throws<ArgumentException>(() => new ProviderPlanNode(0, null, ProviderPlanOperator.TableScan, Target, Index));
        Assert.Throws<ArgumentException>(() => new ProviderPlanNode(0, null, ProviderPlanOperator.TableScan, Target, isCovering: false));
        Assert.Throws<ArgumentException>(() => new ProviderPlanNode(0, null, ProviderPlanOperator.Materialize, logicalIndexName: "by_value"));
        Assert.Throws<ArgumentException>(() => new ProviderPlanNode(0, null, ProviderPlanOperator.TableScan, Target, sortPurpose: ProviderPlanSortPurpose.OrderBy));
        Assert.Throws<ArgumentException>(() => new ProviderPlanNode(0, null, ProviderPlanOperator.IndexSearch, Target, Index, " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderPlanNode(0, null, ProviderPlanOperator.Sort, sortPurpose: ProviderPlanSortPurpose.Unknown));
    }

    [Theory]
    [InlineData(ProviderEvidenceAvailability.Unknown)]
    [InlineData(ProviderEvidenceAvailability.NotRequested)]
    [InlineData(ProviderEvidenceAvailability.Unsupported)]
    [InlineData(ProviderEvidenceAvailability.Failed)]
    public void Uncollected_plan_cannot_carry_a_winning_forest(ProviderEvidenceAvailability availability) =>
        Assert.Throws<ArgumentException>(() => new ProviderPlanEvidence(availability,
            failureCategory: availability == ProviderEvidenceAvailability.Failed ? ProviderExecutionFailureCategory.PlanCollection : null,
            winningPlan: new ProviderPlanForest([Scan(0)])));

    private static ProviderPlanNode Scan(int id, int? parent = null) =>
        new(id, parent, ProviderPlanOperator.TableScan, targetId: Target);
}
