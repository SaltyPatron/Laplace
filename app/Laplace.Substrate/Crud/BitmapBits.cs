namespace Laplace.SubstrateCRUD;

/// <summary>
/// Get/set for the packed one-bit-per-candidate <c>byte[]</c> wire format every existence
/// and descent probe answers in (<c>entities_exist_bitmap</c>, tier-batch existence,
/// descent emit bitmaps, working-set present masks, …). The
/// <c>bm[index &gt;&gt; 3] &amp; (1 &lt;&lt; (index &amp; 7))</c> idiom was hand-copied at every
/// probe/descent/emit call site across the descent spine, the existence gate, and the
/// Chess adapters — each free to drift on the bounds check (some guarded with a
/// bit-count comparison, some not, one guarded only by an early-return on empty). One
/// accessor pair, used everywhere a caller reads or writes a bit against one of these
/// wire bitmaps.
/// </summary>
public static class BitmapBits
{
    /// <summary>Byte length of a packed bitmap wide enough for <paramref name="bitCount"/> bits.</summary>
    public static int ByteLength(int bitCount) => (bitCount + 7) / 8;

    /// <summary>
    /// True when bit <paramref name="index"/> is set. Out-of-range (negative, or beyond
    /// the bitmap's actual bit capacity) reads as unset rather than throwing — every
    /// existing call site treated a short or empty bitmap as "not yet confirmed present",
    /// never as an error.
    /// </summary>
    public static bool IsSet(byte[] bitmap, int index) =>
        index >= 0 && index < (long)bitmap.Length * 8 && (bitmap[index >> 3] & (1 << (index & 7))) != 0;

    /// <summary>Sets bit <paramref name="index"/> in place. Caller guarantees the bitmap is wide enough.</summary>
    public static void Set(byte[] bitmap, int index) =>
        bitmap[index >> 3] |= (byte)(1 << (index & 7));
}
