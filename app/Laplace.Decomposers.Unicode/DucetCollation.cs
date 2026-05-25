namespace Laplace.Decomposers.Unicode;

/// <summary>
/// Parses the UCA DUCET (allkeys.txt) into a per-codepoint collation rank —
/// the ordering the substrate-canonical coordinate placement uses (codepoints
/// laid on S³ by collation order, not numeric value). Ported faithfully from
/// the C++ emitter's parse_ducet so an independent C# derivation produces the
/// SAME ranks → the same super-Fibonacci coords → the DB seed and the
/// perf-cache agree byte-for-byte (ADR 0006 independent siblings).
///
/// Single-codepoint entries take their first collation element's
/// (primary, secondary, tertiary) weights as a 64-bit sort key; contractions
/// (multi-cp) are skipped. Codepoints absent from allkeys get UCA §10.1.3
/// implicit weights (Han / extension / other bases). Final order is by
/// (key, codepoint); rank is the position in that order.
/// </summary>
public sealed class DucetCollation
{
    public const int CodepointCount = 0x110000;   // 1,114,112

    private readonly string _allkeysPath;

    public DucetCollation(string allkeysPath)
    {
        _allkeysPath = allkeysPath ?? throw new ArgumentNullException(nameof(allkeysPath));
        if (!File.Exists(_allkeysPath))
            throw new FileNotFoundException($"DUCET allkeys.txt not found: {_allkeysPath}", _allkeysPath);
    }

    private readonly record struct ImplicitRange(uint First, uint Last, uint Base);

    /// <summary>Returns <c>rank[cp]</c> for every codepoint 0..0x10FFFF: the
    /// codepoint's position in DUCET collation order.</summary>
    public uint[] ComputeRanks()
    {
        var key = new ulong[CodepointCount];
        var isExplicit = new bool[CodepointCount];
        var implicits = new List<ImplicitRange>();

        foreach (var raw in File.ReadLines(_allkeysPath))
        {
            // Strip trailing comment FIRST — applies to @-directives too
            // (e.g. "@implicitweights 17000..187FF; FB00 # Tangut").
            string line = raw;
            int hashIdx = line.IndexOf('#');
            if (hashIdx >= 0) line = line.Substring(0, hashIdx);
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '@')
            {
                if (line.StartsWith("@implicitweights", StringComparison.Ordinal))
                {
                    // @implicitweights FIRST..LAST; BASE
                    var rest = line.Substring("@implicitweights".Length);
                    int semi = rest.IndexOf(';');
                    if (semi < 0) continue;
                    var range = rest.Substring(0, semi).Trim();
                    int dots = range.IndexOf("..", StringComparison.Ordinal);
                    if (dots < 0) continue;
                    if (!TryParseHex(range.Substring(0, dots), out uint lo)) continue;
                    if (!TryParseHex(range.Substring(dots + 2), out uint hi)) continue;
                    if (!TryParseHex(rest.Substring(semi + 1), out uint bse)) continue;
                    implicits.Add(new ImplicitRange(lo, hi, bse));
                }
                continue;
            }
            int sc = line.IndexOf(';');
            if (sc < 0) continue;

            // LHS codepoints — only single-cp entries (skip contractions).
            string lhs = line.Substring(0, sc);
            var cps = lhs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cps.Length != 1) continue;
            if (!TryParseHex(cps[0], out uint cp) || cp >= CodepointCount) continue;

            // First collation element on RHS: [.PPPP.SSSS.TTTT] or [*PPPP....]
            string rhs = line.Substring(sc + 1);
            int b = rhs.IndexOf('[');
            if (b < 0) continue;
            int q = b + 1;
            if (q < rhs.Length && (rhs[q] == '.' || rhs[q] == '*')) q++;
            uint pw = ReadHex(rhs, ref q); if (q < rhs.Length && rhs[q] == '.') q++;
            uint sw = ReadHex(rhs, ref q); if (q < rhs.Length && rhs[q] == '.') q++;
            uint tw = ReadHex(rhs, ref q);

            key[cp] = ((ulong)pw << 48) | ((ulong)sw << 32) | ((ulong)tw << 16);
            isExplicit[cp] = true;
        }

        // Implicit weights (UCA §10.1.3) for codepoints not explicitly listed.
        uint BaseFor(uint cp)
        {
            foreach (var r in implicits) if (cp >= r.First && cp <= r.Last) return r.Base;
            if ((cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0xF900 && cp <= 0xFAFF)) return 0xFB40; // Han + compat
            if ((cp >= 0x3400 && cp <= 0x4DBF) || (cp >= 0x20000 && cp <= 0x3FFFF)) return 0xFB80; // CJK ext
            return 0xFBC0; // all other
        }
        for (uint cp = 0; cp < CodepointCount; cp++)
        {
            if (isExplicit[cp]) continue;
            uint bse = BaseFor(cp);
            uint aaaa = bse + (cp >> 15);
            uint bbbb = (cp & 0x7FFF) | 0x8000;
            key[cp] = ((ulong)aaaa << 48) | ((ulong)bbbb << 16);
        }

        // Stable order by (key, codepoint); rank = position.
        var order = new int[CodepointCount];
        for (int i = 0; i < CodepointCount; i++) order[i] = i;
        Array.Sort(order, (a, c) => key[a] != key[c] ? key[a].CompareTo(key[c]) : a.CompareTo(c));

        var rank = new uint[CodepointCount];
        for (uint r = 0; r < CodepointCount; r++) rank[order[r]] = r;
        return rank;
    }

    private static uint ParseHex(string s) => Convert.ToUInt32(s.Trim(), 16);
    private static bool TryParseHex(string s, out uint v)
    {
        try { v = Convert.ToUInt32(s.Trim(), 16); return true; } catch { v = 0; return false; }
    }
    private static uint ReadHex(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && IsHex(s[i])) i++;
        return i > start ? Convert.ToUInt32(s.Substring(start, i - start), 16) : 0u;
    }
    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
}
