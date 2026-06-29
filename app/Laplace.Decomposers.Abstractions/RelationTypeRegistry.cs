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

    public static AttestationRow AttestDeprel(
        Hash128 dependent, string deprel, Hash128 head, Hash128 sourceId, double sourceTrust,
        long observationCount = 1)
    {
        var r = ResolveDeprel(deprel);
        return NativeAttestation.CategoricalResolved(
            dependent, r.Id, head, sourceId, null, r.Rank * sourceTrust, observationCount: observationCount);
    }

    public static AttestationRow AttestEnhancedDeprel(
        Hash128 dependent, string deprel, Hash128 head, Hash128 sourceId, double sourceTrust,
        long observationCount = 1)
    {
        var r = ResolveEnhancedDeprel(deprel);
        return NativeAttestation.CategoricalResolved(
            dependent, r.Id, head, sourceId, null, r.Rank * sourceTrust, observationCount: observationCount);
    }

    public static RelationTypeResolution ResolveEnhancedDeprel(string deprel)
    {
        ArgumentException.ThrowIfNullOrEmpty(deprel);
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

    public static RelationTypeResolution ResolveDbpedia(string rel)
    {
        ArgumentException.ThrowIfNullOrEmpty(rel);
        string norm = rel.Trim();
        if (norm.StartsWith("dbpedia/", StringComparison.OrdinalIgnoreCase)) norm = norm[8..];
        string canon = "DBPEDIA_" + norm.Replace('/', '_').ToUpperInvariant();
        return new RelationTypeResolution(
            RelationTypeId(canon), RelationTypeRank.Associative, Symmetry.Asymmetric, false,
            RelationTypeId("HAS_DBPEDIA_RELATION"), canon);
    }

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

    public static AttestationRow AttestFeature(
        Hash128 subject, string featureName, Hash128 valueEntity, Hash128 sourceId, double sourceTrust,
        long observationCount = 1)
    {
        var r = ResolveFeature(featureName);
        return NativeAttestation.CategoricalResolved(
            subject, r.Id, valueEntity, sourceId, null, r.Rank * sourceTrust, observationCount: observationCount);
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
        // Substrate-native legibility: each relation type's name is a codepoint-walk content
        // entity, reached by HAS_NAME_ALIAS — render() reconstructs it from its own codepoints, so
        // the type never surfaces as a bare hash and needs no canonical_names code-table row.
        foreach (var k in all)
            if (ContentWitnessBatch.Emit(builder, k.Canonical, sourceId) is { } nameId)
                builder.AddAttestation(NativeAttestation.Categorical(
                    k.Id, "HAS_NAME_ALIAS", nameId, sourceId, null, SourceTrust.SubstrateMandate));
    }

    public static void SeedDynamic(SubstrateChangeBuilder builder, in RelationTypeResolution k, Hash128 sourceId,
                                   ISet<Hash128> seenEntitiesThisBatch,
                                   ConcurrentIdSet seenAttestationsThisRun,
                                   ConcurrentDictionary<string, byte>? readbackNames = null)
    {
        
        
        
        
        
        // Readback name kept only as a render-perf cache; the source of truth is the substrate-native
        // HAS_NAME_ALIAS emitted below, so render()/realize()/label() reconstruct the name from codepoints
        // (DEP_DET, FEAT_*, EDEP_* rendered empty before — 0 HAS_NAME_ALIAS, code-table only).
        VocabularyNames.Track(readbackNames, VocabularyNames.RelationType(k.Canonical));
        if (seenEntitiesThisBatch.Add(k.Id))
            builder.AddEntity(new EntityRow(k.Id, EntityTier.Word, BootstrapIntentBuilder.RelationTypeMetaTypeId, sourceId));
        // Parent edge + substrate-native name alias, once per run — emitted even when the type has no
        // parent, so a parentless dynamic type is still legible and walkable in the DAG.
        if (seenAttestationsThisRun.Add(k.Id))
        {
            builder.AddEntity(new EntityRow(k.Id, EntityTier.Word, BootstrapIntentBuilder.RelationTypeMetaTypeId, sourceId));
            if (k.ParentId is { } parent)
            {
                builder.AddEntity(new EntityRow(parent, EntityTier.Word, BootstrapIntentBuilder.RelationTypeMetaTypeId, sourceId));
                builder.AddAttestation(NativeAttestation.Categorical(
                    k.Id, "IS_A", parent, sourceId, null, SourceTrust.AcademicCurated));
            }
            if (ContentWitnessBatch.Emit(builder, k.Canonical, sourceId) is { } nameId)
                builder.AddAttestation(NativeAttestation.Categorical(
                    k.Id, "HAS_NAME_ALIAS", nameId, sourceId, null, SourceTrust.AcademicCurated));
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
