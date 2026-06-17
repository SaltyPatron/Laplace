namespace Laplace.Decomposers.Abstractions;
















public sealed class IliMap
{
    private readonly Dictionary<long, string> _byKey;

    private IliMap(Dictionary<long, string> byKey) => _byKey = byKey;

    
    public int Count => _byKey.Count;

    
    public const string MapFileName = "ili-map-pwn30.tab";

    
    
    
    
    public static IliMap Load(string ciliDir)
    {
        string path = Path.Combine(ciliDir, MapFileName);
        var map = new Dictionary<long, string>(120_000);
        foreach (var raw in File.ReadLines(path))
        {
            int tab = raw.IndexOf('\t');
            if (tab <= 0) continue;
            string ili = raw[..tab];
            ReadOnlySpan<char> offsetPos = raw.AsSpan(tab + 1).Trim(); 
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
        long p = ssType switch { 'n' => 1, 'v' => 2, 'a' => 3, 's' => 4, 'r' => 5, _ => 0 };
        return (offset << 3) | p;
    }

    
    
    
    
    public string? Resolve(long offset, char ssType)
        => _byKey.TryGetValue(Key(offset, ssType), out var ili) ? ili : null;
}
