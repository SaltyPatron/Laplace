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

    [LibraryImport(Library, EntryPoint = "laplace_testimony_pack_walk")]
    internal static partial int LaplaceTestimonyPackWalk(
        Hash128* objectIds, long* scoresFp1e9, ushort* games, nuint n, double* out4n);

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

    [LibraryImport(Library, EntryPoint = "laplace_normalize_nfc_utf8")]
    public static partial int NormalizeNfcUtf8(
        byte* utf8, nuint len, byte** outUtf8, nuint* outLen);

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
        short physicalityType,
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

    

    [LibraryImport(Library, EntryPoint = "laplace_grammar_lookup_by_id", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr GrammarLookupById(string modalityId);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_lookup_by_ext", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr GrammarLookupByExt(string ext);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_parse")]
    internal static partial int GrammarParse(byte* utf8, nuint len, IntPtr recipe, IntPtr* outAst);

    [LibraryImport(Library, EntryPoint = "laplace_ast_node_count")]
    internal static partial nuint AstNodeCount(IntPtr ast);

    [LibraryImport(Library, EntryPoint = "laplace_ast_get_node")]
    internal static partial int AstGetNode(IntPtr ast, nuint idx, LaplaceAstNode* outNode);

    [LibraryImport(Library, EntryPoint = "laplace_ast_type_name")]
    internal static partial IntPtr AstTypeName(IntPtr ast, uint nodeTypeId);

    [LibraryImport(Library, EntryPoint = "laplace_ast_free")]
    internal static partial void AstFree(IntPtr ast);

    

    [LibraryImport(Library, EntryPoint = "hash_composer_compose_node")]
    internal static partial void HashComposerComposeNode(
        byte tier, Hash128* childIds, double* childCoords, nuint n,
        Hash128* outId, double* outCoord, Hilbert128* outHb);

    

    [LibraryImport(Library, EntryPoint = "laplace_grapheme_floor_build_owned")]
    internal static partial IntPtr GraphemeFloorBuildOwned(byte* utf8, nuint len, IntPtr* outTree);

    [LibraryImport(Library, EntryPoint = "laplace_grapheme_floor_cp_n")]
    internal static partial nuint GraphemeFloorCpN(IntPtr floor);

    [LibraryImport(Library, EntryPoint = "laplace_grapheme_floor_graph_first_idx")]
    internal static partial nuint GraphemeFloorGraphFirstIdx(IntPtr floor);

    [LibraryImport(Library, EntryPoint = "laplace_grapheme_floor_graph_count")]
    internal static partial nuint GraphemeFloorGraphCount(IntPtr floor);

    [LibraryImport(Library, EntryPoint = "laplace_grapheme_floor_leaf_text_off")]
    internal static partial uint* GraphemeFloorLeafTextOff(IntPtr floor);

    [LibraryImport(Library, EntryPoint = "laplace_grapheme_floor_leaf_text_len")]
    internal static partial uint* GraphemeFloorLeafTextLen(IntPtr floor);

    [LibraryImport(Library, EntryPoint = "laplace_grapheme_floor_cp_to_graph")]
    internal static partial uint* GraphemeFloorCpToGraph(IntPtr floor);

    [LibraryImport(Library, EntryPoint = "laplace_grapheme_floor_free_owned")]
    internal static partial void GraphemeFloorFreeOwned(IntPtr floor);

    

    [LibraryImport(Library, EntryPoint = "laplace_grammar_tags_run")]
    internal static partial int GrammarTagsRun(
        IntPtr lang, byte* tagsScm, nuint tagsLen, byte* utf8, nuint len,
        LaplaceTag** outTags, nuint* outN);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_tags_free")]
    internal static partial void GrammarTagsFree(LaplaceTag* tags);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_compose", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GrammarCompose(
        byte* utf8, nuint len, IntPtr ast, string modalityId,
        Hash128 sourceId, Hash128 typeMetaId, IntPtr* outResult);

    [LibraryImport(Library, EntryPoint = "laplace_compose_result_free")]
    public static partial void ComposeResultFree(IntPtr result);

    [LibraryImport(Library, EntryPoint = "laplace_compose_entity_count")]
    public static partial nuint ComposeEntityCount(IntPtr result);

    [LibraryImport(Library, EntryPoint = "laplace_compose_physicality_count")]
    public static partial nuint ComposePhysicalityCount(IntPtr result);

    [LibraryImport(Library, EntryPoint = "laplace_compose_precedes_count")]
    public static partial nuint ComposePrecedesCount(IntPtr result);

    [LibraryImport(Library, EntryPoint = "laplace_compose_root_id")]
    public static partial Hash128 ComposeRootId(IntPtr result);

    [LibraryImport(Library, EntryPoint = "laplace_compose_get_entity")]
    public static partial int ComposeGetEntity(IntPtr result, nuint i, ComposeEntityNative* outEntity);

    [LibraryImport(Library, EntryPoint = "laplace_compose_get_physicality")]
    public static partial int ComposeGetPhysicality(IntPtr result, nuint i, ComposePhysicalityNative* outPhys);

    [LibraryImport(Library, EntryPoint = "laplace_compose_get_precedes")]
    public static partial int ComposeGetPrecedes(IntPtr result, nuint i, ComposePrecedesNative* outPrec);

    [LibraryImport(Library, EntryPoint = "laplace_compose_span_lookup")]
    public static partial int ComposeSpanLookup(IntPtr result, uint startByte, uint endByte, Hash128* outId);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_row_iter_new")]
    public static partial int GrammarRowIterNew(IntPtr recipe, IntPtr* outIter);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_row_iter_feed")]
    public static partial int GrammarRowIterFeed(
        IntPtr iter, byte* chunk, nuint len, ParsedRowNative** outRows, nuint* outCount);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_row_iter_free")]
    public static partial void GrammarRowIterFree(IntPtr iter);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_row_iter_free_rows")]
    public static partial void GrammarRowIterFreeRows(ParsedRowNative* rows, nuint count);

    [StructLayout(LayoutKind.Sequential)]
    public struct ParsedRowNative
    {
        public IntPtr Ast;
        public IntPtr RowUtf8;
        public UIntPtr RowLen;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ComposeEntityNative
    {
        public Hash128 Id;
        public byte Tier;
        public byte Pad0, Pad1, Pad2;
        public Hash128 TypeId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ComposePhysicalityNative
    {
        public Hash128 Id;
        public Hash128 EntityId;
        public Hash128 SourceId;
        public double Coord0, Coord1, Coord2, Coord3;
        public Hilbert128 Hilbert;
        public IntPtr TrajectoryXyzm;
        public UIntPtr TrajectoryN;
        public UIntPtr NConstituents;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ComposePrecedesNative
    {
        public Hash128 SubjectId;
        public Hash128 ObjectId;
        public long Games;
    }

    [LibraryImport(Library, EntryPoint = "content_witness_batch_add")]
    internal static partial int ContentWitnessBatchAdd(
        IntPtr stage,
        byte* utf8,
        nuint len,
        Hash128* sourceId,
        Hash128* outRootId);

    [LibraryImport(Library, EntryPoint = "content_witness_reset")]
    internal static partial void ContentWitnessReset();

    [LibraryImport(Library, EntryPoint = "laplace_content_root_id")]
    internal static partial int ContentRootId(
        byte* utf8,
        nuint len,
        Hash128* outRootId);

    [LibraryImport(Library, EntryPoint = "laplace_relation_resolve", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int RelationResolve(string surface, Hash128* outTypeId);

    [LibraryImport(Library, EntryPoint = "laplace_relation_resolve_surface", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int RelationResolveSurface(
        string surface,
        Hash128* outTypeId,
        double* outRank,
        int* outSymmetry,
        byte* outFlip,
        Hash128* outParentId);

    [LibraryImport(Library, EntryPoint = "laplace_relation_type_id", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int RelationTypeIdNative(string canonicalName, Hash128* outTypeId);

    [LibraryImport(Library, EntryPoint = "laplace_relation_manifest_count")]
    internal static partial nuint RelationManifestCount();

    [LibraryImport(Library, EntryPoint = "laplace_relation_manifest_canonical")]
    internal static partial IntPtr RelationManifestCanonical(nuint idx);

    [LibraryImport(Library, EntryPoint = "laplace_relation_canonical_for_type_id")]
    internal static partial IntPtr RelationCanonicalForTypeId(Hash128* typeId);

    [LibraryImport(Library, EntryPoint = "laplace_relation_resolve_deprel", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int RelationResolveDeprel(
        string deprel,
        Hash128* outTypeId,
        double* outRank,
        int* outSymmetry,
        byte* outFlip,
        Hash128* outParentId);

    [LibraryImport(Library, EntryPoint = "laplace_relation_resolve_enhanced_deprel", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int RelationResolveEnhancedDeprel(
        string deprel,
        Hash128* outTypeId,
        double* outRank,
        int* outSymmetry,
        byte* outFlip,
        Hash128* outParentId);

    [LibraryImport(Library, EntryPoint = "laplace_relation_resolve_feature", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int RelationResolveFeature(
        string featureName,
        Hash128* outTypeId,
        double* outRank,
        int* outSymmetry,
        byte* outFlip,
        Hash128* outParentId);

    [LibraryImport(Library, EntryPoint = "laplace_attestation_categorical_build", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int AttestationCategoricalBuild(
        string surfaceRelation,
        Hash128* subjectId,
        Hash128* objectId,
        byte objectIsNull,
        Hash128* source,
        Hash128* context,
        byte contextIsNull,
        double trustWeight,
        int confirm,
        long observationCount,
        long nowUnixUs,
        AttestationStagedNative* outStaged);

    [LibraryImport(Library, EntryPoint = "laplace_attestation_resolved_build")]
    internal static partial int AttestationResolvedBuild(
        Hash128* subjectId,
        Hash128* typeId,
        Hash128* objectId,
        byte objectIsNull,
        Hash128* source,
        Hash128* context,
        byte contextIsNull,
        double witnessWeight,
        int confirm,
        long observationCount,
        long nowUnixUs,
        AttestationStagedNative* outStaged);

    [LibraryImport(Library, EntryPoint = "laplace_attestation_categorical_scored_build", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int AttestationCategoricalScoredBuild(
        string surfaceRelation,
        Hash128* subjectId,
        Hash128* objectId,
        byte objectIsNull,
        Hash128* source,
        Hash128* context,
        byte contextIsNull,
        double trustWeight,
        double magnitude,
        double arenaScale,
        long observationCount,
        long nowUnixUs,
        AttestationStagedNative* outStaged);

    [LibraryImport(Library, EntryPoint = "laplace_attestation_resolved_scored_build")]
    internal static partial int AttestationResolvedScoredBuild(
        Hash128* subjectId,
        Hash128* typeId,
        Hash128* objectId,
        byte objectIsNull,
        Hash128* source,
        Hash128* context,
        byte contextIsNull,
        double witnessWeight,
        double magnitude,
        double arenaScale,
        long observationCount,
        long nowUnixUs,
        AttestationStagedNative* outStaged);

    [LibraryImport(Library, EntryPoint = "laplace_attestation_aggregated_batch_build")]
    internal static partial int AttestationAggregatedBatchBuild(
        AttestationAggregatedCellNative* cells,
        nuint count,
        Hash128* typeId,
        Hash128* source,
        Hash128* context,
        byte contextIsNull,
        double witnessWeight,
        long nowUnixUs,
        AttestationStagedNative* outStaged);

    [LibraryImport(Library, EntryPoint = "laplace_score_batch_fp")]
    internal static partial void ScoreBatchFp(
        float* values,
        nuint count,
        double arenaScale,
        long* outFp);

    [LibraryImport(Library, EntryPoint = "laplace_attestation_aggregated_build")]
    internal static partial int AttestationAggregatedBuild(
        Hash128* subjectId,
        Hash128* typeId,
        Hash128* objectId,
        byte objectIsNull,
        Hash128* source,
        Hash128* context,
        byte contextIsNull,
        double witnessWeight,
        long games,
        long sumScoreFp1e9,
        long nowUnixUs,
        AttestationStagedNative* outStaged);

    [LibraryImport(Library, EntryPoint = "laplace_pos_resolve_entity", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PosResolveEntity(string tag, int tagset, Hash128* outEntityId);

    [LibraryImport(Library, EntryPoint = "laplace_pos_upos_canonical")]
    internal static partial byte** PosUposCanonical(nuint* outCount);

    [LibraryImport(Library, EntryPoint = "laplace_attestation_categorical_add", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int AttestationCategoricalAdd(
        IntPtr stage,
        string surfaceRelation,
        Hash128* subjectId,
        Hash128* objectId,
        byte objectIsNull,
        Hash128* source,
        Hash128* context,
        byte contextIsNull,
        double trustWeight,
        int confirm,
        long observationCount);

    [LibraryImport(Library, EntryPoint = "laplace_attestation_witness_phi")]
    internal static partial double AttestationWitnessPhi(double witnessWeight);

    [LibraryImport(Library, EntryPoint = "laplace_score_fp")]
    internal static partial long ScoreFp(double v, double m);
}
