namespace Laplace.Engine.Core;

/// <summary>
/// Per-source ingest memory model. <see cref="EstBytesPerRecord"/> sizes record
/// batches; <see cref="EstComposeUnitsPerRecord"/> scales working-set probe intervals
/// and commit-row budget (relation triples = two tier-tree composes per assertion).
/// </summary>
public sealed record IngestSourceProfile(
    int EstBytesPerRecord,
    int EstComposeUnitsPerRecord = 1)
{
    public static readonly IngestSourceProfile Default =
        new(IngestSizing.DefaultEstBytesPerRecord, 1);

    /// <summary>Unicode codepoint — tens of bytes, one compose tree.</summary>
    public static readonly IngestSourceProfile Unicode = new(48, 1);

    /// <summary>
    /// Relation-triple sources (ConceptNet, ATOMIC2020, …): each record builds
    /// subject + object tier trees before the categorical edge.
    /// </summary>
    public static readonly IngestSourceProfile RelationTriple = new(8_192, 2);

    /// <summary>UD sentence — a few KB of CoNLL-U tokens per record.</summary>
    public static readonly IngestSourceProfile UdSentence = new(2_048, 1);

    /// <summary>Kaikki wiktextract JSON — tens of KB per entry, many tier trees each.</summary>
    // MEASURED 2026-08-01 over 20,000 records of the 20.4 GB raw-wiktextract-data.jsonl:
    // the mean record is 6,158 bytes. The previous 12,000 was an estimate nothing ever
    // checked, and it is the DENOMINATOR in ResolveRecordBatch's
    // TargetBytesPerBatch / estBytesPerRecord -- so it halved every batch and doubled the
    // round trips across a 20 GB corpus. Rounded up to 6,500 for headroom on the tail.
    // IngestRecordSizeMeasurementTests keeps this honest against the real file.
    //
    // EstComposeUnits sizes ResolveFlushEnvelopeRecordCap / EstimateWorkingSetBytes.
    // Sampled ~26 surfaces/entry. Units=1 under-counted and packed ~0.8M-row verifies;
    // units=64 over-sharded (~500 records/apply) and paid ~1.3–2.5s verify fixed cost
    // per shard → ~100 committed input/s (measured 2026-08-06). Units=12 targets
    // ~recordCap≈2.7k so verify amortizes without returning to the mega-flush.
    public static readonly IngestSourceProfile Wiktionary = new(6_500, 12);

    /// <summary>Document ingest — large text blobs per file chunk.</summary>
    public static readonly IngestSourceProfile Document = new(64_000, 1);

    /// <summary>
    /// Chess PGN game — one input unit explodes into hundreds of substrate rows
    /// (per-ply board replay, geometry, attestations). Fat enough to skip the
    /// cheap-record coreFloor in <see cref="IngestSizing.ResolveRecordBatch"/>,
    /// but not so fat that commit_rows collapses below <c>3 × batch × 8</c> and
    /// <see cref="IngestSizing.ResolveMaxIntentsPerCommit"/> returns 1.
    ///
    /// Apply-side attestation merge cost is billed via
    /// <see cref="IngestSizing.AttestationApplySurchargeBytes"/> so the MemoryTopology
    /// flush envelope closes merge storms; do not reinvent that with EstComposeUnits dials.
    ///
    /// MEASURED 2026-08-03 on Seed — chess games OTB-2025 (run 30850033122):
    /// the previous 4_000_000 estimate resolved to
    /// <c>record_batch=256 commit_rows=429 max_intents=1</c> on a 12-core /
    /// 4 GiB working-set box — one intent per commit, CLI ~50% of one core,
    /// multi-minute single <c>COPY physicalities</c>, progress frozen after
    /// ~4k/224k games. Live throughput before the choke: ~16 games/s and
    /// ~291 novel rows/game. 256001 keeps the fat path (batch 256, above the
    /// 256 KiB coreFloor cut) so commit sizing stays on the fat-batch lane and
    /// preserves multi-intent apply parallelism under the same budget (exact
    /// commit_rows/max_intents follow <see cref="IngestSizing"/>, not a fixed
    /// product of batch×partitions).
    /// </summary>
    public static readonly IngestSourceProfile ChessPgn = new(256_001, 1);

    /// <summary>
    /// Chess analysis derive — same working-set class as <see cref="ChessPgn"/>
    /// (fused Compose and standalone chess-analyze share the explosion shape).
    /// </summary>
    public static readonly IngestSourceProfile ChessAnalyze = new(256_001, 1);

    /// <summary>WordNet synset/sense line — small text, many emitted rows per line.</summary>
    public static readonly IngestSourceProfile WordNet = new(4_096, 4);

    // The profiles below replace hardcoded record-batch literals that lived in individual
    // decomposers (`options.BatchSize > 1 ? options.BatchSize : 2048`) and bypassed this
    // memory model entirely — a per-source constant cannot track the box, which is the
    // whole reason IngestSizing/MemoryTopology exist. Values are per-record BYTE estimates,
    // not batch sizes: IngestSizing.ResolveRecordBatch turns them into a batch under the
    // RAM budget, then clamps to the core band, so they are bounded by construction.

    /// <summary>Tatoeba CSV row — id + lang + one sentence. Was borrowing Wiktionary's
    /// 12 KB profile, over-estimating a ~100-byte row by ~100x.</summary>
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
    /// Image packaging → RGBA recovery buffer size dominates; one codepoint-floor
    /// image ladder compose per file.
    /// </summary>
    public static readonly IngestSourceProfile MediaImage = new(256_000, 1);

    /// <summary>
    /// Audio packaging → PCM16 mono recovery size dominates; one codepoint-floor
    /// audio ladder compose per file.
    /// </summary>
    public static readonly IngestSourceProfile MediaAudio = new(128_000, 1);

    /// <summary>Video as ordered frame recoveries — one image ladder per frame record.</summary>
    public static readonly IngestSourceProfile MediaVideo = new(256_000, 1);

    public int WorkingSetBytesPerRecord =>
        Math.Max(1, EstBytesPerRecord) * Math.Max(1, EstComposeUnitsPerRecord);
}
