using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions.Tests;

internal static class IngestPipelineTestHelpers
{
    internal static readonly Hash128 TestSource =
        SubstrateCanonicalIds.OfVersioned("source", "test", "ingest-pipeline");

    internal static long ContentEntityCount(IEnumerable<SubstrateChange> changes) =>
        changes.Sum(c => c.IntentStages.IsDefaultOrEmpty
            ? 0L
            : c.IntentStages.Sum(s => (long)s.EntityCount));

    internal static long AttestationCount(IEnumerable<SubstrateChange> changes) =>
        changes.Sum(c => (long)c.Attestations.Length +
            (c.IntentStages.IsDefaultOrEmpty ? 0L : c.IntentStages.Sum(s => (long)s.AttestationCount)));

    internal static int ExpectedExistenceRoundChunks(int rowCount, int probeChunkSize) =>
        rowCount == 0 ? 0 : (rowCount + probeChunkSize - 1) / probeChunkSize;

    /// <summary>
    /// Upper bound on probe ROUND TRIPS for a chunked ingest under the
    /// tier-descent architecture: per probe chunk, one existence-gate call
    /// plus at most one call per tier round (tiers 0..4 content + margin
    /// for the tier-0/1 flat completion), plus the batched content-anchor
    /// probe pass at build time (roots bitmap + descent rounds) when the
    /// builder carries witness-emitted content. The invariant that matters
    /// is that probe calls scale with chunks × tiers plus a per-build
    /// constant — never with row count.
    /// </summary>
    internal static int MaxProbeCallsFor(int probeChunks) => probeChunks * 12;

    internal sealed class ProbeTrackingReader : ISubstrateReader
    {
        private readonly bool _present;
        public int FlatProbeCalls;

        /// <summary>
        /// Calls to the LEGACY flat (ids, parents) probe
        /// (ISubstrateReader.ContentDescentBitmapAsync). The pipeline was
        /// migrated to TierTreeDescent's tier-by-tier probing (which lands
        /// in <see cref="FlatProbeCalls"/> via TierBatchExistenceProbeAsync's
        /// default delegation), so tests assert this stays ZERO — no
        /// production caller remains (the native ETL lane that was the last
        /// one is deleted).
        /// </summary>
        public int LegacyContentDescentCalls;
        public int MaxFlatCandidates;
        public int TotalFlatCandidates;
        public readonly List<int> FlatCandidateCounts = [];

        public ProbeTrackingReader(bool present) => _present = present;

        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
            => Task.FromResult(0L);

        public Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
        {
            Interlocked.Increment(ref FlatProbeCalls);
            int n = candidates.Count;
            FlatCandidateCounts.Add(n);
            Interlocked.Add(ref TotalFlatCandidates, n);
            int max = Volatile.Read(ref MaxFlatCandidates);
            while (n > max)
            {
                if (Interlocked.CompareExchange(ref MaxFlatCandidates, n, max) == max) break;
                max = Volatile.Read(ref MaxFlatCandidates);
            }
            return Task.FromResult(MakeBitmap(n, _present));
        }

        public Task<byte[]> ContentDescentBitmapAsync(
            IReadOnlyList<Hash128> ids, IReadOnlyList<int> parents, CancellationToken ct = default)
        {
            Interlocked.Increment(ref LegacyContentDescentCalls);
            return Task.FromResult(MakeBitmap(ids.Count, _present));
        }

        private static byte[] MakeBitmap(int count, bool present)
        {
            var bm = new byte[(count + 7) / 8];
            if (present) Array.Fill(bm, (byte)0xFF);
            return bm;
        }
    }

    internal sealed class Tier01PresentReader : ISubstrateReader
    {
        public int Tier01FlatCalls;
        public int LegacyContentDescentCalls;

        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
            => Task.FromResult(0L);

        public Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Tier01FlatCalls);
            var bm = new byte[(candidates.Count + 7) / 8];

