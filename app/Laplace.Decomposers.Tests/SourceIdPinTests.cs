using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Tests;

// Pins the remaining managed providers' SourceId and TrustClassId. Canonical
// sources retired to Native use content-dependent source trunks, not these fixed
// managed provider IDs. Source key pins were verified independently with the Native
// CLI's text command over substrate/source/NAME/v1, using its UAX #29 composition.
// A trust class id is the content id of
// its label (engine/manifest/trust_classes.toml): Laplace-Native's lp_id_codepoints_utf8 of
// the label, BLAKE3-128 of its codepoints' IDs with no domain byte (Laplace#1896). A failure
// here means a content-addressed source or trust-class identity changed.
public class SourceIdPinTests
{
    public static readonly TheoryData<Func<IDecomposer>, string, string> Pins = new()
    {
        { () => new Laplace.Decomposers.Code.RepoDecomposer(),
          "a5ae53148e15cc55cfd155bdb7f6121f", "c62668c333824c0b89b84db531007db0" },
        { () => new Laplace.Decomposers.Code.StackDecomposer(),
          "a1449c0c48319c66803daf4539bb0b4e", "c62668c333824c0b89b84db531007db0" },
        { () => new Laplace.Decomposers.Code.TinyCodesDecomposer(),
          "7239f8ee5ff1e8639c6f89a18a0029e3", "c62668c333824c0b89b84db531007db0" },
    };

    [Theory]
    [MemberData(nameof(Pins))]
    public async Task SourceId_And_TrustClassId_Match_SubstrateGolden(
        Func<IDecomposer> make, string sourceHex, string trustHex)
    {
        var d = make();
        try
        {
            Assert.Equal(sourceHex, Convert.ToHexStringLower(d.SourceId.ToBytes()));
            Assert.Equal(trustHex, Convert.ToHexStringLower(d.TrustClassId.ToBytes()));
        }
        finally
        {
            await d.DisposeAsync();
        }
    }

}
