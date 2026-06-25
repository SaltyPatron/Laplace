using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Laplace.Engine.Core;
using Microsoft.Extensions.Logging;

namespace Laplace.Decomposers.Abstractions;

public static class SourceEntityIdConventions
{
    private static Lazy<IliMap?> _iliMap = new(LoadIliMap);

    public static string CiliDirectory() =>
        Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR")
        ?? Path.Combine(DataRoot(), "CILI");

    public static string CiliMapPath() => Path.Combine(CiliDirectory(), IliMap.MapFileName);

    public static void EnsureCiliMapForIngest(ILogger logger, string sourceName)
    {
        var (ok, path, _) = EvaluateCiliMap();
        if (ok) return;
        logger.LogError("CILI ILI map missing or empty; expected at {CiliMapPath}", path);
        throw new CiliMapMissingException(path, sourceName);
    }

    public static void WarnIfCiliMapMissing(ILogger? logger, string sourceName)
    {
        var (ok, path, _) = EvaluateCiliMap();
        if (ok || logger is null) return;
        logger.LogWarning(
            "CILI ILI map missing or empty at {CiliMapPath}; {Source} ingest will proceed " +
            "without ILI-resolved synset anchors.",
            path, sourceName);
    }

    internal static void ResetIliMapCacheForTests() => _iliMap = new Lazy<IliMap?>(LoadIliMap);

    private static IliMap? LoadIliMap()
    {
        string path = CiliMapPath();
        if (!File.Exists(path)) return null;
        var map = IliMap.Load(CiliDirectory());
        return map.Count > 0 ? map : null;
    }

    private static (bool Ok, string Path, string? Reason) EvaluateCiliMap()
    {
        string path = CiliMapPath();
        if (!File.Exists(path))
            return (false, path, "missing");
        if (new FileInfo(path).Length == 0)
            return (false, path, "empty");
        try
        {
            return IliMap.Load(CiliDirectory()).Count > 0 ? (true, path, null) : (false, path, "empty");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, path, "unreadable");
        }
    }

    private static string DataRoot() =>
        Environment.GetEnvironmentVariable("LAPLACE_DATA_ROOT")
        ?? (OperatingSystem.IsWindows() ? @"D:\Data\Ingest" : "/vault/Data");

    
    
    
    
    
    
    
    public static string? WordNetIli(long byteOffset, char ssType) => _iliMap.Value?.Resolve(byteOffset, ssType);

    
    
    
    
    
    
    
    
    
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

    /// <summary>
    /// Strip the lemma prefix from a VerbNet class id (e.g. <c>give-13.1</c> → <c>13.1</c>).
    /// Shared by VerbNet, PropBank rolelinks, and SemLink pb-vn2 keys.
    /// </summary>
    public static string NumericVerbNetClassId(string classId)
    {
        if (classId.Length == 0 || char.IsDigit(classId[0])) return classId;
        for (int i = classId.IndexOf('-'); i >= 0 && i + 1 < classId.Length; i = classId.IndexOf('-', i + 1))
            if (char.IsDigit(classId[i + 1])) return classId[(i + 1)..];
        return classId;
    }

    /// <summary>
    /// Strip a Predicate Matrix namespaced token (e.g. <c>id:eng</c> → <c>eng</c>, <c>ili-30-02244956-v</c> → <c>30-02244956-v</c>).
    /// </summary>
    public static string StripPredicateMatrixNamespace(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return raw;
        int colon = raw.IndexOf(':');
        return colon >= 0 && colon + 1 < raw.Length ? raw[(colon + 1)..] : raw;
    }

    /// <summary>
    /// Parse a Predicate Matrix MCR ILI synset key (<c>30-02244956-v</c> or <c>ili-30-02244956-v</c>) to WordNet offset + ss_type.
    /// </summary>
    public static (long Offset, char SsType)? ParseMcrSynsetKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return null;
        string s = StripPredicateMatrixNamespace(raw.Trim());
        if (s.StartsWith("ili-", StringComparison.OrdinalIgnoreCase))
            s = s[4..];
        int lastDash = s.LastIndexOf('-');
        if (lastDash <= 0 || lastDash + 1 >= s.Length) return null;
        char ssType = s[lastDash + 1];
        if (ssType is not ('n' or 'v' or 'a' or 's' or 'r')) return null;
        string rest = s[..lastDash];
        int offDash = rest.LastIndexOf('-');
        ReadOnlySpan<char> offSpan = offDash >= 0 ? rest.AsSpan(offDash + 1) : rest.AsSpan();
        if (!long.TryParse(offSpan, out long offset) || offset <= 0) return null;
        return (offset, ssType);
    }

    /// <summary>
    /// Canonical FrameNet LU category key shared by FrameNet ingest and FN→WN bridge sources (MapNet, WordFrameNet).
    /// </summary>
    public static string FrameNetLuKey(string frame, string luName) =>
        $"{frame.Trim()}/{luName.Trim()}";

    /// <summary>
    /// Parse a MapNet / MultiWordNet synset key (<c>a#00057580</c>, <c>v#01142646</c>) to WordNet offset + ss_type.
    /// </summary>
    public static (long Offset, char SsType)? ParseMapNetSynsetKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return null;
        string s = raw.Trim();
        int hash = s.IndexOf('#');
        if (hash <= 0 || hash + 1 >= s.Length) return null;
        char ssType = s[0];
        if (ssType is not ('n' or 'v' or 'a' or 's' or 'r')) return null;
        // MapNet offsets carry a trailing '$' terminator (e.g. a#00057580$). Take the digit run after
        // '#' and stop at any non-digit suffix — otherwise TryParse fails on the '$' and EVERY row drops.
        var rest = s.AsSpan(hash + 1);
        int n = 0;
        while (n < rest.Length && char.IsDigit(rest[n])) n++;
        if (n == 0 || !long.TryParse(rest[..n], out long offset) || offset <= 0) return null;
        return (offset, ssType);
    }

    /// <summary>
    /// Resolve a WordNet synset or sense reference to a content-addressed anchor id.
    /// MCR/Predicate Matrix keys, MapNet pos#offset keys, and WN-RDF tail tokens (<c>107755101-n</c>) → ILI synset (<see cref="ConceptAnchor"/>);
    /// normalized sense keys → <see cref="SenseAnchor"/>.
    /// </summary>
    public static Hash128? ResolveSynsetAnchor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return null;
        string s = raw.Trim();
        int slash = s.LastIndexOf('/');
        if (slash >= 0 && slash + 1 < s.Length)
            s = s[(slash + 1)..];
        if (ParseMcrSynsetKey(s) is { } mcr)
            return ConceptAnchor.SynsetId(mcr.Offset, mcr.SsType);
        if (ParseMapNetSynsetKey(s) is { } mapNet)
            return ConceptAnchor.SynsetId(mapNet.Offset, mapNet.SsType);
        string? senseKey = NormalizeSenseKey(s);
        return senseKey is null ? null : SenseAnchor.Id(senseKey);
    }

    /// <summary>
    /// SemLink vn-fn2 keys embed the member lemma after the class (e.g. <c>13.1-1-give</c> → <c>13.1-1</c>).
    /// </summary>
    public static string VerbNetClassFromSemLinkKey(string key)
    {
        int last = key.LastIndexOf('-');
        if (last > 0 && last + 1 < key.Length && char.IsLetter(key[last + 1]))
            return key[..last];
        return key;
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
