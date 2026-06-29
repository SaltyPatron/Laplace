using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;








public static class PosReference
{
    public static readonly Hash128 PosTypeId = EntityTypeRegistry.Pos;

    
    
    
    
    public enum PosTagset { Upos = 0, WordNet = 1, Wiktionary = 2, FrameNet = 3 }

    
    public static readonly string[] Canonical = ReadCanonicalFromNative();

    private static unsafe string[] ReadCanonicalFromNative()
    {
        nuint count;
        byte** names = NativeInterop.PosUposCanonical(&count);
        var result = new string[(int)count];
        for (int i = 0; i < (int)count; i++)
            result[i] = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)names[i])
                        ?? throw new InvalidOperationException("pos canonical list returned null entry");
        return result;
    }

    public static Hash128 CanonicalId(string upos) =>
        NativeAttestation.ResolvePos(upos, PosTagset.Upos);

    public static Hash128 Resolve(string sourceTag, PosTagset tagset) =>
        NativeAttestation.ResolvePos(sourceTag, tagset);

    public static Hash128 Resolve(string sourceTag, PosTagset tagset, out bool probationary) =>
        NativeAttestation.ResolvePos(sourceTag, tagset, out probationary);

    
    
    
    
    
    public static Hash128 Attest(
        SubstrateChangeBuilder b, Hash128 subject, string tag, PosTagset tagset,
        Hash128 sourceId, Hash128? contextId, double sourceTrust,
        ConcurrentDictionary<string, byte>? readbackNames = null,
        long observationCount = 1)
    {
        Hash128 posId = NativeAttestation.ResolvePos(tag, tagset, out bool probationary);
        if (probationary)
        {
            b.AddEntity(new EntityRow(posId, EntityTier.Word, PosTypeId, sourceId));
            // A non-UPOS-mappable tag gets its source-tag string as a substrate-native name so it is
            // legible/walkable instead of a code-table-only island. DB folds the repeat emissions.
            if (ContentWitnessBatch.Emit(b, tag, sourceId) is { } nameId)
                b.AddAttestation(NativeAttestation.Categorical(
                    posId, "HAS_NAME_ALIAS", nameId, sourceId, null, sourceTrust));
        }
        VocabularyNames.TrackProbationaryPos(readbackNames, tag, tagset);
        b.AddAttestation(NativeAttestation.Categorical(
            subject, "HAS_POS", posId, sourceId, contextId, sourceTrust,
            observationCount: observationCount));
        return posId;
    }

    
    
    public static void SeedCanonical(SubstrateChangeBuilder builder, Hash128 sourceId)
    {
        builder.AddEntity(new EntityRow(PosTypeId, EntityTier.Word,
            BootstrapIntentBuilder.TypeMetaTypeId, sourceId));
        foreach (var tag in Canonical)
        {
            Hash128 posId = CanonicalId(tag);
            builder.AddEntity(new EntityRow(posId, EntityTier.Word, PosTypeId, sourceId));
            // Substrate-native legibility: the UPOS tag name is a codepoint-walk content entity reached
            // by HAS_NAME_ALIAS, so POS categories render without a canonical_names code-table row.
            if (ContentWitnessBatch.Emit(builder, tag, sourceId) is { } nameId)
                builder.AddAttestation(NativeAttestation.Categorical(
                    posId, "HAS_NAME_ALIAS", nameId, sourceId, null, SourceTrust.SubstrateMandate));
        }
    }
}
