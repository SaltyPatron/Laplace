using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

public static class SourceEntityIdConventions
{
    // NOTE: the old WordNetSynset(offset,pos) → OfCanonical("wordnet/synset/{pos}/{offset}") blob
    // keyer is gone. A synset's identity is its ILI concept id decomposed to content (see
    // ConceptAnchor / WordNetIli below); the offset is only the lookup index into the CILI map.

    // The CILI offset→ILI map, loaded once from LAPLACE_CILI_DIR (the cloned globalwordnet/cili).
    // Null when the data isn't present (tests / non-ingest contexts) — callers fall back / fail
    // loudly rather than fabricate.
    private static readonly Lazy<IliMap?> _iliMap = new(() =>
    {
        string dir = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR") ?? @"D:\Data\Ingest\CILI";
        return File.Exists(Path.Combine(dir, IliMap.MapFileName)) ? IliMap.Load(dir) : null;
    });

    /// <summary>
    /// The stable, language-agnostic ILI concept id for a PWN-3.0 (offset, RAW ss_type), e.g.
    /// <c>"i93445"</c> — or null if the CILI map is unavailable or the synset is unmapped. This is
    /// the omniglottal anchor every wordnet-family witness resolves to; the offset is only the
    /// lookup index, never the identity. Pass the RAW ss_type (n/v/a/s/r): satellites (<c>s</c>)
    /// must NOT be folded to <c>a</c> or 10,693 synsets silently drop out of convergence.
    /// </summary>
    public static string? WordNetIli(long byteOffset, char ssType) => _iliMap.Value?.Resolve(byteOffset, ssType);

    /// <summary>
    /// Canonicalize a WordNet sense key to the ONE form every resource converges on:
    /// <c>lemma%ss:lex_filenum:lex_id</c> (e.g. <c>drop%2:38:00</c>). WordNet's <c>index.sense</c> and
    /// VerbNet's <c>wn</c> attribute carry the 5-field form with a trailing <c>head:head_id</c>
    /// (often empty → <c>::</c>); the Predicate Matrix carries only the first three fields. Dropping
    /// head:head_id is what makes all three decompose to the SAME content id (so a VerbNet member,
    /// a PropBank/FrameNet row via the matrix, and the WordNet sense itself land on one anchor).
    /// Leading <c>?</c>/<c>!</c> confidence markers (VerbNet) are stripped. Null if not a sense key.
    /// </summary>
    public static string? NormalizeSenseKey(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        string k = raw.Trim().TrimStart('?', '!');
        int pct = k.IndexOf('%');
        if (pct <= 0 || pct + 1 >= k.Length) return null;
        string lemma = k[..pct].Replace('_', ' ');
        var fields = k[(pct + 1)..].Split(':');
        if (fields.Length < 3) return null;
        return $"{lemma}%{fields[0]}:{fields[1]}:{fields[2]}";
    }

    public static Hash128 TatoebaSentence(long sentenceId) =>
        Hash128.OfCanonical($"tatoeba/sentence/{sentenceId}");

    private const int ContentHashChunkBytes = 64 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, Hash128> _modelSourceIdCache = new();

    public static Hash128 ContentHashSourceId(string domain, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var children = new List<Hash128>(files.Count + 1) { Hash128.OfCanonical(domain) };
        foreach (var path in files.OrderBy(p => p, StringComparer.Ordinal))
            children.Add(HashFileChunked(path));
        return Hash128.Merkle(0, CollectionsMarshal.AsSpan(children));
    }

    public static Hash128 NormalizedTextSourceId(string domain, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var children = new List<Hash128>(files.Count + 1) { Hash128.OfCanonical(domain) };
        foreach (var path in files.OrderBy(p => p, StringComparer.Ordinal))
        {
            string norm = File.ReadAllText(path)
                              .Replace("\r\n", "\n").Replace('\r', '\n');
            children.Add(Hash128.OfCanonical(norm));
        }
        return Hash128.Merkle(0, CollectionsMarshal.AsSpan(children));
    }

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
        byte[] buf = new byte[ContentHashChunkBytes];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                       bufferSize: 1 << 20, FileOptions.SequentialScan))
        {
            int n;
            while ((n = ReadExact(fs, buf)) > 0)
            {
                chunks.Add(Hash128.Blake3(buf.AsSpan(0, n)));
                if (n < buf.Length) break;
            }
        }
        if (chunks.Count == 0) return Hash128.Blake3(ReadOnlySpan<byte>.Empty);
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
