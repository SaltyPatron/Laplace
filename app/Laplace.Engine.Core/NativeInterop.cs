using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// P/Invoke bindings to liblaplace_core (per ADR 0024 + 0026).
///
/// Per RULES.md R14: engine-boundary types are POD; no exceptions cross
/// the C ABI. Per RULES.md R22: this project does NOT define parallel
/// C# types for liblwgeom's POINT4D — geometry round-trips PG ↔ C# via
/// Npgsql.NetTopologySuite (NTS Coordinate with Ordinates.XYZM).
///
/// All bindings use source-generated <c>[LibraryImport]</c> for AOT
/// friendliness; opaque handles cross as <see cref="IntPtr"/> and are
/// wrapped in <see cref="SafeHandle"/> subclasses (<see cref="TierTree"/>,
/// <see cref="IntentStage"/>) at the public API level.
/// </summary>
public static unsafe partial class NativeInterop
{
    // Linux: liblaplace_core.so   macOS: liblaplace_core.dylib   Windows: laplace_core.dll
    private const string Library = "laplace_core";

    // === version ===

    [LibraryImport(Library, EntryPoint = "laplace_core_version")]
    private static partial IntPtr LaplaceCoreVersionPtr();

    public static string LaplaceCoreVersion() =>
        Marshal.PtrToStringUTF8(LaplaceCoreVersionPtr()) ?? string.Empty;

    // === hash128 (engine/core/include/laplace/core/hash128.h) ===

    [LibraryImport(Library, EntryPoint = "hash128_blake3")]
    internal static partial void Hash128Blake3(byte* data, nuint len, Hash128* outHash);

    [LibraryImport(Library, EntryPoint = "hash128_merkle")]
    internal static partial void Hash128Merkle(byte tier, Hash128* children, nuint n, Hash128* outHash);

    [LibraryImport(Library, EntryPoint = "hash128_compare")]
    internal static partial int Hash128Compare(Hash128* a, Hash128* b);

    [LibraryImport(Library, EntryPoint = "hash128_equals")]
    internal static partial int Hash128Equals(Hash128* a, Hash128* b);

    [LibraryImport(Library, EntryPoint = "hash128_zero")]
    internal static partial void Hash128Zero(Hash128* outHash);

    // === super_fibonacci (engine/core/include/laplace/core/super_fibonacci.h) ===

    [LibraryImport(Library, EntryPoint = "super_fibonacci")]
    internal static partial void SuperFibonacci(nuint n, double* outQuats);

    // === math4d (engine/core/include/laplace/core/math4d.h) ===

    [LibraryImport(Library, EntryPoint = "math4d_centroid")]
    internal static partial void Math4dCentroid(double* points, nuint nPoints, double* out4);

    // === trajectory (engine/core/include/laplace/core/trajectory.h) ===

    [LibraryImport(Library, EntryPoint = "trajectory_build")]
    internal static partial int TrajectoryBuild(Hash128* entityHashes, nuint n, double* outXyzm);

    [LibraryImport(Library, EntryPoint = "trajectory_build_rle")]
    internal static partial int TrajectoryBuildRle(Hash128* constituents, nuint n, double* outXyzm, nuint* outVertexCount);

    [LibraryImport(Library, EntryPoint = "trajectory_constituents")]
    internal static partial int TrajectoryConstituents(double* trajectoryXyzm, nuint nPoints, Hash128* outHashes, nuint outCap);

    // === hilbert4d (engine/core/include/laplace/core/hilbert4d.h) ===

    [LibraryImport(Library, EntryPoint = "hilbert4d_encode")]
    internal static partial void Hilbert4dEncode(double* point, Hilbert128* outHb);

    [LibraryImport(Library, EntryPoint = "hilbert4d_decode")]
    internal static partial void Hilbert4dDecode(Hilbert128* hb, double* outPoint);

    [LibraryImport(Library, EntryPoint = "hilbert128_compare")]
    internal static partial int Hilbert128Compare(Hilbert128* a, Hilbert128* b);

    // === unicode_seed (engine/core/include/laplace/core/unicode_seed.h) ===

