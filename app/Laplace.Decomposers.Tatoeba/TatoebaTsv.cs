using System.Text;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.Tatoeba;

internal ref struct TatoebaSentenceRow
{
    public long Id { get; init; }
    public string Lang { get; init; }
    public ReadOnlySpan<byte> TextUtf8 { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> line, out TatoebaSentenceRow row)
    {
        row = default!;
        if (!TsvSpan.TryField(line, 0, out var idField)) return false;
        if (!TsvSpan.TryField(line, 1, out var langField)) return false;
        if (!TsvSpan.TryField(line, 2, out var textField) || textField.IsEmpty) return false;
        if (!TryParseInt64(idField, out long id)) return false;

        row = new TatoebaSentenceRow
        {
            Id = id,
            Lang = Encoding.UTF8.GetString(langField).Trim(),
            TextUtf8 = textField,
        };
        return true;
    }

    private static bool TryParseInt64(ReadOnlySpan<byte> s, out long v)
    {
        v = 0;
        if (s.IsEmpty) return false;
        for (int i = 0; i < s.Length; i++)
        {
            byte c = s[i];
            if (c < (byte)'0' || c > (byte)'9') return false;
            v = checked(v * 10 + (c - (byte)'0'));
        }
        return true;
    }
}

internal ref struct TatoebaLinkRow
{
    public long A { get; init; }
    public long B { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> line, out TatoebaLinkRow row)
    {
        row = default;
        if (!TsvSpan.TryField(line, 0, out var aField)) return false;
        if (!TsvSpan.TryField(line, 1, out var bField)) return false;
        if (!TryParseInt64(aField, out long a)) return false;
        if (!TryParseInt64(bField, out long b)) return false;
        row = new TatoebaLinkRow { A = a, B = b };
        return true;
    }

    private static bool TryParseInt64(ReadOnlySpan<byte> s, out long v)
    {
        v = 0;
        if (s.IsEmpty) return false;
        for (int i = 0; i < s.Length; i++)
        {
            byte c = s[i];
            if (c < (byte)'0' || c > (byte)'9') return false;
            v = checked(v * 10 + (c - (byte)'0'));
        }
        return true;
    }
}
