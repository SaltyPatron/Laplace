using Laplace.Decomposers.Abstractions;
using Laplace.Ingestion;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// The progress denominator self-corrects: a sampled estimate stands until a
/// background exact count publishes, after which every reader sees the exact
/// total. Guards the contract that made input_pct read 111% on the 2026-08-13
/// wiktionary run impossible to reintroduce silently.
/// </summary>
public sealed class IngestInventoryRefinementTests
{
    [Fact]
    public void ArtifactGraph_RequiresCompleteCanonicalIdentity()
    {
        var invalid = Artifact("artifact", "/source/a.dat", IngestArtifactDisposition.Admitted)
            with { Source = "" };

        var error = Assert.Throws<InvalidOperationException>(
            () => new IngestArtifactGraph([invalid]));

        Assert.Contains("identity components are required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactGraph_RejectsDuplicatePhysicalPath()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new IngestArtifactGraph(
        [
            Artifact("a", "/source/shared.dat", IngestArtifactDisposition.Admitted),
            Artifact("b", "/source/shared.dat", IngestArtifactDisposition.Admitted),
        ]));

        Assert.Contains("declared more than once", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactGraph_RequiresReasonForEveryNonAdmittedArtifact()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new IngestArtifactGraph(
        [
            Artifact("archive", "/source/archive.zip",
                IngestArtifactDisposition.EquivalentPackaging),
        ]));

