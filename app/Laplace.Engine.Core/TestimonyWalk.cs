using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// Testimony rides the trajectory law: a subject's thresholded table read at
/// one (plane, layer) is a WALK — an ordered sequence of object references,
/// packed under the same 212-bit vertex law as content (object id in X/Y/Z
/// mantissas, games in run_length, zigzagged fp1e9 score in the flags field).
/// One native crossing packs the whole walk.
/// </summary>
public static class TestimonyWalk
{
    /// <summary>Pack one walk (games = 1 per vertex) into vertex doubles as bytes.</summary>
    public static byte[] Pack(ReadOnlySpan<Hash128> objects, ReadOnlySpan<long> scoresFp1e9)
        => Pack(objects, scoresFp1e9, ReadOnlySpan<ushort>.Empty);

    /// <summary>Pack one walk with per-vertex games (RLE = repeated observation).</summary>
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
