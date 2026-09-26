using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Laplace.Decomposers.Structured;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.ISO;
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
    [Trait("Tier", "perf")]
    [Trait("Scope", "full-corpus")]
    public async Task Emits_All_Codepoints_As_T0_Entities_With_Content_Physicalities()
    {
        var dec = NewDecomposer();
        var ctx = Context(new NullWriter());

        await dec.EstimateUnitCountAsync(ctx);
        Hash128 aHash = Hash128.Blake3(new byte[] { 0x41 });

        var codepointEntities = new HashSet<Hash128>();
        var highByteEntities = new HashSet<Hash128>();
        long codepointPhysicalities = 0, passThreeEntities = 0, inputUnits = 0;
        bool allTier0 = true, anySourceWitness = false;
        EntityRow? aEntity = null;
        PhysicalityRow? aPhys = null;

        await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default).WithoutWriter())
        {
            inputUnits += change.Metadata.InputUnitsConsumed;
            for (int i = 0; i < change.Entities.Length; i++)
            {
                var e = change.Entities[i];
                if (e.TypeId == UnicodeDecomposer.CodepointType)
                {
                    codepointEntities.Add(e.Id);
                    if (e.Tier != 0) allTier0 = false;
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
            if (change.Attestations.Any(a => a.SourceId == UnicodeDecomposer.Source))
                anySourceWitness = true;
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
        Assert.True(anySourceWitness, "the Unicode source witnesses its codepoints through attestations");

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
        Assert.DoesNotContain(boot.Attestations, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_TRUST_CLASS"));
        Assert.DoesNotContain(boot.Attestations, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_LICENSE")
            || a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_VERSION"));
    }

    [Fact]
    public async Task PhaseOrder_Tier0Codepoints_Before_MappingAttestations()
    {
        var dec = NewDecomposer();
        var ctx = Context(new NullWriter());
        // A small deterministic Tier-0 prefix is sufficient to prove the persistence
        // ordering contract; mapping phases must remain unreachable for any positive cap.
        var opts = DecomposerOptions.Default with { MaxInputUnits = 64 };

        bool sawCodepointEntity = false;
        bool sawMappingAttestation = false;
        HashSet<Hash128> mappingTypes = [UcdProperties.RelTypeHasCaseMapping];

        await foreach (var change in dec.DecomposeAsync(ctx, opts).WithoutWriter())
        {
            if (change.Entities.Any(e => e.TypeId == UnicodeDecomposer.CodepointType))
                sawCodepointEntity = true;
            if (change.Attestations.Any(a => mappingTypes.Contains(a.TypeId)))
                sawMappingAttestation = true;

            if (!change.IntentStages.IsDefaultOrEmpty)
            {
                // Staged COPY rows are the persisted intent surface; direct managed
                // row arrays may be empty after native stage materialization. Inspect
                // that persisted surface rather than requiring a duplicate managed row.
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
        await foreach (var c in dec.DecomposeAsync(ctx, opts).WithoutWriter())
            first.Add(c.Metadata.IntentId);

        var second = new List<Hash128>();
        await foreach (var c in dec.DecomposeAsync(ctx, opts).WithoutWriter())
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
    public async Task Range_parser_windows_expansion_without_changing_source_row_accounting()
    {
        string file = Path.Combine(
            Path.GetTempPath(), "laplace-range-window-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(file, "0000..0009 ; Alphabetic\n");
            var rows = new List<UnicodePhysicalArtifactParser.BinaryPropertyRange>();
            await foreach (var row in UnicodePhysicalArtifactParser.BinaryPropertyRangesAsync(
                               file, CancellationToken.None, maxExpandedRows: 5))
                rows.Add(row);

            Assert.Equal(4, rows.Count);
            Assert.Equal([(uint)3, 3, 3, 1], rows.Select(static r => r.End - r.Start + 1));
            Assert.True(rows[0].CountsSourceRow);
            Assert.All(rows.Skip(1), static row => Assert.False(row.CountsSourceRow));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Range_parser_accounts_for_multi_relation_expansion()
    {
        string file = Path.Combine(
            Path.GetTempPath(), "laplace-range-multi-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(file, "0041..0048 ; Latn Grek Cyrl\n");
            var rows = new List<UnicodePhysicalArtifactParser.RangeRecord>();
            await foreach (var row in UnicodePhysicalArtifactParser.RangeRecordsAsync(
                               file,
                               CancellationToken.None,
                               maxExpandedRows: 9,
                               outputRowsPerCodepoint: static value => value.Split(
                                   ' ', StringSplitOptions.RemoveEmptyEntries).Length))
                rows.Add(row);

            Assert.Equal(4, rows.Count);
            Assert.All(rows, static row => Assert.Equal((uint)2, row.End - row.Start + 1));
            Assert.True(rows[0].CountsSourceRow);
            Assert.All(rows.Skip(1), static row => Assert.False(row.CountsSourceRow));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Ucdxml_recipe_accounts_for_binary_reference_content_and_top_level_structures()
    {
        var recipe = InstalledSourceGeneration.Load("Unicode/UCD", "17.0.0", "UAX42/ucd.all.grouped.xml");
        SourceRecipeField gc = recipe.Recipe.Field("repertoire/*/@gc");
        SourceRecipeField whiteSpace = recipe.Recipe.Field("repertoire/*/@WSpace");
        SourceRecipeField definition = recipe.Recipe.Field("repertoire/*/@kDefinition");
        Assert.Equal("General_Category", gc.PropertyName);
        Assert.Equal(SourceValueKind.Enumerated, gc.ValueKind);
        Assert.Equal(SourceValueKind.Boolean, whiteSpace.ValueKind);
        Assert.Equal("White_Space", whiteSpace.PropertyName);
        Assert.Equal(SourceValueKind.Text, definition.ValueKind);
        Assert.True(definition.Disposition.HasFlag(SourceFieldDisposition.Content));
        Assert.Contains(recipe.Recipe.Structures, static s => s.SyntaxPath == "named-sequences");
        Assert.Contains(recipe.Recipe.Structures, static s => s.SyntaxPath == "do-not-emit");
    }

    [SkippableFact]
    [Trait("Tier", "perf")]
    [Trait("Scope", "full-corpus")]
    public async Task Ucdxml_recipe_accounts_for_the_complete_selected_provider_schema()
    {
        string aliases = Path.Combine(TestIngestPaths.UcdLatest, "ucd", "PropertyAliases.txt");
        string archivePath = Path.Combine(
            TestIngestPaths.UcdLatest, "ucdxml", "ucd.all.grouped.zip");
        Skip.IfNot(File.Exists(aliases) && File.Exists(archivePath),
            $"selected UCD generation is not present at {TestIngestPaths.UcdLatest}");

        var recipe = InstalledSourceGeneration.Load("Unicode/UCD", "17.0.0", "UAX42/ucd.all.grouped.xml");
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry entry = Assert.Single(archive.Entries);
        await using Stream xml = entry.Open();
        var runtime = new NativeSourceRecipe(recipe.Recipe, recordDepth: 3);
        long records = 0;
        await foreach (var change in runtime.ReadChangesAsync(xml, UnicodeDecomposer.Source, 1,
                           "complete-ucd", 32768, 32L * 1024 * 1024, 64 * 1024))
        {
            records += change.Metadata.InputUnitsConsumed;
            foreach (var stage in change.IntentStages) stage.Dispose();
        }
        Assert.True(records > 0);
    }

    [SkippableFact]
    [Trait("Tier", "perf")]
    [Trait("Scope", "full-corpus")]
    public async Task Unicode_snapshot_replays_the_exact_inflated_xml_bytes()
    {
        string xmlPath = Path.Combine(
            TestIngestPaths.UcdLatest, "ucdxml", "ucd.all.grouped.zip");
        string ducetPath = Path.Combine(TestIngestPaths.UcdLatest, "uca", "allkeys.txt");
        Skip.IfNot(File.Exists(xmlPath) && File.Exists(ducetPath),
            $"selected UCD generation is not present at {TestIngestPaths.UcdLatest}");

        using UnicodeSeedSnapshot snapshot = UnicodeSeed.OpenSnapshot(xmlPath, ducetPath);
        await using Stream replay = snapshot.OpenUcdXmlStream();
        byte[] actual = await SHA256.HashDataAsync(replay);

        using ZipArchive archive = ZipFile.OpenRead(xmlPath);
        ZipArchiveEntry entry = Assert.Single(archive.Entries);
        await using Stream expectedStream = entry.Open();
        byte[] expected = await SHA256.HashDataAsync(expectedStream);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("HAS_GENERAL_CATEGORY")]
    [InlineData("HAS_BLOCK")]
    [InlineData("HAS_AGE")]
    [InlineData("HAS_EMOJI_PROPERTY")]
    [InlineData("HAS_NUMERIC_VALUE")]
    [InlineData("USES_SCRIPT_EXTENSION")]
    public void Per_property_unicode_relations_are_retired_into_the_character_property(string retired)
    {
        // One element per meaning: the property is the object's first part, never a relation.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => RelationTypeRegistry.Resolve(retired));
        Assert.Contains("retired", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_character_property_value_is_the_composition_of_property_and_value()
    {
        Hash128 source = Hash128.OfCanonical("test/unicode/source");
        using var builder = new SubstrateChangeBuilder(source, "test/unicode/property-value");
        Hash128? pair = ContentEmitter.StagePropertyValue(
            builder, "General_Category", "Space_Separator", source);
        Hash128? property = ContentEmitter.Emit(builder, "General_Category", source);
        Hash128? value = ContentEmitter.Emit(builder, "Space_Separator", source);
        Assert.NotNull(pair);
        Assert.NotEqual(property, pair);
        Assert.NotEqual(value, pair);
        Assert.Equal(pair, ContentEmitter.StagePropertyValue(
            builder, "General_Category", "Space_Separator", source));
        Assert.NotEqual(pair, ContentEmitter.StagePropertyValue(
            builder, "Bidi_Class", "Space_Separator", source));
    }

    [Fact]
    public void Ucd_recipe_reuses_iso15924_script_identity()
    {
        var recipe = InstalledSourceGeneration.Load("Unicode/UCD", "17.0.0", "UAX42/ucd.all.grouped.xml");
        Assert.Equal("Latin", recipe.CanonicalValue("Script", "Latn"));
        Assert.Equal("Latin", recipe.CanonicalValue("Script_Extensions", "Latn"));
        Assert.Equal("Basic_Latin", recipe.CanonicalValue("Block", "Basic Latin"));
    }

    [Fact]
    public void Ucd_structured_references_preserve_every_target_and_source_qualifier()
    {
        var recipe = InstalledSourceGeneration.Load("Unicode/UCD", "17.0.0", "UAX42/ucd.all.grouped.xml");
        byte[] program = NativeRecipeCompiler.Compile(recipe.Recipe, recordDepth: 3);
        List<AttestationRow> Parse(string value)
        {
            using var stream = NativeRecipeStream.Open(program, UnicodeDecomposer.Source, 1);
            string xml = "<ucd xmlns=\"http://www.unicode.org/ns/2003/ucd/1.0\"><repertoire>"
                + "<group gc=\"Lo\" Alpha=\"Y\">"
                + "<char cp=\"4E00\" kSemanticVariant=\"" + value.Replace("<", "&lt;")
                + "\" kCompatibilityVariant=\"U+7471\"/>"
                + "</group></repertoire></ucd>";
            stream.Feed(Encoding.UTF8.GetBytes(xml), final: true);
            var rows = new List<AttestationRow>();
            while (true)
            {
                using var stage = stream.Drain(32768, 32L * 1024 * 1024, out _);
                if (stage is null) return rows;
                CopyTupleParser.DecodeAttestations([stage.TupleBuffer(IntentStageTable.Attestations)], rows);
            }
        }
        var rows = Parse("U+5EDD<kMatthews U+53AE<kFenn,kMatthews");
        var relation = RelationTypeRegistry.RelationTypeId("UCD_KSEMANTICVARIANT");
        var references = rows.Where(row => row.TypeId == relation).ToArray();
        Assert.Equal(2, references.Length);
        using var expected = new SubstrateChangeBuilder(UnicodeDecomposer.Source, "reference-contexts");
        Assert.Contains(references, row => row.ObjectId is { } id
            && CodepointPerfcache.TryLookupCodepoint(id, out uint cp) && cp == 0x5EDD
            && row.ContextId == ContentEmitter.Emit(expected, "kMatthews", UnicodeDecomposer.Source));
        Assert.Contains(references, row => row.ObjectId is { } id
            && CodepointPerfcache.TryLookupCodepoint(id, out uint cp) && cp == 0x53AE
            && row.ContextId == ContentEmitter.Emit(expected, "kFenn,kMatthews", UnicodeDecomposer.Source));
        Assert.Contains(rows, row => row.TypeId == RelationTypeRegistry.RelationTypeId("UCD_KCOMPATIBILITYVARIANT")
            && row.ObjectId is { } id && CodepointPerfcache.TryLookupCodepoint(id, out uint cp) && cp == 0x7471);
        Assert.Throws<InvalidDataException>(() => Parse("kMatthews"));
    }

    [Fact]
    public async Task Normalization_test_parser_preserves_all_five_sequences_and_description()
    {
        string file = Path.Combine(
            Path.GetTempPath(), "laplace-normalization-test-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(file,
                "@Part0 # Specific cases\n"
                + "1E0A 0323;1E0C 0307;0044 0323 0307;1E0C 0307;0044 0323 0307; # exact case\n");
            var rows = new List<UnicodePhysicalArtifactParser.NormalizationTestRow>();
            await foreach (var row in UnicodePhysicalArtifactParser.NormalizationTestsAsync(
                               file, CancellationToken.None))
                rows.Add(row);

            UnicodePhysicalArtifactParser.NormalizationTestRow parsed = Assert.Single(rows);
            Assert.Equal("Ḍ̇", parsed.Source);
            Assert.Equal("Ḍ̇", parsed.Nfc);
            Assert.Equal("Ḍ̇", parsed.Nfd);
            Assert.Equal(parsed.Nfc, parsed.Nfkc);
            Assert.Equal(parsed.Nfd, parsed.Nfkd);
            Assert.Equal("exact case", parsed.Description);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Emoji_test_parser_preserves_sequence_qualification_version_and_palette_groups()
    {
        string file = Path.Combine(
            Path.GetTempPath(), "laplace-emoji-test-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(file,
                "# group: Smileys & Emotion\n"
                + "# subgroup: face-smiling\n"
                + "1F600 ; fully-qualified # 😀 E1.0 grinning face\n");
            var rows = new List<UnicodePhysicalArtifactParser.EmojiTestRow>();
            await foreach (var row in UnicodePhysicalArtifactParser.EmojiTestsAsync(
                               file, CancellationToken.None))
                rows.Add(row);

            UnicodePhysicalArtifactParser.EmojiTestRow parsed = Assert.Single(rows);
            Assert.Equal("😀", parsed.Sequence);
            Assert.Equal("fully-qualified", parsed.Status);
            Assert.Equal("E1.0", parsed.Version);
            Assert.Equal("grinning face", parsed.Name);
            Assert.Equal("Smileys & Emotion", parsed.Group);
            Assert.Equal("face-smiling", parsed.Subgroup);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Grouped_ucd_parent_properties_split_discontiguous_child_coverage()
    {
        var recipe = InstalledSourceGeneration.Load(
            "Unicode/UCD", "17.0.0", "UAX42/ucd.all.grouped.xml");
        byte[] program = NativeRecipeCompiler.Compile(recipe.Recipe, recordDepth: 3);
        using var stream = NativeRecipeStream.Open(
            program, UnicodeDecomposer.Source, SourceTrust.StandardsDerived);

        const string xml =
            "<ucd xmlns=\"http://www.unicode.org/ns/2003/ucd/1.0\"><repertoire>"
            + "<group Alpha=\"Y\">"
            + "<char cp=\"0041\"/><char cp=\"0043\"/>"
            + "</group></repertoire></ucd>";
        stream.Feed(Encoding.UTF8.GetBytes(xml), final: true);

        var rows = new List<AttestationRow>();
        while (true)
        {
            using var stage = stream.Drain(
                32768, 32L * 1024 * 1024, out _);
            if (stage is null) break;
            CopyTupleParser.DecodeAttestations(
                [stage.TupleBuffer(IntentStageTable.Attestations)], rows);
        }

        Hash128 relation = RelationTypeRegistry.RelationTypeId("UCD_ALPHABETIC");
        AttestationRow[] alphabetic = rows.Where(row => row.TypeId == relation).ToArray();
        Assert.Equal(2, alphabetic.Length);
        Assert.Contains(alphabetic, row => row.SubjectId == CodepointPerfcache.Records[0x41].Hash);
        Assert.Contains(alphabetic, row => row.SubjectId == CodepointPerfcache.Records[0x43].Hash);
        Assert.DoesNotContain(alphabetic, row => row.SubjectId == CodepointPerfcache.Records[0x42].Hash);
    }

    [Fact]
    public void Grouped_ucd_parent_properties_apply_to_each_codepoint_and_child_overrides_win()
    {
        var recipe = InstalledSourceGeneration.Load(
            "Unicode/UCD", "17.0.0", "UAX42/ucd.all.grouped.xml");
        byte[] program = NativeRecipeCompiler.Compile(recipe.Recipe, recordDepth: 3);
        using var stream = NativeRecipeStream.Open(
            program, UnicodeDecomposer.Source, SourceTrust.StandardsDerived);

        const string xml =
            "<ucd xmlns=\"http://www.unicode.org/ns/2003/ucd/1.0\"><repertoire>"
            + "<group Alpha=\"Y\" gc=\"Lu\">"
            + "<char cp=\"0041\"/><char cp=\"0042\" Alpha=\"N\"/>"
            + "</group></repertoire></ucd>";
        stream.Feed(Encoding.UTF8.GetBytes(xml), final: true);

        var rows = new List<AttestationRow>();
        while (true)
        {
            using var stage = stream.Drain(
                32768, 32L * 1024 * 1024, out _);
            if (stage is null) break;
            CopyTupleParser.DecodeAttestations(
                [stage.TupleBuffer(IntentStageTable.Attestations)], rows);
        }

        Hash128 alphabetic = RelationTypeRegistry.RelationTypeId("UCD_ALPHABETIC");
        Hash128 category = RelationTypeRegistry.RelationTypeId("UCD_GENERAL_CATEGORY");
        AttestationRow[] alpha = rows.Where(row => row.TypeId == alphabetic).ToArray();
        AttestationRow[] gc = rows.Where(row => row.TypeId == category).ToArray();
        Assert.Single(alpha);
        Assert.Equal(CodepointPerfcache.Records[0x41].Hash, alpha[0].SubjectId);
        Assert.Equal(2, gc.Length);
        Assert.Contains(gc, row => row.SubjectId == CodepointPerfcache.Records[0x41].Hash);
        Assert.Contains(gc, row => row.SubjectId == CodepointPerfcache.Records[0x42].Hash);
    }

    [Fact]
    public void Ucdxml_binary_defaults_are_declared_and_do_not_expand_into_refuting_rows()
    {
        var recipe = InstalledSourceGeneration.Load(
            "Unicode/UCD", "17.0.0", "UAX42/ucd.all.grouped.xml");
        SourceRecipeField[] binary = recipe.Recipe.Fields
            .Where(static field => field.ValueKind == SourceValueKind.Boolean)
            .ToArray();
        Assert.NotEmpty(binary);
        Assert.All(binary, static field =>
        {
            Assert.Equal("N", field.DefaultValue);
            Assert.True(field.OmitDefaultTestimony);
        });

        byte[] program = NativeRecipeCompiler.Compile(recipe.Recipe, recordDepth: 3);
        List<AttestationRow> Parse(string value)
        {
            using var stream = NativeRecipeStream.Open(
                program, UnicodeDecomposer.Source, 1);
            string xml =
                "<ucd xmlns=\"http://www.unicode.org/ns/2003/ucd/1.0\"><repertoire>"
                + "<group Alpha=\"" + value + "\">"
                + "<char cp=\"0041\"/>"
                + "</group></repertoire></ucd>";
            stream.Feed(Encoding.UTF8.GetBytes(xml), final: true);
            var rows = new List<AttestationRow>();
            while (true)
            {
                using var stage = stream.Drain(
                    32768, 32L * 1024 * 1024, out _);
                if (stage is null) return rows;
                CopyTupleParser.DecodeAttestations(
                    [stage.TupleBuffer(IntentStageTable.Attestations)], rows);
            }
        }

        Hash128 relation = RelationTypeRegistry.RelationTypeId("UCD_ALPHABETIC");
        Assert.DoesNotContain(Parse("N"), row => row.TypeId == relation);
        AttestationRow positive = Assert.Single(
            Parse("Y").Where(row => row.TypeId == relation));
        Assert.Equal(AttestationOutcome.Confirm, positive.Outcome);
    }

    [Fact]
    public void Binary_property_negative_is_refuting_testimony_on_the_same_typed_cell()
    {
        Hash128 source = Hash128.OfCanonical("test/unicode/source");
        Hash128 relation = RelationTypeRegistry.RelationTypeId("HAS_CHARACTER_PROPERTY");
        Hash128 whiteSpace = Hash128.OfCanonical("test/unicode/[White_Space, Yes]");
        using var builder = new SubstrateChangeBuilder(
            source, "test/unicode/binary-refute", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 1);

        NativeAttestation.AddCodepointRange(
            builder.ContentStage, 0x41, 0x41, relation, objectId: whiteSpace,
            sourceId: source, contextId: null, sourceTrust: SourceTrust.StandardsDerived,
            confirm: false);
        SubstrateChange change = builder.Build();
        var decoded = new List<AttestationRow>();
        CopyTupleParser.DecodeAttestations(
            change.IntentStages.Select(
                static stage => stage.TupleBuffer(IntentStageTable.Attestations)).ToList(),
            decoded);

        AttestationRow row = Assert.Single(decoded);
        Assert.Equal(AttestationOutcome.Refute, row.Outcome);
        Assert.Equal(relation, row.TypeId);
        Assert.Equal(whiteSpace, row.ObjectId);
        Assert.Null(row.ContextId);
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
    public async Task Uax_boundary_test_parser_preserves_sequence_breaks_and_explanation()
    {
        string file = Path.Combine(
            Path.GetTempPath(), "laplace-grapheme-test-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(
                file, "÷ 000D × 000A ÷ # CR × LF ÷ [0.3]\n");
            var rows = new List<UnicodePhysicalArtifactParser.BoundaryTestRow>();
            await foreach (UnicodePhysicalArtifactParser.BoundaryTestRow row in
                           UnicodePhysicalArtifactParser.BoundaryTestsAsync(
                               file, CancellationToken.None))
                rows.Add(row);

            UnicodePhysicalArtifactParser.BoundaryTestRow parsed = Assert.Single(rows);
            Assert.Equal("\r\n", parsed.Sequence);
            Assert.Equal("÷×÷", parsed.Boundaries);
            Assert.Equal("CR × LF ÷ [0.3]", parsed.Description);
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
