using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;








public static class PosReference
{
    public static readonly Hash128 PosTypeId = EntityTypeRegistry.Pos;
    public static readonly Hash128 HasPosTypeId = RelationTypeRegistry.RelationTypeId("HAS_POS");





    // A tagset reaches the native POS law by its name, never by ordinal.
    public enum PosTagset { Upos, WordNet, FrameNet, PropBank, Wiktionary }


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

    /// <summary>The canonical UPOS label a source tag normalizes to (its POS entity's content).</summary>
    public static string ResolveLabel(string sourceTag, PosTagset tagset) =>
        ResolveContent(sourceTag, tagset, out _);

    /// <summary>
    /// Frozen UPOS slot of a source tag. The English canonical spelling is not the slot.
    /// Unmapped tags have no slot.
    /// </summary>
    public static bool TrySlot(string sourceTag, PosTagset tagset, out int slot)
    {
        string? canonical = NativeAttestation.ResolvePosCanonical(sourceTag, tagset);
        if (canonical is null)
        {
            slot = -1;
            return false;
        }
        slot = Array.IndexOf(Canonical, canonical);
        return slot >= 0;
    }

    public static Hash128 Resolve(string sourceTag, PosTagset tagset) =>
        Resolve(sourceTag, tagset, out _);

    public static CompositionLaw.Face ResolveFace(string sourceTag, PosTagset tagset)
    {
        string content = ResolveContent(sourceTag, tagset, out _);
        return CompositionLaw.RequiredFace(content);
    }

    public static Hash128 Resolve(string sourceTag, PosTagset tagset, out bool probationary)
    {
        string content = ResolveContent(sourceTag, tagset, out probationary);
        return ContentEmitter.RootId(content)
            ?? throw new InvalidOperationException($"POS content could not be composed: {content}");
    }

    /// <summary>
    /// Paints the frozen UPOS slot on a carrier that already has its own coordinate.
    /// The English spelling of the tag is not the slot and is not attested.
    /// </summary>
    public static bool Deposit(
        SubstrateChangeBuilder b,
        Hash128 entityId,
        double x, double y, double z, double m,
        string sourceTag,
        PosTagset tagset)
    {
        if (!TrySlot(sourceTag, tagset, out int slot)) return false;
        Span<double> coord = stackalloc double[] { x, y, z, m };
        b.ContentStage.DepositUpos(entityId, coord, Hilbert128.Encode(coord), slot);
        return true;
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