        Assert.Contains("without a reason", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactGraph_ProjectsOnlyAdmittedArtifactsIntoExecutionInventory()
    {
        var graph = new IngestArtifactGraph(
        [
            Artifact("a", "/source/a.conllu", IngestArtifactDisposition.Admitted),
            Artifact("archive", "/source/archive.zip",
                IngestArtifactDisposition.EquivalentPackaging, "a is the extracted equivalent"),
            Artifact("b", "/source/b.conllu", IngestArtifactDisposition.Admitted),
        ]);

        var inventory = graph.ToFileInventory("files");

        Assert.NotNull(inventory);
        Assert.Equal(["test/r1/a", "test/r1/b"], inventory!.Files.Select(static file => file.Id));
        Assert.Equal(
            ["/source/a.conllu", "/source/b.conllu"],
            inventory.Files.Select(static file => file.Path));
        Assert.Equal(0, inventory.TotalInputUnits);
        Assert.All(inventory.Files, static file => Assert.Equal(0, file.InputUnits));
    }

    [Fact]
    public void ArtifactGraph_RecordCapDoesNotTruncatePhysicalInventory()
    {
        var graph = new IngestArtifactGraph(
        [
            Artifact("a", "/source/a.conllu", IngestArtifactDisposition.Admitted),
            Artifact("b", "/source/b.conllu", IngestArtifactDisposition.Admitted),
        ]);

        var inventory = graph.ToFileInventory("records");

        Assert.NotNull(inventory);
        Assert.Equal(2, inventory!.Files.Count);
        Assert.Equal(0, inventory.TotalInputUnits);
    }

    [Fact]
    public void ArtifactGraph_RejectsProviderInventoryThatOmitsSelectedArtifact()
    {
        var graph = new IngestArtifactGraph(
        [
            Artifact("a", "/source/a.conllu", IngestArtifactDisposition.Admitted),
            Artifact("b", "/source/b.conllu", IngestArtifactDisposition.Admitted),
        ]);
        var inventory = new IngestInventory("records", 1,
            [new IngestFileSpec("a", "/source/a.conllu", 1)]);

        var error = Assert.Throws<InvalidOperationException>(() => graph.ValidateInventory(inventory));

        Assert.Contains("Omitted", error.Message, StringComparison.Ordinal);
        Assert.Contains("/source/b.conllu", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiFileScheduling_UsesManifestSelectionInsteadOfLegacyDiscovery()
    {
        IngestArtifact[] selected =
        [
            Artifact("selected", "/source/selected.conllu", IngestArtifactDisposition.Admitted),
        ];
        (string Path, string Label)[] discovered =
        [
            ("/source/selected.conllu", "legacy-selected"),
            ("/source/undeclared.conllu", "legacy-undeclared"),
        ];

        var scheduled = IngestInput.ResolveScheduledFiles(selected, discovered);

        var file = Assert.Single(scheduled);
        Assert.Equal("/source/selected.conllu", file.Path);
        Assert.Equal("test/r1/selected", file.Label);
    }

    private static IngestArtifact Artifact(
        string artifact,
        string path,
        IngestArtifactDisposition disposition,
        string notes = "") => new(
            "test", "r1", artifact, Path.GetFileName(path), path, disposition,
            "", "", null, "", "", "", "", "", "", "", "", notes);

    [Fact]
    public void EffectiveTotal_IsTheEstimate_UntilRefined()
    {
        var inv = IngestInventory.Single(9_397_812);
        Assert.Equal(9_397_812, inv.EffectiveTotalInputUnits);
    }

    [Fact]
    public void EffectiveTotal_IsTheExactCount_OncePublished()
    {
        var inv = IngestInventory.Single(9_397_812);
        inv.PublishExactTotal(10_482_360);
        Assert.Equal(10_482_360, inv.EffectiveTotalInputUnits);
        Assert.Equal(9_397_812, inv.TotalInputUnits); // declared estimate is preserved
    }

    [Fact]
    public void PublishingZeroOrNegative_NeverClobbersTheEstimate()
    {
        var inv = IngestInventory.Single(500);
        inv.PublishExactTotal(0);
        Assert.Equal(500, inv.EffectiveTotalInputUnits);
        inv.PublishExactTotal(-1);
        Assert.Equal(500, inv.EffectiveTotalInputUnits);
    }

    [Fact]
    public void ObservedUnitsRaiseAnUnderestimatedDenominatorMonotonically()
    {
        var inv = IngestInventory.Single(189_852, "games");

        inv.PublishObservedFloor(190_705);
        Assert.Equal(190_705, inv.EffectiveTotalInputUnits);

        inv.PublishObservedFloor(190_000);
        Assert.Equal(190_705, inv.EffectiveTotalInputUnits);

        inv.PublishExactTotal(191_200);
        inv.PublishObservedFloor(191_000);
        Assert.Equal(191_200, inv.EffectiveTotalInputUnits);

        // Exact refinement may lawfully correct a sampled overestimate downward, but it
        // can never move below units extraction has already observed.
        var overestimated = IngestInventory.Single(250_000, "games");
        overestimated.PublishObservedFloor(190_705);
        overestimated.PublishExactTotal(190_000);
        Assert.Equal(190_705, overestimated.EffectiveTotalInputUnits);
    }

    [Fact]
    public void ProgressPercentDefensivelyNeverExceedsOneHundred()
    {
        var progress = new IngestProgress(
            "ChessPgn", 20, 0, 0, 0,
            InputUnitsTotal: 189_852,
            InputUnitsDone: 190_705,
            FilesTotal: 0,
            FilesDone: 0,
            CurrentFile: null,
            UnitType: "games",
            Elapsed: TimeSpan.Zero);

        Assert.Equal(100.0, progress.InputPercent);
    }

    [Fact]
    public void SmallFiles_GetExactCountsUpFront_NoRefinementNeeded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laplace-inv-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, Enumerable.Repeat("{\"w\":1}", 1234));
        try
        {
            var inv = IngestInventory.FromFiles("jsonl", [path], maxInputUnits: 0);
            Assert.NotNull(inv);
            Assert.Equal(1234, inv!.TotalInputUnits);
            Assert.Equal(1234, inv.EffectiveTotalInputUnits);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ManyIndividuallySmallFiles_StillRefineWhenCorpusExceedsSharedBudget()
    {
        string dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"laplace-inv-refine-{Guid.NewGuid():N}"))
            .FullName;
        try
        {
            string a = Path.Combine(dir, "a.tab");
            string b = Path.Combine(dir, "b.tab");
            long each = EtlInventory.MultiFileInventoryBudgetBytes / 2 + 1;
            using (var stream = File.Create(a)) stream.SetLength(each);
            using (var stream = File.Create(b)) stream.SetLength(each);

            Assert.All(new[] { a, b }, path =>
                Assert.True(new FileInfo(path).Length < EtlInventory.ExactScanThresholdBytes));
            Assert.True(IngestInventory.NeedsBackgroundRefinement([a, b]));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
