namespace Laplace.Decomposers.Abstractions;















public sealed class IliMap
{
    private readonly Dictionary<long, string> _byKey;

    private IliMap(Dictionary<long, string> byKey) => _byKey = byKey;


    public int Count => _byKey.Count;


    public const string MapFileName = "ili-map-pwn30.tab";





    // The ili-map-pwn30.tab is a fast-parse CACHE generated from the authoritative RDF that
    // ships in the CILI distribution (ili-map-wn30.ttl; pwn30 == wn30). A stale or failed
    // regeneration silently truncated it to a single line, dropping WordNet's ENTIRE
    // synset->ILI crosswalk (445k misses / 3 hits, 6k attestations, senses()==0). A .tab
    // below this many entries is treated as broken and the authoritative .ttl is loaded
    // instead — one source of truth, self-healing, no silent WordNet collapse.
    private const int HealthyMinEntries = 1_000;
    private const string TtlSourceFileName = "ili-map-wn30.ttl";

    public static IliMap Load(string ciliDir)
    {
        string tabPath = Path.Combine(ciliDir, MapFileName);
        var map = File.Exists(tabPath) ? LoadTabFile(tabPath) : new Dictionary<long, string>();
        if (map.Count < HealthyMinEntries)
        {
            string ttlPath = Path.Combine(ciliDir, TtlSourceFileName);
            if (File.Exists(ttlPath))
            {
                var fromTtl = LoadTtlFile(ttlPath);
                if (fromTtl.Count > map.Count)
                {
                    Console.Error.WriteLine(
                        $"IliMap: {MapFileName} had {map.Count} entries (truncated/missing) — "
                        + $"loaded {fromTtl.Count} from authoritative {TtlSourceFileName}");
                    map = fromTtl;
                }
            }
        }
        return new IliMap(map);
    }

    public static IliMap? LoadVersion(string ciliDir, string version)
    {
        string root = Path.Combine(ciliDir, $"ili-map-{version}.tab");
        string older = Path.Combine(ciliDir, "older-wn-mappings", $"ili-map-{version}.tab");
        string path = File.Exists(root) ? root : older;
        return File.Exists(path) ? new IliMap(LoadTabFile(path)) : null;
    }

    private static Dictionary<long, string> LoadTabFile(string path)
    {
        var map = new Dictionary<long, string>(120_000);
        foreach (var raw in File.ReadLines(path))
        {
            int tab = raw.IndexOf('\t');
            if (tab <= 0) continue;
            string ili = raw[..tab];
            ReadOnlySpan<char> offsetPos = raw.AsSpan(tab + 1).Trim();
            int t2 = offsetPos.IndexOf('\t');
            if (t2 >= 0) offsetPos = offsetPos[..t2].Trim();
            int dash = offsetPos.LastIndexOf('-');
            if (dash <= 0 || dash + 1 >= offsetPos.Length) continue;
            if (!long.TryParse(offsetPos[..dash], out long offset)) continue;
            char ssType = offsetPos[dash + 1];
            map[Key(offset, ssType)] = ili;
        }
        return map;
    }

    // Parse the CILI RDF crosswalk lines: `<i1>  owl:sameAs  pwn30:00001740-a . # able`
    // → ILI `i1`, synset offset-pos `00001740-a`. Same (offset, ssType) key space as the .tab.
    private static Dictionary<long, string> LoadTtlFile(string path)
    {
        var map = new Dictionary<long, string>(120_000);
        foreach (var raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '@' || line[0] == '#') continue;
            int sameAs = line.IndexOf("owl:sameAs", StringComparison.Ordinal);
            if (sameAs < 0) continue;

            string ili = line[..sameAs].Trim().Trim('<', '>', ' ', '\t');
            if (ili.Length == 0 || ili[0] != 'i') continue;

            string rest = line[(sameAs + "owl:sameAs".Length)..];
            int hash = rest.IndexOf('#');
            if (hash >= 0) rest = rest[..hash];
            int dot = rest.LastIndexOf('.');
            if (dot >= 0) rest = rest[..dot];
            rest = rest.Trim();
            int colon = rest.IndexOf(':');
            if (colon >= 0) rest = rest[(colon + 1)..].Trim();

            int dash = rest.LastIndexOf('-');
            if (dash <= 0 || dash + 1 >= rest.Length) continue;
            if (!long.TryParse(rest.AsSpan(0, dash), out long offset)) continue;
            char ssType = rest[dash + 1];
            map[Key(offset, ssType)] = ili;
        }
        return map;
    }

    private static long Key(long offset, char ssType)
    {
        long p = ssType switch { 'n' => 1, 'v' => 2, 'a' => 3, 's' => 3, 'r' => 5, _ => 0 };
        return (offset << 3) | p;
    }





    public string? Resolve(long offset, char ssType)
        => _byKey.TryGetValue(Key(offset, ssType), out var ili) ? ili : null;
}
