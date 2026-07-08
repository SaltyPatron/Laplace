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
    public async Task SemLinkJsonPipeline_BatchedProbe()
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

            await foreach (var _ in phase.DecomposeAsync(ctx, options))
            { }

            Assert.Equal(0, reader.LegacyContentDescentCalls);
            Assert.InRange(reader.FlatProbeCalls, 1,
                MaxProbeCallsFor(ExpectedExistenceRoundChunks(pairCount, pairCount)));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SemLinkJsonPipeline_PresentReader_ZeroEntities()
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

            Assert.Equal(0, ContentEntityCount(changes));
            Assert.True(AttestationCount(changes) > 0,
                "SemLink witness edges must still stage on present compose roots");
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
}
