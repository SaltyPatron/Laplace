using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Model;

public readonly record struct ArenaSlot(
    string Role,
    int Layer,
    Hash128 KindId,
    string TensorName,
    string InSpace,
    string OutSpace,
    bool RowsAreOut,
    bool IsNorm);

public static class ModelArenaPlan
{
    public static Hash128 BaseKindId(string role) => RelationTypeRegistry.RelationTypeId(role);

    public static Hash128 LayerKindId(string role, int layer) =>
        Hash128.OfCanonical($"substrate/kind/{role}/L{layer}/v1");

    public static IEnumerable<(string Role, int Layer, Hash128 KindId)> AllKinds(
        LlamaRecipeExtractor.RecipeInfo recipe, ArchitectureProfile prof)
    {
        foreach (var s in Slots(recipe, prof))
            yield return (s.Role, s.Layer, s.KindId);
    }

    public static IEnumerable<ArenaSlot> Slots(
        LlamaRecipeExtractor.RecipeInfo recipe, ArchitectureProfile prof)
    {
        yield return new ArenaSlot("EMBEDS", -1, BaseKindId("EMBEDS"),
            prof.EmbedTokens, "TOKEN", "channel", false, false);
        yield return new ArenaSlot("OUTPUT_PROJECTS", -1, BaseKindId("OUTPUT_PROJECTS"),
            prof.LmHead ?? prof.EmbedTokens, "channel", "TOKEN", true, false);

        for (int l = 0; l < recipe.NumLayers; l++)
        {
            yield return new ArenaSlot("Q_PROJECTS", l, LayerKindId("Q_PROJECTS", l),
                ArchitectureProfile.Layer(prof.QProj, l), "channel", "attn_dim", true, false);
            yield return new ArenaSlot("K_PROJECTS", l, LayerKindId("K_PROJECTS", l),
                ArchitectureProfile.Layer(prof.KProj, l), "channel", "kv_dim", true, false);
            yield return new ArenaSlot("V_PROJECTS", l, LayerKindId("V_PROJECTS", l),
                ArchitectureProfile.Layer(prof.VProj, l), "channel", "kv_dim", true, false);
            yield return new ArenaSlot("O_PROJECTS", l, LayerKindId("O_PROJECTS", l),
                ArchitectureProfile.Layer(prof.OProj, l), "attn_dim", "channel", true, false);
            if (prof.GateProj is not null)
                yield return new ArenaSlot("GATES", l, LayerKindId("GATES", l),
                    ArchitectureProfile.Layer(prof.GateProj, l), "channel", "neuron", true, false);
            yield return new ArenaSlot("UP_PROJECTS", l, LayerKindId("UP_PROJECTS", l),
                ArchitectureProfile.Layer(prof.UpProj, l), "channel", "neuron", true, false);
            yield return new ArenaSlot("DOWN_PROJECTS", l, LayerKindId("DOWN_PROJECTS", l),
                ArchitectureProfile.Layer(prof.DownProj, l), "neuron", "channel", true, false);
        }

        for (int l = 0; l < recipe.NumLayers; l++)
            for (int t = 0; t < prof.PerLayerNorms.Count; t++)
                yield return new ArenaSlot($"NORM_SCALES.{t}", l, LayerKindId($"NORM_SCALES.{t}", l),
                    ArchitectureProfile.Layer(prof.PerLayerNorms[t], l), "channel", "", false, true);
        yield return new ArenaSlot("NORM_SCALES.final", -1, LayerKindId("NORM_SCALES.final", 0),
            prof.FinalNorm, "channel", "", false, true);
    }
}
