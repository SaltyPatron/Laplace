using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

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
            try { Directory.Delete(d, recursive: true); } catch { }
    }




    [Fact]
    public void Resolve_CollapsesAdjectiveSatelliteAndHead()
    {
        string dir = NewDir();
        Write(dir, IliMap.MapFileName,
            "i100\t00000001-a",
            "i200\t00000002-s");
        var map = IliMap.Load(dir);

        Assert.Equal("i200", map.Resolve(2, 's'));
        Assert.Equal("i200", map.Resolve(2, 'a'));
        Assert.Equal("i100", map.Resolve(1, 'a'));
        Assert.Equal("i100", map.Resolve(1, 's'));

        Assert.Null(map.Resolve(1, 'n'));
    }


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

        Assert.Equal("i999", IliMap.LoadVersion(dir, "pwn16")!.Resolve(5, 'n'));
    }

    [Fact]
    public void LoadVersion_ReturnsNull_WhenVersionAbsent()
    {
        string dir = NewDir();
        Write(dir, IliMap.MapFileName, "i1\t00000001-a");
        Assert.Null(IliMap.LoadVersion(dir, "pwn99"));
    }



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
