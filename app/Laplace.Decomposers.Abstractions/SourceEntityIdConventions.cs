using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Canonical BLAKE3-128 ID formulas for per-source external-reference entities
/// AND for content-addressed SOURCE identities.
///
/// <para>External-reference ids (WordNet synset offset, Tatoeba sentence id):
/// stable cross-session join keys for a source-specific record that is NOT
/// derived from text content alone (ADR 0016).</para>
///
/// <para>Source identity (truth #5 — identity is content, not name): a source
/// is identified by the CONTENT it carries, not its file/dir name, so two
/// byte-identical copies are ONE witness (no double-counting) and a changed
/// corpus / fine-tuned model is a DISTINCT witness. Binary corpora (models) use
/// <see cref="ContentHashSourceId"/> (raw bytes, streamed); text corpora use
/// <see cref="NormalizedTextSourceId"/> (BOM/CRLF-normalized).</para>
/// </summary>
public static class SourceEntityIdConventions
{
    /// <summary>
    /// Canonical ID for a WordNet 3.0 synset. Formula:
    /// <c>BLAKE3("wordnet/synset/{pos}/{byteOffset}")</c> where pos is the
    /// one-character POS tag (n, v, a, r, s) and byteOffset is the
    /// data-file byte offset that uniquely identifies the synset.
    /// </summary>
    public static Hash128 WordNetSynset(long byteOffset, char pos) =>
        Hash128.OfCanonical($"wordnet/synset/{pos}/{byteOffset}");

    /// <summary>
    /// Canonical ID for a Tatoeba sentence. Formula:
    /// <c>BLAKE3("tatoeba/sentence/{sentenceId}")</c>.
    ///
    /// <para>Note: the content-addressed entity ID derived via TextDecomposer
    /// of the sentence text is the PREFERRED identity for the sentence as a
    /// substrate entity. This Tatoeba-specific ID is used only for
    /// HAS_EXTERNAL_ID attestations — the cross-source join key.</para>
    /// </summary>
    public static Hash128 TatoebaSentence(long sentenceId) =>
        Hash128.OfCanonical($"tatoeba/sentence/{sentenceId}");

    // ── Content-addressed SOURCE identity ───────────────────────────────────

    /// <summary>Streaming chunk size for binary content hashing — fixed (NOT a
    /// rented-buffer length) so chunk boundaries, hence the id, are deterministic
    /// regardless of allocator behaviour.</summary>
    private const int ContentHashChunkBytes = 64 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, Hash128> _modelSourceIdCache = new();

    /// <summary>
    /// Content-addressed source id over a set of files: a BLAKE3 chunk-Merkle of
    /// each file's RAW bytes (fixed 64&#160;MiB chunks), combined in sorted-path
    /// order under <paramref name="domain"/>. Bounded memory — streams; never
    /// loads a whole file. Same bytes ⇒ same id regardless of file/dir names;
    /// any byte differs ⇒ distinct id. <paramref name="domain"/> separates a
    /// source id from a content-entity id that might hash the same bytes.
    /// </summary>
    public static Hash128 ContentHashSourceId(string domain, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var children = new List<Hash128>(files.Count + 1) { Hash128.OfCanonical(domain) };
        foreach (var path in files.OrderBy(p => p, StringComparer.Ordinal))
            children.Add(HashFileChunked(path));
        return Hash128.Merkle(0, CollectionsMarshal.AsSpan(children));
    }

    /// <summary>
    /// Content-addressed source id for TEXT corpora: like
    /// <see cref="ContentHashSourceId"/> but each file is decoded and NORMALIZED
    /// (BOM stripped by the decoder, CRLF/CR folded to LF) before hashing, so a
    /// re-encoded-but-identical corpus converges to the same source. Reads each
    /// file fully (text corpora are modest); use the binary helper for models.
    /// </summary>
    public static Hash128 NormalizedTextSourceId(string domain, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var children = new List<Hash128>(files.Count + 1) { Hash128.OfCanonical(domain) };
        foreach (var path in files.OrderBy(p => p, StringComparer.Ordinal))
        {
            string norm = File.ReadAllText(path)          // decoder strips a leading BOM
                              .Replace("\r\n", "\n").Replace('\r', '\n');
            children.Add(Hash128.OfCanonical(norm));
        }
        return Hash128.Merkle(0, CollectionsMarshal.AsSpan(children));
    }

    /// <summary>
    /// Content-addressed source id for a transformer model: chunk-Merkle of
    /// <c>config.json</c> (if present) ++ every <c>*.safetensors</c> shard (or
    /// <c>*.gguf</c>) in the dir, under the <c>substrate/source/model</c> domain.
    /// Distinguishes fine-tunes (same shapes, different weights) and
    /// re-quantizations; converges two byte-identical copies in differently-named
    /// dirs. Returns <c>null</c> when no weight files are present (non-model dir /
    /// fixture) so the caller can fall back to a name-based id.
    ///
    /// <para>Result is memoised per (dir, file path|length|mtime) signature so the
    /// multi-GB hash runs once per process, not on every <c>SourceForModel</c>
    /// call (ctor, ingest, re-ingest guard, synthesis).</para>
    /// </summary>
    public static Hash128? ModelContentSourceId(string modelDir)
    {
        if (string.IsNullOrEmpty(modelDir) || !Directory.Exists(modelDir)) return null;

        string[] weights = Directory.GetFiles(modelDir, "*.safetensors");
        if (weights.Length == 0) weights = Directory.GetFiles(modelDir, "*.gguf");
        if (weights.Length == 0) return null;

        var files = new List<string>(weights.Length + 1);
        string cfg = Path.Combine(modelDir, "config.json");
        if (File.Exists(cfg)) files.Add(cfg);
        files.AddRange(weights);
        files.Sort(StringComparer.Ordinal);

        // Cheap signature (path|len|mtime) keys the cache; if any shard changes
        // on disk the key changes and the heavy hash recomputes.
        var sig = new StringBuilder(modelDir);
        foreach (var f in files)
        {
            var fi = new FileInfo(f);
            sig.Append('|').Append(f).Append(':').Append(fi.Length)
               .Append(':').Append(fi.LastWriteTimeUtc.Ticks);
        }
        string key = sig.ToString();
        if (_modelSourceIdCache.TryGetValue(key, out var cached)) return cached;

        Hash128 id = ContentHashSourceId("substrate/source/model/v1", files);
        _modelSourceIdCache[key] = id;
        return id;
    }

    private static Hash128 HashFileChunked(string path)
    {
        var chunks = new List<Hash128>();
        byte[] buf = new byte[ContentHashChunkBytes];   // fixed-size; one per file hash
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                       bufferSize: 1 << 20, FileOptions.SequentialScan))
        {
            int n;
            while ((n = ReadExact(fs, buf)) > 0)
            {
                chunks.Add(Hash128.Blake3(buf.AsSpan(0, n)));
                if (n < buf.Length) break;              // last (short) chunk
            }
        }
        if (chunks.Count == 0) return Hash128.Blake3(ReadOnlySpan<byte>.Empty);   // empty file
        return Hash128.Merkle(0, CollectionsMarshal.AsSpan(chunks));
    }

    private static int ReadExact(Stream s, byte[] buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int r = s.Read(buf, total, buf.Length - total);
            if (r == 0) break;
            total += r;
        }
        return total;
    }
}
