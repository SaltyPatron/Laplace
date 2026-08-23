using System.Text;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.OMW;

public static class OMWRowParser
{
    public static bool TryParseRow(
        ReadOnlySpan<byte> line, string fileLang, out OmwRow row, out ReadOnlySpan<byte> valueUtf8)
    {
        row = default;
        valueUtf8 = default;
        if (line.IsEmpty || line[0] == (byte)'#') return false;
        if (!TsvSpan.TryField(line, 0, out var synKey)) return false;
        if (!TsvSpan.TryField(line, 1, out var typeField)) return false;
        return TryParseCore(synKey, typeField, line, fileLang, out row, out valueUtf8);
    }

    /// <summary>
    /// wn-freq-*.tab: synset-pos \t lemma \t frequency. A different shape from the data
    /// tabs (whose second field is lang:type), so it gets its own entry point rather than
    /// a flag threaded through the shared one. The language comes from the filename; the
    /// frequency becomes the membership's magnitude.
    /// </summary>
    public static bool TryParseFreqRow(
        ReadOnlySpan<byte> line, string fileLang, out OmwRow row, out ReadOnlySpan<byte> valueUtf8)
    {
        row = default;
        valueUtf8 = default;
        if (line.IsEmpty || line[0] == (byte)'#') return false;
        if (!TsvSpan.TryField(line, 0, out var synKey)) return false;
        if (!TsvSpan.TryField(line, 1, out var lemma)) return false;
        if (!TsvSpan.TryField(line, 2, out var freq)) return false;
        if (synKey.IsEmpty || lemma.IsEmpty) return false;

        string synStr = Encoding.UTF8.GetString(synKey);
        int dash = synStr.LastIndexOf('-');
        if (dash < 0 || dash + 1 >= synStr.Length) return false;
        if (!long.TryParse(synStr.AsSpan(0, dash), out long offset)) return false;

        // A malformed or absent count must not silently become a magnitude of 1 and pass
        // for evidence -- the row exists only to carry the count, so drop it.
        if (!long.TryParse(Encoding.UTF8.GetString(freq), out long n) || n <= 0) return false;

        valueUtf8 = lemma;
        row = new OmwRow(offset, synStr[dash + 1], fileLang, OmwType.Freq, Removed: false,
                         Magnitude: n);
        return true;
    }

    public static bool TryParseFields(
        IReadOnlyList<(uint Start, uint End)> fields,
        ReadOnlySpan<byte> utf8,
        string fileLang,
        out OmwRow row,
        out ReadOnlySpan<byte> valueUtf8)
    {
        row = default;
        valueUtf8 = default;
        if (fields.Count < 3) return false;
        var synKey = Slice(utf8, fields[0]);
        var typeField = Slice(utf8, fields[1]);
        if (synKey.IsEmpty || synKey[0] == (byte)'#') return false;
        return TryParseCore(synKey, typeField, utf8, fileLang, out row, out valueUtf8, fields);
    }

    private static bool TryParseCore(
        ReadOnlySpan<byte> synKey,
        ReadOnlySpan<byte> typeField,
        ReadOnlySpan<byte> line,
        string fileLang,
        out OmwRow row,
        out ReadOnlySpan<byte> valueUtf8,
        IReadOnlyList<(uint Start, uint End)>? fields = null)
    {
        row = default;
        valueUtf8 = default;

        string lang = fileLang;
        ReadOnlySpan<byte> typeSpan = typeField;
        int colon = typeField.IndexOf((byte)':');
        if (colon >= 0)
        {
            lang = Encoding.UTF8.GetString(typeField[..colon]);
            typeSpan = typeField[(colon + 1)..];
        }






        int subColon = typeSpan.IndexOf((byte)':');
        if (subColon >= 0)
            typeSpan = typeSpan[..subColon];

        OmwType rowType;
        ReadOnlySpan<byte> valueSpan;
        if (typeSpan.SequenceEqual("lemma"u8))
        {
            rowType = OmwType.Lemma;
            valueSpan = fields is not null && fields.Count > 2
                ? Slice(line, fields[2])
                : (TsvSpan.TryField(line, 2, out var v2) ? v2 : default);
        }
        else if (typeSpan.SequenceEqual("def"u8))
        {
            rowType = OmwType.Def;
            valueSpan = fields is not null && fields.Count > 3
                ? Slice(line, fields[3])
                : (TsvSpan.TryField(line, 3, out var v3) ? v3
                    : (TsvSpan.TryField(line, 2, out var v2) ? v2 : default));
        }
        else if (typeSpan.SequenceEqual("exe"u8))
        {
            rowType = OmwType.Exe;
            valueSpan = fields is not null && fields.Count > 3
                ? Slice(line, fields[3])
                : (TsvSpan.TryField(line, 3, out var v3) ? v3
                    : (TsvSpan.TryField(line, 2, out var v2) ? v2 : default));
        }
        else return false;

        if (valueSpan.IsEmpty) return false;
        valueUtf8 = valueSpan;

        string synStr = Encoding.UTF8.GetString(synKey);
        int dash = synStr.LastIndexOf('-');
        if (dash < 0 || dash + 1 >= synStr.Length) return false;
        if (!long.TryParse(synStr.AsSpan(0, dash), out long offset)) return false;
        char ssType = synStr[dash + 1];
        row = new OmwRow(offset, ssType, lang, rowType);
        return true;
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> utf8, (uint Start, uint End) sp) =>
        utf8[(int)sp.Start..(int)sp.End];
}
