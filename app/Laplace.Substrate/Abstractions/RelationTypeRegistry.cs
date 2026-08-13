using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class RelationTypeRegistry
{
    public enum Symmetry { Asymmetric, Symmetric }

    public readonly record struct RelationTypeResolution(
        Hash128 Id, double Rank, Symmetry Symmetry, bool Flip, Hash128? ParentId, string Canonical);

    // Resolution is a pure function of the input string over small, bounded
    // vocabularies (governed surfaces, ~50 UD deprels, feature names), but the
    // native resolve is a P/Invoke plus 3-4 string allocations — and hot
    // emitters call it per token edge. Memoize per distinct key.
    private static readonly ConcurrentDictionary<string, RelationTypeResolution> SurfaceCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, RelationTypeResolution> DeprelCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, RelationTypeResolution> EnhancedDeprelCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, RelationTypeResolution> FeatureCache = new(StringComparer.Ordinal);

    public static Hash128 RelationTypeId(string canonicalName)
    {
        ArgumentException.ThrowIfNullOrEmpty(canonicalName);
        unsafe
        {
            Hash128 id;
            NativeInterop.RelationTypeIdNative(canonicalName, &id);
            return id;
        }
    }

    public static RelationTypeResolution Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return SurfaceCache.GetOrAdd(name, static n => ResolveUncached(n));
    }

    private static RelationTypeResolution ResolveUncached(string name)
    {
        unsafe
        {
            Hash128 typeId, parentId;
            double rank;
            byte flip;
            int symmetry;
            NativeInterop.RelationResolveSurface(name, &typeId, &rank, &symmetry, &flip, &parentId);

            string canonical = Marshal.PtrToStringUTF8(NativeInterop.RelationCanonicalForTypeId(&typeId)) ?? name;
            Hash128? parent = parentId.Equals(Hash128.Zero) ? null : parentId;
            return new RelationTypeResolution(
                typeId, rank,
                symmetry == 1 ? Symmetry.Symmetric : Symmetry.Asymmetric,
                flip != 0, parent, canonical);
        }
    }

    public static RelationTypeResolution ResolveDeprel(string deprel)
    {
        ArgumentException.ThrowIfNullOrEmpty(deprel);
        return DeprelCache.GetOrAdd(deprel, static d => ResolveDeprelUncached(d));
    }

    private static RelationTypeResolution ResolveDeprelUncached(string deprel)
    {
        unsafe
        {
            Hash128 typeId, parentId;
            double rank;
            byte flip;
            int symmetry;
            NativeInterop.RelationResolveDeprel(deprel, &typeId, &rank, &symmetry, &flip, &parentId);
            return DynamicResolution(deprel, "DEP_", typeId, parentId, rank, symmetry, flip);
        }
    }

    private static RelationTypeResolution DynamicResolution(
        string input, string prefix, Hash128 typeId, Hash128 parentId,
        double rank, int symmetry, byte flip)
    {
        string canonical = BuildDynamicCanonical(input, prefix);
        Hash128? parent = parentId.Equals(Hash128.Zero) ? null : parentId;
        return new RelationTypeResolution(
            typeId, rank,
            symmetry == 1 ? Symmetry.Symmetric : Symmetry.Asymmetric,
            flip != 0, parent, canonical);
    }

    private static string BuildDynamicCanonical(string input, string prefix)
    {
        string norm = prefix.StartsWith("FEAT_", StringComparison.Ordinal)
            ? input.Trim().ToUpperInvariant()
            : input.Trim().ToLowerInvariant().Replace(':', '_').ToUpperInvariant();
        return prefix + norm;
    }

    public static RelationTypeResolution ResolveEnhancedDeprel(string deprel)
    {
        ArgumentException.ThrowIfNullOrEmpty(deprel);
        return EnhancedDeprelCache.GetOrAdd(deprel, static d => ResolveEnhancedDeprelUncached(d));
    }

    private static RelationTypeResolution ResolveEnhancedDeprelUncached(string deprel)
    {
        unsafe
        {
            Hash128 typeId, parentId;
            double rank;
            byte flip;
            int symmetry;
            NativeInterop.RelationResolveEnhancedDeprel(deprel, &typeId, &rank, &symmetry, &flip, &parentId);
            return DynamicResolution(deprel, "EDEP_", typeId, parentId, rank, symmetry, flip);
        }
    }

    // ResolveDbpedia was deleted here. It minted DBPEDIA_<REL> types from ConceptNet's
    // dbpedia lane, which puts the SOURCE into the type name. consensus.id is
    // blake3(subject‖type‖object), so a source-scoped type guarantees that the same
    // triple witnessed by dbpedia and by prose hashes to two different consensus rows:
    // they never merge, witness_count never climbs, RD never tightens. Provenance
    // already has a slot — AttestationRow.SourceId. It does not belong in TypeId.
    // It had zero callers. dbpedia edges map onto generic manifest relations instead.

    public static bool ParseFeature(string feature, out string name, out string value)
    {
        name = ""; value = "";
        if (string.IsNullOrEmpty(feature)) return false;
        int eq = feature.IndexOf('=');
        if (eq <= 0 || eq >= feature.Length - 1) return false;
        name = feature[..eq].Trim();
        value = feature[(eq + 1)..].Trim();
        return name.Length > 0 && value.Length > 0;
    }

    public static RelationTypeResolution ResolveFeature(string featureName)
    {
        ArgumentException.ThrowIfNullOrEmpty(featureName);
        return FeatureCache.GetOrAdd(featureName, static f => ResolveFeatureUncached(f));
    }

    private static RelationTypeResolution ResolveFeatureUncached(string featureName)
    {
        unsafe
        {
            Hash128 typeId, parentId;
            double rank;
            byte flip;
            int symmetry;
            NativeInterop.RelationResolveFeature(featureName, &typeId, &rank, &symmetry, &flip, &parentId);
            return DynamicResolution(featureName, "FEAT_", typeId, parentId, rank, symmetry, flip);
        }
    }

    public static IEnumerable<RelationTypeResolution> AllCanonical()
    {
        nuint n = NativeInterop.RelationManifestCount();
        for (nuint i = 0; i < n; i++)
        {
            var ptr = NativeInterop.RelationManifestCanonical(i);
            if (ptr == IntPtr.Zero) continue;
            var name = Marshal.PtrToStringUTF8(ptr);
            if (name is null) continue;
            yield return Resolve(name);
        }
    }

    public static void SeedCanonical(SubstrateChangeBuilder builder, Hash128 sourceId)
    {
        var all = new List<RelationTypeResolution>(AllCanonical());
        foreach (var k in all)
            builder.AddEntity(new EntityRow(k.Id, EntityTier.Word, BootstrapIntentBuilder.RelationTypeMetaTypeId, sourceId));
        foreach (var k in all)
            if (k.ParentId is { } parent)
                builder.AddAttestation(NativeAttestation.Categorical(
                    k.Id, "IS_A", parent, sourceId, null, SourceTrust.SubstrateMandate));

        // GH #1041: no content DAGs for canonical relation names — the ids are
        // blake3(canonical) and every canonical name is in the static
        // canonical_names seed, so realize.render resolves them from arm 1.
        // The old loop staged a text DAG per relation name (221 identifier
        // strings minted as word/sentence entities).
    }

    public static void SeedDynamic(SubstrateChangeBuilder builder, in RelationTypeResolution k, Hash128 sourceId,
                                   ISet<Hash128> seenEntitiesThisBatch,
                                   ConcurrentIdSet seenAttestationsThisRun,
                                   ConcurrentDictionary<string, byte>? readbackNames = null)
    {








        VocabularyNames.Track(readbackNames, VocabularyNames.RelationType(k.Canonical));
        if (seenEntitiesThisBatch.Add(k.Id))
            builder.AddEntity(new EntityRow(k.Id, EntityTier.Word, BootstrapIntentBuilder.RelationTypeMetaTypeId, sourceId));


        if (seenAttestationsThisRun.Add(k.Id))
        {
            builder.AddEntity(new EntityRow(k.Id, EntityTier.Word, BootstrapIntentBuilder.RelationTypeMetaTypeId, sourceId));
            if (k.ParentId is { } parent)
            {
                builder.AddEntity(new EntityRow(parent, EntityTier.Word, BootstrapIntentBuilder.RelationTypeMetaTypeId, sourceId));
                builder.AddAttestation(NativeAttestation.Categorical(
                    k.Id, "IS_A", parent, sourceId, null, SourceTrust.AcademicCurated));
            }
            // GH #1041: no content DAG for the label — "DEP_NSUBJ" was a
            // measured tier-2 Word entity. The type id is blake3(canonical) =
            // realize.canonical_id(canonical); VocabularyNames.Track above
            // feeds register_canonicals, and realize.render resolves from
            // canonical_names arm 1. Nothing walks the label's sub-tokens.
        }
    }

    public static void SeedDeprel(SubstrateChangeBuilder builder, string deprel, Hash128 sourceId,
                                  ISet<Hash128> seenEntitiesThisBatch,
                                  ConcurrentIdSet seenAttestationsThisRun,
                                  ConcurrentDictionary<string, byte>? readbackNames = null)
    {
        int colon = deprel.IndexOf(':');
        if (colon > 0) SeedDynamic(builder, ResolveDeprel(deprel[..colon]), sourceId, seenEntitiesThisBatch, seenAttestationsThisRun, readbackNames);
        SeedDynamic(builder, ResolveDeprel(deprel), sourceId, seenEntitiesThisBatch, seenAttestationsThisRun, readbackNames);
    }

    public static void SeedEnhancedDeprel(SubstrateChangeBuilder builder, string deprel, Hash128 sourceId,
                                          ISet<Hash128> seenEntitiesThisBatch,
                                          ConcurrentIdSet seenAttestationsThisRun,
                                          ConcurrentDictionary<string, byte>? readbackNames = null)
    {
        int colon = deprel.IndexOf(':');
        if (colon > 0) SeedDynamic(builder, ResolveEnhancedDeprel(deprel[..colon]), sourceId, seenEntitiesThisBatch, seenAttestationsThisRun, readbackNames);
        SeedDynamic(builder, ResolveEnhancedDeprel(deprel), sourceId, seenEntitiesThisBatch, seenAttestationsThisRun, readbackNames);
    }
}
