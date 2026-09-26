using Laplace.Decomposers.Model;
using Xunit;

namespace Laplace.Decomposers.Model.Tests;

/// <summary>
/// A recipe that names an operator gets that operator in the emitted tensors.
/// Continuation compile (which zeroes every operator outside the continuation set) is
/// selected only by an explicit <c>compile</c> field, never inferred from <c>lm_head</c>.
/// </summary>
public sealed class RecipeCompileDefaultTests
{
    private const string KnowledgeRecipe = """
{
  "kind": "laplace.recipe",
  "name": "knowledge",
  "hidden_size": 256,
  "layers": [
    { "heads": [ { "op": "relation", "type": "IS_A" },
                 { "op": "relation", "type": "HAS_PROPERTY" } ],
      "ffn": { "op": "relation", "type": "IS_SYNONYM_OF" } }
  ],
  "vocab": { "source": "substrate", "size": 32 }
}
""";

    [Fact]
    public void OmittedCompile_DoesNotSilentlyDisableDeclaredOperators()
    {
        var desc = RecipeDescriptor.Parse(KnowledgeRecipe);

        // lm_head defaults to "trajectory"; that alone does not select continuation.
        Assert.Equal("trajectory", desc.LmHead.Key);
        Assert.False(
            desc.ContinuationCompile,
            "omitting `compile` must not select continuation mode: FoundryCommands zeroes "
            + "every non-continuation operator, so the declared IS_A/HAS_PROPERTY heads "
            + "would be read, counted and then contribute nothing to the tensors");
    }

    [Fact]
    public void ContinuationCompile_IsStillHonouredWhenAskedForExplicitly()
    {
        var desc = RecipeDescriptor.Parse(
            KnowledgeRecipe.Replace("\"name\": \"knowledge\",",
                                    "\"name\": \"knowledge\", \"compile\": \"continuation\","));
        Assert.True(desc.ContinuationCompile);
    }
}
