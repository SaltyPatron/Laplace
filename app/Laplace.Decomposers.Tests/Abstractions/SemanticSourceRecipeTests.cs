using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Tests.Abstractions;

public sealed class SemanticSourceRecipeTests
{
    [Fact]
    public void Recipe_identity_is_semantic_and_independent_of_field_declaration_order()
    {
        SourceRecipeField first = new(
            "row/@name", "Name", SourceValueKind.Text,
            SourceFieldDisposition.Content | SourceFieldDisposition.Testimony);
        SourceRecipeField second = new(
            "row/@kind", "Kind", SourceValueKind.Enumerated,
            SourceFieldDisposition.Testimony);

        var left = new SemanticSourceRecipe(
            "fixture", "1", "tree-sitter/fixture@1", "fixture", [first, second]);
        var right = new SemanticSourceRecipe(
            "fixture", "1", "tree-sitter/fixture@1", "fixture", [second, first]);

        Assert.Equal(left.CanonicalForm, right.CanonicalForm);
        Assert.Equal(left.RecipeId, right.RecipeId);
    }

    [Fact]
    public void Recipe_rejects_silent_or_ambiguous_field_dispositions()
    {
        Assert.Throws<ArgumentException>(() => new SemanticSourceRecipe(
            "fixture", "1", "provider", "syntax",
            [new SourceRecipeField(
                "row/@value", "Value", SourceValueKind.Text,
                SourceFieldDisposition.None)]));

        Assert.Throws<ArgumentException>(() => new SemanticSourceRecipe(
            "fixture", "1", "provider", "syntax",
            [new SourceRecipeField(
                "row/@value", "Value", SourceValueKind.Text,
                SourceFieldDisposition.Excluded | SourceFieldDisposition.Content)]));
    }

    [Fact]
    public void Semantic_default_is_distinct_from_absence_and_changes_recipe_identity()
    {
        SourceRecipeField compacted = new(
            "row/@enabled", "Enabled", SourceValueKind.Boolean,
            SourceFieldDisposition.Testimony,
            AbsentSentinel: "#",
            DefaultValue: "N",
            OmitDefaultTestimony: true);
        SourceRecipeField ordinary = compacted with
        {
            DefaultValue = null,
            OmitDefaultTestimony = false,
        };

        var sparse = new SemanticSourceRecipe(
            "fixture", "1", "provider", "syntax", [compacted]);
        var dense = new SemanticSourceRecipe(
            "fixture", "1", "provider", "syntax", [ordinary]);
        Assert.NotEqual(sparse.RecipeId, dense.RecipeId);

        var target = new CapturingTarget();
        var interpreter = new LaplaceRecipeInterpreter<string>(sparse);
        interpreter.Lower(
            new SourceRecipeAssertion<string>("subject", "row/@enabled", "N"),
            target);
        SourceRecipeValue value = Assert.Single(target.Values);
        Assert.False(value.IsAbsent);
        Assert.True(value.IsDefault);
        Assert.False(value.Boolean);
    }

    [Fact]
    public void Cookbook_resolves_exact_generations_and_reports_schema_drift()
    {
        var v1 = new SemanticSourceRecipe(
            "ISO/example", "1", "tree-sitter/csv@1", "rows/v1",
            [new SourceRecipeField(
                "row/name", "Name", SourceValueKind.Text,
                SourceFieldDisposition.Content | SourceFieldDisposition.Testimony)]);
        var v2 = new SemanticSourceRecipe(
            "ISO/example", "2", "tree-sitter/csv@1", "rows/v2",
            [
                new SourceRecipeField(
                    "row/name", "Name", SourceValueKind.StructuredText,
                    SourceFieldDisposition.Content | SourceFieldDisposition.Testimony),
                new SourceRecipeField(
                    "row/status", "Status", SourceValueKind.Enumerated,
                    SourceFieldDisposition.Testimony),
            ]);
        var cookbook = new LaplaceCookbook();
        cookbook.Register(v2);
        cookbook.Register(v1);
        cookbook.Register(v1);

        Assert.Equal(v1.RecipeId, cookbook.Resolve(new LaplaceRecipeKey(
            "ISO/example", "1", "tree-sitter/csv@1", "rows/v1")).RecipeId);
        Assert.Equal([v1.RecipeId, v2.RecipeId],
            cookbook.Recipes.Select(static recipe => recipe.RecipeId));

        LaplaceRecipeSchemaReport report = LaplaceCookbook.ValidateProviderSchema(
            v2, ["row/name", "row/unmapped"]);
        Assert.Equal(["row/unmapped"], report.UnknownProviderFields);
        Assert.Equal(["row/status"], report.MissingRecipeFields);
        Assert.False(report.IsComplete);

        IReadOnlyList<LaplaceRecipeFieldChange> changes = LaplaceCookbook.Diff(v1, v2);
        Assert.Collection(
            changes,
            changed =>
            {
                Assert.Equal("row/name", changed.SyntaxPath);
                Assert.Equal(LaplaceRecipeFieldChangeKind.Changed, changed.Kind);
            },
            added =>
            {
                Assert.Equal("row/status", added.SyntaxPath);
                Assert.Equal(LaplaceRecipeFieldChangeKind.Added, added.Kind);
            });
    }

    [Fact]
    public void Interpreter_preserves_exact_values_and_supplies_typed_views()
    {
        var recipe = new SemanticSourceRecipe(
            "fixture", "1", "provider", "syntax",
            [
                new SourceRecipeField(
                    "row/@enabled", "Enabled", SourceValueKind.Boolean,
                    SourceFieldDisposition.Testimony),
                new SourceRecipeField(
                    "row/@ordinal", "Ordinal", SourceValueKind.Integer,
                    SourceFieldDisposition.Calculation),
                new SourceRecipeField(
                    "row/@members", "Members", SourceValueKind.CodepointSequence,
                    SourceFieldDisposition.Reference | SourceFieldDisposition.Trajectory,
                    AbsentSentinel: "#", SequenceSeparator: " "),
            ]);
        var target = new CapturingTarget();
        var interpreter = new LaplaceRecipeInterpreter<string>(recipe);

        interpreter.LowerBatch(
            [
                new SourceRecipeAssertion<string>("subject", "row/@enabled", "N"),
                new SourceRecipeAssertion<string>("subject", "row/@ordinal", "9007199254740993"),
                new SourceRecipeAssertion<string>("subject", "row/@members", "0041 0301"),
            ],
            target);

        Assert.False(target.Values[0].Boolean);
        Assert.Equal("9007199254740993", target.Values[1].Raw);
        Assert.Equal(System.Numerics.BigInteger.Parse("9007199254740993"), target.Values[1].Integer);
        Assert.Equal(["0041", "0301"], target.Values[2].Sequence);
        Assert.Throws<KeyNotFoundException>(() => interpreter.Lower(
            new SourceRecipeAssertion<string>("subject", "row/@silent", "lost"), target));
    }

    private sealed class CapturingTarget : ILaplaceRecipeLoweringTarget<string>
    {
        internal List<SourceRecipeValue> Values { get; } = [];

        public void Lower(
            in SourceRecipeAssertion<string> assertion,
            SourceRecipeField field,
            in SourceRecipeValue value) => Values.Add(value);
    }
}
