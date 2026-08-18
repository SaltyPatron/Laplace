using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Xunit;
using static Laplace.Decomposers.Abstractions.Tests.IngestPipelineTestHelpers;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// laplace.ingest_file_journal holds ZERO rows for every source that has ever run —
/// measured 2026-08-16 against the live cluster, with no matching error anywhere in the
/// postgres log, so the INSERTs are never issued at all. The run journal (7 rows) is
/// written by a DIRECT call on the observer; the per-file ledger is reached only through
/// IngestObservabilityScope.Current, an AsyncLocal whose fallback is NoOpObservability,
/// whose methods are empty bodies. Nothing throws and the run reports ok.
///
/// No test covered either call site, which is how a ledger that records nothing shipped
/// and stayed silent. These pin the contract at the pipeline boundary — one OnFileStarted
/// and one OnFileComposed per file, through the ambient scope, on both the sequential and
/// the parallel path. The runner emits OnFileFinished only after the file boundary applies.
/// </summary>
[Collection("GrammarPerfcache")]
public sealed class PerFileLedgerScopeTests
{
    [Theory]
    [InlineData(1)]  // RunMultiFileSequentialAsync — IngestPipeline.cs:560/614
    [InlineData(2)]  // RunMultiFileParallelAsync   — IngestPipeline.cs:846/891
    public async Task MultiFile_EmitsOneLedgerPairPerFile_ThroughAmbientScope(int fileWorkers)
    {
        var files = new Dictionary<string, IReadOnlyList<ContentIngestRecord>>
        {
            ["ledger-a"] = [ContentRecord("ledger a1")],
            ["ledger-b"] = [ContentRecord("ledger b1")],
        };

        var obs = new RecordingObservability();
        using (IngestObservabilityScope.Begin(obs, "LedgerTest"))
        {
            await foreach (var _ in IngestBatchPipeline.RunMultiFileAsync(
                new LabeledContentMultiFileStream(files),
                _ => new ContentIngestHandler(TestSource),
                label => new IngestBatchConfig
                {
                    SourceId = TestSource,
                    BatchLabelPrefix = $"ledger/{label}",
                    BatchSize = 8,
                    ProbeChunkSize = 1024,
                },
                fileWorkers: fileWorkers))
            {
            }
        }

        Assert.Equal(
            new[] { "ledger-a", "ledger-b" },
            obs.Started.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { "ledger-a", "ledger-b" },
            obs.Composed.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Empty(obs.Finished);
        Assert.All(obs.SourceNames, s => Assert.Equal("LedgerTest", s));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MultiFile_ComposedEvent_PrecedesBoundaryVisibility(int fileWorkers)
    {
        var files = new Dictionary<string, IReadOnlyList<ContentIngestRecord>>
        {
            ["ledger-order"] = [ContentRecord("ledger order")],
        };
        var obs = new RecordingObservability();

        using (IngestObservabilityScope.Begin(obs, "LedgerTest"))
        {
            await foreach (var change in IngestBatchPipeline.RunMultiFileAsync(
                new LabeledContentMultiFileStream(files),
                _ => new ContentIngestHandler(TestSource),
                label => new IngestBatchConfig
                {
                    SourceId = TestSource,
                    BatchLabelPrefix = $"ledger/{label}",
                    BatchSize = 8,
                    ProbeChunkSize = 1024,
                },
                fileWorkers: fileWorkers))
            {
                if (change.Metadata.SourceContentUnitName.StartsWith(
                        IngestBatchPipeline.PeriodBoundaryUnitPrefix, StringComparison.Ordinal))
                    Assert.Contains("ledger-order", obs.ComposedSnapshot());
            }
        }
    }

    private sealed class RecordingObservability : IIngestObservability
    {
        private readonly object _gate = new();
        public List<string> Started { get; } = [];
        public List<string> Composed { get; } = [];
        public List<string> Finished { get; } = [];
        public List<string> SourceNames { get; } = [];

        public string[] ComposedSnapshot()
        {
            lock (_gate) return Composed.ToArray();
        }

        public void OnRunStart(string sourceName, int layerOrder, IngestInventory? inventory) { }
        public void OnIntentApplied(string sourceName, ApplyResult result) { }
        public void OnIntentFailed(string sourceName, IngestFailure failure) { }
        public void OnRunFinished(string sourceName, IngestRunResult result, string status, string? error) { }

        public void OnFileStarted(string sourceName, string fileLabel, long bytes = 0)
        {
            lock (_gate) { Started.Add(fileLabel); SourceNames.Add(sourceName); }
        }

        public void OnFileComposed(
            string sourceName, string fileLabel, Laplace.Engine.Core.Hash128? fileId = null,
            long records = 0, long entities = 0, long physicalities = 0, long attestations = 0)
        {
            lock (_gate) { Composed.Add(fileLabel); SourceNames.Add(sourceName); }
        }

        public void OnFileFinished(
            string sourceName, string fileLabel, string status, string? error = null)
        {
            lock (_gate) { Finished.Add(fileLabel); SourceNames.Add(sourceName); }
        }
    }
}
