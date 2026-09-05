using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Laplace.Engine.Core;
using Microsoft.Extensions.Logging;

namespace Laplace.Decomposers.Abstractions;

public static class SourceEntityIdConventions
{
    private static Lazy<IliMap?> _iliMap = new(LoadIliMap);

    private static readonly ConcurrentDictionary<string, IliMap?> _versionMaps = new();



    private static long _synsetHits;
    private static long _synsetMisses;
    public static long SynsetHits => Interlocked.Read(ref _synsetHits);
    public static long SynsetMisses => Interlocked.Read(ref _synsetMisses);

    internal static string? TestCiliDirOverride { get; set; }

    public static string CiliDirectory() => TestCiliDirOverride ?? LaplaceInstall.ResolveCiliDir();

    public static string CiliMapPath() => Path.Combine(CiliDirectory(), IliMap.MapFileName);

    public const string MultiWordNetWnVersion = "pwn16";

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

    internal static void ResetIliMapCacheForTests()
    {
        _iliMap = new Lazy<IliMap?>(LoadIliMap);
        _versionMaps.Clear();
    }

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

    private static string DataRoot() => LaplaceInstall.ResolveDataRoot();








    public static string? WordNetIli(long byteOffset, char ssType) => WordNetIli(byteOffset, ssType, "pwn30");

    public static string? WordNetIli(long byteOffset, char ssType, string version)
    {
        IliMap? map = string.IsNullOrEmpty(version) || version == "pwn30"
            ? _iliMap.Value
            : _versionMaps.GetOrAdd(version, static v => IliMap.LoadVersion(CiliDirectory(), v));
        string? ili = map?.Resolve(byteOffset, ssType);
        if (ili is null) Interlocked.Increment(ref _synsetMisses);
        else Interlocked.Increment(ref _synsetHits);
        return ili;
    }










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
    /// Canonicalizes a complete WordNet sense key without discarding the satellite-head
    /// fields. Unlike <see cref="NormalizeSenseKey"/>, this is an exact source identity,
    /// not the deliberately lossy three-field compatibility key used by older bridges.
    /// </summary>
    public static string? NormalizeExactSenseKey(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        string key = raw.Trim().TrimStart('?', '!');
        int pct = key.IndexOf('%');
        if (pct <= 0 || pct + 1 >= key.Length) return null;

        string[] fields = key[(pct + 1)..].Split(':', StringSplitOptions.None);
        if (fields.Length != 5 || fields[0].Length != 1
            || fields[0][0] is < '1' or > '5'
            || fields[1].Length == 0 || fields[2].Length == 0)
            return null;

        return $"{key[..pct]}%{string.Join(':', fields)}";
    }

    public static string NumericVerbNetClassId(string classId)
    {
        if (classId.Length == 0 || char.IsDigit(classId[0])) return classId;
        for (int i = classId.IndexOf('-'); i >= 0 && i + 1 < classId.Length; i = classId.IndexOf('-', i + 1))
            if (char.IsDigit(classId[i + 1])) return classId[(i + 1)..];
        return classId;
    }

    public static string StripPredicateMatrixNamespace(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return raw;
        int colon = raw.IndexOf(':');
        return colon >= 0 && colon + 1 < raw.Length ? raw[(colon + 1)..] : raw;
    }

