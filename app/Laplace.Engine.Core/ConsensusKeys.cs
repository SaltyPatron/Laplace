namespace Laplace.Engine.Core;

/// <summary>
/// The single client-side implementation of the substrate's consensus edge id.
/// Byte-identical to SQL laplace.consensus_id and the native fold engine's ident:
/// blake3(subject(16) || type(16) || object-or-zero(16)) — FOLD_IDENT_LEN = 48
/// (consensus_fold_io.h / consensus_fold_incremental.c). A missing object is
/// sixteen zero bytes, not an absent field.
/// </summary>
public static class ConsensusKeys
{
    private static readonly byte[] ZeroObject = new byte[16];

    public static Hash128 EdgeId(Hash128 subject, Hash128 type, Hash128 obj)
    {
        Span<byte> buf = stackalloc byte[48];
        subject.WriteBytes(buf[..16]);
        type.WriteBytes(buf.Slice(16, 16));
        obj.WriteBytes(buf.Slice(32, 16));
        return Hash128.Blake3(buf);
    }

    public static Hash128 EdgeId(Hash128 subject, Hash128 type, Hash128? obj)
    {
        if (obj is { } o) return EdgeId(subject, type, o);
        Span<byte> buf = stackalloc byte[48];
        subject.WriteBytes(buf[..16]);
        type.WriteBytes(buf.Slice(16, 16));
        ZeroObject.CopyTo(buf.Slice(32, 16));
        return Hash128.Blake3(buf);
    }
}
