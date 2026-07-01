namespace Laplace.Decomposers.Abstractions;















public sealed class IliMap
{
    private readonly Dictionary<long, string> _byKey;

    private IliMap(Dictionary<long, string> byKey) => _byKey = byKey;

    
    public int Count => _byKey.Count;

    
    public const string MapFileName = "ili-map-pwn30.tab";

    
    
    
    
    public static IliMap Load(string ciliDir) => LoadFile(Path.Combine(ciliDir, MapFileName));

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
        long p = ssType switch { 'n' => 1, 'v' => 2, 'a' => 3, 's' => 3, 'r' => 5, _ => 0 };
        return (offset << 3) | p;
    }

    
    
    
    
    public string? Resolve(long offset, char ssType)
        => _byKey.TryGetValue(Key(offset, ssType), out var ili) ? ili : null;
}
