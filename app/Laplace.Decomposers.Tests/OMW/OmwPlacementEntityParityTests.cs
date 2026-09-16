using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.OMW;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Decomposers.Tests.OMW;

/// <summary>
/// OMW admits the selected lemma/definition/example as content. The enclosing TSV row is source
/// packaging: its synset key, field tag, and delimiters must not create a second managed content
/// tree. Compare the exact native semantic-value rows and source ownership instead of
/// bounding physicality observations by the number of newly declared canonical entities.
/// </summary>
public sealed class OmwPlacementEntityParityTests(ITestOutputHelper output)
{
    private static string WnsDir => Path.Combine(TestIngestPaths.Root, "OMW", "wns");

    [SkippableFact]
    public async Task SemanticValuesComposeWithoutTsvPackagingPhysicalities()
    {
        Skip.IfNot(Directory.Exists(WnsDir), $"dataset absent: {WnsDir}");

        CodepointPerfcache.LoadDefault();
        LanguageReference.EnsureLoaded(TestIngestPaths.Iso639);

        string? tab = OMWTabFiles.EnumerateTabFiles(WnsDir, langs: null)
            .OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
        Assert.NotNull(tab);

        string fileLang = OMWTabFiles.FileLang(tab!);
        var builder = new SubstrateChangeBuilder(OMWDecomposer.Source, "omw-semantic-values");
        using var expected = IntentStage.New(256);
        long rows = 0;

        await foreach (var line in StreamingUtf8LineReader.ReadLinesAsync(tab!))
        {
            if (rows >= 2_000) break;
            if (!OMWRowParser.TryParseRow(line.Span, fileLang, out var row, out var valueUtf8))
                continue;
            OMWEmitter.Emit(builder, row, valueUtf8);

            // Supply only the selected semantic value to the native content owner.
            // The synset key, field tag and TSV delimiters never enter this oracle.
            byte[] semanticValue = NormalizeValue(valueUtf8);
            if (semanticValue.Length > 0)
                Assert.True(expected.TryAddContentWitness(
                    semanticValue, OMWDecomposer.Source, out _));
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
        Assert.Empty(change.PhysicalityObservations);
        Assert.True(stagedPhysicalities > 0, "semantic values must still compose");
        using var actual = Assert.Single(change.IntentStages);
        Assert.Equal(expected.EntityCount, stagedEntities);
        Assert.Equal(expected.PhysicalityCount, stagedPhysicalities);

        // Exact COPY parity preserves identities, coordinates, Hilbert values,
        // trajectories and every occurrence, while rejecting extra packaging rows.
        // Native content emission uses PgEpochUnixUs, so no timestamp is masked.
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
