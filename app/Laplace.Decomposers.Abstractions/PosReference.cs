using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// THE pos surface. All POS normalization law (tagset maps, canonical set, probationary
/// minting) lives in native pos_law.c, generated from engine/manifest/pos_tags.toml.
/// Decomposers witness POS exclusively through <see cref="Attest"/> — it resolves
/// natively and emits probationary pos entities in the same change, so a referenced
/// pos id can never be a ghost. No decomposer carries its own tag map.
/// </summary>
public static class PosReference
{
    public static readonly Hash128 PosTypeId = EntityTypeRegistry.Pos;

    /// <summary>
    /// Tagset modes. Values are the native pos_law ABI: manifest section order in
    /// engine/manifest/pos_tags.toml (UPOS = 0). Append-only, mirroring the manifest.
    /// </summary>
    public enum PosTagset { Upos = 0, WordNet = 1, Wiktionary = 2, FrameNet = 3 }

    /// <summary>The canonical UPOS set, read from the native law — never duplicated here.</summary>
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

    /// <summary>
    /// The pos witness verb: resolve the source tag through the native law, emit the
    /// probationary pos entity when the tag is unmapped (canonicals are bootstrap-seeded),
    /// and stamp the HAS_POS attestation. Returns the pos entity id (usable as context).
    /// </summary>
    public static Hash128 Attest(
        SubstrateChangeBuilder b, Hash128 subject, string tag, PosTagset tagset,
        Hash128 sourceId, Hash128? contextId, double sourceTrust, long observationCount = 1)
    {
        Hash128 posId = NativeAttestation.ResolvePos(tag, tagset, out bool probationary);
        if (probationary)
            b.AddEntity(new EntityRow(posId, EntityTier.Vocabulary, PosTypeId, sourceId));
        b.AddAttestation(NativeAttestation.Categorical(
            subject, "HAS_POS", posId, sourceId, contextId, sourceTrust,
            observationCount: observationCount));
        return posId;
    }

    /// <summary>Seeds the pos meta-type + every canonical pos entity. Called by
    /// <see cref="BootstrapIntentBuilder.Build"/> — every bootstrapping decomposer gets it.</summary>
    public static void SeedCanonical(SubstrateChangeBuilder builder, Hash128 sourceId)
    {
        builder.AddEntity(new EntityRow(PosTypeId, EntityTier.Vocabulary,
            BootstrapIntentBuilder.TypeMetaTypeId, sourceId));
        foreach (var tag in Canonical)
            builder.AddEntity(new EntityRow(CanonicalId(tag), EntityTier.Vocabulary, PosTypeId, sourceId));
    }
}
