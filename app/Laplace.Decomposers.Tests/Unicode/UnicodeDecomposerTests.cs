using System.Collections.Immutable;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Decomposers.Unicode.Tests;

public sealed class UnicodeDecomposerTests
{
    static UnicodeDecomposerTests()
    {
        // Process-global native state: only the first test class pays the mmap+CRC load.
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(ResolvePerfcacheBlob());
    }

    private static string ResolvePerfcacheBlob() => TestInstall.ResolvePerfcacheOrThrow();

    private const int TotalCodepoints = 1_114_112;

    private static UnicodeDecomposer NewDecomposer() => new UnicodeDecomposer();

    private static IDecomposerContext Context(ISubstrateWriter writer) =>
        new FakeContext(TestIngestPaths.UcdLatest, writer);


    [Fact]
    public async Task Emits_All_Codepoints_As_T0_Entities_With_Content_Physicalities()
    {
        var dec = NewDecomposer();
        var ctx = Context(new NullWriter());

        await dec.EstimateUnitCountAsync(ctx);
        Hash128 aHash = Hash128.Blake3(new byte[] { 0x41 });

        var codepointEntities = new HashSet<Hash128>();
        var highByteEntities = new HashSet<Hash128>();
        long codepointPhysicalities = 0, passThreeEntities = 0, inputUnits = 0;
        bool allTier0 = true, allFirstObserved = true;
        EntityRow? aEntity = null;
        PhysicalityRow? aPhys = null;

        await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
        {
            inputUnits += change.Metadata.InputUnitsConsumed;
            for (int i = 0; i < change.Entities.Length; i++)
            {
                var e = change.Entities[i];
                if (e.TypeId == UnicodeDecomposer.CodepointType)
                {
                    codepointEntities.Add(e.Id);
                    if (e.Tier != 0) allTier0 = false;
                    if (e.FirstObservedBy != UnicodeDecomposer.Source) allFirstObserved = false;
                    if (aEntity is null && e.Id == aHash)
                    {
                        aEntity = e;
                        foreach (var ph in change.Physicalities)
                            if (ph.EntityId == aHash) { aPhys = ph; break; }
                    }
                }
                else
                {
                    passThreeEntities++;
                    if (e.TypeId == ByteAtoms.TypeId)
                        highByteEntities.Add(e.Id);
                }
            }
            foreach (var ph in change.Physicalities)
                if (ph.Type == PhysicalityType.Content && ph.TrajectoryXyzm is null)
                    codepointPhysicalities++;
        }

        Assert.Equal(TotalCodepoints, codepointEntities.Count);
        Assert.True(inputUnits > TotalCodepoints,
            "whole-source accounting includes DUCET codepoints plus later admitted UCD/property rows");
        Assert.Equal(ByteAtoms.Count, highByteEntities.Count);
        for (int value = ByteAtoms.First; value <= byte.MaxValue; ++value)
            Assert.Contains(ByteAtoms.Id((byte)value), highByteEntities);
        Assert.DoesNotContain(ByteAtoms.Id(0x41), highByteEntities);
        Assert.True(codepointPhysicalities >= TotalCodepoints,
            "one CONTENT physicality per codepoint (pass-3 content adds more)");
        Assert.True(passThreeEntities > 0,
            "pass 3 must witness name aliases / confusable sequences as content");
        Assert.True(allTier0, "all codepoint entities are tier 0");
        Assert.True(allFirstObserved, "all codepoint entities first_observed_by UnicodeDecomposer");

        Assert.NotNull(aEntity);
        Assert.NotNull(aPhys);
        Assert.Equal(PhysicalityType.Content, aPhys!.Type);
        ref readonly CodepointRecord cachedA = ref CodepointPerfcache.Records['A'];
        Assert.Equal(cachedA.Hash, aPhys.EntityId);
        Assert.Equal(cachedA.CoordX, aPhys.CoordX);
        Assert.Equal(cachedA.CoordY, aPhys.CoordY);
        Assert.Equal(cachedA.CoordZ, aPhys.CoordZ);
        Assert.Equal(cachedA.CoordM, aPhys.CoordM);
        Assert.Equal(0, cachedA.Hilbert.CompareToBytewise(aPhys.HilbertIndex));
        double r2 = aPhys.CoordX * aPhys.CoordX + aPhys.CoordY * aPhys.CoordY
                  + aPhys.CoordZ * aPhys.CoordZ + aPhys.CoordM * aPhys.CoordM;
        Assert.InRange(Math.Sqrt(r2), 1.0 - 1e-9, 1.0 + 1e-9);
        Assert.Null(aPhys.TrajectoryXyzm);
        Assert.Equal(0, aPhys.NConstituents);
        Assert.Equal(0, aPhys.ObservedAtUnixUs);
        Assert.Equal(UnicodeDecomposer.Source, aPhys.SourceId);
        Assert.Equal(aEntity!.Id, aPhys.EntityId);
    }