    public static (long Offset, char SsType, string? WnVersion)? ParseMcrSynsetKey(string? raw)
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
        string? wnVersion = null;
        ReadOnlySpan<char> offSpan;
        if (offDash >= 0)
        {
            wnVersion = McrVersionToPwn(rest.AsSpan(..offDash));
            offSpan = rest.AsSpan(offDash + 1);
        }
        else
        {
            offSpan = rest.AsSpan();
        }
        if (!long.TryParse(offSpan, out long offset) || offset <= 0) return null;
        return (offset, ssType, wnVersion);
    }

    private static string? McrVersionToPwn(ReadOnlySpan<char> mcrVersion) => mcrVersion switch
    {
        "30" => "pwn30",
        "21" => "pwn21",
        "20" => "pwn20",
        "171" => "pwn171",
        "17" => "pwn17",
        "16" => "pwn16",
        "15" => "pwn15",
        _ => null,
    };

    public static string FrameNetLuKey(string frame, string luName) =>
    $"{frame.Trim()}/{luName.Trim()}";

    public static (long Offset, char SsType)? ParseMapNetSynsetKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return null;
        string s = raw.Trim();
        int hash = s.IndexOf('#');
        if (hash <= 0 || hash + 1 >= s.Length) return null;
        char ssType = s[0];
        if (ssType is not ('n' or 'v' or 'a' or 's' or 'r')) return null;


        var rest = s.AsSpan(hash + 1);
        int n = 0;
        while (n < rest.Length && char.IsDigit(rest[n])) n++;
        if (n == 0 || !long.TryParse(rest[..n], out long offset) || offset <= 0) return null;
        return (offset, ssType);
    }

    public static Hash128? ResolveSynsetAnchor(string? raw, string version = "pwn30")
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return null;
        string s = raw.Trim();
        int slash = s.LastIndexOf('/');
        if (slash >= 0 && slash + 1 < s.Length)
            s = s[(slash + 1)..];
        if (ParseMcrSynsetKey(s) is { } mcr)
            return ConceptAnchor.SynsetId(mcr.Offset, mcr.SsType, mcr.WnVersion ?? version);
        if (ParseMapNetSynsetKey(s) is { } mapNet)
            return ConceptAnchor.SynsetId(mapNet.Offset, mapNet.SsType, version);
        string? senseKey = NormalizeSenseKey(s);
        return senseKey is null ? null : SenseAnchor.Id(senseKey);
    }

    public static string VerbNetClassFromSemLinkKey(string key)
    {
        int last = key.LastIndexOf('-');
        if (last > 0 && last + 1 < key.Length && char.IsLetter(key[last + 1]))
            return key[..last];
        return key;
    }

    private const int ContentHashChunkBytes = 64 * 1024 * 1024;

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
        using ModelContentSnapshot? snapshot = OpenModelContentSnapshot(modelDir);
        return snapshot?.SourceId;
    }

    public static ModelContentSnapshot? OpenModelContentSnapshot(string modelDir)
    {
        if (string.IsNullOrEmpty(modelDir) || !Directory.Exists(modelDir)) return null;

        string[] weights = Directory.GetFiles(modelDir, "*.safetensors");
        if (weights.Length == 0) weights = Directory.GetFiles(modelDir, "*.gguf");
        if (weights.Length == 0) return null;

        var files = new List<string>(weights.Length + 2);
        string cfg = Path.Combine(modelDir, "config.json");
        if (File.Exists(cfg)) files.Add(cfg);
        string tokenizer = Path.Combine(modelDir, SafetensorSnapshotWitness.TokenizerFile);
        if (File.Exists(tokenizer)) files.Add(tokenizer);
        files.AddRange(weights);
        files.Sort(StringComparer.Ordinal);

        // Open the complete selected file set before hashing any member.  The
        // source id then names the bytes held by one coherent handle snapshot;
        // path metadata is neither identity nor a digest cache key.
        var opened = new Dictionary<string, FileStream>(files.Count, StringComparer.Ordinal);
        try
        {
            foreach (string path in files)
                opened.Add(path, new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    bufferSize: 1, FileOptions.SequentialScan));

            var children = new List<Hash128>(opened.Count + 1)
            {
                Hash128.OfCanonical("substrate/source/model/v1")
            };
            foreach (string path in files)
                children.Add(HashStreamChunked(opened[path]));
            Hash128 sourceId = Hash128.Merkle(0, CollectionsMarshal.AsSpan(children));
            return new ModelContentSnapshot(sourceId, weights, opened);
        }
        catch
        {
            foreach (FileStream stream in opened.Values) stream.Dispose();
            throw;
        }
    }

    public sealed class ModelContentSnapshot : IDisposable
    {
        private readonly Dictionary<string, FileStream> _streams;
        private readonly string[] _orderedPaths;

        internal ModelContentSnapshot(
            Hash128 sourceId, IReadOnlyList<string> weightPaths,
            Dictionary<string, FileStream> streams)
        {
            SourceId = sourceId;
            WeightPaths = weightPaths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
            _streams = streams;
            _orderedPaths = streams.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }

        public Hash128 SourceId { get; }
        public IReadOnlyList<string> WeightPaths { get; }

        public T Read<T>(string path, Func<Stream, T> read)
        {
            ArgumentNullException.ThrowIfNull(read);
            if (!_streams.TryGetValue(path, out FileStream? stream))
                throw new InvalidOperationException($"'{path}' is outside the held model-content snapshot");
            lock (stream)
            {
                stream.Position = 0;
                return read(stream);
            }
        }

        public byte[] ReadRange(string path, long offset, long length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            if (length > int.MaxValue)
                throw new InvalidDataException($"model tensor range is too large for one managed buffer: {length}");
            return Read(path, stream =>
            {
                stream.Position = offset;
                var bytes = new byte[(int)length];
                int total = 0;
                while (total < bytes.Length)
                {
                    int count = stream.Read(bytes, total, bytes.Length - total);
                    if (count == 0)
                        throw new IOException($"model snapshot '{path}' ended inside range [{offset}, {offset + length})");
                    total += count;
                }
                return bytes;
            });
        }

        public void VerifySourceId()
        {
            var children = new List<Hash128>(_orderedPaths.Length + 1)
            {
                Hash128.OfCanonical("substrate/source/model/v1")
            };
            foreach (string path in _orderedPaths)
                children.Add(Read(path, HashStreamChunked));
            Hash128 verified = Hash128.Merkle(0, CollectionsMarshal.AsSpan(children));
            if (verified != SourceId)
                throw new InvalidDataException(
                    "model checkpoint bytes changed while the admitted source snapshot was in use");
        }

        public void Dispose()
        {
            foreach (FileStream stream in _streams.Values) stream.Dispose();
            _streams.Clear();
        }
    }

    private static Hash128 HashFileChunked(string path)
    {
        using var fs = IngestIo.OpenSequentialRead(path);
        return HashStreamChunked(fs);
    }

    private static Hash128 HashStreamChunked(Stream stream)
    {
        var chunks = new List<Hash128>();
        byte[] buf = new byte[ContentHashChunkBytes];
        int n;
        while ((n = ReadExact(stream, buf)) > 0)
        {
            chunks.Add(Hash128.Blake3(buf.AsSpan(0, n)));
            if (n < buf.Length) break;
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
