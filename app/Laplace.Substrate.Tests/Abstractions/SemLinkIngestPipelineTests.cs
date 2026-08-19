using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.SemLink;
using Laplace.SubstrateCRUD;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Laplace.Decomposers.Abstractions.Tests.IngestPipelineTestHelpers;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class SemLinkIngestPipelineTests
{
    private const string PbVnJson =
        """{"give.01": {"13.1-1": {"ARG0": "agent", "ARG1": "theme"}}, "abdicate.01": {"10.11-2": {}}}""";

    [Fact]
    public void SemLinkConfig_UsesSharedSizingAndPreservesExplicitBatch()
    {
        var automatic = SemLinkIngestSupport.PipelineConfig(
            default, "semlink/automatic", DecomposerOptions.Default, reader: null);
        var explicitOptions = DecomposerOptions.Default with { BatchSize = 777 };
        var explicitConfig = SemLinkIngestSupport.PipelineConfig(
            default, "semlink/explicit", explicitOptions, reader: null);

        Assert.Equal(
            IngestPipelineDefaults.ResolveBatch(
                Laplace.Engine.Core.IngestSourceProfile.Wiktionary,
                DecomposerOptions.Default),
            automatic.BatchSize);
        Assert.NotEqual(1, automatic.BatchSize);
        Assert.Equal(777, explicitConfig.BatchSize);
    }

    [Fact]
    public async Task SemLinkJsonPipeline_DoesNotProbeOrComposeJsonPackaging()
    {
        string path = Path.Combine(Path.GetTempPath(), $"laplace-semlink-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, PbVnJson, Encoding.UTF8);
            var reader = new ProbeTrackingReader(present: false);
            const int pairCount = 2;
            var phase = new SemLinkJsonDocumentPhase(path, SemLinkDocumentKind.PbVn, "semlink/test");
            var ctx = new SemLinkTestContext(reader);
            var options = DecomposerOptions.Default with { BatchSize = pairCount };

            var changes = new List<SubstrateChange>();
            await foreach (var change in phase.DecomposeAsync(ctx, options))
                changes.Add(change);

            Assert.Equal(0, reader.LegacyContentDescentCalls);
            Assert.Equal(0, reader.FlatProbeCalls);
            Assert.Equal(0, PhysicalityCount(changes));
            Assert.True(AttestationCount(changes) > 0);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SemLinkJsonPipeline_PresentReader_StillEmitsSemanticRows()
    {
        string path = Path.Combine(Path.GetTempPath(), $"laplace-semlink-present-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, PbVnJson, Encoding.UTF8);
            var reader = new ProbeTrackingReader(present: true);
            var phase = new SemLinkJsonDocumentPhase(path, SemLinkDocumentKind.PbVn, "semlink/present");
            var ctx = new SemLinkTestContext(reader);
            var options = DecomposerOptions.Default with { BatchSize = 2 };

            var changes = new List<SubstrateChange>();
            await foreach (var change in phase.DecomposeAsync(ctx, options))
                changes.Add(change);

            Assert.Equal(0, reader.FlatProbeCalls);
            Assert.Equal(0, PhysicalityCount(changes));
            Assert.True(AttestationCount(changes) > 0,
                "SemLink witness edges must not depend on a packaging-root probe");
            Assert.Equal(2, changes.Sum(c => c.Metadata.InputUnitsConsumed));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class SemLinkTestContext(ISubstrateReader reader) : IDecomposerContext
    {
        public string EcosystemPath => "";
        public ISubstrateWriter Writer => throw new NotSupportedException();
        public ISubstrateReader Reader => reader;
        public Microsoft.Extensions.Logging.ILogger Logger => NullLogger.Instance;
        public string SubstrateVersion => "test";
    }

    private static int PhysicalityCount(IEnumerable<SubstrateChange> changes) =>
        changes.Sum(change => change.Physicalities.Length
            + change.IntentStages.Sum(stage => stage.PhysicalityCount));
}
