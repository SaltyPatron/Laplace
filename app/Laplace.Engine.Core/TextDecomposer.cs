using System.Text;

namespace Laplace.Engine.Core;

/// <summary>
/// Managed wrapper over the engine <c>laplace_text_decomposer_run</c>
/// (engine/core/include/laplace/core/text_decomposer.h). Pure NFC +
/// UAX#29 text decomposition per ADR 0047.
///
/// <para>Same input bytes -> identical TierTree -> identical content-
/// addressed hashes (RULES R7). Determinism is pinned by the compiled-in
/// UCD tables from <c>LAPLACE_UCD_PATH</c> at the version
/// <c>LAPLACE_UNICODE_VERSION</c>.</para>
/// </summary>
public static class TextDecomposer
{
    /// <summary>Decompose UTF-8 bytes into a tier_tree. Caller owns
    /// the returned <see cref="TierTree"/> and must dispose it.</summary>
    public static TierTree Run(ReadOnlySpan<byte> utf8)
    {
        IntPtr handle = IntPtr.Zero;
        unsafe
        {
            fixed (byte* p = utf8)
            {
                int rc = NativeInterop.TextDecomposerRun(p, (nuint)utf8.Length, &handle);
                if (rc != 0)
                {
                    string reason = rc switch
                    {
                        -1 => "null args",
                        -2 => "invalid UTF-8 sequence",
                        -3 => "allocation failure",
                        _  => $"rc={rc}",
                    };
                    throw new InvalidOperationException(
                        $"laplace_text_decomposer_run failed: {reason}");
                }
            }
        }
        if (handle == IntPtr.Zero) throw new InvalidOperationException(
            "laplace_text_decomposer_run returned NULL with rc=0");
        return TierTree.FromExistingHandle(handle);
    }

    /// <summary>UTF-8-encode <paramref name="text"/> and decompose.</summary>
    public static TierTree Run(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return Run(ReadOnlySpan<byte>.Empty);
        int max = Encoding.UTF8.GetMaxByteCount(text.Length);
        if (max <= 4096)
        {
            Span<byte> buf = stackalloc byte[max];
            int n = Encoding.UTF8.GetBytes(text, buf);
            return Run(buf.Slice(0, n));
        }
        byte[] heap = Encoding.UTF8.GetBytes(text);
        return Run(heap.AsSpan());
    }
}
