using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// The BYTE TIER — the substrate's modality-blind floor (2026-06-05 ruling:
/// binary is authored content; bytes are the universal atoms every encoded
/// modality bottoms out at). Bytes 0x00–0x7F ARE their ASCII codepoint
/// entities (identical content bytes ⇒ identical BLAKE3 ⇒ one entity — the
/// content law, not a special case). Bytes 0x80–0xFF are 128 atoms BELOW the
/// codepoint tier (a lone high byte is an encoding fragment, not a character)
/// with their own canonical structural placements.
///
/// Placement: super-Fibonacci over the 128-point band, index = byte − 0x80 —
/// the byte's own standard order, same placement law as codepoints
/// (structural, never semantic). ONE implementation serves both the seed
/// (UnicodeDecomposer byte pass) and any anchorer (TokenS3Morph) so
/// coordinates are bit-identical everywhere.
/// </summary>
public static class ByteAtoms
{
    public const byte First = 0x80;
    public const int Count = 128;

    public static readonly Hash128 TypeId = Hash128.OfCanonical("substrate/type/Byte/v1");

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

    /// <summary>Entity id of a byte atom: BLAKE3 of the single byte — for
    /// b ≤ 0x7F this IS the ASCII codepoint's id (one content, one entity).</summary>
    public static Hash128 Id(byte b) => Hash128.Blake3(stackalloc byte[1] { b });

    /// <summary>Canonical placement of a high byte atom (b ≥ 0x80).</summary>
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

    /// <summary>UTF-8 role of a byte value — the standard's own classification
    /// (RFC 3629): continuation (0x80–0xBF), 2/3/4-byte lead, or never-valid.</summary>
    public static string Utf8Role(byte b) => b switch
    {
        >= 0x80 and <= 0xBF => "continuation",
        0xC0 or 0xC1        => "invalid",
        >= 0xC2 and <= 0xDF => "lead2",
        >= 0xE0 and <= 0xEF => "lead3",
        >= 0xF0 and <= 0xF4 => "lead4",
        >= 0xF5             => "invalid",
        _                   => "ascii",
    };

    /// <summary>Windows-1252 decoding of the 0x80–0x9F band (the 27 remapped
    /// characters; 5 slots undefined → 0). 0xA0–0xFF and Latin-1 agree.</summary>
    public static readonly ushort[] Cp1252High = new ushort[32]
    {
        0x20AC, 0, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021,
        0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0, 0x017D, 0,
        0, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014,
        0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0, 0x017E, 0,
    };
}
