using Laplace.Decomposers.Model;
using Xunit;

namespace Laplace.Decomposers.Model.Tests;

/// <summary>
/// A recipe that names an operator must get that operator in the emitted tensors.
///
/// RecipeDescriptor.Parse used to infer `compile` from `lm_head`:
///     compile = lmHead.Key == "trajectory" ? "continuation" : "full";
/// and lm_head itself defaults to "trajectory". So a recipe that explicitly declared
/// relation:IS_A heads and said nothing about `compile` -- a field with no visible
/// connection to those heads -- selected continuation mode, and FoundryCommands then
/// applied OpAttnScale = OpResidScale = 0 to every operator outside the whitelist
/// (context, trajectory, sentence_order, relation:PRECEDES).
///
/// The planes were still read and their edge counts still printed, so the census said the
/// capability was present while the artifact had it removed. Continuation-only must be
/// requested, never inferred.
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

        // The condition that used to flip this on is still present and still true.
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
