using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static unsafe partial class NativeInterop
{
    private const string Library = "laplace_core";

    [LibraryImport(Library, EntryPoint = "laplace_core_version")]
    private static partial IntPtr LaplaceCoreVersionPtr();

    public static string LaplaceCoreVersion() =>
        Marshal.PtrToStringUTF8(LaplaceCoreVersionPtr()) ?? string.Empty;

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

    [LibraryImport(Library, EntryPoint = "laplace_score_fp")]
    internal static partial long LaplaceScoreFp(double v, double m);

    [LibraryImport(Library, EntryPoint = "laplace_score_inverse_fp")]
    internal static partial double LaplaceScoreInverseFp(long scoreFp, double m);

    [LibraryImport(Library, EntryPoint = "laplace_score_batch_fp")]
    internal static partial void LaplaceScoreBatchFp(float* w, nuint n, double m, long* outScores);

    [LibraryImport(Library, EntryPoint = "super_fibonacci")]
    internal static partial void SuperFibonacci(nuint n, double* outQuats);

    [LibraryImport(Library, EntryPoint = "math4d_centroid")]
    internal static partial void Math4dCentroid(double* points, nuint nPoints, double* out4);

    [LibraryImport(Library, EntryPoint = "trajectory_build")]
    internal static partial int TrajectoryBuild(Hash128* entityHashes, nuint n, double* outXyzm);

    [LibraryImport(Library, EntryPoint = "trajectory_build_flagged")]
    internal static partial int TrajectoryBuildFlagged(Hash128* entityHashes, ulong* flags, nuint n, double* outXyzm);

    [LibraryImport(Library, EntryPoint = "trajectory_build_rle")]
    internal static partial int TrajectoryBuildRle(Hash128* constituents, nuint n, double* outXyzm, nuint* outVertexCount);

    [LibraryImport(Library, EntryPoint = "trajectory_constituents")]
    internal static partial int TrajectoryConstituents(double* trajectoryXyzm, nuint nPoints, Hash128* outHashes, nuint outCap);

    [LibraryImport(Library, EntryPoint = "hilbert4d_encode")]
    internal static partial void Hilbert4dEncode(double* point, Hilbert128* outHb);

    [LibraryImport(Library, EntryPoint = "hilbert4d_decode")]
    internal static partial void Hilbert4dDecode(Hilbert128* hb, double* outPoint);

    [LibraryImport(Library, EntryPoint = "hilbert128_compare")]
    internal static partial int Hilbert128Compare(Hilbert128* a, Hilbert128* b);

    [LibraryImport(Library, EntryPoint = "laplace_unicode_seed_compute", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int UnicodeSeedCompute(string ucdxmlPath, string ducetPath,
                                                   CodepointRecord* outRecords, nuint outCapacity);

    [LibraryImport(Library, EntryPoint = "codepoint_table_load_perfcache", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int CodepointTableLoadPerfcache(string path);

    [LibraryImport(Library, EntryPoint = "codepoint_table_unload")]
    internal static partial void CodepointTableUnload();

    [LibraryImport(Library, EntryPoint = "codepoint_table_is_loaded")]
    internal static partial int CodepointTableIsLoaded();

    [LibraryImport(Library, EntryPoint = "codepoint_table_records")]
    internal static partial int CodepointTableRecords(CodepointRecord** outRecords, ulong* outCount);

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

    [LibraryImport(Library, EntryPoint = "laplace_text_decomposer_run")]
    internal static partial int TextDecomposerRun(byte* utf8, nuint len, IntPtr* outTree);

    [LibraryImport(Library, EntryPoint = "tier_tree_id_array")]
    internal static partial IntPtr TierTreeIdArray(IntPtr tree);

    [LibraryImport(Library, EntryPoint = "hash_composer_run")]
    internal static partial int HashComposerRun(
        IntPtr tree,
        delegate* unmanaged[Cdecl]<uint, IntPtr, Hash128*, double*, Hilbert128*, int> resolver,
        IntPtr resolverUserData);

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
        Hash128* id, Hash128* subjectId, Hash128* typeId,
        Hash128* objectId, Hash128* sourceId, Hash128* contextId,
        short outcome,
        long lastObservedAtUnixUs, long observationCount);

    [LibraryImport(Library, EntryPoint = "intent_stage_emit_copy_binary")]
    internal static partial nuint IntentStageEmitCopyBinary(
        IntPtr stage, int table, byte* buf, nuint bufCapacity);

    [LibraryImport(Library, EntryPoint = "intent_stage_tuple_ptr")]
    internal static partial byte* IntentStageTuplePtr(
        IntPtr stage, int table, nuint* outLen);

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
