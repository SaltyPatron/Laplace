namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// The Collaborative Interlingual Index (CILI) map: Princeton WordNet 3.0 (offset, ss_type)
/// → the stable, language-agnostic ILI concept id (e.g. "i93445"). The ILI is the omniglottal
/// anchor every wordnet / OMW / cross-resource witness resolves to; the WN byte-offset is only
/// the lookup index, never the identity (offsets differ across languages and WN versions —
/// CILI is the version-stable concept id).
///
/// Loaded from <c>CILI/ili-map-pwn30.tab</c>, whose rows are <c>"ILI\tOFFSET-POS"</c> with CRLF
/// line endings, e.g. <c>i93445\t10676319-n</c>.
///
/// ss_type is kept RAW (n/v/a/s/r). CILI distinguishes satellite adjectives (<c>s</c>, 10,693
/// of them) from head adjectives (<c>a</c>); folding s→a — as the legacy blob convention did via
/// NormPos — silently drops every satellite synset from convergence. Callers must pass the raw
/// ss_type straight from the WN data file, not a normalized pos.
/// </summary>
public sealed class IliMap
{
    private readonly Dictionary<long, string> _byKey;

    private IliMap(Dictionary<long, string> byKey) => _byKey = byKey;

    /// <summary>Number of (offset, pos) → ILI mappings loaded.</summary>
    public int Count => _byKey.Count;

    /// <summary>Standard filename of the PWN-3.0 → ILI map inside the CILI directory.</summary>
    public const string MapFileName = "ili-map-pwn30.tab";

    /// <summary>
    /// Load the PWN-3.0 → ILI map from a CILI directory (the cloned globalwordnet/cili repo,
    /// e.g. <c>D:\Data\Ingest\CILI</c>).
    /// </summary>
    public static IliMap Load(string ciliDir)
    {
        string path = Path.Combine(ciliDir, MapFileName);
        var map = new Dictionary<long, string>(120_000);
        foreach (var raw in File.ReadLines(path))
        {
            int tab = raw.IndexOf('\t');
            if (tab <= 0) continue;
            string ili = raw[..tab];
            ReadOnlySpan<char> offsetPos = raw.AsSpan(tab + 1).Trim(); // trims trailing CR
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

    /// <summary>
    /// Resolve a PWN-3.0 (offset, raw ss_type) to its ILI concept id, or <c>null</c> if the
    /// synset is not in the index. Pass the raw ss_type (n/v/a/s/r), never a normalized pos.
    /// </summary>
    public string? Resolve(long offset, char ssType)
        => _byKey.TryGetValue(Key(offset, ssType), out var ili) ? ili : null;
}
