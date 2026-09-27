using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.OMW;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Decomposers.Tests.OMW;

/// <summary>
/// The selected lemma/definition/example value is the content admitted; the enclosing TSV row
/// (synset key, field tag, delimiters) is packaging and creates no content tree. The staged
/// COPY rows must equal those of the native content composer fed only the values.
/// </summary>
public sealed class OmwPlacementEntityParityTests(ITestOutputHelper output)
{
    private static string WnsDir => Path.Combine(TestIngestPaths.Root, "OMW", "wns");

    [SkippableFact]
    public async Task SemanticValuesComposeWithoutTsvPackagingPhysicalities()
    {
        Skip.IfNot(Directory.Exists(WnsDir), $"dataset absent: {WnsDir}");

        CodepointPerfcache.LoadDefault();

        string? tab = OMWTabFiles.EnumerateTabFiles(WnsDir, langs: null)
            .OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
        Assert.NotNull(tab);

        string fileLang = OMWTabFiles.FileLang(tab!);
        var builder = new SubstrateChangeBuilder(OMWDecomposer.Source, "omw-semantic-values");
        // The oracle stages what a row states and nothing of its packaging: the lemma, the
        // synset it names, its language tag and a lemma's part of speech, each as content.
        using var oracle = new SubstrateChangeBuilder(OMWDecomposer.Source, "omw-oracle");
        IntentStage expected = oracle.ContentStage;
        long rows = 0;

        await foreach (var line in StreamingUtf8LineReader.ReadLinesAsync(tab!))
        {
            if (rows >= 2_000) break;
            if (!OMWRowParser.TryParseRow(line.Span, fileLang, out var row, out var valueUtf8))
                continue;
            OMWEmitter.Emit(builder, row, valueUtf8);

            // Field tags and TSV delimiters never enter this oracle.
            byte[] semanticValue = NormalizeValue(valueUtf8);
            if (semanticValue.Length > 0
                && expected.TryAddContentWitness(semanticValue, OMWDecomposer.Source, out _)
                && ConceptAnchor.EmitAnchor(oracle, row.Offset, row.SsType, OMWDecomposer.Source) is not null)
            {
                LanguageReference.Emit(oracle, row.Lang, OMWDecomposer.Source, SourceTrust.AcademicCurated);
                if (row.Type == OmwType.Lemma && !row.Removed)
                    PosReference.Emit(oracle, row.SsType.ToString(), PosReference.PosTagset.WordNet,
                        OMWDecomposer.Source, SourceTrust.AcademicCurated);
            }
            rows++;
        }

        var change = builder.Build();
        int stagedEntities = change.IntentStages.Sum(stage => stage.EntityCount);
        int stagedPhysicalities = change.IntentStages.Sum(stage => stage.PhysicalityCount);
        output.WriteLine(
            $"rows={rows} managed_entities={change.Entities.Length} "
            + $"managed_physicalities={change.Physicalities.Length} "
            + $"semantic_entities={stagedEntities} semantic_physicalities={stagedPhysicalities}");

        Assert.True(rows > 0, "no OMW records read; the assertion would be vacuous");
        Assert.Empty(change.Physicalities);
        Assert.True(stagedPhysicalities > 0, "semantic values must still compose");
        using var actual = Assert.Single(change.IntentStages);
        Assert.Equal(expected.EntityCount, stagedEntities);
        Assert.Equal(expected.PhysicalityCount, stagedPhysicalities);

        // Byte-equal COPY output means equal identities, coordinates, Hilbert values,
        // trajectories and occurrences, and no extra packaging rows. Native emission
        // stamps PgEpochUnixUs, so no timestamp needs masking.
        Assert.Equal(expected.EmitCopyBinary(IntentStageTable.Entities),
            actual.EmitCopyBinary(IntentStageTable.Entities));
        Assert.Equal(expected.EmitCopyBinary(IntentStageTable.Physicalities),
            actual.EmitCopyBinary(IntentStageTable.Physicalities));
        Assert.Equal(
            new PhysicalitySourceRange(0, actual.PhysicalityCount, OMWDecomposer.Source),
            Assert.Single(actual.PhysicalitySourceRanges));
    }

    private static byte[] NormalizeValue(ReadOnlySpan<byte> value)
    {
        int first = 0, end = value.Length;
        while (first < end && value[first] == (byte)' ') first++;
        while (end > first && value[end - 1] == (byte)' ') end--;
        byte[] normalized = value[first..end].ToArray();
        for (int i = 0; i < normalized.Length; i++)
            if (normalized[i] == (byte)'_') normalized[i] = (byte)' ';
        return normalized;
    }
}
