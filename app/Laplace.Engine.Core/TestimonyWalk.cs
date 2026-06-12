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
    public static unsafe byte[] Pack(ReadOnlySpan<Hash128> objects, ReadOnlySpan<long> scoresFp1e9)
    {
        if (objects.Length == 0) throw new ArgumentException("empty walk");
        if (objects.Length != scoresFp1e9.Length)
            throw new ArgumentException("objects/scores length mismatch");

        var bytes = new byte[objects.Length * 4 * sizeof(double)];
        fixed (Hash128* po = objects)
        fixed (long* ps = scoresFp1e9)
        fixed (byte* pb = bytes)
        {
            int rc = NativeInterop.LaplaceTestimonyPackWalk(
                po, ps, null, (nuint)objects.Length, (double*)pb);
            if (rc != 0)
                throw new InvalidOperationException($"testimony pack failed: {rc}");
        }
        return bytes;
    }
}
