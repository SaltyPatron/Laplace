using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>Test doubles for ingest pipeline probe accounting.</summary>
internal static class IngestPipelineTestHelpers
{
    internal static readonly Hash128 TestSource =
        Hash128.OfCanonical("substrate/source/test/ingest-pipeline/v1");

    internal static long ContentEntityCount(IEnumerable<SubstrateChange> changes) =>
        changes.Sum(c => c.IntentStages.IsDefaultOrEmpty
            ? 0L
            : c.IntentStages.Sum(s => (long)s.EntityCount));

    internal static long AttestationCount(IEnumerable<SubstrateChange> changes) =>
        changes.Sum(c => (long)c.Attestations.Length +
            (c.IntentStages.IsDefaultOrEmpty ? 0L : c.IntentStages.Sum(s => (long)s.AttestationCount)));

    internal static int ExpectedDescentProbeChunks(int rowCount, int probeChunkSize) =>
        rowCount == 0 ? 0 : (rowCount + probeChunkSize - 1) / probeChunkSize;

    /// <summary>Uniform present/absent for all probe types; counts flat vs descent calls.</summary>
    internal sealed class ProbeTrackingReader : ISubstrateReader
    {
        private readonly bool _present;
        public int FlatProbeCalls;
        public int DescentProbeCalls;
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
            Interlocked.Increment(ref DescentProbeCalls);
            return Task.FromResult(MakeBitmap(ids.Count, _present));
        }

        private static byte[] MakeBitmap(int count, bool present)
        {
            var bm = new byte[(count + 7) / 8];
            if (present) Array.Fill(bm, (byte)0xFF);
            return bm;
        }
    }

    /// <summary>
    /// T2+ trunks absent via descent, but tier 0/1 nodes present via flat probe — regression for
    /// parallel-path bug where tier01 bits were never OR'd into the emit bitmap.
    /// </summary>
    internal sealed class Tier01PresentReader : ISubstrateReader
    {
        public int Tier01FlatCalls;
        public int DescentCalls;

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
            // Root existence gate uses single-id bulk IN — leave absent so compose + tree probe run.
            if (candidates.Count == 1) return Task.FromResult(bm);
            for (int i = 0; i < candidates.Count; i++)
                bm[i >> 3] |= (byte)(1 << (i & 7));
            return Task.FromResult(bm);
        }

        public Task<byte[]> ContentDescentBitmapAsync(
            IReadOnlyList<Hash128> ids, IReadOnlyList<int> parents, CancellationToken ct = default)
        {
            Interlocked.Increment(ref DescentCalls);
            return Task.FromResult(new byte[(ids.Count + 7) / 8]); // all T2+ absent
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

    /// <summary>Yields records in small chunks — never buffers the full logical file.</summary>
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

    /// <summary>
    /// Line-delimited temp file; reads with a tiny FileStream buffer to prove incremental I/O.
    /// </summary>
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
        public async IAsyncEnumerable<(string FileLabel, ContentIngestRecord Record)> RecordsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var (label, records) in files)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return (label, records[i]);
                    await Task.Yield();
                }
            }
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
