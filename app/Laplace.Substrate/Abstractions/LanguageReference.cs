using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

public static class LanguageReference
{
    private static Dictionary<string, string>? _canon;
    private static long _resolveMisses;
    private static readonly object _gate = new();

    public static bool IsLoaded => Volatile.Read(ref _canon) != null;
    public static long ResolveMisses => Interlocked.Read(ref _resolveMisses);
    public static int AliasCount => _canon?.Count ?? 0;

    public static string DefaultDir =>
        Environment.GetEnvironmentVariable("LAPLACE_ISO639_DIR") is { Length: > 0 } d
            ? d : "/vault/Data/ISO639";

    public static void EnsureLoaded(string? iso639Dir = null)
    {
        if (IsLoaded) return;
        lock (_gate) { _canon ??= Build(iso639Dir ?? DefaultDir); }
    }

    public static void Load(string? iso639Dir = null)
    {
        lock (_gate) { _canon = Build(iso639Dir ?? DefaultDir); }
    }

    public static string? ResolveCode(string? input)
    {
        var map = _canon ?? throw new InvalidOperationException(
            "LanguageReference not loaded; call LanguageReference.EnsureLoaded(...) first.");
        if (string.IsNullOrWhiteSpace(input)) return null;

        string s = input.Trim().ToLowerInvariant().Replace('_', '-');
        if (map.TryGetValue(s, out var c)) return c;

        int dash = s.IndexOf('-');
        if (dash > 0 && map.TryGetValue(s[..dash], out c)) return c;
        return null;
    }

    public static Hash128 Resolve(string? input)
    {
        string? code = ResolveCode(input);
        if (code is null) { Interlocked.Increment(ref _resolveMisses); code = "und"; }
        return LanguageEntityId.FromIso639_3(code);
    }

    private static Dictionary<string, string> Build(string dir)
    {
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"LanguageReference: ISO639 dir not found: {dir}");

        var map = new Dictionary<string, string>(16_384, StringComparer.Ordinal);
        var nameBuf = new List<(string key, string canon)>(16_384);

        void Code(string? key, string canon)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            map.TryAdd(key.Trim().ToLowerInvariant(), canon);
        }
        void Name(string? key, string canon)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            nameBuf.Add((key.Trim().ToLowerInvariant(), canon));
        }

        string tab = Path.Combine(dir, "iso-639-3.tab");
        if (!File.Exists(tab)) throw new FileNotFoundException($"LanguageReference: missing {tab}");
        ForEachRow(tab, sep: '\t', skipHeader: true, p =>
        {
            if (p.Length < 7) return;
            string id = p[0].Trim().ToLowerInvariant();
            if (id.Length != 3) return;
            Code(id, id);
            Code(p[3], id);
            Code(p[1], id);
            Code(p[2], id);
            Name(p[6], id);
        });







        ForEachRow(Path.Combine(dir, "ISO-639-2_utf-8.txt"), sep: '|', skipHeader: false, p =>
        {
            if (p.Length < 5) return;
            string biblio = p[0].Trim().ToLowerInvariant();
            string termino = p[1].Trim().ToLowerInvariant();
            if (biblio.Length != 3) return;
            string canon =
                (termino.Length == 3 && map.TryGetValue(termino, out var ct)) ? ct
                : map.TryGetValue(biblio, out var cb) ? cb
                : biblio;
            Code(biblio, canon);
            Code(termino, canon);
            Code(p[2], canon);
            Name(p[3], canon);
            Name(p[4], canon);
        });

        ForEachRow(Path.Combine(dir, "iso-639-3_Retirements.tab"), sep: '\t', skipHeader: true, p =>
        {
            if (p.Length < 4) return;
            string id = p[0].Trim().ToLowerInvariant();
            string to = p[3].Trim().ToLowerInvariant();
            if (id.Length == 3 && to.Length == 3 && map.ContainsKey(to))
                map[id] = to;
        });

        ParseIana(Path.Combine(dir, "iana", "language-subtag-registry.txt"), map, Name);

        ForEachRow(Path.Combine(dir, "iso-639-3_Name_Index.tab"), sep: '\t', skipHeader: true, p =>
        {
            if (p.Length < 3) return;
            string id = p[0].Trim().ToLowerInvariant();
            if (id.Length == 3) { Name(p[1], id); Name(p[2], id); }
        });

        foreach (var (k, c) in nameBuf) map.TryAdd(k, c);

        if (map.Count == 0)
            throw new InvalidOperationException($"LanguageReference: built an empty map from {dir}");
        return map;
    }

    private static void ForEachRow(string path, char sep, bool skipHeader, Action<string[]> onRow)
    {
        if (!File.Exists(path)) return;
        bool first = true;
        foreach (var line in File.ReadLines(path))
        {
            if (skipHeader && first) { first = false; continue; }
            first = false;
            if (string.IsNullOrWhiteSpace(line)) continue;
            onRow(line.Split(sep));
        }
    }

    private static void ParseIana(string path, Dictionary<string, string> map, Action<string?, string> alias)
    {
        if (!File.Exists(path)) return;
        string type = "", subtag = "", preferred = "";
        var descs = new List<string>();

        void Flush()
        {
            if (type == "language" && subtag.Length != 0)
            {
                string key = subtag.ToLowerInvariant();
                string? canon = null;
                if (preferred.Length != 0 && map.TryGetValue(preferred.ToLowerInvariant(), out var pc)) canon = pc;
                else if (map.TryGetValue(key, out var sc)) canon = sc;
                if (canon != null)
                {
                    map[key] = canon;
                    foreach (var d in descs) alias(d, canon);
                }
            }
            type = ""; subtag = ""; preferred = ""; descs.Clear();
        }

        foreach (var line in File.ReadLines(path))
        {
            if (line == "%%") { Flush(); continue; }
            int c = line.IndexOf(':');
            if (c <= 0) continue;
            string key = line[..c].Trim();
            string val = line[(c + 1)..].Trim();
            switch (key)
            {
                case "Type": type = val.ToLowerInvariant(); break;
                case "Subtag": subtag = val; break;
                case "Preferred-Value": preferred = val; break;
                case "Description": descs.Add(val); break;
            }
        }
        Flush();
    }
}
