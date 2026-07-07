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

    public int WorkingSetBytesPerRecord =>
        Math.Max(1, EstBytesPerRecord) * Math.Max(1, EstComposeUnitsPerRecord);
}