    [LibraryImport(Library, EntryPoint = "laplace_unicode_seed_compute", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int UnicodeSeedCompute(string ucdxmlPath, string ducetPath,
                                                   CodepointRecord* outRecords, nuint outCapacity);

    // === codepoint_table (engine/core/include/laplace/core/codepoint_table.h) ===

    [LibraryImport(Library, EntryPoint = "codepoint_table_load_perfcache", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int CodepointTableLoadPerfcache(string path);

    [LibraryImport(Library, EntryPoint = "codepoint_table_unload")]
    internal static partial void CodepointTableUnload();

    [LibraryImport(Library, EntryPoint = "codepoint_table_is_loaded")]
    internal static partial int CodepointTableIsLoaded();

    [LibraryImport(Library, EntryPoint = "codepoint_table_records")]
    internal static partial int CodepointTableRecords(CodepointRecord** outRecords, ulong* outCount);

    // === tier_tree (engine/core/include/laplace/core/tier_tree.h) ===

    [LibraryImport(Library, EntryPoint = "tier_tree_new")]
    internal static partial IntPtr TierTreeNew(nuint capacityHint);

    [LibraryImport(Library, EntryPoint = "tier_tree_free")]
    internal static partial void TierTreeFree(IntPtr tree);

    [LibraryImport(Library, EntryPoint = "tier_tree_node_count")]
    internal static partial nuint TierTreeNodeCount(IntPtr tree);

    [LibraryImport(Library, EntryPoint = "tier_tree_capacity")]
    internal static partial nuint TierTreeCapacity(IntPtr tree);

    [LibraryImport(Library, EntryPoint = "tier_tree_add_leaf")]
    internal static partial uint TierTreeAddLeaf(IntPtr tree, byte tier, uint atom,
                                                   uint textRangeOff, uint textRangeLen);

    [LibraryImport(Library, EntryPoint = "tier_tree_add_node")]
    internal static partial uint TierTreeAddNode(IntPtr tree, byte tier,
                                                   uint firstChildIdx, uint childCount,
                                                   uint textRangeOff, uint textRangeLen);

    [LibraryImport(Library, EntryPoint = "tier_tree_finalize")]
    internal static partial int TierTreeFinalize(IntPtr tree);

    [LibraryImport(Library, EntryPoint = "tier_tree_get_node")]
    internal static partial int TierTreeGetNode(IntPtr tree, uint idx, TierNodeView* outView);

    [LibraryImport(Library, EntryPoint = "tier_tree_set_id")]
    internal static partial int TierTreeSetId(IntPtr tree, uint idx, Hash128* id);

    [LibraryImport(Library, EntryPoint = "tier_tree_set_coord")]
    internal static partial int TierTreeSetCoord(IntPtr tree, uint idx, double* coord);

    [LibraryImport(Library, EntryPoint = "tier_tree_set_hilbert")]
    internal static partial int TierTreeSetHilbert(IntPtr tree, uint idx, Hilbert128* hilbert);

    // === text_decomposer (engine/core/include/laplace/core/text_decomposer.h) ===

    [LibraryImport(Library, EntryPoint = "laplace_text_decomposer_run")]
    internal static partial int TextDecomposerRun(byte* utf8, nuint len, IntPtr* outTree);

    [LibraryImport(Library, EntryPoint = "tier_tree_id_array")]
    internal static partial IntPtr TierTreeIdArray(IntPtr tree);

    // === hash_composer (engine/core/include/laplace/core/hash_composer.h) ===

    [LibraryImport(Library, EntryPoint = "hash_composer_run")]
    internal static partial int HashComposerRun(
        IntPtr tree,
        delegate* unmanaged[Cdecl]<uint, IntPtr, Hash128*, double*, Hilbert128*, int> resolver,
        IntPtr resolverUserData);

    // === merkle_dedup (engine/core/include/laplace/core/merkle_dedup.h) ===

    [LibraryImport(Library, EntryPoint = "merkle_dedup_filter_novel")]
    internal static partial int MerkleDedupFilterNovel(
        Hash128* candidates, nuint n,
        byte* existingBitmap, nuint bitmapBits,
        Hash128* outNovel, nuint* outN);

    [LibraryImport(Library, EntryPoint = "merkle_dedup_trunk_shortcircuit")]
    internal static partial int MerkleDedupTrunkShortcircuit(
        IntPtr tree,
        byte* existingBitmap, nuint bitmapBits,
        uint* outNovelIndices, nuint* outN);

    // === intent_stage (engine/core/include/laplace/core/intent_stage.h) ===

    [LibraryImport(Library, EntryPoint = "intent_stage_new")]
    internal static partial IntPtr IntentStageNew(nuint rowCapacityHint);

    [LibraryImport(Library, EntryPoint = "intent_stage_free")]
    internal static partial void IntentStageFree(IntPtr stage);

    [LibraryImport(Library, EntryPoint = "intent_stage_entity_count")]
    internal static partial nuint IntentStageEntityCount(IntPtr stage);

    [LibraryImport(Library, EntryPoint = "intent_stage_physicality_count")]
    internal static partial nuint IntentStagePhysicalityCount(IntPtr stage);

    [LibraryImport(Library, EntryPoint = "intent_stage_attestation_count")]
    internal static partial nuint IntentStageAttestationCount(IntPtr stage);

    [LibraryImport(Library, EntryPoint = "intent_stage_copy_column_list")]
    internal static partial IntPtr IntentStageCopyColumnList(int table);

    [LibraryImport(Library, EntryPoint = "intent_stage_add_entity")]
    internal static partial int IntentStageAddEntity(
        IntPtr stage, Hash128* id, short tier, Hash128* typeId, Hash128* firstObservedBy);

    [LibraryImport(Library, EntryPoint = "intent_stage_add_physicality")]
    internal static partial int IntentStageAddPhysicality(
        IntPtr stage,
        Hash128* id, Hash128* entityId, Hash128* sourceId,
        short kind,
        double* coord, Hilbert128* hilbertIndex,
        double* trajectoryXyzm, uint trajectoryNVertices,
        int nConstituents,
        int alignmentResidualIsNull, double alignmentResidual,
        int sourceDimIsNull,         int    sourceDim,
        long observedAtUnixUs);

    [LibraryImport(Library, EntryPoint = "intent_stage_add_attestation")]
    internal static partial int IntentStageAddAttestation(
        IntPtr stage,
        Hash128* id, Hash128* subjectId, Hash128* kindId,
        Hash128* objectId, Hash128* sourceId, Hash128* contextId,
        long rating, long rd, long volatility,
        long lastObservedAtUnixUs, long observationCount);

    [LibraryImport(Library, EntryPoint = "intent_stage_emit_copy_binary")]
    internal static partial nuint IntentStageEmitCopyBinary(
        IntPtr stage, int table, byte* buf, nuint bufCapacity);

    // === glicko2 (engine/core/include/laplace/core/glicko2.h) ===

    [LibraryImport(Library, EntryPoint = "glicko2_effective_mu")]
    internal static partial long Glicko2EffectiveMu(Glicko2State* state);

    [LibraryImport(Library, EntryPoint = "glicko2_update_period")]
    internal static partial void Glicko2UpdatePeriod(
        Glicko2State* state,
        Glicko2Observation* obs,
        nuint n,
        long tau,
        long nowNs);
}
