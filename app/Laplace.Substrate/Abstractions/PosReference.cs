using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;








public static class PosReference
{
    public static readonly Hash128 PosTypeId = EntityTypeRegistry.Pos;
    public static readonly Hash128 HasPosTypeId = RelationTypeRegistry.RelationTypeId("HAS_POS");





    public enum PosTagset { Upos = 0, WordNet = 1, Wiktionary = 2, FrameNet = 3 }


    public static readonly string[] Canonical = ReadCanonicalFromNative();

    // The native POS law is the governed tagset mapping; a POS endpoint is the
    // ordinary content entity of its canonical UPOS label.


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

    public static Hash128 CanonicalId(string upos)
    {
        string content = ResolveContent(upos, PosTagset.Upos, out bool probationary);
        if (probationary)
            throw new InvalidOperationException($"UPOS tag is not canonical: {upos}");
        return ContentEmitter.RootId(content)
            ?? throw new InvalidOperationException($"POS content could not be composed: {content}");
    }

    public static Hash128 Resolve(string sourceTag, PosTagset tagset) =>
        Resolve(sourceTag, tagset, out _);

    public static Hash128 Resolve(string sourceTag, PosTagset tagset, out bool probationary)
    {
        string content = ResolveContent(sourceTag, tagset, out probationary);
        return ContentEmitter.RootId(content)
            ?? throw new InvalidOperationException($"POS content could not be composed: {content}");
    }

    private static string ResolveContent(string sourceTag, PosTagset tagset, out bool probationary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTag);
        string? canonical = NativeAttestation.ResolvePosCanonical(sourceTag, tagset);
        probationary = canonical is null;
        if (canonical is not null)
            return canonical;
        // Unknown/probationary labels remain exact observed content. Do not case-fold
        // or namespace-salt them: "NN", "nn", "Nn" are distinct fragments unless
        // testimony later relates them.
        return sourceTag.Trim();
    }






    public static Hash128 Emit(
        SubstrateChangeBuilder b, string tag, PosTagset tagset,
        Hash128 sourceId, double sourceTrust,
        ConcurrentDictionary<string, byte>? readbackNames = null)
    {
        string content = ResolveContent(tag, tagset, out bool probationary);
        Hash128 posId = ContentEmitter.Emit(b, content, sourceId)
            ?? throw new InvalidOperationException($"POS content could not be admitted: {content}");
        VocabularyNames.TrackProbationaryPos(readbackNames, tag, tagset, probationary);
        return posId;
    }

    public static Hash128 Attest(
        SubstrateChangeBuilder b, Hash128 subject, string tag, PosTagset tagset,
        Hash128 sourceId, Hash128? contextId, double sourceTrust,
        ConcurrentDictionary<string, byte>? readbackNames = null,
        long observationCount = 1)
    {
        Hash128 posId = Emit(b, tag, tagset, sourceId, sourceTrust, readbackNames);
        b.AddAttestation(NativeAttestation.CategoricalResolved(
            subject, HasPosTypeId, posId, sourceId, contextId, sourceTrust,
            observationCount: observationCount));
        return posId;
    }



    public static void SeedCanonical(SubstrateChangeBuilder builder, Hash128 sourceId)
    {
        foreach (var tag in Canonical)
        {
            Hash128 posId = ContentEmitter.Emit(builder, tag, sourceId)
                ?? throw new InvalidOperationException($"POS content could not be admitted: {tag}");
        }
    }
}
