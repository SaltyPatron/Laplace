namespace Laplace.Decomposers.Abstractions;

using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// THE canonical text decomposer interface. Codepoint-anchored, NFC-normalized,
/// UAX29-segmented (graphemes / words / sentences via ICU). Every text-bearing
/// decomposer (WordNet glosses, OMW translations, UD sentences, Wiktionary
/// entries, Tatoeba sentences, AI model tokenizer surfaces, AI model
/// config.json values, user text, image alt-text, audio transcripts, video
/// subtitles, math LaTeX, code identifiers, structured-data values) routes
/// content through this interface so cross-source dedup is automatic.
///
/// Same content from any source = ONE substrate entity row + N provenance
/// edges (one per source that contributed it).
///
/// There is exactly ONE concrete implementation. Parallel "TextDecomposer-but-
/// different" implementations break cross-source dedup and are forbidden by
/// the coding standards.
/// </summary>
public interface ITextDecomposition
{
    /// <summary>
    /// Decompose UTF-8 bytes through the canonical pipeline:
    ///   bytes → NFC normalize → UTF-32 codepoints (lookup in atom pool)
    ///         → graphemes (UAX29 GB)
    ///         → words (UAX29 WB)
    ///         → sentences (UAX29 SB)
    ///         → document composition
    /// Emits all entities, composition_child rows (with RLE counts), sequence-
    /// table rows, and provenance edges. Returns the document atom hash.
    /// </summary>
    /// <param name="utf8Content">The raw UTF-8 bytes of the text.</param>
    /// <param name="provenanceSource">Substrate entity hash of the source.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The document atom hash (the root of the composition tree).</returns>
    Task<AtomId> DecomposeAsync(
        ReadOnlyMemory<byte> utf8Content,
        AtomId provenanceSource,
        CancellationToken cancellationToken);
}
