using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Groundwork.Analyzers;
using Groundwork.Schema;
using Groundwork.Kernel;
using Groundwork.Query.Linq.Fragments;
using Groundwork.Query.Linq;
using Groundwork.Query.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Groundwork.Analyzers.Tests;

public sealed class CoverageAnalyzerTests
{
    [Fact]
    public async Task Linq_analyzer_reports_bare_startswith_at_the_subexpression()
    {
        const string source = "using System; using System.Linq.Expressions; namespace Groundwork.Query.Linq { [AttributeUsage(AttributeTargets.Property)] public sealed class GwStringComparisonAttribute : Attribute { public GwStringComparisonAttribute(StringComparison comparison) { } } public sealed class GwQueryTable<T> { public void Where(Expression<Func<T, bool>> predicate) { } } } public sealed class Ticket { [Groundwork.Query.Linq.GwStringComparison(StringComparison.Ordinal)] public string Name { get; set; } = \"\"; } public static class Use { public static void Run(Groundwork.Query.Linq.GwQueryTable<Ticket> table) { table.Where(ticket => ticket.Name.StartsWith(\"x\")); } }";
        var diagnostics = await AnalyzeLinq(source);
        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "GW_LINQ_108"));
        Assert.Contains("StringComparison.Ordinal", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.True(diagnostic.Location.SourceSpan.Length < source.Length / 2);
    }

    [Fact]
    public async Task Linq_analyzer_reports_column_arithmetic_and_column_comparison()
    {
        var diagnostics = await AnalyzeLinq("using System; using System.Linq.Expressions; namespace Groundwork.Query.Linq { public sealed class GwQueryTable<T> { public void Where(Expression<Func<T, bool>> predicate) { } } } public sealed class Ticket { public int A { get; set; } public int B { get; set; } } public static class Use { public static void Run(Groundwork.Query.Linq.GwQueryTable<Ticket> table) { table.Where(ticket => ticket.A + 1 > 2 && ticket.A == ticket.B); } }");
        Assert.Contains(diagnostics, item => item.Id == "GW_LINQ_102");
        Assert.Contains(diagnostics, item => item.Id == "GW_LINQ_103");
    }

    [Fact]
    public async Task Linq_code_fix_inserts_the_explicit_ordinal_overload()
    {
        const string source = "using System; using System.Linq.Expressions; namespace Groundwork.Query.Linq { [AttributeUsage(AttributeTargets.Property)] public sealed class GwStringComparisonAttribute : Attribute { public GwStringComparisonAttribute(StringComparison comparison) { } } public sealed class GwQueryTable<T> { public void Where(Expression<Func<T, bool>> predicate) { } } } public sealed class Ticket { [Groundwork.Query.Linq.GwStringComparison(StringComparison.Ordinal)] public string Name { get; set; } = \"\"; } public static class Use { public static void Run(Groundwork.Query.Linq.GwQueryTable<Ticket> table) { table.Where(ticket => ticket.Name.StartsWith(\"x\")); } }";
        var diagnostic = Assert.Single((await AnalyzeLinq(source)).Where(item => item.Id == "GW_LINQ_108"));
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("LinqCodeFix", LanguageNames.CSharp);
        var document = project.AddDocument("Query.cs", SourceText.From(source));
        var actions = new List<CodeAction>();
        await new LinqCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));
        var operation = Assert.Single(await Assert.Single(actions).GetOperationsAsync(CancellationToken.None));
        var changed = Assert.IsType<ApplyChangesOperation>(operation).ChangedSolution.GetDocument(document.Id)!;
        Assert.Contains("StringComparison.Ordinal", (await changed.GetTextAsync()).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linq_analyzer_ignores_unrelated_func_lambdas()
    {
        var diagnostics = await AnalyzeLinq("using System; public sealed class Ticket { public string Name { get; set; } = \"\"; public int A { get; set; } } public static class Use { public static Func<Ticket, bool> Run = ticket => ticket.Name.ToLower() == \"x\" && ticket.A + 1 > 2; }");
        Assert.DoesNotContain(diagnostics, item => item.Id.StartsWith("GW_LINQ_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Linq_analyzer_accepts_an_attributed_fragment_body()
    {
        var diagnostics = await AnalyzeLinq("using System; using System.Linq.Expressions; namespace Groundwork.Query.Linq { [AttributeUsage(AttributeTargets.Property)] public sealed class GwQueryFragmentAttribute : Attribute { } } public sealed class Ticket { public bool IsOpen { get; set; } } public static class Fragments { [Groundwork.Query.Linq.GwQueryFragment] public static Expression<Func<Ticket, bool>> Open => ticket => ticket.IsOpen; }");
        Assert.DoesNotContain(diagnostics, item => item.Id.StartsWith("GW_LINQ_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Linq_analyzer_uses_declared_ignore_case_folding_and_rejects_mismatch()
    {
        const string source = "using System; using System.Linq.Expressions; namespace Groundwork.Query.Linq { [AttributeUsage(AttributeTargets.Property)] public sealed class GwStringComparisonAttribute : Attribute { public GwStringComparisonAttribute(StringComparison comparison) { } } public sealed class GwQueryTable<T> { public void Where(Expression<Func<T, bool>> predicate) { } } } public sealed class Ticket { [Groundwork.Query.Linq.GwStringComparison(StringComparison.OrdinalIgnoreCase)] public string Name { get; set; } = \"\"; } public static class Use { public static void Run(Groundwork.Query.Linq.GwQueryTable<Ticket> table) { table.Where(ticket => ticket.Name.StartsWith(\"x\", StringComparison.Ordinal)); } }";
        var diagnostics = await AnalyzeLinq(source);
        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "GW_LINQ_108"));
        Assert.Contains("OrdinalIgnoreCase", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linq_analyzer_rejects_unsupported_string_policy_on_fields()
    {
        const string source = "using System; using System.Linq.Expressions; namespace Groundwork.Query.Linq { [AttributeUsage(AttributeTargets.Field)] public sealed class GwStringComparisonAttribute : Attribute { public GwStringComparisonAttribute(StringComparison comparison) { } } public sealed class GwQueryTable<T> { public void Where(Expression<Func<T, bool>> predicate) { } } } public sealed class Ticket { [Groundwork.Query.Linq.GwStringComparison((StringComparison)1)] public string Name = \"\"; } public static class Use { public static void Run(Groundwork.Query.Linq.GwQueryTable<Ticket> table) { table.Where(ticket => ticket.Name.StartsWith(\"x\", StringComparison.Ordinal)); } }";
        var diagnostics = await AnalyzeLinq(source);
        Assert.Contains(diagnostics, item => item.Id == "GW_LINQ_108");
    }

    [Fact]
    public async Task Linq_analyzer_accepts_external_fragments_but_rejects_unmarked_helpers()
    {
        const string source = "using System; using System.Linq.Expressions; using Groundwork.Query.Linq.Fragments; namespace Groundwork.Query.Linq { public sealed class GwQueryTable<T> { public void Where(Expression<Func<T, bool>> predicate) { } public void WhereIf(bool enabled, Expression<Func<T, bool>> predicate) { } } } public static class Use { public static void Run(Groundwork.Query.Linq.GwQueryTable<ExternalTicket> table) { table.Where(ExternalFragments.IsOpen); table.WhereIf(true, ExternalFragments.Unmarked); } }";
        var diagnostics = await AnalyzeLinq(source);
        Assert.DoesNotContain(diagnostics, item => item.GetMessage().Contains("ExternalFragments", StringComparison.Ordinal));
        Assert.Contains(diagnostics, item => item.Id == "GW_LINQ_107");
    }

    [Fact]
    public async Task Linq_analyzer_accepts_membership_and_equality_element_sets()
    {
        const string source = "using System; using System.Linq; using System.Linq.Expressions; namespace Groundwork.Query.Linq { public sealed class GwQueryTable<T> { public void Where(Expression<Func<T, bool>> predicate) { } } } public sealed class Ticket { public int[] Tags { get; set; } = Array.Empty<int>(); public int Id { get; set; } } public static class Use { public static void Run(Groundwork.Query.Linq.GwQueryTable<Ticket> table, int[] ids) { table.Where(ticket => ids.Contains(ticket.Id) && Enumerable.Contains(ids, ticket.Id) && ticket.Tags.Any(value => value == 7) && ticket.Tags.All(value => value == 7)); } }";
        var diagnostics = await AnalyzeLinq(source);
        Assert.DoesNotContain(diagnostics, item => item.Id is "GW_LINQ_107" or "GW_LINQ_108");
    }

    [Fact]
    public async Task Linq_analyzer_rejects_unsupported_contains_overload()
    {
        var diagnostics = await AnalyzeLinq("using System; using System.Linq; using System.Linq.Expressions; using System.Collections.Generic; public sealed class Ticket { public int Id { get; set; } } public static class Use { public static void Run(Groundwork.Query.Linq.GwQueryTable<Ticket> table, int[] ids) { table.Where(ticket => Enumerable.Contains(ids, ticket.Id, EqualityComparer<int>.Default)); } }");
        Assert.Contains(diagnostics, item => item.Id == "GW_LINQ_107");
    }

    [Fact]
    public async Task Linq_analyzer_rejects_contains_without_a_closed_collection_and_direct_row_column()
    {
        var diagnostics = await AnalyzeLinq("using System; using System.Linq; using System.Linq.Expressions; public sealed class Ticket { public int Id { get; set; } public int[] Tags { get; set; } = Array.Empty<int>(); } public static class Use { public static void Run(Groundwork.Query.Linq.GwQueryTable<Ticket> table, int[] ids) { table.Where(ticket => ids.Contains(7) || ticket.Tags.Contains(ticket.Id)); } }");
        Assert.Equal(2, diagnostics.Count(item => item.Id == "GW_LINQ_107"));
    }

    [Fact]
    public async Task Linq_analyzer_rejects_reduced_extension_contains_lookalikes()
    {
        var diagnostics = await AnalyzeLinq("using System; using System.Linq.Expressions; using CustomExtensions; public sealed class Ticket { public int Id { get; set; } } namespace CustomExtensions { public sealed class CustomValues { } public static class CustomValueExtensions { public static bool Contains(this CustomValues values, int value) => false; } } public static class Use { public static void Run(Groundwork.Query.Linq.GwQueryTable<Ticket> table, CustomValues values) { table.Where(ticket => values.Contains(ticket.Id)); } }");
        Assert.Contains(diagnostics, item => item.Id == "GW_LINQ_107");
    }

    [Fact]
    public async Task Linq_analyzer_rejects_exact_name_enumerable_spoofs_from_an_aliased_assembly()
    {
        using var stream = new MemoryStream();
        var spoof = CSharpCompilation.Create(
            "SpoofEnumerable",
            [CSharpSyntaxTree.ParseText("namespace System.Linq { public static class Enumerable { public static bool Contains<T>(System.Collections.Generic.IEnumerable<T> values, T value) => true; public static bool Any<T>(System.Collections.Generic.IEnumerable<T> values, System.Func<T, bool> predicate) => true; } }")],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.True(spoof.Emit(stream).Success);
        stream.Position = 0;
        var reference = MetadataReference.CreateFromStream(stream, new MetadataReferenceProperties(aliases: ["spoof"]));

        var diagnostics = await AnalyzeLinq(
            "extern alias spoof; using System; using System.Linq.Expressions; public sealed class Ticket { public int Id { get; set; } public int[] Tags { get; set; } = Array.Empty<int>(); } public static class Use { public static void Run(Groundwork.Query.Linq.GwQueryTable<Ticket> table, int[] ids) { table.Where(ticket => spoof::System.Linq.Enumerable.Contains(ids, ticket.Id) && spoof::System.Linq.Enumerable.Any(ticket.Tags, value => value == 7)); } }",
            [reference]);

        Assert.Equal(2, diagnostics.Count(item => item.Id == "GW_LINQ_107"));
    }

    [Fact]
    public async Task Linq_analyzer_ignores_spoofed_groundwork_namespace_apis()
    {
        var diagnostics = await AnalyzeLinq("using System; using System.Linq.Expressions; namespace Groundwork.Query.Linq.Spoof { public sealed class GwQueryTable<T> { public void Where(Expression<Func<T, bool>> predicate) { } } } public sealed class Ticket { public string Name { get; set; } = \"\"; } public static class Use { public static void Run(Groundwork.Query.Linq.Spoof.GwQueryTable<Ticket> table) { table.Where(ticket => ticket.Name.ToLower() == \"x\"); } }");
        Assert.DoesNotContain(diagnostics, item => item.Id.StartsWith("GW_LINQ_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Linq_analyzer_rejects_non_static_fragment_properties()
    {
        var diagnostics = await AnalyzeLinq("using System; using System.Linq.Expressions; public sealed class Ticket { public bool IsOpen { get; set; } } public sealed class FragmentHolder { [Groundwork.Query.Linq.GwQueryFragment] public Expression<Func<Ticket, bool>> Open => ticket => ticket.IsOpen; } public static class Use { public static void Run(Groundwork.Query.Linq.GwQueryTable<Ticket> table, FragmentHolder holder) { table.Where(holder.Open); } }");
        Assert.Contains(diagnostics, item => item.Id == "GW_LINQ_107");
    }

    [Fact]
    public async Task Linq_analyzer_rejects_element_set_predicates_that_capture_the_outer_row()
    {
        var diagnostics = await AnalyzeLinq("using System; using System.Linq; using System.Linq.Expressions; namespace Groundwork.Query.Linq { public sealed class GwQueryTable<T> { public void Where(Expression<Func<T, bool>> predicate) { } } } public sealed class Ticket { public int[] Tags { get; set; } = Array.Empty<int>(); public int Id { get; set; } } public static class Use { public static void Run(Groundwork.Query.Linq.GwQueryTable<Ticket> table) { table.Where(ticket => ticket.Tags.Any(value => value == ticket.Id)); } }");
        Assert.Contains(diagnostics, item => item.Id == "GW_LINQ_106");
    }

    [Fact]
    public async Task Linq_analyzer_requires_element_set_equality_to_compare_the_nested_element_once()
    {
        var diagnostics = await AnalyzeLinq("using System; using System.Linq; using System.Linq.Expressions; namespace Groundwork.Query.Linq { public sealed class GwQueryTable<T> { public void Where(Expression<Func<T, bool>> predicate) { } } } public sealed class Ticket { public int[] Tags { get; set; } = Array.Empty<int>(); } public static class Use { public static void Run(Groundwork.Query.Linq.GwQueryTable<Ticket> table) { table.Where(ticket => ticket.Tags.Any(value => 1 == 2) || ticket.Tags.All(value => value == value)); } }");
        Assert.Equal(2, diagnostics.Count(item => item.Id == "GW_LINQ_106"));
    }

    [Fact]
    public async Task Linq_analyzer_rejects_element_set_equality_with_a_nested_element_expression()
    {
        var diagnostics = await AnalyzeLinq("using System; using System.Linq; using System.Linq.Expressions; namespace Groundwork.Query.Linq { public sealed class GwQueryTable<T> { public void Where(Expression<Func<T, bool>> predicate) { } } } public sealed class Ticket { public int[] Tags { get; set; } = Array.Empty<int>(); } public static class Use { public static void Run(Groundwork.Query.Linq.GwQueryTable<Ticket> table) { table.Where(ticket => ticket.Tags.Any(value => value == Math.Abs(value))); } }");
        Assert.Contains(diagnostics, item => item.Id == "GW_LINQ_106");
    }

    [Fact]
    public async Task Linq_analyzer_rejects_unlisted_enumerable_composition()
    {
        var diagnostics = await AnalyzeLinq("using System; using System.Linq; using System.Linq.Expressions; namespace Groundwork.Query.Linq { public sealed class GwQueryTable<T> { public void Where(Expression<Func<T, bool>> predicate) { } } } public sealed class Ticket { public int[] Tags { get; set; } = Array.Empty<int>(); } public static class Use { public static void Run(Groundwork.Query.Linq.GwQueryTable<Ticket> table) { table.Where(ticket => ticket.Tags.Select(value => value).Any()); } }");
        Assert.Contains(diagnostics, item => item.Id == "GW_LINQ_107");
    }

    [Fact]
    public async Task Linq_analyzer_accepts_utc_instant_arithmetic()
    {
        const string source = "using System; using System.Linq.Expressions; namespace Groundwork.Query.Linq { public sealed class GwQueryTable<T> { public void Where(Expression<Func<T, bool>> predicate) { } } } public sealed class Ticket { public DateTimeOffset CreatedAt { get; set; } } public static class Use { public static void Run(Groundwork.Query.Linq.GwQueryTable<Ticket> table) { table.Where(ticket => ticket.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-1)); } }";
        var diagnostics = await AnalyzeLinq(source);
        Assert.DoesNotContain(diagnostics, item => item.Id == "GW_LINQ_107");
    }

    [Fact]
    public async Task Uncovered_query_reports_the_q3_code_and_suggested_index()
    {
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_other", "other ASC")) + QuerySource("var result = db.Table<Ticket>().Where(t => t.Status == status).ToListAsync();"));

        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "GW_COVER_006"));
        Assert.Contains("GwIndex", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("status", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.True(diagnostic.Location.GetLineSpan().StartLinePosition.Line > 0);
    }

    [Fact]
    public async Task Distinct_projection_is_covered_by_an_index_on_the_projected_column()
    {
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_status", "status ASC")) +
            QuerySource("var result = db.Table<Ticket>().Where(t => t.Status == status).OrderBy(t => t.Status).Select(t => new { t.Status }).Distinct().Take(1).ToListAsync();"));

        Assert.DoesNotContain(diagnostics, item => item.Id.StartsWith("GW_COVER_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unbounded_distinct_projection_reports_the_scan_refusal()
    {
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_status", "status ASC")) +
            QuerySource("var result = db.Table<Ticket>().Select(t => new { t.Status }).Distinct().ToListAsync();"));

        Assert.Contains(diagnostics, item => item.Id == "GW_COVER_005");
    }

    [Fact]
    public async Task Unbounded_distinct_projection_accepts_an_explicit_scan()
    {
        var diagnostics = await Analyze(
            WithSchema(SchemaWithIndex("ix_status", "status ASC"), allowAcceptedScans: true) +
            QuerySource("var result = db.Table<Ticket>().Select(t => new { t.Status }).Distinct().AcceptScan(\"GW-SCAN-DISTINCT\", \"distinct report\", \"query-team\", \"2027-01-01\").ToListAsync();"),
            now: new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero));

        Assert.Contains(diagnostics, item => item.Id == "GW_COVER_905");
        Assert.DoesNotContain(diagnostics, item => item.Id is "GW_COVER_005" or "GW_COVER_006" or "GW_COVER_901");
    }

    [Fact]
    public async Task Take_zero_is_resolved_as_an_empty_query()
    {
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_status", "status ASC")) +
            QuerySource("var result = db.Table<Ticket>().OrderBy(t => t.Status).Take(0).FirstAsync();"));

        Assert.DoesNotContain(diagnostics, item => item.Id.StartsWith("GW_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Take_zero_first_terminals_still_require_deterministic_order()
    {
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_status", "status ASC")) +
            QuerySource("var first = db.Table<Ticket>().Take(0).FirstAsync(); var fallback = db.Table<Ticket>().Take(0).FirstOrDefaultAsync();"));

        Assert.Equal(2, diagnostics.Count(item => item.Id == "GW_COVER_016"));
        Assert.All(diagnostics, item => Assert.Contains("deterministic order", item.GetMessage(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Projection_initializers_and_constructors_are_resolved_for_distinct_and_cardinality()
    {
        var source = QuerySource("""
            public sealed class StatusDto { public string Status { get; set; } = ""; }
            public sealed class ConstructorStatusDto { public ConstructorStatusDto(string status) { Status = status; } public string Status { get; } }
            var initialized = db.Table<Ticket>().Where(t => t.Status == "open").OrderBy(t => t.Status).Select(t => new StatusDto { Status = t.Status }).Distinct().Take(1).ToListAsync();
            var constructed = db.Table<Ticket>().OrderBy(t => t.Status).Select(t => new ConstructorStatusDto(t.Status)).FirstAsync();
            """);
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_status", "status ASC")) + source);

        Assert.DoesNotContain(diagnostics, item => item.Id.StartsWith("GW_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reduction_terminals_are_covered_for_closed_surface_calls()
    {
        var source = WithSchema(
            SchemaWithIndex("ix_status_amount", "status ASC, amount ASC", "ix_status_created", "status ASC, created_at ASC")) +
            QuerySource("""
                var sum = db.Table<Ticket>().Where(t => t.Status == status).Sum(t => t.Amount);
                var minimum = db.Table<Ticket>().Where(t => t.Status == status).Min(t => t.CreatedAt);
                var maximum = db.Table<Ticket>().Where(t => t.Status == status).Max(t => t.Amount);
                """);

        var diagnostics = await Analyze(source);

        Assert.DoesNotContain(diagnostics, item => item.Id.StartsWith("GW_COVER_", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, item => item.Id == "GW_LINQ_112");
    }

    [Fact]
    public async Task Async_reduction_terminals_match_sync_selector_and_coverage_diagnostics()
    {
        var source = WithSchema(
            SchemaWithIndex("ix_status_amount", "status ASC, amount ASC", "ix_status_created", "status ASC, created_at ASC")) +
            QuerySource("""
                var sum = db.Table<Ticket>().Where(t => t.Status == status).SumAsync(executor, t => t.Amount, default);
                var minimum = db.Table<Ticket>().Where(t => t.Status == status).MinAsync(executor, t => t.CreatedAt);
                var maximum = db.Table<Ticket>().Where(t => t.Status == status).MaxAsync(executor, t => t.Amount);
                """);

        var diagnostics = await Analyze(source);

        Assert.DoesNotContain(diagnostics, item => item.Id.StartsWith("GW_COVER_", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, item => item.Id == "GW_LINQ_112");
    }

    [Fact]
    public async Task Async_reduction_terminals_report_the_same_selector_and_index_failures()
    {
        var diagnostics = await Analyze(
            WithSchema(SchemaWithIndex("ix_status", "status ASC")) +
            QuerySource("""
                var unsupported = db.Table<Ticket>().MinAsync(executor, t => t.IsOpen);
                var uncovered = db.Table<Ticket>().Where(t => t.Status == status).SumAsync(executor, t => t.Amount);
                """));

        Assert.Contains(diagnostics, item => item.Id == "GW_LINQ_112");
        var coverage = Assert.Single(diagnostics.Where(item => item.Id == "GW_COVER_006"));
        Assert.Contains("reduction column", coverage.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reduction_terminals_reject_non_orderable_columns()
    {
        var diagnostics = await Analyze(
            WithSchema(SchemaWithIndex("ix_status", "status ASC")) +
            QuerySource("var result = db.Table<Ticket>().Min(t => t.IsOpen);"));

        Assert.Contains(diagnostics, item => item.Id == "GW_LINQ_112");
    }

    [Fact]
    public async Task Reduction_target_must_be_present_in_the_covering_index()
    {
        var diagnostics = await Analyze(
            WithSchema(SchemaWithIndex("ix_status", "status ASC")) +
            QuerySource("var result = db.Table<Ticket>().Where(t => t.Status == status).Sum(t => t.Amount);"));

        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "GW_COVER_006"));
        Assert.Contains("reduction column", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reduction_selector_casts_and_nullable_value_match_runtime_lowering()
    {
        var diagnostics = await Analyze(
            WithSchema(SchemaWithIndex("ix_status_amount", "status ASC, amount ASC")) +
            QuerySource("var casted = db.Table<Ticket>().Where(t => t.Status == status).Sum(t => (decimal)t.Amount.Value);"));

        Assert.DoesNotContain(diagnostics, item => item.Id is "GW_LINQ_112" or "GW_LINQ_113");
    }

    [Fact]
    public async Task Reduction_after_projection_is_rejected_like_runtime()
    {
        var diagnostics = await Analyze(
            WithSchema(SchemaWithIndex("ix_status_amount", "status ASC, amount ASC")) +
            QuerySource("var result = db.Table<Ticket>().Select(t => new { t.Amount }).Sum(t => t.Amount);"));

        Assert.Contains(diagnostics, item => item.Id == "GW_LINQ_112");
    }

    [Fact]
    public async Task Skip_without_take_is_rejected_in_static_resolution()
    {
        var diagnostics = await Analyze(
            WithSchema(SchemaWithIndex("ix_status", "status ASC")) +
            QuerySource("var result = db.Table<Ticket>().Skip(3).ToListAsync();"));

        Assert.Contains(diagnostics, item => item.Id == "GW_LINQ_113");
    }

    [Fact]
    public async Task Distinct_projection_without_a_covering_index_reports_the_projection_refusal()
    {
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_other", "other ASC")) +
            QuerySource("var result = db.Table<Ticket>().Where(t => t.Status == status).Select(t => new { t.Status }).Distinct().ToListAsync();"));

        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "GW_COVER_006"));
        Assert.Contains("not index-covered", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_without_an_order_reports_the_deterministic_order_refusal()
    {
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_status", "status ASC")) +
            QuerySource("var result = db.Table<Ticket>().FirstAsync();"));

        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "GW_COVER_016"));
        Assert.Contains("deterministic order", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ordered_first_is_covered_by_the_requested_order_index()
    {
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_created", "created_at ASC")) +
            QuerySource("var result = db.Table<Ticket>().OrderBy(t => t.CreatedAt).FirstAsync();"));

        Assert.DoesNotContain(diagnostics, item => item.Id.StartsWith("GW_COVER_", StringComparison.Ordinal));
    }

    /// <summary>
    /// The declared key is a coverage candidate. This schema declares an index on an unrelated
    /// column, so nothing but the key itself can cover the read.
    /// </summary>
    [Fact]
    public async Task Declared_key_equality_is_covered_without_a_declared_index()
    {
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_other", "other ASC")) + QuerySource("var result = db.Table<Ticket>().Where(t => t.Id == status).ToListAsync();"));

        Assert.DoesNotContain(diagnostics, item => item.Id.StartsWith("GW_COVER_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhereIf_enumerates_shapes_and_the_all_filters_absent_shape_fails()
    {
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_status", "status ASC")) + QuerySource("var result = db.Table<Ticket>().WhereIf(enabled, t => t.Status == status).ToListAsync();"));

        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "GW_COVER_005"));
        Assert.Contains("shape 1 of 2", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("all filters absent", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhereIf_with_an_index_for_each_shape_is_clean()
    {
        var diagnostics = await Analyze(WithSchema(
            SchemaWithIndex("ix_status", "status ASC", "ix_status_created", "status ASC, created_at DESC")) +
            QuerySource("var result = db.Table<Ticket>().Where(t => t.Status == status).WhereIf(enabled, t => t.CreatedAt >= from).ToListAsync();"));

        Assert.DoesNotContain(diagnostics, item => item.Id.StartsWith("GW_COVER_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reassignment_dataflow_enumerates_conditional_where()
    {
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_status", "status ASC")) + QueryInfrastructure + " public static class Reassignment { public static void Run() { var q = db.Table<Ticket>(); if (enabled) q = q.Where(t => t.Status == status); var result = q.ToListAsync(); } }");

        Assert.True(diagnostics.Any(item => item.Id == "GW_COVER_005"), string.Join(Environment.NewLine, diagnostics));
        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "GW_COVER_005"));
        Assert.Contains("shape 1 of 2", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reassignment_that_escapes_into_a_collection_is_unresolvable()
    {
        var source = WithSchema(SchemaWithIndex("ix_status", "status ASC")) + QueryInfrastructure +
                     " using System.Collections.Generic; public static class Escape { public static void Run() { var q = db.Table<Ticket>(); var values = new List<object>(); values.Add(q); var result = q.ToListAsync(); } }";

        var diagnostics = await Analyze(source);

        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "GW_COVER_900"));
        Assert.Contains("escapes", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task More_than_six_conditional_filters_are_unresolvable_by_the_blessed_bound()
    {
        var filters = string.Join("", Enumerable.Range(0, 7).Select(index => $".WhereIf(c{index}, t => t.Status == status)"));
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_status", "status ASC")) + QuerySource($"var result = db.Table<Ticket>(){filters}.ToListAsync();"));

        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "GW_COVER_900"));
        Assert.Contains("six", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Additional_file_schema_fallback_is_consumed()
    {
        var diagnostics = await Analyze(QuerySource("var result = db.Table<Ticket>().Where(t => t.Status == status).ToListAsync();"),
            new InMemoryAdditionalText("groundwork.schema.json", SchemaWithIndex("ix_other", "other ASC")));

        Assert.Contains(diagnostics, item => item.Id == "GW_COVER_006");
    }

    [Fact]
    public async Task Referenced_package_schema_metadata_is_consumed()
    {
        using var stream = new MemoryStream();
        var referenceCompilation = CSharpCompilation.Create(
            "SchemaPackage",
            [CSharpSyntaxTree.ParseText(WithSchema(SchemaWithIndex("ix_other", "other ASC")))],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.True(referenceCompilation.Emit(stream).Success);
        stream.Position = 0;

        var diagnostics = await Analyze(QuerySource("var result = db.Table<Ticket>().Where(t => t.Status == status).ToListAsync();"),
            references: [MetadataReference.CreateFromStream(stream)]);

        Assert.Contains(diagnostics, item => item.Id == "GW_COVER_006");
    }

    [Fact]
    public async Task Unknown_operation_on_a_groundwork_query_is_advisory_error_900()
    {
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_status", "status ASC")) +
            QueryInfrastructure.Replace(
                "public Query<T> Take(int count) => this;",
                "public Query<T> Take(int count) => this; public Query<T> Custom() => this;") +
            " public static class UnknownOperation { public static void Run() { var result = db.Table<Ticket>().Custom().ToListAsync(); } }");

        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "GW_COVER_900"));
        Assert.Contains("closed query surface", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unrelated_methods_with_the_same_terminal_name_are_ignored()
    {
        const string source = "public sealed class Client { public System.Threading.Tasks.Task ToListAsync() => System.Threading.Tasks.Task.CompletedTask; } public static class Use { public static void Run(Client client) { _ = client.ToListAsync(); } }";

        var diagnostics = await Analyze(source);

        Assert.DoesNotContain(diagnostics, item => item.Id.StartsWith("GW_COVER_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Linq_shaped_terminals_on_non_groundwork_tables_are_ignored()
    {
        const string usings = "using System.Collections.Generic; using System.Linq; ";
        const string foreign = "public sealed class Row { public string Status { get; set; } = \"\"; } " +
            "public sealed class Sheet { public IEnumerable<T> Table<T>() => new List<T>(); } " +
            "public static class Spreadsheet { public static List<Row> Run(Sheet sheet) { var rows = sheet.Table<Row>().Where(r => r.Status == \"open\").ToList(); _ = rows.Count(); _ = rows.Any(); return rows; } }";

        var bare = await Analyze(usings + foreign);
        var withSchema = await Analyze(usings + WithSchema(SchemaWithIndex("ix_status", "status ASC")) + foreign);

        Assert.DoesNotContain(bare, item => item.Id.StartsWith("GW_COVER_", StringComparison.Ordinal));
        Assert.DoesNotContain(withSchema, item => item.Id.StartsWith("GW_COVER_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plain_linq_over_a_materialized_schema_row_collection_is_ignored()
    {
        const string usings = "using System.Collections.Generic; using System.Linq; ";
        const string members = "[GwTable(\"tickets\")] public sealed class Ticket { public string Status { get; set; } = \"\"; } " +
            "public sealed class Db { public Rows<T> Table<T>() => new Rows<T>(); } " +
            "public sealed class Rows<T> { public Rows<T> Where(System.Func<T, bool> predicate) => this; public List<T> ToList() => new List<T>(); } " +
            "public static class Report { public static int Run(Db db, string status) { var rows = db.Table<Ticket>().Where(t => t.Status == status).ToList(); return rows.Count(t => t.Status == status); } }";

        var diagnostics = await Analyze(usings + WithSchema(SchemaWithIndex("ix_status", "status ASC")) + members);

        Assert.DoesNotContain(diagnostics, item => item.Id.StartsWith("GW_COVER_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Non_generic_query_facades_are_still_analyzed()
    {
        const string members = "[GwTable(\"tickets\")] public sealed class Ticket { public string Status { get; set; } = \"\"; } " +
            "public sealed class Db { public TicketQuery Table<T>() => new TicketQuery(); } " +
            "public sealed class TicketQuery { public TicketQuery Where(System.Func<Ticket, bool> predicate) => this; public System.Threading.Tasks.Task ToListAsync() => System.Threading.Tasks.Task.CompletedTask; } " +
            "public static class Report { public static void Run(Db db, string status) { _ = db.Table<Ticket>().Where(t => t.Status == status).ToListAsync(); } }";

        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_other", "other ASC")) + members);

        Assert.Contains(diagnostics, item => item.Id == "GW_COVER_006");
    }

    [Fact]
    public async Task Unresolvable_reassignment_has_a_working_where_if_code_fix()
    {
        var source = WithSchema(SchemaWithIndex("ix_status", "status ASC")) + QueryInfrastructure +
                     " public static class Reassignment { public static void Run() { var q = db.Table<Ticket>(); for (var i = 0; i < 1; i++) if (enabled) q = q.Where(t => t.Status == status); var result = q.ToListAsync(); } }";
        var allDiagnostics = await Analyze(source);
        Assert.True(allDiagnostics.Any(item => item.Id == "GW_COVER_900"), string.Join(Environment.NewLine, allDiagnostics));
        var diagnostic = Assert.Single(allDiagnostics.Where(item => item.Id == "GW_COVER_900"));

        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("CodeFix", LanguageNames.CSharp);
        var document = project.AddDocument("Query.cs", SourceText.From(source));
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
        await new CoverageCodeFixProvider().RegisterCodeFixesAsync(context);

        Assert.True(actions.Count > 0, $"span={diagnostic.Location.SourceSpan}; text={source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length)}");
        var action = Assert.Single(actions);
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        var apply = Assert.Single(operations.OfType<ApplyChangesOperation>());
        var changed = apply.ChangedSolution.GetDocument(document.Id)!;
        var rewritten = (await changed.GetTextAsync()).ToString();
        Assert.Contains("q = q.WhereIf(enabled, t => t.Status == status);", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Large_compilation_analysis_stays_within_the_bounded_editor_budget()
    {
        var queries = string.Join(Environment.NewLine, Enumerable.Range(0, 500).Select(index =>
            $"public static void Query{index}() {{ var result = db.Table<Ticket>().Where(t => t.Status == status).ToListAsync(); }}"));
        var source = WithSchema(SchemaWithIndex("ix_status", "status ASC")) + QueryInfrastructure +
                     " public static class LargeQuerySurface { " + queries + " }";
        var stopwatch = Stopwatch.StartNew();

        var diagnostics = await Analyze(source);

        stopwatch.Stop();
        Assert.DoesNotContain(diagnostics, item => item.Id.StartsWith("GW_COVER_", StringComparison.Ordinal));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"500 query sites took {stopwatch.Elapsed.TotalMilliseconds:F0} ms to analyze.");
    }

    [Fact]
    public async Task AcceptScan_uncovered_query_emits_inventory_with_reason_and_owner()
    {
        var source = WithSchema(SchemaWithIndex("ix_other", "other ASC"), allowAcceptedScans: true) +
                     QuerySource("var result = db.Table<Ticket>().Where(t => t.Status.Contains(\"open\")).AcceptScan(\"GW-SCAN-0007\", reason: \"admin report\", owner: \"billing\", expiresOn: \"2027-01-01\").ToListAsync();");

        var diagnostics = await Analyze(source, now: new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));

        var inventory = Assert.Single(diagnostics.Where(item => item.Id == "GW_COVER_905"));
        Assert.Contains("GW-SCAN-0007", inventory.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("admin report", inventory.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("billing", inventory.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, item => item.Id == "GW_COVER_006");
    }

    [Fact]
    public async Task AcceptScan_on_covered_query_is_a_build_error()
    {
        var source = WithSchema(SchemaWithIndex("ix_status", "status ASC"), allowAcceptedScans: true) +
                     QuerySource("var result = db.Table<Ticket>().Where(t => t.Status == status).AcceptScan(\"GW-SCAN-0007\", \"admin report\", \"billing\", \"2027-01-01\").ToListAsync();");

        var diagnostics = await Analyze(source, now: new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Contains(diagnostics, item => item.Id == "GW_COVER_901");
    }

    [Fact]
    public async Task AcceptScan_without_assembly_opt_in_is_a_build_error()
    {
        var source = WithSchema(SchemaWithIndex("ix_other", "other ASC")) +
                     QuerySource("var result = db.Table<Ticket>().Where(t => t.Status.Contains(\"open\")).AcceptScan(\"GW-SCAN-0007\", \"admin report\", \"billing\", \"2027-01-01\").ToListAsync();");

        var diagnostics = await Analyze(source, now: new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Contains(diagnostics, item => item.Id == "GW_COVER_902");
        Assert.DoesNotContain(diagnostics, item => item.Id == "GW_COVER_006");
    }

    [Fact]
    public async Task AcceptScan_expiry_warns_then_errors_at_the_expiry_date()
    {
        var source = WithSchema(SchemaWithIndex("ix_other", "other ASC"), allowAcceptedScans: true) +
                     QuerySource("var result = db.Table<Ticket>().Where(t => t.Status.Contains(\"open\")).AcceptScan(\"GW-SCAN-0007\", \"admin report\", \"billing\", \"2027-01-01\").ToListAsync();");

        var warning = await Analyze(source, now: new DateTimeOffset(2026, 12, 15, 0, 0, 0, TimeSpan.Zero));
        Assert.Contains(warning, item => item.Id == "GW_COVER_904" && item.Severity == DiagnosticSeverity.Warning);
        Assert.Contains(warning, item => item.Id == "GW_COVER_905" && item.Severity == DiagnosticSeverity.Info);

        var error = await Analyze(source, now: new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Contains(error, item => item.Id == "GW_COVER_903" && item.Severity == DiagnosticSeverity.Error);
        Assert.Contains(error, item => item.Id == "GW_COVER_905" && item.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public async Task Accepted_aggregation_emits_inventory_and_expiry_diagnostics()
    {
        var source = WithSchema("{\"tables\":[]}", allowAcceptedAggregations: true) +
                     "class Use { static readonly Groundwork.Kernel.AggregationAcceptance Value = Groundwork.Kernel.AggregationAcceptance.Allow(\"GW-AGG-0007\", \"admin report\", \"billing\", new System.DateTimeOffset(2027, 1, 1, 0, 0, 0, System.TimeSpan.Zero), 20, 200); }";

        var warning = await Analyze(source, now: new DateTimeOffset(2026, 12, 15, 0, 0, 0, TimeSpan.Zero));
        var inventory = Assert.Single(warning.Where(item => item.Id == "GW_AGG_ADHOC_905"));
        Assert.Contains("GW-AGG-0007", inventory.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("admin report", inventory.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("billing", inventory.GetMessage(), StringComparison.Ordinal);
        Assert.Contains(warning, item => item.Id == "GW_AGG_ADHOC_904" && item.Severity == DiagnosticSeverity.Warning);

        var expired = await Analyze(source, now: new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Contains(expired, item => item.Id == "GW_AGG_ADHOC_903" && item.Severity == DiagnosticSeverity.Error);
        Assert.Contains(expired, item => item.Id == "GW_AGG_ADHOC_905" && item.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public async Task Accepted_aggregation_without_assembly_opt_in_is_a_build_error()
    {
        var source = WithSchema("{\"tables\":[]}") +
                     "class Use { static readonly Groundwork.Kernel.AggregationAcceptance Value = Groundwork.Kernel.AggregationAcceptance.Allow(\"GW-AGG-0008\", \"admin report\", \"billing\", new System.DateTimeOffset(2027, 1, 1, 0, 0, 0, System.TimeSpan.Zero), 20, 200); }";

        var diagnostics = await Analyze(source, now: new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Contains(diagnostics, item => item.Id == "GW_AGG_ADHOC_902");
        Assert.DoesNotContain(diagnostics, item => item.Id == "GW_AGG_ADHOC_905");
    }

    [Fact]
    public async Task Accepted_aggregation_resolves_const_and_reordered_named_arguments()
    {
        var source = WithSchema("{\"tables\":[]}", allowAcceptedAggregations: true) +
                     "static class Values { public const string Id = \"GW-AGG-0009\"; public const string Reason = \"admin report\"; public const string Owner = \"billing\"; public const int Groups = 20; public const int InputRows = 200; } class Use { static readonly Groundwork.Kernel.AggregationAcceptance Value = Groundwork.Kernel.AggregationAcceptance.Allow(maxInputRows: Values.InputRows, owner: Values.Owner, id: Values.Id, maxGroups: Values.Groups, reason: Values.Reason, expiresOn: new System.DateTimeOffset(day: 1, month: 1, year: 2027, hour: 0, minute: 0, second: 0, offset: System.TimeSpan.Zero)); }";

        var diagnostics = await Analyze(source, now: new DateTimeOffset(2026, 12, 15, 0, 0, 0, TimeSpan.Zero));

        var inventory = Assert.Single(diagnostics.Where(item => item.Id == "GW_AGG_ADHOC_905"));
        Assert.Contains("GW-AGG-0009", inventory.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("maxGroups='20'", inventory.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("maxInputRows='200'", inventory.GetMessage(), StringComparison.Ordinal);
        Assert.Contains(diagnostics, item => item.Id == "GW_AGG_ADHOC_904");
        Assert.DoesNotContain(diagnostics, item => item.Id == "GW_AGG_ADHOC_906");
    }

    [Fact]
    public async Task Accepted_aggregation_resolves_mixed_named_and_positional_arguments()
    {
        var source = WithSchema("{\"tables\":[]}", allowAcceptedAggregations: true) +
                     "class Use { static readonly Groundwork.Kernel.AggregationAcceptance Value = Groundwork.Kernel.AggregationAcceptance.Allow(\"GW-AGG-0010\", reason: \"admin report\", \"billing\", new System.DateTimeOffset(2027, 1, 1, 0, 0, 0, System.TimeSpan.Zero), 20, 200); }";

        var diagnostics = await Analyze(source, now: new DateTimeOffset(2026, 12, 15, 0, 0, 0, TimeSpan.Zero));

        var inventory = Assert.Single(diagnostics.Where(item => item.Id == "GW_AGG_ADHOC_905"));
        Assert.Contains("GW-AGG-0010", inventory.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("maxGroups='20'", inventory.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("maxInputRows='200'", inventory.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, item => item.Id == "GW_AGG_ADHOC_906");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Accepted_aggregation_duplicate_id_does_not_suppress_expiry_diagnostics(bool expiredFirst)
    {
        const string expired = "static readonly Groundwork.Kernel.AggregationAcceptance Expired = Groundwork.Kernel.AggregationAcceptance.Allow(\"GW-AGG-0014\", \"shared report\", \"billing\", new System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero), 20, 200);";
        const string active = "static readonly Groundwork.Kernel.AggregationAcceptance Active = Groundwork.Kernel.AggregationAcceptance.Allow(\"GW-AGG-0014\", \"shared report\", \"billing\", new System.DateTimeOffset(2027, 1, 1, 0, 0, 0, System.TimeSpan.Zero), 20, 200);";
        var source = WithSchema("{\"tables\":[]}", allowAcceptedAggregations: true) +
                     "class Use { " + (expiredFirst ? expired + active : active + expired) + " }";

        var diagnostics = await Analyze(source, now: new DateTimeOffset(2026, 12, 15, 0, 0, 0, TimeSpan.Zero));

        Assert.Contains(diagnostics, item => item.Id == "GW_AGG_ADHOC_903" && item.Severity == DiagnosticSeverity.Error);
        Assert.Equal(2, diagnostics.Count(item => item.Id == "GW_AGG_ADHOC_905"));
    }

    [Fact]
    public async Task Accepted_aggregation_duplicate_id_with_conflicting_metadata_is_fully_inventoried()
    {
        var source = WithSchema("{\"tables\":[]}", allowAcceptedAggregations: true) +
                     "class Use { static readonly Groundwork.Kernel.AggregationAcceptance Billing = Groundwork.Kernel.AggregationAcceptance.Allow(\"GW-AGG-0015\", \"shared report\", \"billing\", new System.DateTimeOffset(2027, 1, 1, 0, 0, 0, System.TimeSpan.Zero), 20, 200); static readonly Groundwork.Kernel.AggregationAcceptance Operations = Groundwork.Kernel.AggregationAcceptance.Allow(\"GW-AGG-0015\", \"shared report\", \"operations\", new System.DateTimeOffset(2027, 1, 1, 0, 0, 0, System.TimeSpan.Zero), 30, 300); }";

        var diagnostics = await Analyze(source, now: new DateTimeOffset(2026, 12, 15, 0, 0, 0, TimeSpan.Zero));

        var inventory = diagnostics.Where(item => item.Id == "GW_AGG_ADHOC_905").ToArray();
        Assert.Equal(2, inventory.Length);
        Assert.Contains(inventory, item => item.GetMessage().Contains("owner='billing'", StringComparison.Ordinal) && item.GetMessage().Contains("maxGroups='20'", StringComparison.Ordinal));
        Assert.Contains(inventory, item => item.GetMessage().Contains("owner='operations'", StringComparison.Ordinal) && item.GetMessage().Contains("maxGroups='30'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Accepted_aggregation_with_persian_calendar_expiry_fails_closed()
    {
        var source = WithSchema("{\"tables\":[]}", allowAcceptedAggregations: true) +
                     "class Use { static readonly Groundwork.Kernel.AggregationAcceptance Value = Groundwork.Kernel.AggregationAcceptance.Allow(\"GW-AGG-0011\", \"admin report\", \"billing\", new System.DateTimeOffset(1405, 1, 1, 0, 0, 0, 0, new System.Globalization.PersianCalendar(), System.TimeSpan.Zero), 20, 200); }";

        var diagnostics = await Analyze(source, now: new DateTimeOffset(2026, 12, 15, 0, 0, 0, TimeSpan.Zero));

        Assert.Contains(diagnostics, item => item.Id == "GW_AGG_ADHOC_906" && item.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, item => item.Id == "GW_AGG_ADHOC_905");
    }

    [Fact]
    public async Task Accepted_aggregation_with_unresolved_calendar_expiry_fails_closed()
    {
        var source = WithSchema("{\"tables\":[]}", allowAcceptedAggregations: true) +
                     "class Use { static System.Globalization.Calendar Calendar => GetCalendar(); static System.Globalization.Calendar GetCalendar() => null!; static readonly Groundwork.Kernel.AggregationAcceptance Value = Groundwork.Kernel.AggregationAcceptance.Allow(\"GW-AGG-0012\", \"admin report\", \"billing\", new System.DateTimeOffset(2027, 1, 1, 0, 0, 0, 0, Calendar, System.TimeSpan.Zero), 20, 200); }";

        var diagnostics = await Analyze(source, now: new DateTimeOffset(2026, 12, 15, 0, 0, 0, TimeSpan.Zero));

        Assert.Contains(diagnostics, item => item.Id == "GW_AGG_ADHOC_906" && item.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, item => item.Id == "GW_AGG_ADHOC_905");
    }

    [Fact]
    public async Task Accepted_aggregation_with_runtime_id_fails_closed_instead_of_bypassing_opt_in()
    {
        var source = WithSchema("{\"tables\":[]}") +
                     "static class Values { static string Id => \"GW-AGG-0013\"; } class Use { static readonly Groundwork.Kernel.AggregationAcceptance Value = Groundwork.Kernel.AggregationAcceptance.Allow(Values.Id, \"admin report\", \"billing\", new System.DateTimeOffset(2027, 1, 1, 0, 0, 0, System.TimeSpan.Zero), 20, 200); }";

        var diagnostics = await Analyze(source, now: new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Contains(diagnostics, item => item.Id == "GW_AGG_ADHOC_902");
        Assert.Contains(diagnostics, item => item.Id == "GW_AGG_ADHOC_906" && item.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, item => item.Id == "GW_AGG_ADHOC_905");
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(
        string source,
        AdditionalText? additional = null,
        IEnumerable<MetadataReference>? references = null,
        DateTimeOffset? now = null)
    {
        var compilation = CSharpCompilation.Create(
            "CoverageInput",
            [CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8))],
            References().Concat(references ?? Array.Empty<MetadataReference>()),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var options = new AnalyzerOptions(additional is null
            ? ImmutableArray<AdditionalText>.Empty
            : [additional]);
        var result = await compilation.WithAnalyzers(
            [now is null ? new CoverageAnalyzer() : new CoverageAnalyzer(() => now.Value)],
            new CompilationWithAnalyzersOptions(options, onAnalyzerException: null, concurrentAnalysis: false, logAnalyzerExecutionTime: true))
            .GetAnalyzerDiagnosticsAsync();
        return result;
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeLinq(string source, IEnumerable<MetadataReference>? references = null)
    {
        var compilation = CSharpCompilation.Create(
            "LinqInput",
            [CSharpSyntaxTree.ParseText(SourceText.From(NormalizeLinqSource(source), Encoding.UTF8))],
            References().Concat(references ?? Array.Empty<MetadataReference>()),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return await compilation.WithAnalyzers(
            [new LinqAnalyzer()],
            new CompilationWithAnalyzersOptions(
                new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty),
                onAnalyzerException: null,
                concurrentAnalysis: false,
                logAnalyzerExecutionTime: true)).GetAnalyzerDiagnosticsAsync();
    }

    private static string NormalizeLinqSource(string source)
    {
        const string marker = "namespace Groundwork.Query.Linq {";
        while (true)
        {
            var start = source.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) break;
            var open = source.IndexOf('{', start);
            if (open < 0) break;
            var depth = 0;
            var end = open;
            for (; end < source.Length; end++)
            {
                if (source[end] == '{') depth++;
                else if (source[end] == '}' && --depth == 0) break;
            }
            var length = end - start + 1;
            source = source[..start] + new string(source.Substring(start, length).Select(character => character is '\r' or '\n' ? character : ' ').ToArray()) + source[(start + length)..];
        }
        return source;
    }

    private static IEnumerable<MetadataReference> References()
    {
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return paths
            .Concat([typeof(GroundworkSchemaAttribute).Assembly.Location])
            .Concat([typeof(ScanAcceptance).Assembly.Location])
            .Concat([typeof(AggregationAcceptance).Assembly.Location])
            .Concat([typeof(GwQueryTable<>).Assembly.Location])
            .Concat([typeof(ExternalFragments).Assembly.Location])
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
    }

    private static string WithSchema(
        string schema,
        bool allowAcceptedScans = false,
        bool allowAcceptedAggregations = false)
    {
        var document = GroundworkSchemaCanonical.Parse(schema);
        var fingerprint = GroundworkSchemaCanonical.Fingerprint(document);
        return "using Groundwork.Schema; using Groundwork.Query.Model; " +
               (allowAcceptedScans ? "[assembly: GwAllowAcceptedScans] " : string.Empty) +
               (allowAcceptedAggregations ? "[assembly: Groundwork.Kernel.GwAllowAcceptedAggregations] " : string.Empty) +
               "[assembly: GroundworkSchema(" + Literal(schema) + ", " + Literal(fingerprint) + ")]\n";
    }

    private static string QuerySource(string query) => "#nullable enable\n" + QueryInfrastructure + "\n" + query + "\n";

    private static string SchemaWithIndex(params string[] values)
    {
        var indexes = new StringBuilder();
        for (var index = 0; index < values.Length; index += 2)
        {
            if (index != 0) indexes.Append(',');
            indexes.Append("{\"name\":\"").Append(values[index]).Append("\",\"columns\":[");
            var columns = values[index + 1].Split(',');
            for (var column = 0; column < columns.Length; column++)
            {
                if (column != 0) indexes.Append(',');
                var parts = columns[column].Trim().Split(' ');
                indexes.Append("{\"name\":\"").Append(parts[0]).Append("\",\"descending\":").Append(parts[1] == "DESC" ? "true" : "false").Append('}');
            }
            indexes.Append("],\"includeNulls\":true,\"unique\":false}");
        }

        return "{\"tables\":[{\"name\":\"tickets\",\"columns\":[" +
            "{\"name\":\"id\",\"type\":\"String\",\"nullable\":false}," +
            "{\"name\":\"status\",\"type\":\"String\",\"nullable\":true}," +
            "{\"name\":\"other\",\"type\":\"String\",\"nullable\":true}," +
            "{\"name\":\"created_at\",\"type\":\"DateTimeOffset\",\"nullable\":false}," +
            "{\"name\":\"amount\",\"type\":\"Decimal\",\"nullable\":true}," +
            "{\"name\":\"is_open\",\"type\":\"Boolean\",\"nullable\":false}]," +
            "\"key\":[\"id\"],\"indexes\":[" + indexes + "]}]}";
    }

    private static string Literal(string value) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);

    private const string QueryInfrastructure = """
        using System;
        using System.Threading.Tasks;
        using Groundwork.Schema;
        using static QueryHost;
        [GwTable("tickets")] public sealed class Ticket { public string Id { get; set; } = ""; public string Status { get; set; } = ""; public string Other { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } public decimal? Amount { get; set; } public bool IsOpen { get; set; } }
        public sealed class Db { public Query<T> Table<T>() => new Query<T>(); }
        public sealed class Query<T>
        {
            public Query<T> Where(Func<T, bool> predicate) => this;
            public Query<T> WhereIf(bool condition, Func<T, bool> predicate) => this;
            public Query<T> AcceptScan(string id, string reason, string owner, string expiresOn) => this;
            public Query<T> OrderBy<TKey>(Func<T, TKey> selector) => this;
            public Query<T> OrderByDescending<TKey>(Func<T, TKey> selector) => this;
            public Query<T> ThenBy<TKey>(Func<T, TKey> selector) => this;
            public Query<T> ThenByDescending<TKey>(Func<T, TKey> selector) => this;
            public Query<T> Skip(int count) => this;
            public Query<T> Take(int count) => this;
            public Query<TResult> Select<TResult>(Func<T, TResult> selector) => new Query<TResult>();
            public Query<T> Distinct() => this;
            public decimal Sum(Func<T, decimal?> selector) => 0;
            public DateTimeOffset Min(Func<T, DateTimeOffset> selector) => default;
            public decimal? Max(Func<T, decimal?> selector) => null;
            public Task First() => Task.CompletedTask;
            public Task FirstOrDefault() => Task.CompletedTask;
            public Task FirstAsync() => Task.CompletedTask;
            public Task FirstOrDefaultAsync() => Task.CompletedTask;
            public Task Single() => Task.CompletedTask;
            public Task SingleOrDefault() => Task.CompletedTask;
            public Task SingleAsync() => Task.CompletedTask;
            public Task SingleOrDefaultAsync() => Task.CompletedTask;
            public Task ToListAsync() => Task.CompletedTask;
        }
        public static class QueryHost { public static Db db = new Db(); public static object executor = new object(); public static bool enabled; public static bool c0, c1, c2, c3, c4, c5, c6; public static string status = "open"; public const string term = "open"; public static DateTimeOffset from; }
        """;

    private sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(content);
    }
}
