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
/// tree. This real-data sample also preserves the entity/physicality admission invariant that
/// originally exposed the duplicate composition path.
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
        long rows = 0;

        await foreach (var line in StreamingUtf8LineReader.ReadLinesAsync(tab!))
        {
            if (rows >= 2_000) break;
            if (!OMWRowParser.TryParseRow(line.Span, fileLang, out var row, out var valueUtf8))
                continue;
            OMWEmitter.Emit(builder, row, valueUtf8);
            rows++;
        }

        var change = builder.Build();
        int stagedEntities = change.IntentStages.Sum(stage => stage.EntityCount);
        int stagedPhysicalities = change.IntentStages.Sum(stage => stage.PhysicalityCount);
        long entities = change.Entities.Length + stagedEntities;
        long physicalities = change.Physicalities.Length + stagedPhysicalities;

        output.WriteLine(
            $"rows={rows} managed_entities={change.Entities.Length} "
            + $"managed_physicalities={change.Physicalities.Length} "
            + $"semantic_entities={stagedEntities} semantic_physicalities={stagedPhysicalities}");

        Assert.True(rows > 0, "no OMW records read; the assertion would be vacuous");
        Assert.Empty(change.Physicalities);
        Assert.True(stagedPhysicalities > 0, "semantic values must still compose");
        Assert.True(physicalities <= entities,
            $"{physicalities - entities} placement(s) beyond declared entities");
    }
}
