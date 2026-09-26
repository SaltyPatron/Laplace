using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.SemLink;

/// <summary>
/// A FrameNet lexical unit named "lemma.pos" in a frame, as the mapping sources (SemLink,
/// PredicateMatrix, MapNet, WordFrameNet) reference it: the ordered composition
/// [frame, lemma, UPOS]. FrameNet itself is read through its recipe.
/// </summary>
public static class FrameNetLexicalUnit
{
    private static readonly Hash128 LuTypeId = EntityTypeRegistry.FrameNetLu;

    /// <summary>The lexical unit's identity without admitting it (same law as Declare).</summary>
    public static Hash128? Id(string frameName, string luName)
    {
        string lemma = FrameNetLemmaHelper.LemmaOf(luName);
        int dot = luName.LastIndexOf('.');
        string pos = dot > 0 ? luName[(dot + 1)..].ToLowerInvariant() : "";
        if (lemma.Length == 0 || pos.Length == 0 || string.IsNullOrWhiteSpace(frameName)) return null;
        Hash128? frame = ContentEmitter.RootId(frameName.Trim());
        Hash128? word = ContentEmitter.RootId(lemma.Trim());
        Hash128? upos = ContentEmitter.RootId(PosReference.ResolveLabel(pos, PosReference.PosTagset.FrameNet));
        if (frame is null || word is null || upos is null) return null;
        return StructureComposer.Id([frame.Value, word.Value, upos.Value]);
    }

    public static Hash128? Declare(
        SubstrateChangeBuilder b, string frameName, string luName, Hash128 source)
    {
        string lemma = FrameNetLemmaHelper.LemmaOf(luName);
        int dot = luName.LastIndexOf('.');
        string pos = dot > 0 ? luName[(dot + 1)..].ToLowerInvariant() : "";
        if (lemma.Length == 0 || pos.Length == 0) return null;
        var frame = StructureComposer.Text(b, frameName, source);
        var word = StructureComposer.Text(b, lemma, source);
        var upos = StructureComposer.Text(b,
            PosReference.ResolveLabel(pos, PosReference.PosTagset.FrameNet), source);
        if (frame is null || word is null || upos is null) return null;
        return StructureComposer.Compose(b, LuTypeId, source, [frame.Value, word.Value, upos.Value]).Id;
    }
}
