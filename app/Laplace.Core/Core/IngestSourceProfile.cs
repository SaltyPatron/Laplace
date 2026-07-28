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

    /// <summary>Kaikki wiktextract JSON — tens of KB per entry.</summary>
    public static readonly IngestSourceProfile Wiktionary = new(12_000, 1);

    /// <summary>Document ingest — large text blobs per file chunk.</summary>
    public static readonly IngestSourceProfile Document = new(64_000, 1);

    /// <summary>
    /// Chess PGN game — one input unit explodes into dozens–hundreds of substrate rows
    /// (per-ply board replay, geometry, attestations). Sized like a fat record, not a flat triple.
    /// </summary>
    public static readonly IngestSourceProfile ChessPgn = new(4_000_000, 1);

    /// <summary>
    /// Chess analysis derive — replays witnessed movetext into positions/geometry per game.
    /// </summary>
    public static readonly IngestSourceProfile ChessAnalyze = new(4_000_000, 1);

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

    public int WorkingSetBytesPerRecord =>
        Math.Max(1, EstBytesPerRecord) * Math.Max(1, EstComposeUnitsPerRecord);
}
