using Laplace.Engine.Core;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Engine.Core.Tests;

/// <summary>The C# binding must call the SAME engine super_fibonacci that the
/// blob emitter compiles in — same n ⇒ same XYZW per index, bit-identical.
/// Mismatches there would silently shift every codepoint's coord.</summary>
public sealed class SuperFibonacciTests
{
    private const string Blob = "/home/ahart/Projects/Laplace/build/engine-only/core/perfcache/laplace_t0_perfcache.bin";
    private const int CpCount = 0x110000;
    private const int HeaderBytes = 128;
    private const int RecordBytes = 80;

    private readonly ITestOutputHelper _out;
    public SuperFibonacciTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void CSharpSuperFibonacci_Matches_BlobCoords_ByRank()
    {
        if (!File.Exists(Blob)) return;

        // C# computes the full spiral at the same N the C++ emitter used.
        double[] sf = SuperFibonacci.Generate(CpCount);

        // For each codepoint in the blob, look up its rank, fetch the rank-th
        // C# point, and compare against the blob's coord — bit-for-bit.
        byte[] b = File.ReadAllBytes(Blob);
        int diff = 0; int firstCp = -1;
        for (int cp = 0; cp < CpCount; cp++)
        {
            int o = HeaderBytes + cp * RecordBytes;
            uint rank = BitConverter.ToUInt32(b, o + 4);
            double bx = BitConverter.ToDouble(b, o + 8);
            double by = BitConverter.ToDouble(b, o + 16);
            double bz = BitConverter.ToDouble(b, o + 24);
            double bm = BitConverter.ToDouble(b, o + 32);
            int k = (int)rank * 4;
            if (sf[k] != bx || sf[k + 1] != by || sf[k + 2] != bz || sf[k + 3] != bm)
            {
                if (diff < 4)
                    _out.WriteLine($"  diverge U+{cp:X6} rank={rank}  cs=({sf[k]},{sf[k+1]},{sf[k+2]},{sf[k+3]})  cpp=({bx},{by},{bz},{bm})");
                if (diff == 0) firstCp = cp;
                diff++;
            }
        }
        if (diff == 0) { _out.WriteLine("all 1,114,112 codepoint coords match bit-for-bit."); return; }
        Assert.Fail($"super_fibonacci diverges; first U+{firstCp:X6}; total={diff}");
    }
}
