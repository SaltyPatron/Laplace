namespace Laplace.SubstrateCRUD;

/// <summary>
/// Get/set for the packed one-bit-per-candidate <c>byte[]</c> wire format that existence
/// and descent probes answer in (<c>entities_exist_bitmap</c>, tier-batch existence,
/// descent emit bitmaps, working-set present masks). Bit <c>i</c> is
/// <c>bm[i &gt;&gt; 3] &amp; (1 &lt;&lt; (i &amp; 7))</c>, least-significant bit first.
/// </summary>
public static class BitmapBits
{
    /// <summary>Byte length of a packed bitmap wide enough for <paramref name="bitCount"/> bits.</summary>
    public static int ByteLength(int bitCount) => (bitCount + 7) / 8;

    /// <summary>
    /// True when bit <paramref name="index"/> is set. An index that is negative or beyond
    /// the bitmap's bit capacity reads as unset: a short or empty bitmap means
    /// "not confirmed present", not an error.
    /// </summary>
    public static bool IsSet(byte[] bitmap, int index) =>
        index >= 0 && index < (long)bitmap.Length * 8 && (bitmap[index >> 3] & (1 << (index & 7))) != 0;

    /// <summary>Sets bit <paramref name="index"/> in place. Caller guarantees the bitmap is wide enough.</summary>
    public static void Set(byte[] bitmap, int index) =>
        bitmap[index >> 3] |= (byte)(1 << (index & 7));
}
