using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;













public static class CategoryAnchor
{
    
    
    
    
    
    
    public static Hash128? Emit(
        SubstrateChangeBuilder b, string key, Hash128 categoryTypeId, Hash128 source, double trust)
    {
        string? normalized = Normalize(key);
        if (normalized is null) return null;
        Hash128? id = ContentEmitter.Emit(b, normalized, source);
        if (id is null) return null;
        AttestCategory(b, id.Value, categoryTypeId, source, trust);
        return id;
    }

    
    public static void AttestCategory(
        SubstrateChangeBuilder b, Hash128 anchor, Hash128 categoryTypeId, Hash128 source, double trust)
        => b.AddAttestation(NativeAttestation.Categorical(anchor, "IS_TYPED_AS", categoryTypeId, source, trust));

    
    
    
    
    public static Hash128? Id(string key) =>
        Normalize(key) is { } normalized ? ContentEmitter.RootId(normalized) : null;

    // Frame/class/roleset keys arrive from independently-produced files (FrameNet XML, MapNet TSV,
    // PredicateMatrix TSV, WordFrameNet native text, SemLink JSON) that all happen to agree on the
    // same surface convention today — but unlike language/POS/sense keys, there is no canonical
    // lookup table backing that agreement, only convention. Trim defensively at the one chokepoint
    // every category key passes through, instead of relying on every caller to trim its own field.
    private static string? Normalize(string key) =>
        string.IsNullOrEmpty(key) ? null : key.Trim() is { Length: > 0 } trimmed ? trimmed : null;
}
