using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

[StructLayout(LayoutKind.Sequential)]
public struct NativeTextSpan
{
    public uint Offset;
    public uint Length;
}

[StructLayout(LayoutKind.Sequential)]
public struct FidePlayerProjection
{
    public NativeTextSpan FideId;
    public NativeTextSpan Name;
    public NativeTextSpan Country;
    public NativeTextSpan Sex;
    public NativeTextSpan Title;
    public NativeTextSpan StandardRating;
    public NativeTextSpan RapidRating;
    public NativeTextSpan BlitzRating;
    public NativeTextSpan Birthday;
    public NativeTextSpan Flag;
}

public static unsafe class FideXmlProjection
{
    public static int Project(ReadOnlySpan<byte> utf8, Span<FidePlayerProjection> destination)
    {
        nuint count = 0;
        fixed (byte* input = utf8)
        fixed (FidePlayerProjection* output = destination)
        {
            int rc = NativeInterop.FideXmlProject(
                input, (nuint)utf8.Length, output, (nuint)destination.Length, &count);
            if (rc != 0)
                throw new InvalidDataException($"native FIDE XML projection failed ({rc})");
        }
        return checked((int)count);
    }
}
