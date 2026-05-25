using Laplace.Decomposers.Unicode;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Decomposers.Unicode.Tests;

/// <summary>Cross-verify: the C# DUCET parser must produce the SAME per-codepoint
/// rank as the C++ emitter (the blob's uca_order field). If they differ for any
/// codepoint, the two derivations of the substrate seed disagree and you'd see
/// physicalities_entity_id_source_id_kind_key collisions in the seed.</summary>
public sealed class DucetCollationVsBlobTests
{
    private const string Ducet = "/vault/Data/Unicode/Public/17.0.0/uca/allkeys.txt";
    private const string Blob  = "/home/ahart/Projects/Laplace/build/engine-only/core/perfcache/laplace_t0_perfcache.bin";
    private const int HeaderBytes = 128;
    private const int RecordBytes = 80;
    private const int CpCount = 0x110000;

    private readonly ITestOutputHelper _out;
    public DucetCollationVsBlobTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void CSharpRanks_Match_CppEmitterRanks_ByteForByte()
    {
        if (!File.Exists(Ducet) || !File.Exists(Blob)) return;   // skip if no source/blob locally

        uint[] cs = new DucetCollation(Ducet).ComputeRanks();
        Assert.Equal(CpCount, cs.Length);

        byte[] b = File.ReadAllBytes(Blob);
        int diffs = 0;
        int firstDiffCp = -1;
        uint firstCs = 0, firstCpp = 0;
        for (int cp = 0; cp < CpCount; cp++)
        {
            int o = HeaderBytes + cp * RecordBytes;
            uint cppRank = BitConverter.ToUInt32(b, o + 4);
            if (cs[cp] != cppRank)
            {
                if (diffs < 8) _out.WriteLine($"  diverge U+{cp:X6}  cs={cs[cp]}  cpp={cppRank}");
                if (diffs == 0) { firstDiffCp = cp; firstCs = cs[cp]; firstCpp = cppRank; }
                diffs++;
            }
        }
        if (diffs == 0) { _out.WriteLine("ranks match for all 1,114,112 codepoints."); return; }
        _out.WriteLine($"TOTAL divergences: {diffs} of {CpCount}");
        Assert.Fail($"DUCET ranks diverge; first at U+{firstDiffCp:X6} (cs={firstCs}, cpp={firstCpp}); total={diffs}");
    }
}
