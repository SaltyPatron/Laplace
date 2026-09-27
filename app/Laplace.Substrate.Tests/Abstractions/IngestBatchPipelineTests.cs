using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;
using static Laplace.Decomposers.Abstractions.Tests.IngestPipelineTestHelpers;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class IngestBatchPipelineTests
{
    [Fact]
    public void FileResumeFingerprint_IsPathIndependentAndContentSensitive()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"laplace-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.bin");
            string b = Path.Combine(dir, "renamed.bin");
            File.WriteAllBytes(a, Encoding.UTF8.GetBytes("same file bytes"));
            File.WriteAllBytes(b, Encoding.UTF8.GetBytes("same file bytes"));

            Hash128? first = IngestBatchPipeline.TryResolveFileIdentity(a);
            Hash128? renamed = IngestBatchPipeline.TryResolveFileIdentity(b);
            File.WriteAllBytes(b, Encoding.UTF8.GetBytes("some file bytes"));
            Hash128? changed = IngestBatchPipeline.TryResolveFileIdentity(b);

            Assert.NotNull(first);
            Assert.Equal(first, renamed);
            Assert.NotEqual(first, changed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FileCompletionBoundary_PreservesTheJournalLabel()
    {
        var fileRoot = Hash128.OfCanonical("file/root");
        var boundary = IngestBatchPipeline.BuildFileCompletion(
            TestSource, "ud/en_ewt-ud-train", fileRoot, layerOrder: 2);

        Assert.Equal(
            "period-boundary/ud/en_ewt-ud-train",
            boundary.Metadata.SourceContentUnitName);
        Assert.Empty(boundary.Attestations);
        Assert.Equal(new IngestUnitCompletionKey(TestSource, fileRoot, 2),
            Assert.Single(boundary.UnitCompletions));
    }

    [Fact]
    public void PeriodBoundaryWithoutResumeIdentity_PreservesTheJournalLabel()
    {
        var boundary = IngestBatchPipeline.BuildPeriodBoundary(
            TestSource, "stream/archive/member.jsonl");

        Assert.Equal(
            "period-boundary/stream/archive/member.jsonl",
            boundary.Metadata.SourceContentUnitName);
    }

    [Fact]
    public async Task BatchProbeAmortization_OneDescentCallPerProbeChunk()
    {
        const int rowCount = 20;
        const int probeChunk = rowCount;
        var records = Enumerable.Range(1, rowCount)
            .Select(i => ContentRecord($"probe word {i}"))
            .ToList();

        int totalTier01 = 0;
        int totalNodes = 0;
        foreach (var r in records)
        {
            using var tree = IntentStage.BuildContentTree(r.CanonicalUtf8);
            if (tree is null) continue;
            totalNodes += tree.NodeCount;
            TierTreeDescent.BuildTier01Probe(tree, out var tier01Ids, out _);
            totalTier01 += tier01Ids.Count;
        }

        var reader = new ProbeTrackingReader(present: false);
        var config = DefaultConfig(reader, batchSize: rowCount, probeChunk: probeChunk);

        var changes = new List<SubstrateChange>();
        await foreach (var change in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource), config))
            changes.Add(change);

        Assert.Equal(0, reader.LegacyContentDescentCalls);
        Assert.True(reader.FlatCandidateCounts.Count >= 1);
        Assert.Equal(rowCount, reader.FlatCandidateCounts[0]);
        Assert.True(reader.FlatProbeCalls >= 2, "root bulk IN then tier rounds");
        Assert.InRange(reader.FlatProbeCalls, 2,
            MaxProbeCallsFor(ExpectedExistenceRoundChunks(rowCount, probeChunk)));
        Assert.True(ContentEntityCount(changes) > 0);
    }

    [Fact]
    public async Task BatchedDescent_AmortizesRoundTrips()
    {
        const int rowCount = 100;
        const int probeChunk = 100;
        var records = Enumerable.Range(1, rowCount)
            .Select(i => ContentRecord($"batched descent {i}"))
            .ToList();

        var reader = new ProbeTrackingReader(present: false);
        var config = DefaultConfig(reader, batchSize: rowCount, probeChunk: probeChunk);

        await foreach (var _ in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource), config))
        { }

        Assert.Equal(0, reader.LegacyContentDescentCalls);
        Assert.True(reader.FlatProbeCalls >= 1, "root bulk IN + tier rounds");
        Assert.InRange(reader.FlatProbeCalls, 1, MaxProbeCallsFor(1));
    }

    [Fact]
    public async Task PresentBatch_StagesNothingAfterOneRootProbe()
    {
        const int rowCount = 12;
        var records = Enumerable.Range(1, rowCount)
            .Select(i => ContentRecord($"present batch {i}"))
            .ToList();
        var baseline = new List<SubstrateChange>();
        var changes = new List<SubstrateChange>();
        try
        {
            await foreach (var c in IngestBatchPipeline.RunAsync(
                new ListContentStream(records), new ContentIngestHandler(TestSource),
                DefaultConfig(batchSize: rowCount, probeChunk: rowCount)))
                baseline.Add(c);
            Assert.True(ContentEntityCount(baseline) > 0);

            var reader = new ProbeTrackingReader(present: true);
            await foreach (var c in IngestBatchPipeline.RunAsync(
                new ListContentStream(records), new ContentIngestHandler(TestSource),
                DefaultConfig(reader, batchSize: rowCount, probeChunk: rowCount)))
                changes.Add(c);

            // A present root is its whole subtree: one root probe decides every record,
            // and nothing beneath a present root is asked or staged.
            Assert.Equal(rowCount, reader.FlatCandidateCounts[0]);
            Assert.Equal(1, reader.FlatProbeCalls);
            Assert.Equal(0, reader.LegacyContentDescentCalls);
            Assert.Equal(0, ContentEntityCount(changes));
            Assert.Equal(records.Count, changes.Sum(x => x.Metadata.InputUnitsConsumed));
            Assert.NotEmpty(PhysicalityBodies(baseline));
            Assert.Empty(PhysicalityBodies(changes));
        }
        finally
        {
            DisposeStages(baseline);
            DisposeStages(changes);
        }
    }

    [Fact]
    public async Task PresentBatch_StillEmitsAttestations()
    {
        string path = Path.Combine(Path.GetTempPath(), $"laplace-present-attest-{Guid.NewGuid():N}.tsv");
        try
        {
            var lines = Enumerable.Range(1, 8)
                .Select(i => $"{i}\tRelatedTo\t/c/en/dog{i}\t/c/en/animal{i}\t{{}}")
                .ToArray();
            await File.WriteAllLinesAsync(path, lines, Encoding.UTF8);

            var witness = new AttestingGrammarWitness("tsv");
            var reader = new ProbeTrackingReader(present: true);

            var changes = new List<SubstrateChange>();
            await foreach (var change in StructuredGrammarIngest.IngestFileViaPipelineAsync(
                path, "tsv", TestSource, sourceTrust: 1.0, witness, batchSize: 8, witnessWeight: 1.0,
                batchLabelPrefix: "present-attest", reportUnits: null, containmentReader: reader))
                changes.Add(change);

            Assert.Equal(0, ContentEntityCount(changes));
            Assert.True(AttestationCount(changes) > 0,
                "present rows must still attest via witness walk after probe-gated drain");
            Assert.Equal(lines.Length, changes.Sum(c => c.Metadata.InputUnitsConsumed));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task NovelBatch_EmitsSameAsNoReader()
    {
        var records = Enumerable.Range(1, 12)
            .Select(i => ContentRecord($"novel batch {i}"))
            .ToList();

        var baseline = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource), DefaultConfig()))
            baseline.Add(c);

        var reader = new ProbeTrackingReader(present: false);
        var changes = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource), DefaultConfig(reader)))
            changes.Add(c);

        Assert.Equal(ContentEntityCount(baseline), ContentEntityCount(changes));
        Assert.Equal(
            baseline.Sum(x => x.Metadata.InputUnitsConsumed),
            changes.Sum(x => x.Metadata.InputUnitsConsumed));
    }

    [Fact]
    public async Task Tier01Completion_FlatProbeMarksPresentWhenTrunksAbsent()
    {
        // q+combining-acute survives NFC (no precomposed form), so this word carries a
        // tier-1 grapheme row the presence probe can prune. A single-codepoint cluster
        // is its codepoint, so in plain ASCII the word is the smallest emission unit.
        var records = new[] { ContentRecord("q́x") };
        var reader = new Tier01PresentReader();

        var changes = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource), DefaultConfig(reader)))
            changes.Add(c);

        Assert.True(reader.Tier01FlatCalls >= 2,
            "root gate plus tier rounds all flow through the flat probe surface");
        Assert.Equal(0, reader.LegacyContentDescentCalls);
        var baseline = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource), DefaultConfig()))
            baseline.Add(c);
        Assert.True(ContentEntityCount(changes) < ContentEntityCount(baseline));
    }

    [Fact]
    public async Task ComposeAfterProbe_DescentRunsBeforeMaterialize()
    {
        const string row = "1\tRelatedTo\t/c/en/dog\t/c/en/animal\t{}";
        byte[] utf8 = Encoding.UTF8.GetBytes(row);
        using var ast = GrammarDecomposer.Parse(utf8, "tsv");
        using var composer = new GrammarRowComposer(utf8, ast, TestSource, "tsv");

        var reader = new ProbeTrackingReader(present: true);
        byte[]? bm = await composer.ProbeDescentBitmapAsync(reader);
        Assert.NotNull(bm);
        Assert.True(reader.FlatProbeCalls > 0);

        using var fresh = new GrammarRowComposer(utf8, ast, TestSource, "tsv");
        var (baseEnts, basePhys, basePrec, baseRoot) = fresh.Materialize(1.0);
        Assert.NotEmpty(baseEnts);
        Assert.NotEmpty(basePhys);
        var (ents, phys, prec, root) = composer.Materialize(1.0, bm);
        Assert.Empty(ents);
        Assert.Equal(baseRoot, root);
        Assert.Equal(basePhys.Length, phys.Length);
        Assert.Equal(basePhys.Select(p => p with { ObservedAtUnixUs = 0, TrajectoryXyzm = null }),
            phys.Select(p => p with { ObservedAtUnixUs = 0, TrajectoryXyzm = null }));
        for (int i = 0; i < basePhys.Length; i++)
            Assert.Equal(basePhys[i].TrajectoryXyzm, phys[i].TrajectoryXyzm);
        Assert.Equal(basePrec.Length, prec.Length);
    }

    [Fact]
    public async Task RecordStream_YieldsIncrementally()
    {
        var records = Enumerable.Range(1, 10)
            .Select(i => ContentRecord($"stream {i}"))
            .ToList();
        var stream = new ChunkedContentStream(records, chunkSize: 2);

        int count = 0;
        await foreach (var _ in IngestBatchPipeline.RunAsync(
            stream, new ContentIngestHandler(TestSource), DefaultConfig()))
            count++;

        Assert.Equal(10, stream.YieldCount);
        Assert.True(count >= 1);
    }

    [Fact]
    public async Task StreamAdapter_NeverAllocatesFullFile()
    {
        var lines = Enumerable.Range(1, 12).Select(i => $"incremental line {i}");
        await using var stream = await IncrementalLineFileStream.CreateAsync(lines);

        int units = 0;
        await foreach (var change in IngestBatchPipeline.RunAsync(
            stream, new ContentIngestHandler(TestSource), DefaultConfig()))
            units += (int)change.Metadata.InputUnitsConsumed;

        Assert.Equal(12, units);
        Assert.True(stream.MaxReadChunk <= 32,
            $"stream adapter must read in small chunks (max read {stream.MaxReadChunk}), not slurp the whole file");
    }

    [Fact]
    public async Task MultiFileTier_ProcessesPerFileBatches()
    {
        var files = new Dictionary<string, IReadOnlyList<ContentIngestRecord>>
        {
            ["file-a"] = [ContentRecord("alpha"), ContentRecord("beta")],
            ["file-b"] = [ContentRecord("gamma")],
            ["file-c"] = [ContentRecord("delta"), ContentRecord("epsilon"), ContentRecord("zeta")],
        };

        var changes = new List<SubstrateChange>();
        await foreach (var change in IngestBatchPipeline.RunMultiFileAsync(
            new LabeledContentMultiFileStream(files),
            label => new ContentIngestHandler(TestSource),
            label => new IngestBatchConfig
            {
                SourceId = TestSource,
                BatchLabelPrefix = $"multi/{label}",
                BatchSize = 2,
                ProbeChunkSize = 1024,
                ContainmentReader = null,
            }))
            changes.Add(change);

        Assert.Equal(6, changes.Sum(c => c.Metadata.InputUnitsConsumed));
        Assert.Contains(changes, c => c.Metadata.SourceContentUnitName.StartsWith("multi/file-a/"));
        Assert.Contains(changes, c => c.Metadata.SourceContentUnitName.StartsWith("multi/file-b/"));
        Assert.Contains(changes, c => c.Metadata.SourceContentUnitName.StartsWith("multi/file-c/"));
        Assert.Equal(3, changes.Count(c =>
            c.Metadata.SourceContentUnitName.StartsWith(
                IngestBatchPipeline.PeriodBoundaryUnitPrefix, StringComparison.Ordinal)));
    }

    /// <summary>
    /// The per-file resume probe is a set operation: one round trip per chunk of files,
    /// never one per file. <c>PerFileResume</c> is on for every DecomposerMultiFile, and a
    /// scalar probe produces identical rows and markers, so the call shape is asserted.
    /// </summary>
    [Fact]
    public async Task MultiFileResume_MarkerProbeIsBatchedNotPerFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"laplace-resume-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            const int fileCount = 40;
            var paths = new List<string>(fileCount);
            for (int i = 0; i < fileCount; i++)
            {
                string p = Path.Combine(dir, $"probe-{i:D3}.txt");
                await File.WriteAllTextAsync(p, $"probe body {i}\n");
                paths.Add(p);
            }

            var reader = new ProbeTrackingReader(present: false)
            {
                SourceCompleted = (_, _) => false,   // nothing complete: every file is read
            };
            var resume = new IngestBatchPipeline.PerFileResumePlan(reader, 3, IgnoreCompletedFiles: false)
            {
                Resolved = new System.Collections.Concurrent.ConcurrentDictionary<
                    string, (Hash128? Root, bool Skip)>(StringComparer.Ordinal),
            };

            var sources = new List<IFileRecordSource<ContentIngestRecord>>();
            foreach (var path in paths)
            {
                string label = Path.GetFileName(path);
                sources.Add(new DelegateFileRecordSource<ContentIngestRecord>(
                    label, ct => OneAsync(label, ct), filePath: path));
            }

            static async IAsyncEnumerable<ContentIngestRecord> OneAsync(
                string label,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                yield return ContentRecord($"payload-{label}");
                await Task.Yield();
            }

            int seen = 0;
            await foreach (var _ in IngestBatchPipeline.RunMultiFileAsync(
                new SourceListMultiFileStream<ContentIngestRecord>(sources),
                _ => new ContentIngestHandler(TestSource),
                label => new IngestBatchConfig
                {
                    SourceId = TestSource,
                    BatchLabelPrefix = $"batch/{label}",
                    BatchSize = 4,
                    ProbeChunkSize = 1024,
                    ContainmentReader = reader,
                },
                fileWorkers: 4,
                resume: resume))
                seen++;

            Assert.True(seen > 0, "the run must actually produce changes");

            // Probes scale sub-linearly with files and none are scalar; the chunk size
            // ramps, so no particular call count is asserted.
            Assert.Equal(0, reader.ScalarSourceCompletedCalls);
            Assert.True(reader.BatchedSourceCompletedCalls > 0,
                "the batched probe must actually be used");
            Assert.True(reader.BatchedSourceCompletedCalls <= fileCount / 4,
                $"probes must scale sub-linearly with files: {reader.BatchedSourceCompletedCalls} "
                + $"probes for {fileCount} files is approaching one-per-file");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Killed after file 2 applies, a restart skips marker-complete files 1–2 (no fold,
    /// no units) and re-opens only file 3.
    /// </summary>
    [Fact]
    public async Task MultiFileResume_MarkerCompleteFilesTrueSkip()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"laplace-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var paths = new[]
            {
                Path.Combine(dir, "file-1.txt"),
                Path.Combine(dir, "file-2.txt"),
                Path.Combine(dir, "file-3.txt"),
            };
            await File.WriteAllTextAsync(paths[0], "resume one\n");
            await File.WriteAllTextAsync(paths[1], "resume two\n");
            await File.WriteAllTextAsync(paths[2], "resume three\n");

            var roots = paths.Select(p =>
                IngestBatchPipeline.TryResolveFileIdentity(p)!.Value).ToArray();
            var completed = new HashSet<Hash128> { roots[0], roots[1] };
            var reader = new ProbeTrackingReader(present: false)
            {
                SourceCompleted = (id, _) => completed.Contains(id),
            };
            const int layer = 3;
            var resume = new IngestBatchPipeline.PerFileResumePlan(reader, layer, IgnoreCompletedFiles: false);

            var sources = new List<IFileRecordSource<ContentIngestRecord>>();
            foreach (var path in paths)
            {
                string label = Path.GetFileName(path);
                string captured = path;
                sources.Add(new DelegateFileRecordSource<ContentIngestRecord>(
                    label,
                    ct => OpenOneAsync(label, ct),
                    filePath: captured));
            }

            static async IAsyncEnumerable<ContentIngestRecord> OpenOneAsync(
                string label,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                yield return ContentRecord($"payload-{label}");
                await Task.Yield();
            }

            var changes = new List<SubstrateChange>();
            await foreach (var change in IngestBatchPipeline.RunMultiFileAsync(
                new SourceListMultiFileStream<ContentIngestRecord>(sources),
                _ => new ContentIngestHandler(TestSource),
                label => new IngestBatchConfig
                {
                    SourceId = TestSource,
                    BatchLabelPrefix = $"resume/{label}",
                    BatchSize = 4,
                    ProbeChunkSize = 1024,
                    ContainmentReader = reader,
                },
                fileWorkers: 1,
                resume: resume))
                changes.Add(change);

            // A marker-complete file is skipped (no rows, no testimony, no re-fold) but still
            // counts: files_done advances only on a period boundary, so every file emits one
            // boundary; rows come only from files actually read, and the completion marker
            // only from a file that just finished (a skipped file already carries its own).
            Assert.Equal(3, changes.Count(c =>
                c.Metadata.SourceContentUnitName.StartsWith(
                    IngestBatchPipeline.PeriodBoundaryUnitPrefix, StringComparison.Ordinal)));
            foreach (var stem in new[] { "file-1", "file-2" })
            {
                var emitted = changes.Where(c =>
                    c.Metadata.SourceContentUnitName.Contains(stem, StringComparison.Ordinal)).ToList();
                Assert.Single(emitted);
                Assert.StartsWith(IngestBatchPipeline.PeriodBoundaryUnitPrefix,
                    emitted[0].Metadata.SourceContentUnitName, StringComparison.Ordinal);
                Assert.Equal(0, emitted[0].Metadata.InputUnitsConsumed);
                Assert.Empty(emitted[0].Entities);
                Assert.Empty(emitted[0].Attestations);
            }

            Assert.Equal(1, changes.Sum(c => c.Metadata.InputUnitsConsumed));
            Assert.Contains(changes, c =>
                c.Metadata.SourceContentUnitName.Contains("file-3", StringComparison.Ordinal));
            Assert.Equal(1, UnitCompletionCount(changes));
            Assert.Equal(0, NonMarkerAttestationCount(changes.Where(c =>
                c.Metadata.SourceContentUnitName.Contains("file-1", StringComparison.Ordinal)
                || c.Metadata.SourceContentUnitName.Contains("file-2", StringComparison.Ordinal))));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MultiFileTier_MaxTotalUnits_StopsAcrossFiles()
    {
        var files = new Dictionary<string, IReadOnlyList<ContentIngestRecord>>
        {
            ["file-a"] = [ContentRecord("a1"), ContentRecord("a2"), ContentRecord("a3")],
            ["file-b"] = [ContentRecord("b1"), ContentRecord("b2")],
        };

        var changes = new List<SubstrateChange>();
        await foreach (var change in IngestBatchPipeline.RunMultiFileAsync(
            new LabeledContentMultiFileStream(files),
            _ => new ContentIngestHandler(TestSource),
            label => new IngestBatchConfig
            {
                SourceId = TestSource,
                BatchLabelPrefix = $"cap/{label}",
                BatchSize = 8,
                ProbeChunkSize = 1024,
            },
            maxTotalUnits: 4))
            changes.Add(change);

        Assert.Equal(4, changes.Sum(c => c.Metadata.InputUnitsConsumed));
    }

    // The parallel pool opens files concurrently (each worker opens its own source) rather than
    // funnelling every file through one dispatcher. Each source records the live open count;
    // with 4 workers over 6 held-open files, opens overlap.
    [Fact]
    public async Task MultiFileParallel_OpensFilesConcurrently()
    {
        const int fileCount = 6;
        int current = 0, maxObserved = 0;
        var gate = new object();

        async IAsyncEnumerable<ContentIngestRecord> OpenTracked(
            string label,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            int now = System.Threading.Interlocked.Increment(ref current);
            lock (gate) maxObserved = System.Math.Max(maxObserved, now);
            await Task.Delay(60, ct); // hold the file "open" so concurrent opens overlap observably
            yield return ContentRecord($"{label}-record");
            System.Threading.Interlocked.Decrement(ref current);
        }

        var sources = Enumerable.Range(0, fileCount)
            .Select(i => (IFileRecordSource<ContentIngestRecord>)
                new DelegateFileRecordSource<ContentIngestRecord>(
                    $"file-{i}", ct => OpenTracked($"file-{i}", ct)))
            .ToList();

        var changes = new List<SubstrateChange>();
        await foreach (var change in IngestBatchPipeline.RunMultiFileAsync(
            new SourceListMultiFileStream<ContentIngestRecord>(sources),
            _ => new ContentIngestHandler(TestSource),
            label => new IngestBatchConfig
            {
                SourceId = TestSource,
                BatchLabelPrefix = $"par/{label}",
                BatchSize = 2,
                ProbeChunkSize = 1024,
            },
            fileWorkers: 4))
            changes.Add(change);

        Assert.Equal(fileCount, changes.Count(c =>
            c.Metadata.SourceContentUnitName.StartsWith(
                IngestBatchPipeline.PeriodBoundaryUnitPrefix, StringComparison.Ordinal)));
        Assert.True(maxObserved >= 2,
            $"parallel pool must open files concurrently; observed max {maxObserved} of {fileCount} with 4 workers");
    }

    private sealed class SourceListMultiFileStream<TRecord>(IReadOnlyList<IFileRecordSource<TRecord>> sources)
        : IMultiFileRecordStream<TRecord>
    {
        public async IAsyncEnumerable<IFileRecordSource<TRecord>> FilesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var s in sources) { ct.ThrowIfCancellationRequested(); yield return s; }
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task GrammarPipelineViaAdapter_MatchesStructuredIngestPresentBitmap()
    {
        string path = Path.Combine(Path.GetTempPath(), $"laplace-pipeline-tsv-{Guid.NewGuid():N}.tsv");
        try
        {
            var lines = Enumerable.Range(1, 8)
                .Select(i => $"{i}\tRelatedTo\t/c/en/dog{i}\t/c/en/animal{i}\t{{}}")
                .ToArray();
            await File.WriteAllLinesAsync(path, lines, Encoding.UTF8);

            var witness = new NullGrammarWitness("tsv");
            var reader = new ProbeTrackingReader(present: true);

            var changes = new List<SubstrateChange>();
            await foreach (var change in StructuredGrammarIngest.IngestFileViaPipelineAsync(
                path, "tsv", TestSource, sourceTrust: 1.0, witness, batchSize: 4, witnessWeight: 1.0,
                batchLabelPrefix: "via-pipeline", reportUnits: null, containmentReader: reader))
                changes.Add(change);

            Assert.True(reader.FlatProbeCalls > 0,
                "present ingest must probe (tier descent and/or trunk shortcircuit flat)");
            Assert.Equal(0, ContentEntityCount(changes));
            Assert.Equal(lines.Length, changes.Sum(c => c.Metadata.InputUnitsConsumed));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task GrammarIngest_BatchedProbe()
    {
        string path = Path.Combine(Path.GetTempPath(), $"laplace-grammar-probe-{Guid.NewGuid():N}.tsv");
        try
        {
            const int rowCount = 12;
            var lines = Enumerable.Range(1, rowCount)
                .Select(i => $"{i}\tRelatedTo\t/c/en/legacy{i}\t/c/en/target{i}\t{{}}")
                .ToArray();
            await File.WriteAllLinesAsync(path, lines, Encoding.UTF8);

            var witness = new NullGrammarWitness("tsv");
            var reader = new ProbeTrackingReader(present: false);

            await foreach (var _ in StructuredGrammarIngest.IngestFileAsync(
                path, "tsv", TestSource, sourceTrust: 1.0, witness, batchSize: 64, witnessWeight: 1.0,
                batchLabelPrefix: "grammar-probe", reportUnits: null, IngestSourceProfile.Default,
                containmentReader: reader))
            { }

            Assert.Equal(0, reader.LegacyContentDescentCalls);
            Assert.True(reader.FlatProbeCalls < rowCount,
                "grammar ingest must batch probe within pending chunks, not one probe call per row");
            Assert.InRange(reader.FlatProbeCalls, 1,
                MaxProbeCallsFor(ExpectedExistenceRoundChunks(rowCount, 1024)));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
