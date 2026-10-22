using System.Buffers;
using System.Text;

namespace Laplace.Engine.Core;

internal static class NativeTextIdentity
{
    private static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: true);

    internal static unsafe Hash128 OfText(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        int length = Utf8.GetByteCount(text);
        byte[]? rented = null;
        Span<byte> bytes = length <= 1024
            ? stackalloc byte[length]
            : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
        try
        {
            Utf8.GetBytes(text, bytes);
            Hash128 result;
            fixed (byte* input = bytes)
            {
                int status = NativeInterop.Hash128Text(input, (nuint)length, &result);
                if (status == -2)
                    throw new InvalidOperationException(
                        "Cannot initialize Native text identity. Configure canonical LAPLACE_TIER0 and provision ICU.");
                if (status != 0)
                    throw new ArgumentException("Text is not canonical UTF-8 scalar content.", nameof(text));
            }
            return result;
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