    [Fact]
    public async Task Initialize_Bootstraps_Source_Codepoint_Type_And_TrustClass()
    {
        var dec = NewDecomposer();
        var writer = new CapturingWriter();
        await dec.InitializeAsync(Context(writer));

        // Layer-0 sources bootstrap vocabulary before physical ingestion, but
        // license/version testimony is emitted only after the Tier-0 floor is
        // durably persisted. Initialize must therefore perform exactly one write.
        var boot = Assert.Single(writer.Captured);

        Assert.Contains(boot.Entities, e =>
            e.Id == UnicodeDecomposer.Source && e.TypeId == BootstrapIntentBuilder.SourceTypeId);
        Assert.Contains(boot.Entities, e =>
            e.Id == UnicodeDecomposer.CodepointType && e.TypeId == BootstrapIntentBuilder.TypeMetaTypeId);
        Assert.Contains(boot.Attestations, a =>
            a.SubjectId == UnicodeDecomposer.Source
            && a.TypeId == BootstrapIntentBuilder.HasTrustClassTypeId
            && a.ObjectId == UnicodeDecomposer.TrustClass);
        Assert.DoesNotContain(boot.Attestations, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_LICENSE")
            || a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_VERSION"));
    }

    [Fact]
    public async Task PhaseOrder_Tier0Codepoints_Before_MappingAttestations()
    {
        var dec = NewDecomposer();
        var ctx = Context(new NullWriter());
        // Cap so the test finishes in seconds; uncapped mapping is gated by MaxInputUnits=0 break.
        var opts = DecomposerOptions.Default with { MaxInputUnits = 512 };

        bool sawCodepointEntity = false;
        bool sawMappingAttestation = false;
        HashSet<Hash128> mappingTypes =
        [
            UcdProperties.RelTypeHasUppercaseMapping,
            UcdProperties.RelTypeHasLowercaseMapping,
            UcdProperties.RelTypeHasTitlecaseMapping,
        ];

        await foreach (var change in dec.DecomposeAsync(ctx, opts))
        {
            if (change.Entities.Any(e => e.TypeId == UnicodeDecomposer.CodepointType))
                sawCodepointEntity = true;
            if (change.Attestations.Any(a => mappingTypes.Contains(a.TypeId)))
                sawMappingAttestation = true;

            if (!change.IntentStages.IsDefaultOrEmpty)
            {
                // Staged COPY rows are the persisted intent surface; direct managed
                // row arrays may be empty after native stage materialization.
                var entityRows = CopyTupleParser.ParseEntities(
                    change.IntentStages
                        .Select(stage => stage.TupleBuffer(IntentStageTable.Entities))
                        .ToList());
                if (entityRows.TypeIds.Any(typeId => typeId == UnicodeDecomposer.CodepointType))
                    sawCodepointEntity = true;

                var decoded = new List<AttestationRow>();
                CopyTupleParser.DecodeAttestations(
                    change.IntentStages
                        .Select(stage => stage.TupleBuffer(IntentStageTable.Attestations))
                        .ToList(),
                    decoded);
                if (decoded.Any(a => mappingTypes.Contains(a.TypeId)))
                    sawMappingAttestation = true;
            }
        }
        Assert.True(sawCodepointEntity,
            "MaxInputUnits prefix must still contain persisted Tier-0 codepoint rows");
        Assert.False(sawMappingAttestation,
            "MaxInputUnits cap must stop after Tier-0 floor persistence; mapping phases must not run");
    }

    [Fact]
    public async Task Deterministic_Intent_Ids_Across_Runs()
    {
        // Cap → serial spine (MonolithSegmenter.ResolveSegments → 1). Uncapped
        // working-set segments merge unordered, so IntentId *order* is not a contract.
        var dec = NewDecomposer();
        var ctx = Context(new NullWriter());
        var opts = DecomposerOptions.Default with { MaxInputUnits = 2048 };

        var first = new List<Hash128>();
        await foreach (var c in dec.DecomposeAsync(ctx, opts))
            first.Add(c.Metadata.IntentId);

        var second = new List<Hash128>();
        await foreach (var c in dec.DecomposeAsync(ctx, opts))
            second.Add(c.Metadata.IntentId);

        Assert.Equal(first, second);
        Assert.NotEmpty(first);
    }

    [Fact]
    public async Task Estimate_Reports_Full_Codepoint_Space()
    {
        var dec = NewDecomposer();
        Assert.Equal(TotalCodepoints, await dec.EstimateUnitCountAsync(Context(new NullWriter())));
    }

    [Fact]
    public async Task Artifact_graph_enumerates_unknown_files_instead_of_silently_omitting_them()
    {
        string root = Path.Combine(Path.GetTempPath(), "laplace-unicode-estate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "ucd"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "ucd", "UnicodeData.txt"), "0041;LATIN CAPITAL LETTER A;Lu;0;L;;;;;N;;;;0061;\n");
            await File.WriteAllTextAsync(Path.Combine(root, "ucd", "FutureProperty.txt"), "0041 ; Future_Value\n");

            var dec = NewDecomposer();
            IngestArtifactGraph graph = Assert.IsType<IngestArtifactGraph>(
                await dec.DescribeArtifactsAsync(root, DecomposerOptions.Default));

            IngestArtifact unicodeData = Assert.Single(
                graph.Artifacts, a => a.RelativePath == "ucd/UnicodeData.txt");
            Assert.Equal(IngestArtifactDisposition.Admitted, unicodeData.Disposition);

            IngestArtifact unknown = Assert.Single(
                graph.Artifacts, a => a.RelativePath == "ucd/FutureProperty.txt");
            Assert.Equal(IngestArtifactDisposition.Unsupported, unknown.Disposition);
            Assert.Contains("must not be reported as complete coverage", unknown.Notes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Binary_property_parser_retains_source_ranges_without_managed_point_expansion()
    {
        string file = Path.Combine(Path.GetTempPath(), "laplace-proplist-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(file, "0041..0042 ; Alphabetic # fixture\n0043 ; White_Space\n");
            var rows = new List<UnicodePhysicalArtifactParser.BinaryPropertyRange>();
            await foreach (var row in UnicodePhysicalArtifactParser.BinaryPropertyRangesAsync(file, CancellationToken.None))
                rows.Add(row);

            Assert.Equal(2, rows.Count);
            Assert.Equal((uint)0x41, rows[0].Start);
            Assert.Equal((uint)0x42, rows[0].End);
            Assert.Equal("Alphabetic", rows[0].Property);
            Assert.Equal((uint)0x43, rows[1].Start);
            Assert.Equal((uint)0x43, rows[1].End);
            Assert.Equal("White_Space", rows[1].Property);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Unihan_parser_preserves_property_name_and_exact_value()
    {
        string file = Path.Combine(Path.GetTempPath(), "laplace-unihan-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(file, "U+4E00\tkDefinition\tone; a, an; alone\n");
            var rows = new List<UnicodePhysicalArtifactParser.UnihanPropertyRow>();
            await foreach (var row in UnicodePhysicalArtifactParser.UnihanPropertiesAsync(file, CancellationToken.None))
                rows.Add(row);

            var parsed = Assert.Single(rows);
            Assert.Equal((uint)0x4E00, parsed.Codepoint);
            Assert.Equal("kDefinition", parsed.Property);
            Assert.Equal("one; a, an; alone", parsed.Value);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Artifact_graph_admits_declared_extended_UCD_property_files()
    {
        string root = Path.Combine(Path.GetTempPath(), "laplace-unicode-extended-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "ucd", "auxiliary"));
        Directory.CreateDirectory(Path.Combine(root, "ucd", "extracted"));
        Directory.CreateDirectory(Path.Combine(root, "ucd", "Unihan"));
        try
        {
            string[] files =
            [
                "ucd/PropList.txt",
                "ucd/DerivedCoreProperties.txt",
                "ucd/ScriptExtensions.txt",
                "ucd/auxiliary/GraphemeBreakProperty.txt",
                "ucd/extracted/DerivedGeneralCategory.txt",
                "ucd/Unihan/Unihan_Readings.txt",
            ];
            foreach (string relative in files)
            {
                string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "# synthetic fixture\n");
            }

            var dec = NewDecomposer();
            IngestArtifactGraph graph = Assert.IsType<IngestArtifactGraph>(
                await dec.DescribeArtifactsAsync(root, DecomposerOptions.Default));

            foreach (string relative in files)
            {
                IngestArtifact artifact = Assert.Single(
                    graph.Artifacts, a => a.RelativePath == relative);
                Assert.Equal(IngestArtifactDisposition.Admitted, artifact.Disposition);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Installed_Ucd_estate_has_no_unhandled_physical_artifacts()
    {
        string root = TestIngestPaths.UcdLatest;
        Skip.IfNot(Directory.Exists(root), $"UCD not present at {root}");

        var dec = NewDecomposer();
        IngestArtifactGraph graph = Assert.IsType<IngestArtifactGraph>(
            await dec.DescribeArtifactsAsync(root, DecomposerOptions.Default));

        string[] unsupported = graph.Artifacts
            .Where(static a => a.Disposition == IngestArtifactDisposition.Unsupported)
            .Select(static a => a.RelativePath)
            .OrderBy(static p => p, StringComparer.Ordinal)
            .ToArray();

        Assert.True(unsupported.Length == 0,
            "Installed Unicode estate still has unhandled physical artifacts:\n"
            + string.Join("\n", unsupported));
    }

}
