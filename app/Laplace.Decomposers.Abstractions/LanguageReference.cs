using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// App-side omni-glottal language resolution index — the "language perf-cache",
/// sibling to <see cref="Laplace.Engine.Core.CodepointPerfcache"/>. Built once at
/// ingest from the attested ISO 639 reference (the same <c>/vault/Data/ISO639</c>
/// files the <c>ISODecomposer</c> seeds), it canonicalizes ANY language reference —
/// any ISO code form (639-1 <c>en</c> / 639-2B/2T / 639-3 <c>eng</c>), any BCP-47
/// tag (<c>en-US</c>, <c>zh-Hans</c>), any reference name (<c>English</c>,
/// <c>français</c>) — to the ONE canonical ISO 639-3 code. Every decomposer resolves
/// through it, so all sources write the SAME language entity id: the substrate
/// unifies at INGEST and consensus reads stay join-free at runtime (the whole point
/// of seeding ISO).
///
/// <para>APP DATA / non-attested: this is the hot-path resolution projection of the
/// language reference graph the ISODecomposer seeds into the substrate (which is the
/// authoritative record). Loading is idempotent and fail-loud.</para>
/// </summary>
public static class LanguageReference
{
    // lowercased alias (code / tag / name) -> canonical ISO 639-3 code
    private static Dictionary<string, string>? _canon;
    private static long _resolveMisses;
    private static readonly object _gate = new();

    public static bool IsLoaded => Volatile.Read(ref _canon) != null;
    public static long ResolveMisses => Interlocked.Read(ref _resolveMisses);
    public static int  AliasCount => _canon?.Count ?? 0;

    /// <summary>Reference directory; overridable via <c>LAPLACE_ISO639_DIR</c>.</summary>
    public static string DefaultDir =>
        Environment.GetEnvironmentVariable("LAPLACE_ISO639_DIR") is { Length: > 0 } d
            ? d : "/vault/Data/ISO639";

    /// <summary>Build the index once if not already loaded. Thread-safe, idempotent.</summary>
    public static void EnsureLoaded(string? iso639Dir = null)
    {
        if (IsLoaded) return;
        lock (_gate) { _canon ??= Build(iso639Dir ?? DefaultDir); }
    }

    /// <summary>Force a (re)build. Fail-loud if the reference is missing or empty.</summary>
    public static void Load(string? iso639Dir = null)
    {
        lock (_gate) { _canon = Build(iso639Dir ?? DefaultDir); }
    }

    /// <summary>Canonical ISO 639-3 code for any reference, or null if unresolvable.</summary>
    public static string? ResolveCode(string? input)
    {
        var map = _canon ?? throw new InvalidOperationException(
            "LanguageReference not loaded; call LanguageReference.EnsureLoaded(...) first.");
        if (string.IsNullOrWhiteSpace(input)) return null;

        string s = input.Trim().ToLowerInvariant().Replace('_', '-');
        if (map.TryGetValue(s, out var c)) return c;

        // BCP-47: fall back to the primary language subtag (en-us -> en, zh-hans -> zh).
        int dash = s.IndexOf('-');
        if (dash > 0 && map.TryGetValue(s[..dash], out c)) return c;
        return null;
    }

    /// <summary>Canonical language entity id for any reference. Unresolvable input
    /// routes to the <c>und</c> (undetermined) entity and increments
    /// <see cref="ResolveMisses"/> — never a silent misroute to a wrong language.</summary>
    public static Hash128 Resolve(string? input)
    {
        string? code = ResolveCode(input);
        if (code is null) { Interlocked.Increment(ref _resolveMisses); code = "und"; }
        return LanguageEntityId.FromIso639_3(code);
    }

    // ---- build ---------------------------------------------------------------

    private static Dictionary<string, string> Build(string dir)
    {
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"LanguageReference: ISO639 dir not found: {dir}");

        var map = new Dictionary<string, string>(16_384, StringComparer.Ordinal);
        // Names share the keyspace with codes (a 2-letter language NAME like "En"
        // collides with the 639-1 CODE "en"). Codes are authoritative, so all code
        // forms are added first; names are buffered and applied LAST via TryAdd, so a
        // name can never clobber a code.
        var nameBuf = new List<(string key, string canon)>(16_384);

