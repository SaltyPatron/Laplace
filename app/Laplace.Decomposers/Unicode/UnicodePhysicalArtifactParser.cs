using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.Unicode;

internal static class UnicodePhysicalArtifactParser
{
    internal readonly record struct UnicodeDataRow(
        uint Codepoint,
        string? Name,
        string GeneralCategory,
        byte CombiningClass,
        string BidiClass,
        uint[]? CanonicalDecomposition,
        uint[]? CompatibilityDecomposition,
        string? NumericValue,
        uint UppercaseMapping,
        uint LowercaseMapping,
        uint TitlecaseMapping);

    internal readonly record struct RangePoint(
        uint Codepoint,
        string Value,
        bool CountsSourceRow);

    internal readonly record struct MirrorRow(uint Codepoint, uint Mirror);
    internal readonly record struct AliasRow(uint Codepoint, string Alias);
    internal readonly record struct ConfusableRow(uint Codepoint, string Target);
    internal readonly record struct NormalizationRow(
        uint Codepoint,
        string Form,
        bool Maybe,
        bool CountsSourceRow);

    internal static async IAsyncEnumerable<UnicodeDataRow> UnicodeDataAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            if (lineMem.IsEmpty) continue;
            string line = Encoding.UTF8.GetString(lineMem.Span);
            string[] fields = line.Split(';');
            if (fields.Length < 15
                || !uint.TryParse(fields[0], NumberStyles.HexNumber, null, out uint cp)
                || cp > 0x10FFFFu)
                continue;

            string? name = fields[1].Length > 0 && fields[1][0] != '<' ? fields[1] : null;
            string category = fields[2];
            _ = byte.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out byte cc);
            string bidi = fields[4];

            uint[]? canonical = null;
            uint[]? compatibility = null;
            string decomposition = fields[5];
            if (decomposition.Length > 0)
            {
                bool compat = decomposition[0] == '<';
                if (compat)
                {
                    int close = decomposition.IndexOf('>');
                    decomposition = close >= 0 ? decomposition[(close + 1)..] : string.Empty;
                }
                var targets = new List<uint>(2);
                foreach (string token in decomposition.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (uint.TryParse(token, NumberStyles.HexNumber, null, out uint target)
                        && target <= 0x10FFFFu)
                        targets.Add(target);
                if (targets.Count > 0)
                {
                    if (compat) compatibility = targets.ToArray();
                    else canonical = targets.ToArray();
                }
            }

            string? numeric = fields[8].Length > 0 ? fields[8] : null;
            uint upper = ParseHexOrZero(fields[12]);
            uint lower = ParseHexOrZero(fields[13]);
            uint title = ParseHexOrZero(fields[14]);
            yield return new UnicodeDataRow(
                cp, name, category, cc, bidi,
                canonical, compatibility, numeric, upper, lower, title);
        }
    }

    internal static async IAsyncEnumerable<RangePoint> RangePointsAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = StripComment(Encoding.UTF8.GetString(lineMem.Span));
            if (line.Length == 0) continue;
            int semi = line.IndexOf(';');
            if (semi <= 0) continue;
            string range = line[..semi].Trim();
            string value = line[(semi + 1)..].Trim();
            if (value.Length == 0 || !TryRange(range, out uint start, out uint end)) continue;
            bool first = true;
            for (uint cp = start; cp <= end; ++cp)
            {
                ct.ThrowIfCancellationRequested();
                yield return new RangePoint(cp, value, first);
                first = false;
                if (cp == 0x10FFFFu) break;
            }
        }
    }

    internal static async IAsyncEnumerable<MirrorRow> MirrorsAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = StripComment(Encoding.UTF8.GetString(lineMem.Span));
            int semi = line.IndexOf(';');
            if (semi <= 0) continue;
            if (uint.TryParse(line[..semi].Trim(), NumberStyles.HexNumber, null, out uint cp)
                && uint.TryParse(line[(semi + 1)..].Trim(), NumberStyles.HexNumber, null, out uint mirror)
                && cp <= 0x10FFFFu && mirror <= 0x10FFFFu)
                yield return new MirrorRow(cp, mirror);
        }
    }

    internal static async IAsyncEnumerable<AliasRow> AliasesAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = StripComment(Encoding.UTF8.GetString(lineMem.Span));
            string[] fields = line.Split(';');
            if (fields.Length < 2
                || !uint.TryParse(fields[0].Trim(), NumberStyles.HexNumber, null, out uint cp)
                || cp > 0x10FFFFu)
                continue;
            string alias = fields[1].Trim();
            if (alias.Length > 0) yield return new AliasRow(cp, alias);
        }
    }

    internal static async IAsyncEnumerable<ConfusableRow> ConfusablesAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = StripComment(Encoding.UTF8.GetString(lineMem.Span));
            string[] fields = line.Split(';');
            if (fields.Length < 2
                || !uint.TryParse(fields[0].Trim(), NumberStyles.HexNumber, null, out uint cp)
                || cp > 0x10FFFFu)
                continue;

            var sb = new StringBuilder();
            bool valid = true;
            foreach (string token in fields[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!uint.TryParse(token, NumberStyles.HexNumber, null, out uint target)
                    || target > 0x10FFFFu
                    || target is >= 0xD800u and <= 0xDFFFu)
                {
                    valid = false;
                    break;
                }
                sb.Append(char.ConvertFromUtf32((int)target));
            }
            if (valid && sb.Length > 0)
                yield return new ConfusableRow(cp, sb.ToString());
        }
    }

    internal static async IAsyncEnumerable<NormalizationRow> NormalizationAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = StripComment(Encoding.UTF8.GetString(lineMem.Span));
            if (line.Length == 0) continue;
            string[] fields = line.Split(';');
            if (fields.Length < 3) continue;
            string prop = fields[1].Trim();
            string value = fields[2].Trim();
            if (!prop.EndsWith("_QC", StringComparison.Ordinal)) continue;
            string form = prop[..^3];
            if (Array.IndexOf(UcdProperties.NormalizationForms, form) < 0) continue;
            bool maybe = value == "M";
            if (!maybe && value != "N") continue;
            if (!TryRange(fields[0].Trim(), out uint start, out uint end)) continue;
            bool first = true;
            for (uint cp = start; cp <= end; ++cp)
            {
                ct.ThrowIfCancellationRequested();
                yield return new NormalizationRow(cp, form, maybe, first);
                first = false;
                if (cp == 0x10FFFFu) break;
            }
        }
    }

    private static uint ParseHexOrZero(string value) =>
        uint.TryParse(value, NumberStyles.HexNumber, null, out uint parsed)
            && parsed <= 0x10FFFFu
            ? parsed
            : 0u;

    private static string StripComment(string value)
    {
        int hash = value.IndexOf('#');
        return (hash >= 0 ? value[..hash] : value).Trim();
    }

    private static bool TryRange(string value, out uint start, out uint end)
    {
        start = end = 0;
        int dots = value.IndexOf("..", StringComparison.Ordinal);
        if (dots < 0)
        {
            if (!uint.TryParse(value, NumberStyles.HexNumber, null, out start)
                || start > 0x10FFFFu)
                return false;
            end = start;
            return true;
        }
        if (!uint.TryParse(value[..dots], NumberStyles.HexNumber, null, out start)
            || !uint.TryParse(value[(dots + 2)..], NumberStyles.HexNumber, null, out end)
            || start > 0x10FFFFu)
            return false;
        end = Math.Min(end, 0x10FFFFu);
        return end >= start;
    }
}
