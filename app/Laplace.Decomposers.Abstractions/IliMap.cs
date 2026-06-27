namespace Laplace.Decomposers.Abstractions;
















public sealed class IliMap
{
    private readonly Dictionary<long, string> _byKey;

    private IliMap(Dictionary<long, string> byKey) => _byKey = byKey;

    
    public int Count => _byKey.Count;

    
    public const string MapFileName = "ili-map-pwn30.tab";

    
    
    
    
    public static IliMap Load(string ciliDir) => LoadFile(Path.Combine(ciliDir, MapFileName));

    /// <summary>
    /// Load a specific WordNet-version ILI map (e.g. <c>pwn16</c> for MapNet/WordFrameNet, which align
    /// to WordNet 1.6 / MultiWordNet — confirmed 99.7% offset coverage vs 0.5% against pwn30). The
    /// older-version maps (pwn15..pwn21) live under <c>older-wn-mappings/</c> and carry a third
    /// confidence column. Returns null if the version file is absent.
    /// </summary>
    public static IliMap? LoadVersion(string ciliDir, string version)
    {
        string root = Path.Combine(ciliDir, $"ili-map-{version}.tab");
        string older = Path.Combine(ciliDir, "older-wn-mappings", $"ili-map-{version}.tab");
        string path = File.Exists(root) ? root : older;
        return File.Exists(path) ? LoadFile(path) : null;
    }

    private static IliMap LoadFile(string path)
    {
        var map = new Dictionary<long, string>(120_000);
        foreach (var raw in File.ReadLines(path))
        {
            int tab = raw.IndexOf('\t');
            if (tab <= 0) continue;
            string ili = raw[..tab];
            ReadOnlySpan<char> offsetPos = raw.AsSpan(tab + 1).Trim();
            // Older-wn maps are "ili \t offset-pos \t confidence" — keep only the offset-pos field.
            int t2 = offsetPos.IndexOf('\t');
            if (t2 >= 0) offsetPos = offsetPos[..t2].Trim();
            int dash = offsetPos.LastIndexOf('-');
            if (dash <= 0 || dash + 1 >= offsetPos.Length) continue;
            if (!long.TryParse(offsetPos[..dash], out long offset)) continue;
            char ssType = offsetPos[dash + 1];
            map[Key(offset, ssType)] = ili;
        }
        return new IliMap(map);
    }

    private static long Key(long offset, char ssType)
    {
        // Adjective satellites carry ss_type 's'. OMW writes every adjective (head AND satellite) as
        // 'a', while pwn30 stores satellites as 's' — so an OMW '-a' lookup of a satellite missed the
        // '-s' map entry and silently dropped ~3% of OMW lemmas (every satellite, in every language;
        // e.g. i12345's foreign words). An adjective offset is 'a' XOR 's' (verified disjoint in
        // ili-map-pwn30.tab: 0 offsets appear as both), so the offset alone identifies the synset —
        // collapse 'a' and 's' to one pos code so the satellite resolves instead of dropping.
        long p = ssType switch { 'n' => 1, 'v' => 2, 'a' => 3, 's' => 3, 'r' => 5, _ => 0 };
        return (offset << 3) | p;
    }

    
    
    
    
    public string? Resolve(long offset, char ssType)
        => _byKey.TryGetValue(Key(offset, ssType), out var ili) ? ili : null;
}