        void Code(string? key, string canon)   // authoritative code/tag
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            map.TryAdd(key.Trim().ToLowerInvariant(), canon);
        }
        void Name(string? key, string canon)    // deferred, never overwrites a code
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            nameBuf.Add((key.Trim().ToLowerInvariant(), canon));
        }

        // 1. iso-639-3.tab — the spine. Codes: Id, Part1, Part2b, Part2t. Name: Ref_Name.
        string tab = Path.Combine(dir, "iso-639-3.tab");
        if (!File.Exists(tab)) throw new FileNotFoundException($"LanguageReference: missing {tab}");
        ForEachRow(tab, sep: '\t', skipHeader: true, p =>
        {
            if (p.Length < 7) return;
            string id = p[0].Trim().ToLowerInvariant();
            if (id.Length != 3) return;
            Code(id, id);
            Code(p[3], id);   // Part1   (639-1)
            Code(p[1], id);   // Part2b  (639-2 bibliographic)
            Code(p[2], id);   // Part2t  (639-2 terminologic)
            Name(p[6], id);   // Ref_Name (deferred)
        });

        // 2. ISO-639-2 (pipe-delimited) — alt codes + French/English names, via Part2t
        //    when it is a known 639-3 id. Codes added now, names deferred.
        ForEachRow(Path.Combine(dir, "ISO-639-2_utf-8.txt"), sep: '|', skipHeader: false, p =>
        {
            if (p.Length < 5) return;
            string t = p[0].Trim().ToLowerInvariant();   // Part2t
            if (t.Length == 3 && map.TryGetValue(t, out var canon))
            {
                Code(p[1], canon);   // Part2b
                Code(p[2], canon);   // Part1
                Name(p[3], canon);   // English name (deferred)
                Name(p[4], canon);   // French name  (deferred)
            }
        });

        // 3. Retirements — retired Id routes to its single successor (Change_To).
        //    A redirect of a non-current code; force-set.
        ForEachRow(Path.Combine(dir, "iso-639-3_Retirements.tab"), sep: '\t', skipHeader: true, p =>
        {
            if (p.Length < 4) return;
            string id = p[0].Trim().ToLowerInvariant();
            string to = p[3].Trim().ToLowerInvariant();
            if (id.Length == 3 && to.Length == 3 && map.ContainsKey(to))
                map[id] = to;
        });

        // 4. IANA BCP-47 registry — Preferred-Value (deprecated -> preferred) redirects
        //    (force-set on non-current subtags) + Descriptions (deferred names).
        ParseIana(Path.Combine(dir, "iana", "language-subtag-registry.txt"), map, Name);

        // 5. Name index — Print_Name / Inverted_Name (deferred names).
        ForEachRow(Path.Combine(dir, "iso-639-3_Name_Index.tab"), sep: '\t', skipHeader: true, p =>
        {
            if (p.Length < 3) return;
            string id = p[0].Trim().ToLowerInvariant();
            if (id.Length == 3) { Name(p[1], id); Name(p[2], id); }
        });

        // 6. Apply all buffered names LAST — TryAdd, so codes/redirects always win.
        foreach (var (k, c) in nameBuf) map.TryAdd(k, c);

        if (map.Count == 0)
            throw new InvalidOperationException($"LanguageReference: built an empty map from {dir}");
        return map;
    }

    private static void ForEachRow(string path, char sep, bool skipHeader, Action<string[]> onRow)
    {
        if (!File.Exists(path)) return;   // optional supplementary files
        bool first = true;
        foreach (var line in File.ReadLines(path))
        {
            if (skipHeader && first) { first = false; continue; }
            first = false;
            if (string.IsNullOrWhiteSpace(line)) continue;
            onRow(line.Split(sep));
        }
    }

    // IANA registry: records separated by a line "%%"; key/value lines "Key: Value".
    // We canonicalize each Type:language subtag (honouring Preferred-Value for
    // deprecated tags) and alias its Descriptions.
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
                    map[key] = canon;                 // ensure the subtag itself resolves
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
                case "Type":            type = val.ToLowerInvariant(); break;
                case "Subtag":          subtag = val; break;
                case "Preferred-Value": preferred = val; break;
                case "Description":     descs.Add(val); break;
            }
        }
        Flush();
    }
}
