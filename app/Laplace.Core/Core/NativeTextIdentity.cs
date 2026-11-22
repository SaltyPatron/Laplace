using System.Buffers;
using System.Text;
using Laplace.Native;

namespace Laplace.Engine.Core;

internal static unsafe class NativeTextIdentity
{
    private static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: true);

    [ThreadStatic]
    private static void* _threadText;
    [ThreadStatic]
    private static lp_tier0_record* _threadTable;

    internal static void* ThreadContext()
    {
        lp_tier0_record* t0 = CodepointPerfcache.TablePointer;
        if (_threadText != null && _threadTable == t0)
            return _threadText;
        if (_threadText != null)
        {
            LaplaceNative.lp_text_free(_threadText);
            _threadText = null;
        }
        void* ctx = LaplaceNative.lp_text_new(t0);
        if (ctx == null)
            throw new InvalidOperationException(
                "Cannot initialize Native text identity. Configure canonical LAPLACE_TIER0 and provision ICU.");
        _threadText = ctx;
        _threadTable = t0;
        return ctx;
    }

    internal static Hash128 OfText(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        int length = Utf8.GetByteCount(text);
        void* ctx = ThreadContext();
        byte[]? rented = null;
        Span<byte> bytes = length <= 1024
            ? stackalloc byte[length]
            : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
        try
        {
            Utf8.GetBytes(text, bytes);
            lp_id result;
            fixed (byte* input = bytes)
            {
                if (!LaplaceNative.lp_text_id(ctx, input, (nuint)length, &result))
                    throw new ArgumentException("Text is not canonical UTF-8 scalar content.", nameof(text));
            }
            return Hash128.FromNative(result);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
