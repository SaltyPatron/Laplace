using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;








public static class TestimonyWalk
{

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
}
