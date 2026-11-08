using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static unsafe partial class NativeInterop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RawRowNative
    {
        public IntPtr RowUtf8;
        public UIntPtr RowLen;
    }

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
        /// <summary>Navigation-only node: present for span/containment lookup, never a row.</summary>
        public byte Packaging;
        public byte Pad1, Pad2;
        public Hash128 TypeId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ComposePhysicalityNative
    {
        public Hash128 Id;
        public Hash128 EntityId;
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

    [LibraryImport(Library, EntryPoint = "content_witness_batch_add_workers")]
    internal static partial int ContentWitnessBatchAddWorkers(
        IntPtr stage,
        byte* utf8,
        nuint len,
        Hash128* sourceId,
        nuint workerCount,
        Hash128* outRootId);

    [LibraryImport(Library, EntryPoint = "content_witness_tree_build")]
    internal static partial int ContentWitnessTreeBuild(
        byte* utf8,
        nuint len,
        IntPtr* outTree);

    [LibraryImport(Library, EntryPoint = "content_witness_tree_build_workers")]
    internal static partial int ContentWitnessTreeBuildWorkers(
        byte* utf8,
        nuint len,
        nuint workerCount,
        IntPtr* outTree);

    [LibraryImport(Library, EntryPoint = "content_witness_source_tree_build")]
    internal static partial int ContentWitnessSourceTreeBuild(
        byte* utf8,
        nuint len,
        IntPtr* outTree);

    [LibraryImport(Library, EntryPoint = "content_witness_emit_tree")]
    internal static partial int ContentWitnessEmitTree(
        IntPtr stage,
        IntPtr tree,
        Hash128* sourceId,
        byte* existingBitmap,
        nuint bitmapBits,
        Hash128* outRootId);

    [LibraryImport(Library, EntryPoint = "content_witness_tree_root_id")]
    internal static partial int ContentWitnessTreeRootId(
        IntPtr tree,
        Hash128* outRootId);

    [LibraryImport(Library, EntryPoint = "content_witness_reset")]
    internal static partial void ContentWitnessReset();

    [LibraryImport(Library, EntryPoint = "laplace_content_root_id")]
    internal static partial int ContentRootId(
        byte* utf8,
        nuint len,
        Hash128* outRootId);

    [LibraryImport(Library, EntryPoint = "laplace_content_source_root_id")]
    internal static partial int ContentSourceRootId(
        byte* utf8,
        nuint len,
        Hash128* outRootId);

    // Native definitions of PhysicalityId.Compute / TierTree.CollapseIndex. Layout and
    // collapse rule are identity axioms; the managed forms are checked against these.
    [LibraryImport(Library, EntryPoint = "laplace_physicality_id_compute")]
    internal static partial void PhysicalityIdCompute(
        Hash128 entityId,
        short physicalityType,
        Hash128* outId);

    [LibraryImport(Library, EntryPoint = "laplace_tier_tree_collapse_index")]
    internal static partial uint TierTreeCollapseIndex(IntPtr tree, uint idx);

    [LibraryImport(Library, EntryPoint = "laplace_ordered_composition_stage_batch")]
    internal static partial int OrderedCompositionStageBatch(
        IntPtr stage,
        OrderedCompositionRequestNative* requests,
        nuint requestCount,
        OrderedCompositionResultNative* outResults);

    [LibraryImport(Library, EntryPoint = "laplace_ordered_composition_compose_batch")]
    internal static partial int OrderedCompositionComposeBatch(
        OrderedCompositionRequestNative* requests,
        nuint requestCount,
        OrderedCompositionResultNative* outResults);

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct OrderedCompositionComponentNative
    {
        public Hash128 Id;
        public double Coord0, Coord1, Coord2, Coord3;
        public uint Atom;
        public byte Tier;
        public byte HasAtom;
        private byte _pad1, _pad2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct OrderedCompositionRequestNative
    {
        public IntPtr Components;
        public UIntPtr ComponentCount;
        public Hash128 TypeId;
        public Hash128 SourceId;
        public long ObservedAtUnixUs;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct OrderedCompositionResultNative
    {
        public Hash128 Id;
        public double Coord0, Coord1, Coord2, Coord3;
        public Hilbert128 Hilbert;
        public byte Tier;
        private byte _pad1, _pad2, _pad3, _pad4, _pad5, _pad6, _pad7;
        public nuint FirstPhysicalityRow;
        public nuint EmittedPhysicalityRows;
    }

    // --- Modality ladders above shared codepoint T0 (packaging buffers in, compose out) ---
    // Identity is the digit→number→… composition trajectory. RGBA / PCM16 are recovery
    // inputs only; they never mint Tier-0 leaves.

    [LibraryImport(Library, EntryPoint = "laplace_image_tree_build")]
    internal static partial int ImageTreeBuild(
        byte* rgba, uint width, uint height, IntPtr* outTree);

    [LibraryImport(Library, EntryPoint = "laplace_audio_tree_build")]
    internal static partial int AudioTreeBuild(
        short* pcm, nuint nSamples, IntPtr* outTree);

    [LibraryImport(Library, EntryPoint = "laplace_modality_witness_emit_tree")]
    internal static partial int ModalityWitnessEmitTree(
        IntPtr stage,
        IntPtr tree,
        int ladderKind,
        Hash128* sourceId,
        byte* existingBitmap,
        nuint bitmapBits,
        Hash128* outRootId);

    [LibraryImport(Library, EntryPoint = "laplace_image_root_id")]
    internal static partial int ImageRootId(
        byte* rgba, uint width, uint height, Hash128* outRootId);

    [LibraryImport(Library, EntryPoint = "laplace_audio_root_id")]
    internal static partial int AudioRootId(
        short* pcm, nuint nSamples, Hash128* outRootId);

    // --- Packaging decode (JPEG/PNG/… → RGBA recovery; MP3/FLAC/Ogg/WAV → mono s16 recovery) ---

    [LibraryImport(Library, EntryPoint = "laplace_media_free")]
    internal static partial void MediaFree(IntPtr p);

    [LibraryImport(Library, EntryPoint = "laplace_media_decode_image_file", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int MediaDecodeImageFile(string path, out MediaImageNative outImage);

    [LibraryImport(Library, EntryPoint = "laplace_media_decode_audio_file", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int MediaDecodeAudioFile(string path, out MediaAudioNative outAudio);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MediaImageNative
    {
        public IntPtr Rgba;
        public uint Width;
        public uint Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MediaAudioNative
    {
        public IntPtr Pcm;
        public nuint NSamples;
        public int SampleRate;
    }

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

    [LibraryImport(Library, EntryPoint = "laplace_relation_surface_qualifier", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int RelationSurfaceQualifier(string surface);

    [LibraryImport(Library, EntryPoint = "laplace_entity_type_id", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int EntityTypeIdNative(string canonicalName, Hash128* outTypeId);

    [LibraryImport(Library, EntryPoint = "laplace_relation_manifest_count")]
    internal static partial nuint RelationManifestCount();

    [LibraryImport(Library, EntryPoint = "laplace_relation_manifest_canonical")]
    internal static partial IntPtr RelationManifestCanonical(nuint idx);

    [LibraryImport(Library, EntryPoint = "laplace_relation_manifest_successor")]
    internal static partial IntPtr RelationManifestSuccessor(nuint idx);

    [LibraryImport(Library, EntryPoint = "laplace_relation_canonical_for_type_id")]
    internal static partial IntPtr RelationCanonicalForTypeId(Hash128* typeId);

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

    [LibraryImport(Library, EntryPoint = "laplace_attestation_resolved_outcome_build")]
    internal static partial int AttestationResolvedOutcomeBuild(
        Hash128* subjectId,
        Hash128* typeId,
        Hash128* objectId,
        byte objectIsNull,
        Hash128* source,
        Hash128* context,
        byte contextIsNull,
        double witnessWeight,
        short outcome,
        long observationCount,
        long nowUnixUs,
        AttestationStagedNative* outStaged);

    [LibraryImport(Library, EntryPoint = "laplace_attestation_corroboration_mask")]
    internal static partial int AttestationCorroborationMask(
        short* left, short* right, nuint count, byte* admitted);

    [LibraryImport(Library, EntryPoint = "laplace_attestation_resolved_witness_parameters")]
    internal static partial int AttestationResolvedWitnessParameters(
        Hash128* typeId, double sourceTrust, long* opponentRating, long* opponentRd);

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

    [LibraryImport(Library, EntryPoint = "laplace_attestation_staged_batch_add")]
    internal static unsafe partial int AttestationStagedBatchAdd(
        IntPtr stage, AttestationStagedNative* rows, nuint n, byte* masks);

    [LibraryImport(Library, EntryPoint = "laplace_attestation_codepoint_range_add")]
    internal static unsafe partial int AttestationCodepointRangeAdd(
        IntPtr stage, uint firstCodepoint, uint lastCodepoint,
        Hash128* typeId, Hash128* objectId, byte objectIsNull, Hash128* sourceId,
        Hash128* contextId, byte contextIsNull,
        double sourceTrust, int confirm, long observationCount);

    [StructLayout(LayoutKind.Sequential)]
    internal struct CodepointRangeRelationNative
    {
        internal Hash128 TypeId;
        internal Hash128 ObjectId;
        internal Hash128 ContextId;
        internal int ObjectIsNull;
        internal int ContextIsNull;
        internal int Confirm;
        internal int Reserved;
        internal long ObservationCount;
    }

    [LibraryImport(Library, EntryPoint = "laplace_attestation_codepoint_range_relations_add")]
    internal static unsafe partial int AttestationCodepointRangeRelationsAdd(
        IntPtr stage, uint firstCodepoint, uint lastCodepoint,
        CodepointRangeRelationNative* relations, nuint relationCount,
        Hash128* sourceId, double sourceTrust);

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

    [LibraryImport(Library, EntryPoint = "laplace_qualifier_bit", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int QualifierBit(string family, string value);

    [LibraryImport(Library, EntryPoint = "laplace_trust_class_id", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int TrustClassIdNative(string name, Hash128* outId);

    [LibraryImport(Library, EntryPoint = "laplace_trust_class_prior")]
    internal static partial int TrustClassPriorNative(Hash128* id, double* outPrior);

    [LibraryImport(Library, EntryPoint = "laplace_trust_class_prior_by_name", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int TrustClassPriorByNameNative(string name, double* outPrior);

    [LibraryImport(Library, EntryPoint = "laplace_pos_resolve_canonical", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PosResolveCanonical(string tag, int tagset, byte** outCanonical, int* outIndex);

    [LibraryImport(Library, EntryPoint = "laplace_pos_tagset_from_name", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PosTagsetFromName(string name);

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

    [LibraryImport(Library, EntryPoint = "laplace_compose_drain_into_stage")]
    internal static partial int ComposeDrainIntoStage(
        IntPtr compose,
        IntPtr stage,
        Hash128* sourceId,
        long nowUnixUs,
        double witnessWeight,
        byte* existingBitmap,
        nuint bitmapBits);



    [LibraryImport(Library, EntryPoint = "highway_table_load", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int HighwayTableLoad(string path);

    [LibraryImport(Library, EntryPoint = "highway_table_unload")]
    internal static partial void HighwayTableUnload();

    [LibraryImport(Library, EntryPoint = "highway_table_is_loaded")]
    internal static partial int HighwayTableIsLoaded();

    [LibraryImport(Library, EntryPoint = "highway_table_records")]
    internal static partial int HighwayTableRecords(out nint records, out ulong count);

    [LibraryImport(Library, EntryPoint = "highway_table_find")]
    internal static partial nint HighwayTableFind(Hash128* id);

    [LibraryImport(Library, EntryPoint = "highway_table_at", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint HighwayTableAt(string list, uint slot);

    [LibraryImport(Library, EntryPoint = "highway_table_key", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long HighwayTableKey(string list, string key);

    [LibraryImport(Library, EntryPoint = "highway_table_fingerprint")]
    internal static partial int HighwayTableFingerprint(byte* fingerprint);

    [LibraryImport(Library, EntryPoint = "highway_table_edge_span", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int HighwayTableEdgeSpan(string from, uint slot, string to, out nint edges, out ulong count);

    [LibraryImport(Library, EntryPoint = "highway_table_relation_by_hash")]
    internal static partial int HighwayTableRelationByHash(
    Hash128* typeId, byte* outBitPos, float* outRank, byte* outBand);

    [LibraryImport(Library, EntryPoint = "highway_table_band_mask")]
    internal static partial int HighwayTableBandMask(byte band, Mask256* outMask);

    [LibraryImport(Library, EntryPoint = "highway_table_mask_or")]
    internal static partial Mask256 HighwayTableMaskOr(Mask256 a, Mask256 b);

    [LibraryImport(Library, EntryPoint = "highway_table_mask_and")]
    internal static partial Mask256 HighwayTableMaskAnd(Mask256 a, Mask256 b);

    [LibraryImport(Library, EntryPoint = "highway_table_mask_test")]
    internal static partial int HighwayTableMaskTest(Mask256* mask, byte bit);

    [LibraryImport(Library, EntryPoint = "highway_table_mask_any")]
    internal static partial int HighwayTableMaskAny(Mask256* mask);
}
