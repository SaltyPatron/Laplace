namespace Laplace.Decomposers.Abstractions;

/// <summary>Alphabet / dense-prefix scope for a seed source (perfcache selector).</summary>
public enum ScopeTier
{
    Ascii = 0x80,
    Bmp = 0x10000,
    Full = 0x110000,
}

/// <summary>
/// Compile-time alphabet filter. <see cref="InScope"/> is the monomorphized gate;
/// <see cref="Tier"/> selects the matching perfcache dense prefix.
/// </summary>
public interface ISeedScope
{
    static abstract bool InScope(uint symbol);
    static abstract ScopeTier Tier { get; }
}

public readonly struct AsciiScope : ISeedScope
{
    public static bool InScope(uint symbol) => symbol < (uint)ScopeTier.Ascii;
    public static ScopeTier Tier => ScopeTier.Ascii;
}

public readonly struct BmpScope : ISeedScope
{
    public static bool InScope(uint symbol) => symbol < (uint)ScopeTier.Bmp;
    public static ScopeTier Tier => ScopeTier.Bmp;
}

public readonly struct FullScope : ISeedScope
{
    public static bool InScope(uint symbol) => symbol < (uint)ScopeTier.Full;
    public static ScopeTier Tier => ScopeTier.Full;
}
