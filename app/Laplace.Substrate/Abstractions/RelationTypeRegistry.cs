using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class RelationTypeRegistry
{
    public enum Symmetry { Asymmetric, Symmetric }

    /// <summary>A surface's resolution: the element it names, whether it states the
    /// element reversed, and the qualifier it carries (an alias such as mero_member or
    /// domain_topic names one element plus a closed subcategory).</summary>
    public readonly record struct RelationTypeResolution(
        Hash128 Id, double Rank, Symmetry Symmetry, bool Flip, Hash128? ParentId, string Canonical,
        Mask256 Qualifier = default);

    // Resolution is a pure function of the input string over small, bounded
    // vocabularies (governed surfaces, ~50 UD deprels, feature names), but the
    // native resolve is a P/Invoke plus 3-4 string allocations — and hot
    // emitters call it per token edge. Memoize per distinct key.
    private static readonly ConcurrentDictionary<string, RelationTypeResolution> SurfaceCache = new(StringComparer.Ordinal);

    // LAPLACE_REL_RETIRED (relation_law.h): a retired relation keeps its bit and type id
    // for reading admitted evidence, but surface resolution for emission fails closed.
    private const int RetiredRelation = -3;

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
            int rc = NativeInterop.RelationResolveSurface(name, &typeId, &rank, &symmetry, &flip, &parentId);
            if (rc == RetiredRelation)
                throw new InvalidOperationException(
                    $"Relation {name} is retired: emit its successor with the claim's qualifiers "
                    + "(engine/manifest/relation_types.toml, qualifiers.toml).");

            string canonical = Marshal.PtrToStringUTF8(NativeInterop.RelationCanonicalForTypeId(&typeId)) ?? name;
            Hash128? parent = parentId.Equals(Hash128.Zero) ? null : parentId;
            int qualifierBit = NativeInterop.RelationSurfaceQualifier(name);
            return new RelationTypeResolution(
                typeId, rank,
                symmetry == 1 ? Symmetry.Symmetric : Symmetry.Asymmetric,
                flip != 0, parent, canonical,
                qualifierBit < 0 ? Mask256.Zero : Mask256.Zero.Set((byte)qualifierBit));
        }
    }

    // ResolveDbpedia was deleted here. It minted DBPEDIA_<REL> types from ConceptNet's
    // dbpedia lane, which puts the SOURCE into the type name. consensus.id is
    // blake3(subject‖type‖object), so a source-scoped type guarantees that the same
    // triple witnessed by dbpedia and by prose hashes to two different consensus rows:
    // they never merge, witness_count never climbs, RD never tightens. Provenance
    // already has a slot — AttestationRow.SourceId. It does not belong in TypeId.
    // It had zero callers. dbpedia edges map onto generic manifest relations instead.

    /// <summary>The live manifest relations; a retired relation (its meaning carried by a
    /// successor plus qualifiers) is not emittable vocabulary.</summary>
    public static IEnumerable<RelationTypeResolution> AllCanonical()
    {
        nuint n = NativeInterop.RelationManifestCount();
        for (nuint i = 0; i < n; i++)
        {
            var ptr = NativeInterop.RelationManifestCanonical(i);
            if (ptr == IntPtr.Zero || NativeInterop.RelationManifestSuccessor(i) != IntPtr.Zero) continue;
            var name = Marshal.PtrToStringUTF8(ptr);
            if (name is null) continue;
            yield return Resolve(name);
        }
    }

    public static void SeedCanonical(SubstrateChangeBuilder builder, Hash128 sourceId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        _ = sourceId;
        // Relation identity/rank/symmetry/family/bit live in the native manifest and
        // highway perfcache. They are operator vocabulary, not reusable content
        // entities. Do not deposit relation keys into entities/physicalities.
    }
}
