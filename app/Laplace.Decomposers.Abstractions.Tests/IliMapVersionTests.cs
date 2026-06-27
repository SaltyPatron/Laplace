using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Fast, fixture-built lock-in for the version-aware ILI map and the adjective a/s collapse.
/// Builds tiny temp maps and ALWAYS asserts — it never skips on a missing corpus. The silent-skip
/// pattern (<c>if (data absent) return;</c>) is exactly what let the satellite-drop and pwn16
/// version-mismatch bugs reach a full ingest. Pure: Dictionary + file read, no native DLL, no Postgres.
/// </summary>
[Trait("Tier", "fast")]
public sealed class IliMapVersionTests : IDisposable
{
    private readonly List<string> _temp = new();

    private string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-ilimap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _temp.Add(dir);
        return dir;
    }

    private static void Write(string dir, string relPath, params string[] lines)
    {
        string path = Path.Combine(dir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    public void Dispose()
    {
        foreach (var d in _temp)
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
    }

    // OMW writes adjective satellites as '-a'; pwn30 stores them as '-s'. The map key must treat an
    // adjective offset identically under 'a' and 's', or every satellite lemma (incl. i12345's foreign
    // words) silently drops — ~3% of OMW across all 33 languages.
    [Fact]
    public void Resolve_CollapsesAdjectiveSatelliteAndHead()
    {
        string dir = NewDir();
        Write(dir, IliMap.MapFileName,
            "i100\t00000001-a",   // head adjective
            "i200\t00000002-s");  // satellite adjective (pwn stores as -s)
        var map = IliMap.Load(dir);

        Assert.Equal("i200", map.Resolve(2, 's'));   // as stored
        Assert.Equal("i200", map.Resolve(2, 'a'));   // OMW's '-a' query must still resolve the satellite
        Assert.Equal("i100", map.Resolve(1, 'a'));   // head adjective
        Assert.Equal("i100", map.Resolve(1, 's'));   // collapse is symmetric

        Assert.Null(map.Resolve(1, 'n'));            // distinct parts of speech stay distinct
    }

    // Older-wn maps live under older-wn-mappings/ and carry a 3rd confidence column that must be ignored.
    [Fact]
    public void LoadVersion_ReadsOlderWnMap_StrippingConfidenceColumn()
    {
        string dir = NewDir();
        Write(dir, Path.Combine("older-wn-mappings", "ili-map-pwn16.tab"),
            "i1\t00002403-a\t1",
            "i4026\t00006263-a\t0.352");
        var map = IliMap.LoadVersion(dir, "pwn16");

        Assert.NotNull(map);
        Assert.Equal("i1", map!.Resolve(2403, 'a'));
        Assert.Equal("i4026", map.Resolve(6263, 'a'));
    }

    [Fact]
    public void LoadVersion_PrefersRootOverOlderWnMappings()
    {
        string dir = NewDir();
        Write(dir, "ili-map-pwn16.tab", "i999\t00000005-n");
        Write(dir, Path.Combine("older-wn-mappings", "ili-map-pwn16.tab"), "i111\t00000005-n\t1");

        Assert.Equal("i999", IliMap.LoadVersion(dir, "pwn16")!.Resolve(5, 'n'));   // root wins
    }

    [Fact]
    public void LoadVersion_ReturnsNull_WhenVersionAbsent()
    {
        string dir = NewDir();
        Write(dir, IliMap.MapFileName, "i1\t00000001-a");
        Assert.Null(IliMap.LoadVersion(dir, "pwn99"));
    }

    // The reason multi-version resolution exists: the same offset denotes different synsets across
    // WordNet versions, so pwn16 (MapNet/WordFrameNet) and pwn30 must map one offset to different ILIs.
    [Fact]
    public void DifferentVersions_ResolveSameOffsetToDifferentIli()
    {
        string dir = NewDir();
        Write(dir, IliMap.MapFileName, "i30\t00057580-a");
        Write(dir, Path.Combine("older-wn-mappings", "ili-map-pwn16.tab"), "i16\t00057580-a\t1");

        Assert.Equal("i30", IliMap.Load(dir).Resolve(57580, 'a'));
        Assert.Equal("i16", IliMap.LoadVersion(dir, "pwn16")!.Resolve(57580, 'a'));
    }
}
