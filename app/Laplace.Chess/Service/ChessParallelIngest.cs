using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>
/// P-core parallel game framing + parse feeding the same IngestBatchPipeline spine as
/// DecomposerBatch (Rule #8). Chunked novelty filter before yield skips compose for games
/// already in the substrate (unless <paramref name="reObservePresent"/>).
/// </summary>
internal sealed class ParallelChessGameRecordStream : IRecordStream<ChessPgnDecomposer.ParsedGame>
{
    private readonly string _ecosystemPath;
    private readonly ISubstrateReader? _reader;
    private readonly int _probeChunk;
    private readonly int _workerCount;
    private readonly bool _reObservePresent;
    private readonly CancellationToken _ct;

    public ParallelChessGameRecordStream(
        string ecosystemPath,
        ISubstrateReader? reader,
        int probeChunk,
        int workerCount,
        bool reObservePresent,
        CancellationToken ct)
    {
        _ecosystemPath = ecosystemPath;
        _reader = reader;
        _probeChunk = Math.Max(1, probeChunk);
        _workerCount = Math.Max(1, workerCount);
        _reObservePresent = reObservePresent;
        _ct = ct;
    }

    public async IAsyncEnumerable<ChessPgnDecomposer.ParsedGame> RecordsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct, ct);
        var runCt = linked.Token;

        var raw = Channel.CreateBounded<string>(new BoundedChannelOptions(_workerCount * 8)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var parsed = Channel.CreateBounded<ChessPgnDecomposer.ParsedGame>(new BoundedChannelOptions(_workerCount * 4)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        long framed = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var feeder = Task.Run(async () =>
        {
            try
            {
                await foreach (var gameText in ChessPgnDecomposer.StreamAllGamesAsync(_ecosystemPath, runCt))
                {
                    if (++framed % 50_000 == 0)
                        Console.WriteLine(
                            $"WS_COMPOSE feed: {framed:N0} games framed "
                            + $"({framed / Math.Max(1e-3, sw.Elapsed.TotalSeconds):N0} games/s)");
                    await raw.Writer.WriteAsync(gameText, runCt);
                }
            }
            finally { raw.Writer.TryComplete(); }
        }, runCt);

        var workers = new Task[_workerCount];
        for (int w = 0; w < _workerCount; w++)
        {
            workers[w] = Task.Run(async () =>
            {
                while (await raw.Reader.WaitToReadAsync(runCt))
                {
                    while (raw.Reader.TryRead(out var gameText))
                    {
                        if (ChessPgnDecomposer.TryParseGame(gameText) is { } pg)
                            await parsed.Writer.WriteAsync(pg, runCt);
                    }
                }
            }, runCt);
        }

        var closer = Task.Run(async () =>
        {
            await Task.WhenAll(workers);
            parsed.Writer.TryComplete();
        }, runCt);

        var chunk = new List<ChessPgnDecomposer.ParsedGame>(_probeChunk);
        await foreach (var record in parsed.Reader.ReadAllAsync(runCt))
        {
            chunk.Add(record);
            if (chunk.Count < _probeChunk) continue;
            await foreach (var g in ChessPgnDecomposer.YieldChunkAsync(chunk, _reader, _reObservePresent, runCt))
                yield return g;
            chunk.Clear();
        }
        await foreach (var g in ChessPgnDecomposer.YieldChunkAsync(chunk, _reader, _reObservePresent, runCt))
            yield return g;

        await feeder.ConfigureAwait(false);
        await closer.ConfigureAwait(false);
    }
}

internal sealed class ChessGameIngestHandler : IIngestRecordHandler<ChessPgnDecomposer.ParsedGame>
{
    public ValueTask<bool> TryTrunkShortcircuitAsync(
        ChessPgnDecomposer.ParsedGame record,
        SubstrateChangeBuilder builder,
        ISubstrateReader reader,
        double witnessWeight,
        CancellationToken ct = default) =>
        ValueTask.FromResult(reader.IsProvenPresent(record.GameId));

    public IIngestDeferredUnit CreateDeferredUnit(ChessPgnDecomposer.ParsedGame record) => new Unit(record);

    public void WalkWitness(
        ChessPgnDecomposer.ParsedGame record, Hash128 root,
        SubstrateChangeBuilder builder, IIngestDeferredUnit unit) =>
        ChessPgnDecomposer.RecordGame(record, builder);

    private sealed class Unit(ChessPgnDecomposer.ParsedGame record) : IIngestDeferredUnit
    {
        public TierTree? TreeForBatchProbe => null;

        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);

        public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap)
        {
            ChessPgnDecomposer.RecordGame(record, builder);
            return record.GameId;
        }

        public void Dispose() { }
    }
}
