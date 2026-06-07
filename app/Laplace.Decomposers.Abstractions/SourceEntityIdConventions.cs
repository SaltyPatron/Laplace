using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

public static class SourceEntityIdConventions
{
    public static Hash128 WordNetSynset(long byteOffset, char pos) =>
        Hash128.OfCanonical($"wordnet/synset/{pos}/{byteOffset}");

    public static Hash128 ModelAxisEntity(Hash128 modelSource, string space, int index) =>
        Hash128.OfCanonical($"model/{modelSource.Hi:x16}{modelSource.Lo:x16}/{space}/{index}");

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
