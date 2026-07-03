using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static class ByteAtoms
{
    public const byte First = 0x80;
    public const int Count = 128;

    public static readonly Hash128 TypeId = Hash128.Blake3("Byte"u8);

    private static readonly double[] Coords = ComputeCoords();
    private static readonly Hilbert128[] Hilberts = ComputeHilberts();

    private static unsafe double[] ComputeCoords()
    {
        var q = new double[Count * 4];
        fixed (double* p = q) NativeInterop.SuperFibonacci(Count, p);
        return q;
    }

    private static unsafe Hilbert128[] ComputeHilberts()
    {
        var hs = new Hilbert128[Count];
        fixed (double* p = Coords)
        fixed (Hilbert128* h = hs)
            for (int i = 0; i < Count; i++)
                NativeInterop.Hilbert4dEncode(p + i * 4, h + i);
        return hs;
    }

    public static Hash128 Id(byte b) => Hash128.Blake3(stackalloc byte[1] { b });

    public static ReadOnlySpan<double> Coord(byte b)
    {
        if (b < First) throw new ArgumentOutOfRangeException(nameof(b),
            "bytes ≤ 0x7F are codepoint entities — their placement comes from the codepoint seed");
        return Coords.AsSpan((b - First) * 4, 4);
    }

    public static Hilbert128 Hilbert(byte b)
    {
        if (b < First) throw new ArgumentOutOfRangeException(nameof(b));
        return Hilberts[b - First];
    }

    public static string Utf8Role(byte b) => b switch
    {
        >= 0x80 and <= 0xBF => "continuation",
        0xC0 or 0xC1 => "invalid",
        >= 0xC2 and <= 0xDF => "lead2",
        >= 0xE0 and <= 0xEF => "lead3",
        >= 0xF0 and <= 0xF4 => "lead4",
        >= 0xF5 => "invalid",
        _ => "ascii",
    };

    public static readonly ushort[] Cp1252High = new ushort[32]
    {
        0x20AC, 0, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021,
        0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0, 0x017D, 0,
        0, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014,
        0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0, 0x017E, 0,
    };
}
