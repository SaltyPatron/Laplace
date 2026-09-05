using System.Runtime.InteropServices;
using System.Text;

namespace Laplace.Engine.Core;

/// <summary>
/// One source-independent identity-key normalization law. Native core trims and
/// collapses Unicode whitespace, applies Unicode full case folding,
/// and emits NFC UTF-8.
/// </summary>
public static class IdentityKeyNormalization
{
    public static unsafe string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        CodepointPerfcache.LoadDefault();
        byte[] input = Encoding.UTF8.GetBytes(value);
        byte* output = null;
        nuint outputLength = 0;
        fixed (byte* inputPtr = input)
        {
            int rc = NativeInterop.IdentityKeyNormalizeUtf8(
                inputPtr, (nuint)input.Length, &output, &outputLength);
            if (rc != 0 || outputLength == 0)
                throw new InvalidOperationException($"native identity-key normalization failed: {rc}");
        }
        try
        {
            return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(output, checked((int)outputLength)));
        }
        finally
        {
            if (output is not null) NativeMemory.Free(output);
        }
    }
}
