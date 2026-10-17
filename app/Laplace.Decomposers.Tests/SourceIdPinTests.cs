using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Tests;

// Pins every decomposer's SourceId and TrustClassId. A trust class id is the content id of
// its label (engine/manifest/trust_classes.toml): Laplace-Native's lp_id_codepoints_utf8 of
// the label, BLAKE3-128 of its codepoints' IDs with no domain byte (Laplace#1896). A failure
// here means a content-addressed source or trust-class identity changed.
public class SourceIdPinTests
{
    public static readonly TheoryData<Func<IDecomposer>, string, string> Pins = new()
    {
        { () => new Laplace.Decomposers.Unicode.UnicodeDecomposer(),
          "5f36a4c5fb03a7d845c2ad1981b18587", "9ed6235c81cafe911167a95e1a4587ed" },
        { () => new Laplace.Decomposers.ISO.ISODecomposer(),
          "2e0404bdd15c7e19517f1f26d57e240b", "9ed6235c81cafe911167a95e1a4587ed" },
        { () => new Laplace.Decomposers.CILI.CILIDecomposer(),
          "e46123b3b4d43df4a41a95d750c310e1", "d37a0d1e87aae7aaf5aaaad8b119955f" },
        { () => new Laplace.Decomposers.WordNet.WordNetDecomposer(),
          "4b1ee33be3034910df7629b2948cde35", "9ed6235c81cafe911167a95e1a4587ed" },
        { () => new Laplace.Decomposers.OMW.OMWDecomposer(),
          "2d8df8e310d8c0bb26bb08837baf3756", "d37a0d1e87aae7aaf5aaaad8b119955f" },
        { () => new Laplace.Decomposers.SemLink.MapNetDecomposer(),
          "74809dc2edd48bc97e10fee9ba466fca", "d37a0d1e87aae7aaf5aaaad8b119955f" },
        { () => new Laplace.Decomposers.SemLink.WordFrameNetDecomposer(),
          "6e012044174c4f0ac189bbf4cae40e74", "d37a0d1e87aae7aaf5aaaad8b119955f" },
        { () => new Laplace.Decomposers.SemLink.SemLinkDecomposer(),
          "8c1d82e7b9f338ddca954b6e0b8829c7", "d37a0d1e87aae7aaf5aaaad8b119955f" },
        { () => new Laplace.Decomposers.ConceptNet.ConceptNetDecomposer(),
          "7dd057136ea446aeb50d1bb840bdc0f0", "75cabf5d6a62805eae44d24b5bf6dec0" },
        { () => new Laplace.Decomposers.Wiktionary.WiktionaryDecomposer(),
          "65226439241a9e4a7645c2fdf91c60b1", "f1482bb3d9c6b430a7612abb1c238561" },
        { () => new Laplace.Decomposers.Tatoeba.TatoebaDecomposer(),
          "6ed93a67dc2df8c27952c2dd3178d980", "c62668c333824c0b89b84db531007db0" },
        { () => new Laplace.Decomposers.OpenSubtitles.OpenSubtitlesDecomposer(),
          "ee411c0f649e433bd3103d99f87075d0", "c62668c333824c0b89b84db531007db0" },
        { () => new Laplace.Decomposers.Atomic2020.Atomic2020Decomposer(),
          "affee556837b77b5562a026400aece2b", "c62668c333824c0b89b84db531007db0" },
        { () => new Laplace.Decomposers.Code.CodeDecomposer(),
          "c90645d6b0f228c684d95bbbc0b52a42", "c62668c333824c0b89b84db531007db0" },
        { () => new Laplace.Decomposers.Code.RepoDecomposer(),
          "d393ad78a7ccbee830504361f4dde050", "c62668c333824c0b89b84db531007db0" },
        { () => new Laplace.Decomposers.Code.StackDecomposer(),
          "6af57547662bbb37f4e13db9acfe3eed", "c62668c333824c0b89b84db531007db0" },
        { () => new Laplace.Decomposers.Code.TinyCodesDecomposer(),
          "584ccc3cce1291ad4ac04ce4f8b33a8c", "c62668c333824c0b89b84db531007db0" },
        { () => new Laplace.Decomposers.Code.TabularDecomposer(),
          "3f0876af5782433a4991d2d02e93b982", "c62668c333824c0b89b84db531007db0" },
        { () => new Laplace.Decomposers.AgentTrace.AgentTraceDecomposer(),
          "2695dfdb6ee5e398ac86cea7a87d1695", "f0a63ecf6b2b1db8f57f136d2c945483" },
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
