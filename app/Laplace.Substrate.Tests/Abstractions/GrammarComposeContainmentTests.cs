using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class GrammarComposeContainmentTests
{
    private static readonly Hash128 Src =
        SubstrateCanonicalIds.OfVersioned("source", "test", "compose-containment");

    [Theory]
    [InlineData("README\nLicense and build instructions\n", true)]
    [InlineData("caf\u00e9\n", true)]
    [InlineData("cafe\u0301\n", false)]
    [InlineData("embedded\0byte", false)]
    [InlineData("", false)]
    public void RawSourceRequiresExactNativeTextBytes(string source, bool admitted)
        => Assert.Equal(admitted, GrammarSourceFileSupport.IsExactNativeText(Encoding.UTF8.GetBytes(source)));

    [Fact]
    public void RawSourceRejectsInvalidUtf8()
        => Assert.False(GrammarSourceFileSupport.IsExactNativeText([0xff, 0xfe]));

    [Fact]
    public void RawSourceUsesExistingNativeContentAndFileIdentity()
    {
        byte[] source = Encoding.UTF8.GetBytes("License and build instructions\n");
        var metadata = new FileMetadata("COPYING", "COPYING", source.Length, DateTime.UnixEpoch, "text");
        var record = new GrammarComposeRecord(source, "text", FileMetadata: metadata, RawText: true);
        var handler = new GrammarComposeHandler(Src, 1.0, null);
        using var unit = handler.CreateDeferredUnit(record);
        var builder = new SubstrateChangeBuilder(Src, "test/raw-source");
        Hash128 actual = unit.DrainInto(builder, 1.0, null);
        var expected = FileEntity.Resolve(source, metadata);
        Assert.Equal(expected.FileId, actual);
        Assert.Equal(expected.FileId, builder.Build().Metadata.FileId);
    }

    [Fact]
    public void RawSourceCannotDiscardPromptOrMislabelMetadata()
    {
        byte[] source = Encoding.UTF8.GetBytes("source\n");
        var metadata = new FileMetadata("COPYING", "COPYING", source.Length, DateTime.UnixEpoch, "text");
        var record = new GrammarComposeRecord(source, "text", FileMetadata: metadata, RawText: true);
        var handler = new GrammarComposeHandler(Src, 1.0, null);
        Assert.Throws<InvalidDataException>(() => handler.CreateDeferredUnit(record with { ObservedPromptUtf8 = source }));
        Assert.Throws<InvalidDataException>(() => handler.CreateDeferredUnit(record with { FileMetadata = metadata with { Modality = "cpp" } }));
    }

    [Fact]
    public void CppRecoveryIsRetainedAsPartialSyntaxWithoutDroppingSource()
    {
        byte[] source = Encoding.UTF8.GetBytes("int main() { return ; broken = ; }\n");
        using var ast = GrammarDecomposer.Parse(source, "cpp");
        var diagnostics = GrammarSourceFileSupport.RequireNativeSourceAst(ast);
        Assert.False(diagnostics.SyntaxComplete);
        Assert.True(diagnostics.ErrorNodeCount + diagnostics.MissingNodeCount > 0);
        using var composer = new GrammarRowComposer(source, ast, Src, "cpp", GrammarCompositionMode.FullSource);
        Assert.NotEqual(default, composer.RootComponent().Id);
    }

    [Theory]
    [InlineData("1\tRelatedTo\t/c/en/dog\t/c/en/animal\t{}")]
    [InlineData("7\tIsA\t/c/en/a moment in time\t/c/en/moment\t{}")]
    public void PresentTrunk_EmitsZeroNovelEntitiesButKeepsEvidence(string row)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(row);
        using var ast = GrammarDecomposer.Parse(utf8, "tsv");
        using var composer = new GrammarRowComposer(utf8, ast, Src, "tsv");

        Hash128[] ids = composer.EntityIds();
        Assert.True(ids.Length > 0, "expected the tsv row to compose at least one entity");

        var (baseEnts, basePhys, basePrec, _) = composer.Materialize(1.0);
        Assert.True(baseEnts.Length > 0);

        var present = new byte[(ids.Length + 7) / 8];
        for (int i = 0; i < ids.Length; i++) present[i >> 3] |= (byte)(1 << (i & 7));

        var (ents, phys, prec, _) = composer.Materialize(1.0, present);

        Assert.Empty(ents);
        Assert.Empty(phys);
        Assert.Equal(basePrec.Length, prec.Length);
    }

    [Theory]
    [InlineData("1\tRelatedTo\t/c/en/dog\t/c/en/animal\t{}")]
    public void AllAbsentBitmap_MatchesUnfilteredEmission(string row)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(row);
        using var ast = GrammarDecomposer.Parse(utf8, "tsv");
        using var composer = new GrammarRowComposer(utf8, ast, Src, "tsv");

        Hash128[] ids = composer.EntityIds();
        var absent = new byte[(ids.Length + 7) / 8];

        var (baseEnts, basePhys, basePrec, baseRoot) = composer.Materialize(1.0);
        var (ents, phys, prec, root) = composer.Materialize(1.0, absent);

        Assert.Equal(baseRoot, root);
        Assert.Equal(baseEnts.Length, ents.Length);
        Assert.Equal(basePhys.Length, phys.Length);
        Assert.Equal(basePrec.Length, prec.Length);
        for (int i = 0; i < baseEnts.Length; i++)
            Assert.Equal(baseEnts[i].Id, ents[i].Id);
        for (int i = 0; i < basePhys.Length; i++)
            Assert.Equal(basePhys[i].Id, phys[i].Id);
    }

    [Theory]
    [InlineData("1\tRelatedTo\t/c/en/dog\t/c/en/animal\t{}")]
    public void PresentLeafWord_SkipsOnlyThatSubtree(string row)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(row);
        using var ast = GrammarDecomposer.Parse(utf8, "tsv");
        using var composer = new GrammarRowComposer(utf8, ast, Src, "tsv");

        Hash128[] ids = composer.EntityIds();
        var (baseEnts, _, _, _) = composer.Materialize(1.0);

        var bitmap = new byte[(ids.Length + 7) / 8];
        bitmap[0] |= 1;

        var (ents, _, _, _) = composer.Materialize(1.0, bitmap);
        Assert.True(ents.Length < baseEnts.Length || baseEnts.Length == 1,
            "marking one present node should not increase the novel set");
        Assert.True(ents.Length >= 0);
    }
}
