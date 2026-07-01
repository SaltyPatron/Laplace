using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;










[Collection("GrammarPerfcache")]
public class IliMapTests
{
    private static string? CiliDir()
    {
        string dir = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR")
                     ?? @"D:\Data\Ingest\CILI";
        return File.Exists(Path.Combine(dir, IliMap.MapFileName)) ? dir : null;
    }

    [Fact]
    public void LoadsFullMapAndResolvesSynsetToIli()
    {
        if (CiliDir() is not { } dir) return;

        var map = IliMap.Load(dir);


        Assert.Equal(117659, map.Count);


        Assert.Equal("i93445", map.Resolve(10676319, 'n'));


        Assert.Equal("i1", map.Resolve(1740, 'a'));


        Assert.Null(map.Resolve(999999999, 'n'));
    }

    [Fact]
    public void SatellitePosIsDistinctFromHeadAdjective()
    {
        if (CiliDir() is not { } dir) return;

        var map = IliMap.Load(dir);



        int satellites = 0;
        foreach (var line in File.ReadLines(Path.Combine(dir, IliMap.MapFileName)))
        {
            int dash = line.LastIndexOf('-');
            if (dash > 0 && dash + 1 < line.Length && line.AsSpan(dash + 1).Trim().SequenceEqual("s"))
            {
                var op = line.AsSpan(line.IndexOf('\t') + 1).Trim();
                int d = op.LastIndexOf('-');
                if (long.TryParse(op[..d], out long off))
                {
                    Assert.NotNull(map.Resolve(off, 's'));
                    satellites++;
                    if (satellites >= 50) break;
                }
            }
        }
        Assert.True(satellites > 0, "expected satellite-adjective rows in the CILI map");
    }
}
