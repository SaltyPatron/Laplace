using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;








public static class TestimonyWalk
{
    public readonly record struct Vertex(
        Hash128 ObjectId, long ScoreFp1e9, ushort Games,
        int LogicalOrdinal, ushort PackedOrdinal);

    public static byte[] Pack(ReadOnlySpan<Hash128> objects, ReadOnlySpan<long> scoresFp1e9)
        => Pack(objects, scoresFp1e9, ReadOnlySpan<ushort>.Empty);


    public static unsafe byte[] Pack(
        ReadOnlySpan<Hash128> objects, ReadOnlySpan<long> scoresFp1e9, ReadOnlySpan<ushort> games)
    {
        if (objects.Length == 0) throw new ArgumentException("empty walk");
        if (objects.Length != scoresFp1e9.Length)
            throw new ArgumentException("objects/scores length mismatch");
        if (!games.IsEmpty && games.Length != objects.Length)
            throw new ArgumentException("objects/games length mismatch");

        var bytes = new byte[objects.Length * 4 * sizeof(double)];
        fixed (Hash128* po = objects)
        fixed (long* ps = scoresFp1e9)
        fixed (ushort* pg = games)
        fixed (byte* pb = bytes)
        {
            int rc = NativeInterop.LaplaceTestimonyPackWalk(
                po, ps, games.IsEmpty ? null : pg, (nuint)objects.Length, (double*)pb);
            if (rc != 0)
                throw new InvalidOperationException($"testimony pack failed: {rc}");
        }
        return bytes;
    }

    public static unsafe Vertex[] Unpack(ReadOnlySpan<double> xyzm)
    {
        if (xyzm.Length == 0 || xyzm.Length % 4 != 0)
            throw new ArgumentException("testimony trajectory must contain complete XYZM vertices");
        int count = xyzm.Length / 4;
        var result = new Vertex[count];
        fixed (double* pv = xyzm)
        {
            for (int i = 0; i < count; i++)
            {
                Hash128 objectId = default;
                long score = 0;
                ushort games = 0, packedOrdinal = 0;
                int rc = NativeInterop.LaplaceTestimonyUnpackVertex(
                    pv + i * 4, &objectId, &score, &games, &packedOrdinal);
                if (rc != 0)
                    throw new InvalidOperationException(
                        $"testimony unpack failed at logical ordinal {i}: {rc}");
                result[i] = new Vertex(objectId, score, games, i, packedOrdinal);
            }
        }
        return result;
    }
}
