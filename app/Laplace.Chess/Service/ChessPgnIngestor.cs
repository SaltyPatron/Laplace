using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// In-process PGN → substrate ingest: witnessed record (ChessPgn source) plus the calculated
/// analyze pass (ChessAnalysis source) per game, through the same writer spine the live hosts
/// use. This is the loop-closer for games the lab plays via external engines (cutechess drives
/// the laplace-uci binary, which cannot record its own games) — the PGN artifact feeds straight
/// back into consensus instead of waiting for a manual `laplace ingest chess` run.
/// Novelty-gated on content-addressed game ids, so re-ingesting an artifact is a no-op.
/// </summary>
public sealed class ChessPgnIngestor : IAsyncDisposable
{
    // Serialize in-process ingests; bulk CLI ingests hold the command-line mutex, this holds the
    // API process's own lane. Lab artifacts are small (tens of games) so waiting is fine.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private const int ChunkSize = 256;

    private readonly NpgsqlDataSource _ds;
    private readonly ConsensusAccumulatingWriter _writer;
    private readonly NpgsqlSubstrateReader _reader;

    public readonly record struct Result(int Parsed, int Novel, int Applied);

    private ChessPgnIngestor(
        NpgsqlDataSource ds, ConsensusAccumulatingWriter writer, NpgsqlSubstrateReader reader)
    {
        _ds = ds;
        _writer = writer;
        _reader = reader;
    }

    public static async Task<ChessPgnIngestor> CreateAsync(CancellationToken ct = default)
    {
        CodepointPerfcache.LoadDefault();
        var ds = new NpgsqlDataSourceBuilder(ChessEngineService.ResolveConnString()).Build();
        var inner = new NpgsqlSubstrateWriter(ds);
        var writer = new ConsensusAccumulatingWriter(
            inner, ds, foldWorkers: 1, freshSource: false, persistEvidence: true, stageAsWalks: false);
        var reader = new NpgsqlSubstrateReader(ds);

        var names = new HashSet<string>();
        names.UnionWith(await ChessVocabulary.BootstrapAsync(
            writer, ChessVocabulary.PgnSourceId, "ChessPgn", ChessVocabulary.PgnTrustClass, ct));
        names.UnionWith(await ChessVocabulary.BootstrapAsync(
            writer, ChessVocabulary.AnalysisSourceId, "ChessAnalysis", ChessVocabulary.AnalysisTrustClass, ct));
        await RegisterCanonicalsAsync(ds, names, ct);

        return new ChessPgnIngestor(ds, writer, reader);
    }

    public async Task<Result> IngestFileAsync(
        string pgnPath, Action<string>? log = null, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            int parsed = 0, novel = 0, applied = 0;
            var chunk = new List<ChessGameRecord>(ChunkSize);

            foreach (var gameText in PgnGames.StreamGames(pgnPath))
            {
                ct.ThrowIfCancellationRequested();
                if (ChessPgnDecomposer.TryParseGame(gameText) is not { } game) continue;
                parsed++;
                chunk.Add(game);
                if (chunk.Count < ChunkSize) continue;
                (int n, int a) = await ApplyChunkAsync(chunk, ct);
                novel += n; applied += a;
                chunk.Clear();
            }
            if (chunk.Count > 0)
            {
                (int n, int a) = await ApplyChunkAsync(chunk, ct);
                novel += n; applied += a;
            }

            await _writer.FoldIncrementalAsync(ct);
            log?.Invoke($"ingested {applied}/{parsed} games from {Path.GetFileName(pgnPath)}"
                        + (parsed > novel ? $" ({parsed - novel} already present)" : ""));
            return new Result(parsed, novel, applied);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<(int Novel, int Applied)> ApplyChunkAsync(
        List<ChessGameRecord> chunk, CancellationToken ct)
    {
        // Batch the whole chunk into two builders — witnessed layer (ChessPgn) and calculated
        // layer (ChessAnalysis, derived from the in-memory parse) — and apply each ONCE.
        // Per-game round-trips against a bulk writer are the wrong point for that algorithm.
        var record = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "chess/lab/ingest");
        var analyze = new SubstrateChangeBuilder(ChessVocabulary.AnalysisSourceId, "chess/lab/ingest");
        int novel = 0;
        await foreach (var game in ChessPgnDecomposer.FilterNovelAsync(chunk, _reader, ct))
        {
            novel++;
            ChessPgnDecomposer.RecordGame(game, record);
            ChessAnalyze.DeriveFromParsed(analyze, game);
        }
        if (novel == 0) return (0, 0);

        await _writer.ApplyAsync(await record.BuildAsync(ct), ct);
        await _writer.ApplyAsync(await analyze.BuildAsync(ct), ct);
        return (novel, novel);
    }

    private static async Task RegisterCanonicalsAsync(
        NpgsqlDataSource ds, IReadOnlyCollection<string> names, CancellationToken ct)
    {
        if (names.Count == 0) return;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT laplace.register_canonicals(@names)";
        cmd.Parameters.Add(new NpgsqlParameter
        {
            ParameterName = "names",
            Value = names.ToArray(),
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
        });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        await _ds.DisposeAsync();
    }
}
