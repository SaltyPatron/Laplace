using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

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

    internal readonly record struct RangeRecord(
        uint Start,
        uint End,
        string Value,
        bool CountsSourceRow = true);

    internal readonly record struct MirrorRow(uint Codepoint, uint Mirror);
    internal readonly record struct AliasRow(uint Codepoint, string Alias);
    internal readonly record struct ConfusableRow(uint Codepoint, string Target);
    internal readonly record struct NormalizationRow(
        uint Codepoint,
        string Form,
        bool Maybe,
        bool CountsSourceRow);
    internal readonly record struct BinaryPropertyRange(
        uint Start,
        uint End,
        string Property,
        bool CountsSourceRow = true);
    internal readonly record struct UnihanPropertyRow(
        uint Codepoint,
        string Property,
        string Value);

    internal readonly record struct DelimitedCodepointPropertyRow(
        uint Codepoint,
        string Property,
        string Value,
        bool ValueIsUnicodeSequence,
        bool CountsSourceRow);

    internal readonly record struct SequenceMetadataRow(
        string Sequence,
        string Property,
        string Value,
        bool CountsSourceRow);

    internal readonly record struct NamedSequenceRow(
        string Sequence,
        string Name,
        bool CountsSourceRow);

    internal readonly record struct CodepointListRow(
        uint Codepoint,
        bool CountsSourceRow);

    internal readonly record struct CjkRadicalRow(
        string Radical,
        uint RadicalCodepoint,
        uint UnifiedIdeograph);

    internal readonly record struct DoNotEmitRow(
        string Sequence,
        string Replacement,
        string Kind);

    internal readonly record struct PropertyAliasRow(
        string CanonicalProperty,
        string Alias,
        bool CountsSourceRow);

    internal readonly record struct PropertyValueAliasRow(
        string Property,
        string CanonicalValue,
        string Alias,
        bool CountsSourceRow);

    internal readonly record struct IndexTermRow(
        uint Codepoint,
        string Term);

    internal readonly record struct USourcePropertyRow(
        string SourceId,
        uint? Codepoint,
        string Property,
        string Value,
        bool CountsSourceRow);

    internal readonly record struct SequencePairRow(
        string Left,
        string Right);

    internal readonly record struct CttRow(
        uint Codepoint,
        string Primary,
        string Secondary,
        string Tertiary,
        string Quaternary);

    internal readonly record struct NamesListRow(
        uint Codepoint,
        string Kind,
        string Value,
        bool CountsSourceRow);

    internal readonly record struct BoundaryTestRow(
        string Sequence,
        string Boundaries,
        string Description);

    internal readonly record struct NormalizationTestRow(
        string Source,
        string Nfc,
        string Nfd,
        string Nfkc,
        string Nfkd,
        string Description);

    internal readonly record struct EmojiTestRow(
        string Sequence,
        string Status,
        string Version,
        string Name,
        string Group,
        string Subgroup);

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

    internal static async IAsyncEnumerable<RangeRecord> RangeRecordsAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct,
        int? maxExpandedRows = null,
        Func<string, int>? outputRowsPerCodepoint = null)
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
            int rowLimit = maxExpandedRows ?? IngestSizing.ResolveApplyTransactionRows();
            int multiplier = Math.Max(1, outputRowsPerCodepoint?.Invoke(value) ?? 1);
            uint window = checked((uint)Math.Max(
                1, rowLimit / multiplier - 1));
            for (uint cursor = start; cursor <= end;)
            {
                uint chunkEnd = (uint)Math.Min((ulong)end, (ulong)cursor + window - 1UL);
                yield return new RangeRecord(cursor, chunkEnd, value, first);
                first = false;
                if (chunkEnd == uint.MaxValue) break;
                cursor = chunkEnd + 1;
            }
        }
    }

    internal static async IAsyncEnumerable<BinaryPropertyRange> BinaryPropertyRangesAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct,
        int? maxExpandedRows = null)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = StripComment(Encoding.UTF8.GetString(lineMem.Span));
            if (line.Length == 0) continue;
            string[] fields = line.Split(';');
            if (fields.Length < 2) continue;
            string property = fields[1].Trim();
            if (property.Length == 0
                || !TryRange(fields[0].Trim(), out uint start, out uint end))
                continue;
            bool first = true;
            uint window = checked((uint)Math.Max(
                1, (maxExpandedRows ?? IngestSizing.ResolveApplyTransactionRows()) - 2));
            for (uint cursor = start; cursor <= end;)
            {
                uint chunkEnd = (uint)Math.Min((ulong)end, (ulong)cursor + window - 1UL);
                yield return new BinaryPropertyRange(cursor, chunkEnd, property, first);
                first = false;
                if (chunkEnd == uint.MaxValue) break;
                cursor = chunkEnd + 1;
            }
        }
    }

    internal static async IAsyncEnumerable<UnihanPropertyRow> UnihanPropertiesAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            if (lineMem.IsEmpty) continue;
            string line = Encoding.UTF8.GetString(lineMem.Span);
            if (line.Length == 0 || line[0] == '#') continue;
            string[] fields = line.Split('\t', 3);
            if (fields.Length != 3) continue;

            string cpText = fields[0].Trim();
            if (cpText.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
                cpText = cpText[2..];
            if (!uint.TryParse(cpText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint cp)
                || cp > 0x10FFFFu)
                continue;

            string property = fields[1].Trim();
            string value = fields[2].Trim();
            if (property.Length == 0 || value.Length == 0) continue;
            yield return new UnihanPropertyRow(cp, property, value);
        }
    }

    internal static async IAsyncEnumerable<DelimitedCodepointPropertyRow> DelimitedCodepointPropertiesAsync(
        string path,
        IReadOnlyList<string> propertyNames,
        IReadOnlySet<int>? unicodeSequenceFieldIndexes,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = StripComment(Encoding.UTF8.GetString(lineMem.Span));
            if (line.Length == 0) continue;
            string[] fields = line.Split(';');
            if (fields.Length < 2) continue;

            string subject = fields[0].Trim();
            if (subject.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
                subject = subject[2..];
            if (!TryRange(subject, out uint start, out uint end)) continue;

            bool sourceRow = true;
            for (uint cp = start; cp <= end; ++cp)
            {
                int available = Math.Min(propertyNames.Count, fields.Length - 1);
                for (int i = 0; i < available; ++i)
                {
                    string value = fields[i + 1].Trim();
                    if (value.Length == 0) continue;

                    bool isSequence = unicodeSequenceFieldIndexes?.Contains(i) == true;
                    if (isSequence && TryHexSequenceText(value, out string sequence))
                        value = sequence;
                    else if (isSequence)
                        continue;

                    yield return new DelimitedCodepointPropertyRow(
                        cp, propertyNames[i], value, isSequence, sourceRow);
                    sourceRow = false;
                }
                if (cp == 0x10FFFFu) break;
            }
        }
    }

    internal static async IAsyncEnumerable<NamedSequenceRow> NamedSequencesAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = StripComment(Encoding.UTF8.GetString(lineMem.Span));
            if (line.Length == 0) continue;
            string[] fields = line.Split(';');
            if (fields.Length < 2) continue;
            string name = fields[0].Trim();
            if (name.Length == 0 || !TryHexSequenceText(fields[1].Trim(), out string sequence))
                continue;
            yield return new NamedSequenceRow(sequence, name, true);
        }
    }

    internal static async IAsyncEnumerable<SequenceMetadataRow> SequenceMetadataAsync(
        string path,
        IReadOnlyList<string> propertyNames,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = StripComment(Encoding.UTF8.GetString(lineMem.Span));
            if (line.Length == 0) continue;
            string[] fields = line.Split(';');
            if (fields.Length < 2) continue;

            string subject = fields[0].Trim();
            var sequences = new List<string>();
            if (subject.Contains("..", StringComparison.Ordinal)
                && TryRange(subject, out uint start, out uint end))
            {
                for (uint cp = start; cp <= end; ++cp)
                {
                    if (cp is < 0xD800u or > 0xDFFFu)
                        sequences.Add(char.ConvertFromUtf32((int)cp));
                    if (cp == 0x10FFFFu) break;
                }
            }
            else if (TryHexSequenceText(subject, out string one))
            {
                sequences.Add(one);
            }
            if (sequences.Count == 0) continue;

            bool sourceRow = true;
            foreach (string sequence in sequences)
            {
                int available = Math.Min(propertyNames.Count, fields.Length - 1);
                for (int i = 0; i < available; ++i)
                {
                    string value = fields[i + 1].Trim();
                    if (value.Length == 0) continue;
                    yield return new SequenceMetadataRow(
                        sequence, propertyNames[i], value, sourceRow);
                    sourceRow = false;
                }
            }
        }
    }

    internal static async IAsyncEnumerable<CodepointListRow> CodepointListAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = StripComment(Encoding.UTF8.GetString(lineMem.Span));
            if (line.Length == 0) continue;
            int semi = line.IndexOf(';');
            string rangeText = (semi >= 0 ? line[..semi] : line).Trim();
            if (rangeText.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
                rangeText = rangeText[2..];
            if (!TryRange(rangeText, out uint start, out uint end)) continue;
            bool first = true;
            for (uint cp = start; cp <= end; ++cp)
            {
                ct.ThrowIfCancellationRequested();
                yield return new CodepointListRow(cp, first);
                first = false;
                if (cp == 0x10FFFFu) break;
            }
        }
    }

    internal static async IAsyncEnumerable<CjkRadicalRow> CjkRadicalsAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = StripComment(Encoding.UTF8.GetString(lineMem.Span));
            if (line.Length == 0) continue;
            string[] fields = line.Split(';');
            if (fields.Length < 3) continue;
            string radical = fields[0].Trim();
            if (radical.Length == 0
                || !TrySingleCodepoint(fields[1].Trim(), out uint radicalCp)
                || !TrySingleCodepoint(fields[2].Trim(), out uint unified))
                continue;
            yield return new CjkRadicalRow(radical, radicalCp, unified);
        }
    }

    internal static async IAsyncEnumerable<DoNotEmitRow> DoNotEmitAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = StripComment(Encoding.UTF8.GetString(lineMem.Span));
            if (line.Length == 0) continue;
            string[] fields = line.Split(';');
            if (fields.Length < 3
                || !TryHexSequenceText(fields[0].Trim(), out string sequence)
                || !TryHexSequenceText(fields[1].Trim(), out string replacement))
                continue;
            string kind = fields[2].Trim();
            if (kind.Length == 0) continue;
            yield return new DoNotEmitRow(sequence, replacement, kind);
        }
    }

    internal static async IAsyncEnumerable<PropertyAliasRow> PropertyAliasesAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = StripComment(Encoding.UTF8.GetString(lineMem.Span));
            if (line.Length == 0) continue;
            string[] fields = line.Split(';');
            if (fields.Length < 2) continue;
            string shortName = fields[0].Trim();
            string canonical = fields[1].Trim();
            if (canonical.Length == 0) continue;
            bool first = true;
            if (shortName.Length > 0)
            {
                yield return new PropertyAliasRow(canonical, shortName, first);
                first = false;
            }
            for (int i = 2; i < fields.Length; ++i)
            {
                string alias = fields[i].Trim();
                if (alias.Length == 0) continue;
                yield return new PropertyAliasRow(canonical, alias, first);
                first = false;
            }
        }
    }

    internal static async IAsyncEnumerable<PropertyValueAliasRow> PropertyValueAliasesAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = StripComment(Encoding.UTF8.GetString(lineMem.Span));
            if (line.Length == 0) continue;
            string[] fields = line.Split(';');
            if (fields.Length < 3) continue;
            string property = fields[0].Trim();
            if (property.Length == 0) continue;

            int canonicalIndex = string.Equals(property, "ccc", StringComparison.Ordinal)
                && fields.Length >= 4 ? 3 : 2;
            string canonical = fields[canonicalIndex].Trim();
            if (canonical.Length == 0) continue;

            bool first = true;
            for (int i = 1; i < fields.Length; ++i)
            {
                if (i == canonicalIndex) continue;
                string alias = fields[i].Trim();
                if (alias.Length == 0) continue;
                yield return new PropertyValueAliasRow(
                    property, canonical, alias, first);
                first = false;
            }
        }
    }

    internal static async IAsyncEnumerable<IndexTermRow> IndexTermsAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            if (lineMem.IsEmpty) continue;
            string line = Encoding.UTF8.GetString(lineMem.Span);
            if (line.Length == 0 || line[0] == '#') continue;
            string[] fields = line.Split('\t');
            if (fields.Length < 2) continue;
            string term = fields[0].Trim();
            if (term.Length == 0 || !TrySingleCodepoint(fields[1].Trim(), out uint cp))
                continue;
            yield return new IndexTermRow(cp, term);
        }
    }

    internal static async IAsyncEnumerable<USourcePropertyRow> USourceDataAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string[] names =
        [
            "USource_Status", "USource_Codepoint", "kRSUnicode",
            "USource_Virtual_KangXi_Position", "USource_IDS", "USource_Sources",
            "USource_Comment", "kTotalStrokes", "USource_First_Residual_Stroke"
        ];

        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = StripComment(Encoding.UTF8.GetString(lineMem.Span));
            if (line.Length == 0) continue;
            string[] fields = line.Split(';');
            if (fields.Length < 3) continue;
            string id = fields[0].Trim();
            if (id.Length == 0) continue;

            uint? cp = TrySingleCodepoint(fields[2].Trim(), out uint parsedCp)
                ? parsedCp
                : null;
            bool first = true;
            int available = Math.Min(names.Length, fields.Length - 1);
            for (int i = 0; i < available; ++i)
            {
                string value = fields[i + 1].Trim();
                if (value.Length == 0) continue;
                yield return new USourcePropertyRow(
                    id, cp, names[i], value, first);
                first = false;
            }
        }
    }

    internal static async IAsyncEnumerable<SequencePairRow> SequencePairsAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = StripComment(Encoding.UTF8.GetString(lineMem.Span));
            if (line.Length == 0) continue;
            string[] fields = line.Split(';');
            if (fields.Length < 2
                || !TryHexSequenceText(fields[0].Trim(), out string left)
                || !TryHexSequenceText(fields[1].Trim(), out string right))
                continue;
            yield return new SequencePairRow(left, right);
        }
    }

    internal static async IAsyncEnumerable<CttRow> CttAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = Encoding.UTF8.GetString(lineMem.Span);
            int comment = line.IndexOf('%');
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (!line.StartsWith("<U", StringComparison.Ordinal)) continue;

            int close = line.IndexOf('>');
            if (close <= 2) continue;
            string cpText = line[2..close];
            if (!uint.TryParse(
                    cpText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint cp)
                || cp > 0x10FFFFu)
                continue;

            string[] weights = line[(close + 1)..].Trim()
                .Split(';', StringSplitOptions.TrimEntries);
            if (weights.Length < 4) continue;
            yield return new CttRow(
                cp, weights[0], weights[1], weights[2], weights[3]);
        }
    }

    internal static async IAsyncEnumerable<NamesListRow> NamesListAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        uint? current = null;
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            if (lineMem.IsEmpty) continue;
            string raw = Encoding.UTF8.GetString(lineMem.Span);
            if (raw.Length == 0 || raw[0] == ';') continue;

            int tab = raw.IndexOf('\t');
            if (tab > 0)
            {
                string head = raw[..tab].Trim();
                if (uint.TryParse(
                        head, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint cp)
                    && cp <= 0x10FFFFu)
                {
                    current = cp;
                    string name = raw[(tab + 1)..].Trim();
                    if (name.Length > 0)
                        yield return new NamesListRow(cp, "Name", name, true);
                    continue;
                }
            }

            if (current is not { } owner) continue;
            string trimmed = raw.Trim();
            if (trimmed.Length < 2 || trimmed[0] == '@') continue;

            char marker = trimmed[0];
            string value = trimmed[1..].Trim();
            if (value.Length == 0) continue;
            string kind = marker switch
            {
                '=' => "Alias",
                '*' => "Note",
                'x' or 'X' => "Cross_Reference",
                '~' => "Variation",
                ':' => "Canonical_Decomposition_Note",
                '#' => "Compatibility_Decomposition_Note",
                _ => $"Annotation_{(int)marker:X2}",
            };
            yield return new NamesListRow(owner, kind, value, false);
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

    internal static async IAsyncEnumerable<BoundaryTestRow> BoundaryTestsAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (ReadOnlyMemory<byte> lineMemory in
                       StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            if (lineMemory.IsEmpty) continue;
            string line = Encoding.UTF8.GetString(lineMemory.Span);
            int commentAt = line.IndexOf('#');
            string rule = (commentAt >= 0 ? line[..commentAt] : line).Trim();
            if (rule.Length == 0) continue;
            string description = commentAt >= 0 ? line[(commentAt + 1)..].Trim() : string.Empty;
            string[] tokens = rule.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length < 3 || tokens.Length % 2 == 0) continue;

            var sequence = new StringBuilder(tokens.Length / 2);
            var boundaries = new StringBuilder(tokens.Length / 2 + 1);
            bool valid = true;
            for (int i = 0; i < tokens.Length; ++i)
            {
                if ((i & 1) == 0)
                {
                    if (tokens[i] is not ("÷" or "×")) { valid = false; break; }
                    boundaries.Append(tokens[i]);
                    continue;
                }
                if (!uint.TryParse(
                        tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                        out uint codepoint)
                    || !Rune.TryCreate(codepoint, out Rune rune))
                {
                    valid = false;
                    break;
                }
                sequence.Append(rune.ToString());
            }
            if (valid && sequence.Length != 0)
                yield return new BoundaryTestRow(
                    sequence.ToString(), boundaries.ToString(), description);
        }
    }

    internal static async IAsyncEnumerable<NormalizationTestRow> NormalizationTestsAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (ReadOnlyMemory<byte> lineMemory in
                       StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            if (lineMemory.IsEmpty) continue;
            string line = Encoding.UTF8.GetString(lineMemory.Span);
            int commentAt = line.IndexOf('#');
            string rule = (commentAt >= 0 ? line[..commentAt] : line).Trim();
            if (rule.Length == 0 || rule[0] == '@') continue;
            string[] fields = rule.Split(';', StringSplitOptions.TrimEntries);
            if (fields.Length < 5
                || !TryHexSequenceText(fields[0], out string source)
                || !TryHexSequenceText(fields[1], out string nfc)
                || !TryHexSequenceText(fields[2], out string nfd)
                || !TryHexSequenceText(fields[3], out string nfkc)
                || !TryHexSequenceText(fields[4], out string nfkd))
                throw new InvalidDataException(
                    $"NormalizationTest has an invalid conformance row: '{rule}'.");
            yield return new NormalizationTestRow(
                source, nfc, nfd, nfkc, nfkd,
                commentAt >= 0 ? line[(commentAt + 1)..].Trim() : string.Empty);
        }
    }

    internal static async IAsyncEnumerable<EmojiTestRow> EmojiTestsAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string group = string.Empty;
        string subgroup = string.Empty;
        await foreach (ReadOnlyMemory<byte> lineMemory in
                       StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            if (lineMemory.IsEmpty) continue;
            string line = Encoding.UTF8.GetString(lineMemory.Span).Trim();
            if (line.StartsWith("# group:", StringComparison.Ordinal))
            {
                group = line[8..].Trim();
                continue;
            }
            if (line.StartsWith("# subgroup:", StringComparison.Ordinal))
            {
                subgroup = line[11..].Trim();
                continue;
            }
            if (line.Length == 0 || line[0] == '#') continue;

            int hash = line.IndexOf('#');
            string rule = (hash >= 0 ? line[..hash] : line).Trim();
            string comment = hash >= 0 ? line[(hash + 1)..].Trim() : string.Empty;
            string[] fields = rule.Split(';', StringSplitOptions.TrimEntries);
            if (fields.Length < 2
                || !TryHexSequenceText(fields[0], out string sequence)
                || fields[1].Length == 0)
                throw new InvalidDataException($"emoji-test has an invalid row: '{rule}'.");

            string version = string.Empty;
            string name = comment;
            string[] commentParts = comment.Split(
                (char[]?)null, 3,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (commentParts.Length >= 2 && commentParts[1].StartsWith('E'))
            {
                version = commentParts[1];
                name = commentParts.Length >= 3 ? commentParts[2] : string.Empty;
            }
            yield return new EmojiTestRow(
                sequence, fields[1], version, name, group, subgroup);
        }
    }

    private static bool TrySingleCodepoint(string value, out uint codepoint)
    {
        string token = value.Trim();
        if (token.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
            token = token[2..];
        return uint.TryParse(
                token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out codepoint)
            && codepoint <= 0x10FFFFu
            && codepoint is < 0xD800u or > 0xDFFFu;
    }

    private static bool TryHexSequenceText(string value, out string sequence)
    {
        var sb = new StringBuilder();
        foreach (string raw in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim();
            if (token.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
                token = token[2..];
            if (!uint.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint cp)
                || cp > 0x10FFFFu
                || cp is >= 0xD800u and <= 0xDFFFu)
            {
                sequence = string.Empty;
                return false;
            }
            sb.Append(char.ConvertFromUtf32((int)cp));
        }
        sequence = sb.ToString();
        return sequence.Length > 0;
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
