using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static unsafe partial class NativeInterop
{
    private const string Library = "laplace_core";

    // The tablebase prober is a separate library, not laplace_core, so it is never linked
    // into PostgreSQL backends through the laplace_substrate extension. It is built only
    // when the vendored prober is present; otherwise calls raise DllNotFoundException.
    private const string SyzygyLibrary = "laplace_syzygy";

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

    // Syzygy tablebase probe kernel (engine/core/src/syzygy.c over the vendored
    // Fathom prober, built as the separate laplace_syzygy library). Callers serialize
    // every entry point on SyzygyNative's mapping gate.
    [LibraryImport(SyzygyLibrary, EntryPoint = "laplace_syzygy_init", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int SyzygyInit(string path);

    [LibraryImport(SyzygyLibrary, EntryPoint = "laplace_syzygy_free")]
    internal static partial void SyzygyFree();

    [LibraryImport(SyzygyLibrary, EntryPoint = "laplace_syzygy_largest")]
    internal static partial int SyzygyLargest();

    [LibraryImport(SyzygyLibrary, EntryPoint = "laplace_syzygy_probe_wdl")]
    internal static partial int SyzygyProbeWdl(
        ulong white, ulong black, ulong kings, ulong queens, ulong rooks,
        ulong bishops, ulong knights, ulong pawns, uint ep, int whiteToMove);

    [LibraryImport(SyzygyLibrary, EntryPoint = "laplace_syzygy_probe_root")]
    internal static partial int SyzygyProbeRoot(
        ulong white, ulong black, ulong kings, ulong queens, ulong rooks,
        ulong bishops, ulong knights, ulong pawns, uint ep, int whiteToMove,
        out int wdl, out int dtz, out int from, out int to, out int promotes);

    [LibraryImport(Library, EntryPoint = "laplace_testimony_pack_walk")]
    internal static partial int LaplaceTestimonyPackWalk(
        Hash128* objectIds, long* scoresFp1e9, ushort* games, nuint n, double* out4n);

    [LibraryImport(Library, EntryPoint = "laplace_testimony_unpack_vertex")]
    internal static partial int LaplaceTestimonyUnpackVertex(
        double* vertex, Hash128* objectId, long* scoreFp1e9, ushort* games, ushort* ordinal);

    [LibraryImport(Library, EntryPoint = "laplace_factor_pack_values")]
    internal static partial int LaplaceFactorPackValues(
        float* values, nuint n, double* out4, nuint* outVertices);

    [LibraryImport(Library, EntryPoint = "laplace_factor_unpack_vertex")]
    internal static partial int LaplaceFactorUnpackVertex(
        double* vertex, float* outValues6, byte* outCount);

    [LibraryImport(Library, EntryPoint = "laplace_score_fp")]
    internal static partial long LaplaceScoreFp(double v, double m);

    // The draw threshold and the outcome rule live in attestation_engine.c and are
    // called, not restated. SuppressGCTransition: both are leaf integer functions
    // with no allocation, no callback and no blocking, so the full GC transition
    // costs more than the work on the per-cell merge path.
    [LibraryImport(Library, EntryPoint = "laplace_attestation_outcome_from_totals_fp")]
    [System.Runtime.InteropServices.SuppressGCTransition]
    internal static partial int LaplaceAttestationOutcomeFromTotalsFp(
        long games, long sumScoreFp, short* outOutcome);

    [LibraryImport(Library, EntryPoint = "laplace_attestation_score_draw_fp")]
    [System.Runtime.InteropServices.SuppressGCTransition]
    internal static partial long LaplaceAttestationScoreDrawFp();

    [LibraryImport(Library, EntryPoint = "laplace_score_inverse_fp")]
    internal static partial double LaplaceScoreInverseFp(long scoreFp, double m);

    [LibraryImport(Library, EntryPoint = "laplace_score_batch_fp")]
    internal static partial void LaplaceScoreBatchFp(float* w, nuint n, double m, long* outScores);

    [LibraryImport(Library, EntryPoint = "super_fibonacci")]
    internal static partial void SuperFibonacci(nuint n, double* outQuats);

    [LibraryImport(Library, EntryPoint = "laplace_byte_atoms_copy")]
    internal static partial int ByteAtomsCopy(Hash128* ids, double* coordinates,
        Hilbert128* hilberts, nuint capacity);

    [LibraryImport(Library, EntryPoint = "math4d_centroid")]
    internal static partial void Math4dCentroid(double* points, nuint nPoints, double* out4);

    [LibraryImport(Library, EntryPoint = "math4d_karcher_mean")]
    internal static partial void Math4dKarcherMean(
        double* points, nuint nPoints, double* weights, double tol, int maxIters, double* out4);

    [LibraryImport(Library, EntryPoint = "math4d_frechet")]
    internal static partial double Math4dFrechet(double* p, nuint np, double* q, nuint nq);

    [LibraryImport(Library, EntryPoint = "math4d_hausdorff")]
    internal static partial double Math4dHausdorff(double* a, nuint na, double* b, nuint nb);

    [LibraryImport(Library, EntryPoint = "trajectory_build")]
    internal static partial int TrajectoryBuild(Hash128* entityHashes, nuint n, double* outXyzm);

    [LibraryImport(Library, EntryPoint = "trajectory_build_flagged")]
    internal static partial int TrajectoryBuildFlagged(Hash128* entityHashes, ulong* flags, nuint n, double* outXyzm);

    [LibraryImport(Library, EntryPoint = "trajectory_build_rle")]
    internal static partial int TrajectoryBuildRle(Hash128* constituents, nuint n, double* outXyzm, nuint* outVertexCount);

    [LibraryImport(Library, EntryPoint = "trajectory_build_flagged_rle")]
    internal static partial int TrajectoryBuildFlaggedRle(Hash128* constituents, ulong* flags, nuint n, double* outXyzm, nuint* outVertexCount);

    [LibraryImport(Library, EntryPoint = "trajectory_constituent_count")]
    internal static partial int TrajectoryConstituentCount(double* trajectoryXyzm, nuint nPoints, nuint* outCount);

    [LibraryImport(Library, EntryPoint = "trajectory_constituents")]
    internal static partial int TrajectoryConstituents(double* trajectoryXyzm, nuint nPoints, Hash128* outHashes, nuint outCap);

    [LibraryImport(Library, EntryPoint = "hilbert4d_encode")]
    internal static partial void Hilbert4dEncode(double* point, Hilbert128* outHb);

    [LibraryImport(Library, EntryPoint = "hilbert4d_decode")]
    internal static partial void Hilbert4dDecode(Hilbert128* hb, double* outPoint);

    [LibraryImport(Library, EntryPoint = "hilbert128_compare")]
    internal static partial int Hilbert128Compare(Hilbert128* a, Hilbert128* b);

    [LibraryImport(Library, EntryPoint = "codepoint_table_load_perfcache", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int CodepointTableLoadPerfcache(string path);

    [LibraryImport(Library, EntryPoint = "codepoint_table_unload")]
    internal static partial void CodepointTableUnload();

    [LibraryImport(Library, EntryPoint = "codepoint_table_is_loaded")]
    internal static partial int CodepointTableIsLoaded();

    [LibraryImport(Library, EntryPoint = "codepoint_table_copy_receipt")]
    internal static partial int CodepointTableCopyReceipt(Hash128* outReceipt);

    // Game-position perfcache map, loaded and read natively like the codepoint perfcache.
    [LibraryImport(Library, EntryPoint = "chess_position_table_load", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ChessPositionTableLoad(string path);

    [LibraryImport(Library, EntryPoint = "chess_position_table_unload")]
    internal static partial void ChessPositionTableUnload();

    [LibraryImport(Library, EntryPoint = "chess_position_table_is_loaded")]
    internal static partial int ChessPositionTableIsLoaded();

    [LibraryImport(Library, EntryPoint = "chess_position_table_record_count")]
    internal static partial int ChessPositionTableRecordCount(ulong* outCount);

    [LibraryImport(Library, EntryPoint = "chess_position_table_lookup_geom")]
    internal static partial int ChessPositionTableLookupGeom(
        Hash128* id, double* outCoord, Hilbert128* outHb, uint* outN, byte* outTier);

    [LibraryImport(Library, EntryPoint = "codepoint_table_records")]
    internal static partial int CodepointTableRecords(CodepointRecord** outRecords, ulong* outCount);

    [LibraryImport(Library, EntryPoint = "codepoint_table_lookup_id")]
    internal static partial int CodepointTableLookupId(Hash128* id, uint* outCp);

    [LibraryImport(Library, EntryPoint = "codepoint_table_lookup")]
    internal static partial CodepointRecord* CodepointTableLookup(uint scalar);

    [LibraryImport(Library, EntryPoint = "codepoint_table_flags")]
    internal static partial uint CodepointTableFlags(uint scalar);

    [LibraryImport(Library, EntryPoint = "codepoint_table_presence_bitmap")]
    internal static partial int CodepointTablePresenceBitmap(
        Hash128* ids, nuint count, byte* bitmap, nuint bitmapBytes);

    [LibraryImport(Library, EntryPoint = "tier_tree_new")]
    internal static partial IntPtr TierTreeNew(nuint capacityHint);

    [LibraryImport(Library, EntryPoint = "tier_tree_free")]
    internal static partial void TierTreeFree(IntPtr tree);

    [LibraryImport(Library, EntryPoint = "tier_tree_node_count")]
    internal static partial nuint TierTreeNodeCount(IntPtr tree);

    [LibraryImport(Library, EntryPoint = "tier_tree_capacity")]
    internal static partial nuint TierTreeCapacity(IntPtr tree);

    [LibraryImport(Library, EntryPoint = "tier_tree_resident_bytes")]
    internal static partial nuint TierTreeResidentBytes(IntPtr tree);

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

    [LibraryImport(Library, EntryPoint = "laplace_text_decomposer_run_source")]
    internal static partial int TextDecomposerRunSource(byte* utf8, nuint len, IntPtr* outTree);

    [LibraryImport(Library, EntryPoint = "laplace_normalize_nfc_utf8")]
    public static partial int NormalizeNfcUtf8(
        byte* utf8, nuint len, byte** outUtf8, nuint* outLen);

    [LibraryImport(Library, EntryPoint = "laplace_identity_key_normalize_utf8")]
    internal static partial int IdentityKeyNormalizeUtf8(
        byte* utf8, nuint len, byte** outUtf8, nuint* outLen);

    [LibraryImport(Library, EntryPoint = "laplace_xml_stream_new")]
    internal static partial int XmlStreamNew(IntPtr* outStream);

    [LibraryImport(Library, EntryPoint = "laplace_xml_stream_feed")]
    internal static partial int XmlStreamFeed(
        IntPtr stream,
        byte* utf8,
        nuint length,
        int final,
        NativeXmlEvent** outEvents,
        nuint* outCount);

    [LibraryImport(Library, EntryPoint = "laplace_xml_stream_error")]
    internal static partial IntPtr XmlStreamError(IntPtr stream);

    [LibraryImport(Library, EntryPoint = "laplace_xml_stream_free")]
    internal static partial void XmlStreamFree(IntPtr stream);

    [LibraryImport(Library, EntryPoint = "tier_tree_id_array")]
    internal static partial IntPtr TierTreeIdArray(IntPtr tree);

    [LibraryImport(Library, EntryPoint = "hash_composer_run")]
    internal static partial int HashComposerRun(
        IntPtr tree,
        delegate* unmanaged[Cdecl]<uint, IntPtr, Hash128*, double*, Hilbert128*, int> resolver,
        IntPtr resolverUserData);

    [LibraryImport(Library, EntryPoint = "hash_composer_run_workers")]
    internal static partial int HashComposerRunWorkers(
        IntPtr tree,
        delegate* unmanaged[Cdecl]<uint, IntPtr, Hash128*, double*, Hilbert128*, int> resolver,
        IntPtr resolverUserData,
        nuint workerCount);

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

    [LibraryImport(Library, EntryPoint = "intent_stage_semantic_digest_batch")]
    internal static partial int IntentStageSemanticDigestBatch(IntPtr* stages, nuint count, Hash128* result);

    [LibraryImport(Library, EntryPoint = "intent_stage_copy_column_list")]
    internal static partial IntPtr IntentStageCopyColumnList(int table);

    [LibraryImport(Library, EntryPoint = "intent_stage_add_entity")]
    internal static partial int IntentStageAddEntity(
        IntPtr stage, Hash128* id, short tier, Hash128* typeId);

    [LibraryImport(Library, EntryPoint = "intent_stage_deposit_lexical_upos")]
    internal static partial int IntentStageDepositLexicalUpos(
        IntPtr stage, Hash128* entityId, double* coord, Hilbert128* hilbertIndex, int slot);

    [LibraryImport(Library, EntryPoint = "intent_stage_add_physicality")]
    internal static partial int IntentStageAddPhysicality(
        IntPtr stage,
        Hash128* id, Hash128* entityId,
        short physicalityType,
        double* coord, Hilbert128* hilbertIndex,
        double* trajectoryXyzm, uint trajectoryNVertices,
        int nConstituents,
        int alignmentResidualIsNull, double alignmentResidual,
        int sourceDimIsNull, int sourceDim,
        long observedAtUnixUs);

    [LibraryImport(Library, EntryPoint = "intent_stage_add_attestation")]
    internal static partial int IntentStageAddAttestation(
        IntPtr stage,
        Hash128* id, Hash128* subjectId, Hash128* typeId,
        Hash128* objectId, Hash128* sourceId, Hash128* contextId,
        short outcome,
        long lastObservedAtUnixUs, long observationCount,
        long sumScoreFp1e9, long opponentRdFp1e9, long opponentRatingFp1e9,
        byte* qualifierMask);

    [LibraryImport(Library, EntryPoint = "intent_stage_emit_copy_binary")]
    internal static partial nuint IntentStageEmitCopyBinary(
        IntPtr stage, int table, byte* buf, nuint bufCapacity);

    [LibraryImport(Library, EntryPoint = "intent_stage_tuple_ptr")]
    internal static partial byte* IntentStageTuplePtr(
        IntPtr stage, int table, nuint* outLen);

    [LibraryImport(Library, EntryPoint = "intent_stage_witness_seen")]
    internal static partial int IntentStageWitnessSeen(IntPtr stage, Hash128* id);

    [LibraryImport(Library, EntryPoint = "intent_stage_partition")]
    internal static partial int IntentStagePartition(IntPtr src, nuint partCount, IntPtr* outParts);

    [LibraryImport(Library, EntryPoint = "glicko2_effective_mu")]
    internal static partial long Glicko2EffectiveMu(Glicko2State* state);

    [LibraryImport(Library, EntryPoint = "glicko2_update_period")]
    internal static partial void Glicko2UpdatePeriod(
        Glicko2State* state,
        Glicko2Observation* obs,
        nuint n,
        long tau,
        long nowNs);

    [LibraryImport(Library, EntryPoint = "glicko2_init")]
    internal static partial void Glicko2Init(
        Glicko2State* state, long ratingFp1e9, long rdFp1e9, long volatilityFp1e9);

    [LibraryImport(Library, EntryPoint = "glicko2_fold_uniform_period")]
    internal static partial void Glicko2FoldUniformPeriod(
        Glicko2State* state,
        long opponentRatingFp1e9,
        long opponentPhiFp1e9,
        long games,
        long sumScoreFp1e9,
        long tau,
        long nowNs);

    [LibraryImport(Library, EntryPoint = "glicko2_fold_grouped_period")]
    internal static partial int Glicko2FoldGroupedPeriod(
        Glicko2State* state,
        long* opponentRatingsFp1e9,
        long* opponentPhisFp1e9,
        long* games,
        long* sumScoresFp1e9,
        nuint groupCount,
        long tau,
        long nowNs);

    [LibraryImport(Library, EntryPoint = "laplace_glicko2_neutral_mu_fp")]
    internal static partial long Glicko2NeutralMuFp();



    [LibraryImport(Library, EntryPoint = "laplace_grammar_lookup_by_id", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr GrammarLookupById(string modalityId);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_lookup_by_ext", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr GrammarLookupByExt(string ext);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_modality_by_ext", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr GrammarModalityByExt(string ext);

    [LibraryImport(Library, EntryPoint = "laplace_json_string_decode")]
    internal static partial int JsonStringDecode(byte* input, nuint length,
        byte* output, nuint capacity, nuint* written);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_parse")]
    internal static partial int GrammarParse(byte* utf8, nuint len, IntPtr recipe, IntPtr* outAst);

    [LibraryImport(Library, EntryPoint = "laplace_ast_node_count")]
    internal static partial nuint AstNodeCount(IntPtr ast);

    [LibraryImport(Library, EntryPoint = "laplace_ast_get_diagnostics")]
    internal static partial int AstGetDiagnostics(IntPtr ast, GrammarAstDiagnostics* diagnostics);

    [LibraryImport(Library, EntryPoint = "laplace_ast_get_node")]
    internal static partial int AstGetNode(IntPtr ast, nuint idx, LaplaceAstNode* outNode);

    [LibraryImport(Library, EntryPoint = "laplace_fide_xml_project")]
    internal static partial int FideXmlProject(
        byte* utf8, nuint length, FidePlayerProjection* outPlayers,
        nuint capacity, nuint* outCount);

    [LibraryImport(Library, EntryPoint = "laplace_ast_type_name")]
    internal static partial IntPtr AstTypeName(IntPtr ast, uint nodeTypeId);

    [LibraryImport(Library, EntryPoint = "laplace_ast_free")]
    internal static partial void AstFree(IntPtr ast);



    [LibraryImport(Library, EntryPoint = "hash_composer_compose_node")]
    internal static partial int HashComposerComposeNode(
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

    [LibraryImport(Library, EntryPoint = "laplace_grammar_compose_probe", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int GrammarComposeProbe(
        byte* utf8, nuint len, IntPtr ast, string modalityId,
        Hash128 sourceId, Hash128 typeMetaId, IntPtr* outResult);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_source_compose", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int GrammarSourceCompose(
        byte* utf8, nuint len, IntPtr ast, string modalityId, IntPtr* outResult);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_compose_materialize_phys", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int GrammarComposeMaterializePhys(
        IntPtr result, byte* utf8, nuint len, IntPtr ast, string modalityId);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_compose_row_root", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GrammarComposeRowRoot(
        byte* utf8, nuint len, IntPtr ast, string modalityId, Hash128* outId, byte* outTier);

    [LibraryImport(Library, EntryPoint = "laplace_compose_result_free")]
    public static partial void ComposeResultFree(IntPtr result);

    [LibraryImport(Library, EntryPoint = "laplace_compose_resident_bytes")]
    public static partial nuint ComposeResidentBytes(IntPtr result);

    [LibraryImport(Library, EntryPoint = "laplace_ast_resident_bytes")]
    public static partial nuint AstResidentBytes(IntPtr ast);

    [LibraryImport(Library, EntryPoint = "laplace_compose_entity_count")]
    public static partial nuint ComposeEntityCount(IntPtr result);

    [LibraryImport(Library, EntryPoint = "laplace_compose_physicality_count")]
    public static partial nuint ComposePhysicalityCount(IntPtr result);

    [LibraryImport(Library, EntryPoint = "laplace_compose_precedes_count")]
    public static partial nuint ComposePrecedesCount(IntPtr result);

    [LibraryImport(Library, EntryPoint = "laplace_compose_root_id")]
    public static partial Hash128 ComposeRootId(IntPtr result);

    [LibraryImport(Library, EntryPoint = "laplace_compose_root_placement")]
    internal static partial int ComposeRootPlacement(
        IntPtr result, double* coord4, byte* outTier, uint* outAtom, byte* outHasAtom);


    [LibraryImport(Library, EntryPoint = "laplace_compose_get_tier_tree")]
    public static partial IntPtr ComposeGetTierTree(IntPtr result);

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

    [LibraryImport(Library, EntryPoint = "laplace_grammar_row_iter_feed_parsed")]
    public static partial int GrammarRowIterFeedParsed(
        IntPtr iter, byte* chunk, nuint len, ParsedRowNative** outRows, nuint* outCount);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_row_iter_feed_lines")]
    public static partial int GrammarRowIterFeedLines(
        IntPtr iter, byte* chunk, nuint len, RawRowNative** outRows, nuint* outCount);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_row_iter_parse_row")]
    public static partial int GrammarRowIterParseRow(
        IntPtr iter, byte* rowUtf8, nuint rowLen, IntPtr* outAst);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_row_iter_free")]
    public static partial void GrammarRowIterFree(IntPtr iter);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_row_iter_free_rows")]
    public static partial void GrammarRowIterFreeRows(ParsedRowNative* rows, nuint count);

    [LibraryImport(Library, EntryPoint = "laplace_grammar_row_iter_free_lines")]
    public static partial void GrammarRowIterFreeLines(RawRowNative* rows, nuint count);
}
