using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Integration check of <see cref="IliMap"/> against the real CILI map
/// (globalwordnet/cili). Skips when the data isn't on the box. Pins the two things
/// that bite: full PWN-3.0 coverage, and the satellite-adjective (s) ≠ head (a)
/// distinction (folding s→a silently drops 10,693 synsets from convergence).
/// </summary>
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
        if (CiliDir() is not { } dir) return; // CILI not present — integration data, skip

        var map = IliMap.Load(dir);

        // Full PWN-3.0 coverage = the synset count.
        Assert.Equal(117659, map.Count);

        // supermodel noun synset (offset 10676319, ss_type n) → i93445 (verified from the map).
        Assert.Equal("i93445", map.Resolve(10676319, 'n'));

        // First row: head adjective 00001740-a → i1.
        Assert.Equal("i1", map.Resolve(1740, 'a'));

        // Unmapped offset → null (no silent fabrication).
        Assert.Null(map.Resolve(999999999, 'n'));
    }

    [Fact]
    public void SatellitePosIsDistinctFromHeadAdjective()
    {
        if (CiliDir() is not { } dir) return;

        var map = IliMap.Load(dir);

        // A satellite ('s') and a head adjective ('a') at the same offset must NOT collide —
        // proving the raw ss_type is keyed, not folded. Count the 's'-keyed entries are present.
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
                    Assert.NotNull(map.Resolve(off, 's')); // resolvable as satellite
                    satellites++;
                    if (satellites >= 50) break;
                }
            }
        }
        Assert.True(satellites > 0, "expected satellite-adjective rows in the CILI map");
    }
}
