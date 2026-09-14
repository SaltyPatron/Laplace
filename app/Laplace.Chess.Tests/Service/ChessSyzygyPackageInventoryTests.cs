using Laplace.Ingestion;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessSyzygyPackageInventoryTests
{
    [Fact]
    public void SchedulePackages_KeepsWdlDtzAndLargerMaterials_AsIndependentFiles()
    {
        string[] paths =
        [
            "/tb/KQvK.rtbw",
            "/tb/KQvK.rtbz",
            "/tb/KQvKR.rtbw",
            "/tb/KQvKR.rtbz",
            "/tb/KPPvKPP.rtbw",
            "/tb/KPPvKPP.rtbz",
        ];

        var scheduled = ChessSyzygyDecomposer.SchedulePackages(paths);

        Assert.Equal(paths.Length, scheduled.Count);
        Assert.True(paths.ToHashSet(StringComparer.Ordinal)
            .SetEquals(scheduled.Select(static p => p.Path)));
        Assert.Equal(scheduled.Count,
            scheduled.Select(static p => p.Label).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(scheduled, static p => p.Label == "KQvK.rtbw");
        Assert.Contains(scheduled, static p => p.Label == "KQvK.rtbz");
        Assert.Contains(scheduled, static p => p.Label == "KPPvKPP.rtbw");
        Assert.Contains(scheduled, static p => p.Label == "KPPvKPP.rtbz");
    }

    [Theory]
    [InlineData("/tb/KQvK.rtbw", 3, true)]
    [InlineData("/tb/KQvK.rtbz", 3, false)]
    [InlineData("/tb/KQvKR.rtbw", 3, false)]
    [InlineData("/tb/KQvKR.rtbw", 4, true)]
    [InlineData("/tb/KQvKR.rtbz", 7, false)]
    [InlineData("/tb/KPPvKPP.rtbw", 7, true)]
    public void SemanticExpansion_IsSeparateFromPackageAdmission(
        string path, int maxMen, bool expected)
        => Assert.Equal(expected,
            ChessSyzygyDecomposer.ShouldExpandPackage(path, maxMen));

    [Fact]
    public void DtzPackage_MakesZeroDecodedPositions_ARealPackageRun_NotDependencyUnset()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"syzygy-packages-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "KQvKR.rtbw"), [0]);
            File.WriteAllBytes(Path.Combine(dir, "KQvKR.rtbz"), [0]);

            // Four-man semantics are above the default exhaustive ceiling, but both package
            // files are still independently scheduled/fingerprinted/completed. A zero decoded
            // position count therefore is not a missing-dependency no-op.
            Assert.Null(ChessSyzygyDecomposer.ExplainEmptyDirectory(dir, 3));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ChessLayerCompletionMarkers_AreInsideTheConsensusExclusionEnvelope()
    {
        Assert.InRange(new ChessSyzygyDecomposer().LayerOrder, 0, LayerCompletion.MaxMarkedLayer);
        Assert.InRange(new ChessTacticOutcomesDecomposer().LayerOrder, 0, LayerCompletion.MaxMarkedLayer);
    }
}