            if (candidates.Count == 1) return Task.FromResult(bm);
            for (int i = 0; i < candidates.Count; i++)
                bm[i >> 3] |= (byte)(1 << (i & 7));
            return Task.FromResult(bm);
        }

        public Task<byte[]> ContentDescentBitmapAsync(
            IReadOnlyList<Hash128> ids, IReadOnlyList<int> parents, CancellationToken ct = default)
        {
            Interlocked.Increment(ref LegacyContentDescentCalls);
            return Task.FromResult(new byte[(ids.Count + 7) / 8]);
        }
    }

    internal sealed class NullGrammarWitness(string modalityId) : IGrammarWitness
    {
        public string ModalityId => modalityId;
        public void WalkRow(in GrammarComposeContext composed, in RowContext ctx, SubstrateChangeBuilder builder) { }
    }

    internal static ContentIngestRecord ContentRecord(string text, int seq = 0) =>
        new(Encoding.UTF8.GetBytes(text), seq);

    internal sealed class ListContentStream(IReadOnlyList<ContentIngestRecord> records) : IRecordStream<ContentIngestRecord>
    {
        public async IAsyncEnumerable<ContentIngestRecord> RecordsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int i = 0; i < records.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return records[i];
                await Task.Yield();
            }
        }
    }

    internal sealed class AttestingGrammarWitness(string modalityId) : IGrammarWitness
    {
        public string ModalityId => modalityId;

        public void WalkRow(in GrammarComposeContext composed, in RowContext ctx, SubstrateChangeBuilder builder)
        {
            if (composed.RootId == default) return;
            builder.AddAttestation(NativeAttestation.Categorical(
                composed.RootId, "OBSERVED", null, TestSource, sourceTrust: 1.0));
        }
    }

    internal sealed class ChunkedContentStream : IRecordStream<ContentIngestRecord>
    {
        private readonly IReadOnlyList<ContentIngestRecord> _records;
        private readonly int _chunkSize;
        public int YieldCount { get; private set; }
        public bool SimulatedReadAllBytes { get; private set; }

        public ChunkedContentStream(IReadOnlyList<ContentIngestRecord> records, int chunkSize = 2)
        {
            _records = records;
            _chunkSize = chunkSize;
        }

        public async IAsyncEnumerable<ContentIngestRecord> RecordsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int i = 0; i < _records.Count; i += _chunkSize)
            {
                ct.ThrowIfCancellationRequested();
                int end = Math.Min(i + _chunkSize, _records.Count);
                for (int j = i; j < end; j++)
                {
                    YieldCount++;
                    yield return _records[j];
                }
                await Task.Yield();
            }
        }

        public void MarkReadAllBytes() => SimulatedReadAllBytes = true;
    }

    internal sealed class IncrementalLineFileStream : IRecordStream<ContentIngestRecord>, IAsyncDisposable
    {
        private readonly string _path;
        public int MaxReadChunk { get; private set; }

        private IncrementalLineFileStream(string path) => _path = path;

        public static async Task<IncrementalLineFileStream> CreateAsync(IEnumerable<string> lines)
        {
            string path = Path.Combine(Path.GetTempPath(), $"laplace-incr-{Guid.NewGuid():N}.txt");
            await File.WriteAllLinesAsync(path, lines);
            return new IncrementalLineFileStream(path);
        }

        public async IAsyncEnumerable<ContentIngestRecord> RecordsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            const int bufSize = 32;
            await using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: bufSize, useAsync: true);
            var readBuf = new byte[bufSize];
            var line = new List<byte>(128);
            int seq = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int n = await fs.ReadAsync(readBuf, ct);
                if (n > 0) MaxReadChunk = Math.Max(MaxReadChunk, n);
                if (n <= 0)
                {
                    if (line.Count > 0)
                        yield return ContentRecord(Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(line)), seq++);
                    yield break;
                }

                for (int i = 0; i < n; i++)
                {
                    byte b = readBuf[i];
                    if (b == (byte)'\n')
                    {
                        if (line.Count > 0)
                        {
                            yield return ContentRecord(Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(line)), seq++);
                            line.Clear();
                        }
                        continue;
                    }
                    if (b != (byte)'\r') line.Add(b);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            if (File.Exists(_path)) File.Delete(_path);
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class LabeledContentMultiFileStream(
        IReadOnlyDictionary<string, IReadOnlyList<ContentIngestRecord>> files) : IMultiFileRecordStream<ContentIngestRecord>
    {
        public async IAsyncEnumerable<IFileRecordSource<ContentIngestRecord>> FilesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var (label, records) in files)
            {
                ct.ThrowIfCancellationRequested();
                var recs = records;
                yield return new DelegateFileRecordSource<ContentIngestRecord>(label, OpenAsync);

                async IAsyncEnumerable<ContentIngestRecord> OpenAsync(
                    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
                {
                    for (int i = 0; i < recs.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        yield return recs[i];
                        await Task.Yield();
                    }
                }
            }
            await Task.CompletedTask;
        }
    }

    internal static IngestBatchConfig DefaultConfig(
        ISubstrateReader? reader = null, int batchSize = 4, int probeChunk = 1024) =>
        new()
        {
            SourceId = TestSource,
            BatchLabelPrefix = "pipeline-test",
            BatchSize = batchSize,
            ProbeChunkSize = probeChunk,
            ContainmentReader = reader,
        };
}
