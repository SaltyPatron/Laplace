using System.Text;

namespace Laplace.Engine.Core;

public struct NativeTextSpan
{
    public uint Offset;
    public uint Length;
}

public struct FidePlayerProjection
{
    public NativeTextSpan FideId;
    public NativeTextSpan Name;
    public NativeTextSpan Country;
    public NativeTextSpan Sex;
    public NativeTextSpan Title;
    public NativeTextSpan StandardRating;
    public NativeTextSpan RapidRating;
    public NativeTextSpan BlitzRating;
    public NativeTextSpan Birthday;
    public NativeTextSpan Flag;
}

public static class FideXmlProjection
{
    private static readonly byte[] PlayerStart = "<player>"u8.ToArray();
    private static readonly byte[] PlayerEnd = "</player>"u8.ToArray();

    public static int Project(ReadOnlySpan<byte> utf8, Span<FidePlayerProjection> destination)
    {
        int count = 0;
        int pos = 0;
        while (count < destination.Length)
        {
            int relStart = utf8[pos..].IndexOf(PlayerStart);
            if (relStart < 0) break;
            int bodyStart = pos + relStart + PlayerStart.Length;
            int relEnd = utf8[bodyStart..].IndexOf(PlayerEnd);
            if (relEnd < 0) break;
            int bodyEnd = bodyStart + relEnd;

            ReadOnlySpan<byte> record = utf8[..bodyEnd];
            ref var dst = ref destination[count++];
            dst = default;
            dst.FideId = FindTag(record, bodyStart, "fideid"u8);
            dst.Name = FindTag(record, bodyStart, "name"u8);
            dst.Country = FindTag(record, bodyStart, "country"u8);
            dst.Sex = FindTag(record, bodyStart, "sex"u8);
            dst.Title = FindTag(record, bodyStart, "title"u8);
            dst.StandardRating = FindTag(record, bodyStart, "rating"u8);
            dst.RapidRating = FindTag(record, bodyStart, "rapid_rating"u8);
            dst.BlitzRating = FindTag(record, bodyStart, "blitz_rating"u8);
            dst.Birthday = FindTag(record, bodyStart, "birthday"u8);
            dst.Flag = FindTag(record, bodyStart, "flag"u8);

            pos = bodyEnd + PlayerEnd.Length;
        }
        return count;
    }

    private static NativeTextSpan FindTag(ReadOnlySpan<byte> buffer, int from, ReadOnlySpan<byte> tag)
    {
        Span<byte> open = stackalloc byte[tag.Length + 2];
        open[0] = (byte)'<';
        tag.CopyTo(open[1..]);
        open[^1] = (byte)'>';

        int relOpen = buffer[from..].IndexOf(open);
        if (relOpen < 0) return default;
        int valStart = from + relOpen + open.Length;

        Span<byte> close = stackalloc byte[tag.Length + 3];
        close[0] = (byte)'<';
        close[1] = (byte)'/';
        tag.CopyTo(close[2..]);
        close[^1] = (byte)'>';

        int relClose = buffer[valStart..].IndexOf(close);
        if (relClose < 0) return default;
        return new NativeTextSpan { Offset = (uint)valStart, Length = (uint)relClose };
    }
}
