using System.Text;

namespace Laplace.Engine.Core;

public static class TextDecomposer
{
    public static TierTree Run(ReadOnlySpan<byte> utf8)
    {
        IntPtr handle = IntPtr.Zero;
        unsafe
        {
            lock (LaplaceCoreGate.Native)
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
                            _ => $"rc={rc}",
                        };
                        throw new InvalidOperationException(
                            $"laplace_text_decomposer_run failed: {reason}");
                    }
                }
            }
        }
        if (handle == IntPtr.Zero) throw new InvalidOperationException(
            "laplace_text_decomposer_run returned NULL with rc=0");
        return TierTree.FromExistingHandle(handle);
    }

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

    public static Hash128? ContentRootId(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0) return null;
        Hash128 id = default;
        unsafe
        {
            lock (LaplaceCoreGate.Native)
            {
                fixed (byte* p = utf8)
                {
                    int rc = NativeInterop.ContentRootId(p, (nuint)utf8.Length, &id);
                    if (rc == -3) throw new InvalidOperationException(
                        "laplace_content_root_id: perfcache not loaded — call CodepointPerfcache.LoadDefault() first");
                    if (rc != 0)
                    {
                        int previewLen = Math.Min(utf8.Length, 80);
                        string preview = Encoding.UTF8.GetString(utf8.Slice(0, previewLen))
                            .Replace("\r", "\\r").Replace("\n", "\\n");
                        throw new InvalidOperationException(
                            $"laplace_content_root_id failed (rc={rc}, len={utf8.Length}, " +
                            $"preview=\"{preview}\")");
                    }
                }
            }
        }
        return id;
    }

    public static Hash128? ContentRootId(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return null;
        int max = Encoding.UTF8.GetMaxByteCount(text.Length);
        if (max <= 4096)
        {
            Span<byte> buf = stackalloc byte[max];
            int n = Encoding.UTF8.GetBytes(text, buf);
            return ContentRootId(buf.Slice(0, n));
        }
        byte[] heap = Encoding.UTF8.GetBytes(text);
        return ContentRootId(heap.AsSpan());
    }
}
