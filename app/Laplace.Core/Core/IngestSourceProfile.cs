namespace Laplace.Engine.Core;

/// <summary>
/// Per-source ingest memory model. <see cref="EstBytesPerRecord"/> sizes record
/// batches; <see cref="EstComposeUnitsPerRecord"/> scales working-set probe intervals
/// and commit-row budget (relation triples = two tier-tree composes per assertion).
/// </summary>
public sealed record IngestSourceProfile(
    int EstBytesPerRecord,
    int EstComposeUnitsPerRecord = 1,
    // Resident bytes of the native compose trees one compose unit leaves live, distinct
    // from serialized input width: record scheduling uses EstBytesPerRecord, while
    // WorkingSetBytesPerRecord uses this. The flush record cap is derived from it, so an
    // under-declaration here weakens the guard against under-reported staged bytes.
    // Null means resident width equals EstBytesPerRecord.
    int? ResidentBytesPerComposeUnit = null)
{
    public static readonly IngestSourceProfile Default =
        new(IngestSizing.DefaultEstBytesPerRecord, 1);

    /// <summary>Unicode codepoint record — tens of bytes, one compose tree.</summary>
    public static readonly IngestSourceProfile Unicode = new(48, 1);

    /// <summary>
    /// Relation-triple sources (ConceptNet, ATOMIC2020, …): each record builds
    /// subject + object tier trees before the categorical edge.
    /// </summary>
    public static readonly IngestSourceProfile RelationTriple = new(8_192, 2);

    /// <summary>UD sentence — a few KB of CoNLL-U tokens per record.</summary>
    // The sentence's content forest reuses nodes already present in the sentence tree
    // and builds independent trees only for content absent from it (differing lemmas,
    // Gloss/Translit, multiword surfaces). The compose-unit count tracks that fan and is a
    // denominator of ResolveFlushEnvelopeRecordCap / EstimateWorkingSetBytes. The
    // resident width is an upper bound per tree: over-stating closes sets earlier.
    public static readonly IngestSourceProfile UdSentence =
        new(2_048, 16, ResidentBytesPerComposeUnit: 80_000);

    /// <summary>Kaikki wiktextract JSON — tens of KB per entry, many tier trees each.</summary>
    // Byte width is the corpus mean record rounded up for tail variation; a test checks
    // it against the source file. The compose-unit count sizes
    // ResolveFlushEnvelopeRecordCap / EstimateWorkingSetBytes so one working set
    // amortizes presence verification without growing into a single oversized flush.
    public static readonly IngestSourceProfile Wiktionary = new(6_500, 12);

    /// <summary>Document ingest — large text blobs per file chunk.</summary>
    public static readonly IngestSourceProfile Document = new(64_000, 1);

    /// <summary>
    /// PGN game — about 1.2 KB serialized, with a resident compose width covering the
    /// staged rows one game expands into.
    /// </summary>
    public static readonly IngestSourceProfile ChessPgn =
        new(1_200, 1, ResidentBytesPerComposeUnit: 64_000);

    /// <summary>
    /// Game-analysis derivation — the same working-set shape as <see cref="ChessPgn"/>.
    /// </summary>
    public static readonly IngestSourceProfile ChessAnalyze =
        new(1_200, 1, ResidentBytesPerComposeUnit: 64_000);

    /// <summary>WordNet synset/sense line — small text, many emitted rows per line.</summary>
    public static readonly IngestSourceProfile WordNet = new(4_096, 4);

    // Values are per-record byte estimates, not batch sizes:
    // IngestSizing.ResolveRecordBatch turns them into a batch under the RAM budget.

    /// <summary>Tatoeba CSV row — id, language, and one sentence.</summary>
    public static readonly IngestSourceProfile Tatoeba = new(512, 1);

    /// <summary>CILI line — an ILI id and a short definition.</summary>
    public static readonly IngestSourceProfile Cili = new(256, 1);

    /// <summary>ISO 639/15924 tab record — short fixed-width codes.</summary>
    public static readonly IngestSourceProfile Iso = new(256, 1);

    /// <summary>OMW XML lemma/synset entry — one multilingual lemma binding.</summary>
    public static readonly IngestSourceProfile Omw = new(1_024, 1);

    /// <summary>FrameNet XML frame/LU — frame elements and relations build several
    /// trees per record, so this is two compose units like a relation triple.</summary>
    public static readonly IngestSourceProfile FrameNet = new(4_096, 2);

    /// <summary>
    /// PredicateMatrix v1.3 row — about 330 UTF-8 bytes on disk, retained as 27
    /// normalized fields plus its compact bridge projections until the working set drains.
    /// It admits governed references and attestations rather than content trees, so there is
    /// one compose unit; the distinct resident estimate keeps the input batch large without
    /// pretending the parsed object graph costs only its serialized bytes.
    /// </summary>
    public static readonly IngestSourceProfile PredicateMatrix =
        new(384, 1, ResidentBytesPerComposeUnit: 2_048);

    /// <summary>
    /// Image packaging; the recovered RGBA buffer dominates resident size. Channel values
    /// compose from reusable scalar content.
    /// </summary>
    public static readonly IngestSourceProfile MediaImage = new(256_000, 1);

    /// <summary>
    /// Audio packaging; the recovered PCM16 mono buffer dominates resident size. Integer
    /// samples compose from reusable scalar content.
    /// </summary>
    public static readonly IngestSourceProfile MediaAudio = new(128_000, 1);

    /// <summary>Video as ordered frame recoveries — one image ladder per frame record.</summary>
    public static readonly IngestSourceProfile MediaVideo = new(256_000, 1);

    public int WorkingSetBytesPerRecord =>
        Math.Max(1, ResidentBytesPerComposeUnit ?? EstBytesPerRecord)
        * Math.Max(1, EstComposeUnitsPerRecord);

    /// <summary>
    /// Bytes retained before a record has been composed: the parsed/source record plus
    /// the declared deferred-compose residency. Once a deferred unit exists the pipeline
    /// sizes it by its native tree capacity instead.
    /// </summary>
    public long UncomposedResidentBytesPerRecord => checked(
        (long)Math.Max(1, EstBytesPerRecord) + WorkingSetBytesPerRecord);
}
