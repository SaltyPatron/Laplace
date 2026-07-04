using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Tests;

// Golden-id pins for every decomposer's SourceId/TrustClassId. The hex values were minted
// from the DB side (laplace.canonical_id(), i.e. the native blake3 the extension binds), so
// this asserts C# and the substrate agree AND that refactors of the id-minting call paths
// (base classes, helper extraction) never change a content-addressed id. If this test fails,
// substrate identity has drifted — do not update the constants without an owner decision.
public class SourceIdPinTests
{
    public static readonly TheoryData<Func<IDecomposer>, string, string> Pins = new()
    {
        { () => new Laplace.Decomposers.Unicode.UnicodeDecomposer(),
          "909b825fe20bd201e70d8c8f6041ec50", "e992344778ddae6ceee030ebffd16675" },
        { () => new Laplace.Decomposers.ISO.ISODecomposer(),
          "2e0404bdd15c7e19517f1f26d57e240b", "e992344778ddae6ceee030ebffd16675" },
        { () => new Laplace.Decomposers.CILI.CILIDecomposer(),
          "e46123b3b4d43df4a41a95d750c310e1", "0450fa825fb0209d9e47ea60387b1332" },
        { () => new Laplace.Decomposers.WordNet.WordNetDecomposer(),
          "4b1ee33be3034910df7629b2948cde35", "e992344778ddae6ceee030ebffd16675" },
        { () => new Laplace.Decomposers.OMW.OMWDecomposer(),
          "2d8df8e310d8c0bb26bb08837baf3756", "0450fa825fb0209d9e47ea60387b1332" },
        { () => new Laplace.Decomposers.VerbNet.VerbNetDecomposer(),
          "8ee60cfa49a7dff70dc87d6807d3b0a0", "0450fa825fb0209d9e47ea60387b1332" },
        { () => new Laplace.Decomposers.PropBank.PropBankDecomposer(),
          "d93e8179e24e146f9f0498d469073524", "0450fa825fb0209d9e47ea60387b1332" },
        { () => new Laplace.Decomposers.FrameNet.FrameNetDecomposer(),
          "650979dde48d38baa9e403f4739c1dc1", "0450fa825fb0209d9e47ea60387b1332" },
        { () => new Laplace.Decomposers.SemLink.MapNetDecomposer(),
          "74809dc2edd48bc97e10fee9ba466fca", "0450fa825fb0209d9e47ea60387b1332" },
        { () => new Laplace.Decomposers.SemLink.WordFrameNetDecomposer(),
          "6e012044174c4f0ac189bbf4cae40e74", "0450fa825fb0209d9e47ea60387b1332" },
        { () => new Laplace.Decomposers.SemLink.SemLinkDecomposer(),
          "8c1d82e7b9f338ddca954b6e0b8829c7", "0450fa825fb0209d9e47ea60387b1332" },
        { () => new Laplace.Decomposers.ConceptNet.ConceptNetDecomposer(),
          "7dd057136ea446aeb50d1bb840bdc0f0", "985dd69ddedc7744cf27031baedba83f" },
        { () => new Laplace.Decomposers.Wiktionary.WiktionaryDecomposer(),
          "65226439241a9e4a7645c2fdf91c60b1", "5e7ca2f83de242d6e67ad1e360518c08" },
        { () => new Laplace.Decomposers.Tatoeba.TatoebaDecomposer(),
          "6ed93a67dc2df8c27952c2dd3178d980", "d12d1f4f079c1aa7f4826ec4b3266b45" },
        { () => new Laplace.Decomposers.UD.UDDecomposer(),
          "3f90afe0a932cec2f84257856aababbe", "0450fa825fb0209d9e47ea60387b1332" },
        { () => new Laplace.Decomposers.OpenSubtitles.OpenSubtitlesDecomposer(),
          "ee411c0f649e433bd3103d99f87075d0", "d12d1f4f079c1aa7f4826ec4b3266b45" },
        { () => new Laplace.Decomposers.Atomic2020.Atomic2020Decomposer(),
          "affee556837b77b5562a026400aece2b", "d12d1f4f079c1aa7f4826ec4b3266b45" },
        { () => new Laplace.Decomposers.Code.CodeDecomposer(),
          "c90645d6b0f228c684d95bbbc0b52a42", "d12d1f4f079c1aa7f4826ec4b3266b45" },
        { () => new Laplace.Decomposers.Code.RepoDecomposer(),
          "d393ad78a7ccbee830504361f4dde050", "d12d1f4f079c1aa7f4826ec4b3266b45" },
        { () => new Laplace.Decomposers.Code.StackDecomposer(),
          "6af57547662bbb37f4e13db9acfe3eed", "d12d1f4f079c1aa7f4826ec4b3266b45" },
        { () => new Laplace.Decomposers.Code.TinyCodesDecomposer(),
          "584ccc3cce1291ad4ac04ce4f8b33a8c", "d12d1f4f079c1aa7f4826ec4b3266b45" },
        { () => new Laplace.Decomposers.Code.TabularDecomposer(),
          "3f0876af5782433a4991d2d02e93b982", "d12d1f4f079c1aa7f4826ec4b3266b45" },
        { () => new Laplace.Decomposers.Image.ImageDecomposer(),
          "49930e95ae66bc87deef54294c25bdc1", "d12d1f4f079c1aa7f4826ec4b3266b45" },
        { () => new Laplace.Decomposers.Audio.AudioDecomposer(),
          "2b88601bf3dc0543a8ba8efbdc48b28b", "d12d1f4f079c1aa7f4826ec4b3266b45" },
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
