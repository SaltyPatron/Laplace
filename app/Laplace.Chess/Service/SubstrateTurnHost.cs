using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

public sealed class SubstrateTurnHost : IContentAddresser, IEdgeRatings, IStateValuer, ITurnLearner
{
    private readonly NpgsqlDataSource _ds;
    private readonly ConsensusAccumulatingWriter _writer;
    private readonly ISubstrateReader _reader;
    private readonly double _witnessWeight;
    private readonly string _learnContext;
    private readonly SubstrateStateValuer _valuer;

    public SubstrateTurnHost(
        NpgsqlDataSource ds, ConsensusAccumulatingWriter writer, ISubstrateReader reader,
        double witnessWeight, string learnContext = "chess/selfplay/game")
    {
        _ds = ds ?? throw new ArgumentNullException(nameof(ds));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _witnessWeight = witnessWeight;
        _learnContext = learnContext;
        _valuer = new SubstrateStateValuer(ds);
    }





    public Hash128 Address(string canonicalSurface)
        => ChessCompose.PositionId(canonicalSurface);

    public async Task<double[]> EffMuAsync(IReadOnlyList<Hash128> edgeIds, CancellationToken ct = default)
    {
        // Edge ids arrive from ModalityEngine.ScoreMovesAsync, which builds them
        // with ChessVocabulary.MoveType — thread that type so the partitioned
        // consensus scan prunes to the MOVE partition instead of Append-scanning
        // every relation type.
        var byId = await Laplace.SubstrateCRUD.Npgsql.NpgsqlConsensusByIds.ReadAsync(
            _ds, edgeIds, ChessVocabulary.MoveType, ct).ConfigureAwait(false);

        var outv = new double[edgeIds.Count];
        for (int i = 0; i < edgeIds.Count; i++)
            outv[i] = byId.TryGetValue(edgeIds[i], out var row)
                ? ChessShrink.Apply(row.EffMu, row.Witnesses)
                : GlickoPriors.UnratedEffMu;
        return outv;
    }









    public Task<double[]> ValueStatesAsync(
        IReadOnlyList<string> stateSurfaces, CancellationToken ct = default)
        => _valuer.ValueStatesAsync(stateSurfaces, ct);




    private const long CheckmateGames = 3;

    public Task LearnGameAsync(IReadOnlyList<RecordedEdge> edges, CancellationToken ct = default)
    => LearnGameAsync(edges, adjudicated: false, ct);

    public async Task LearnGameAsync(
    IReadOnlyList<RecordedEdge> edges, bool adjudicated, CancellationToken ct = default)
    {
        if (edges.Count == 0) return;

        var b = new SubstrateChangeBuilder(ChessVocabulary.SourceId, _learnContext);

        ChessVocabulary.EmitPlayer(
            b, ChessVocabulary.LaplacePlayerId, "Laplace", ChessVocabulary.SourceId, SourceTrust.Response);

        bool hasWin = false;
        foreach (var e in edges) if (e.MoverOutcome == PlyOutcome.Win) { hasWin = true; break; }
        bool checkmate = !adjudicated && hasWin;
        long games = checkmate ? CheckmateGames : 1;

        foreach (var e in edges)
        {
            var moverOutcome = adjudicated ? PlyOutcome.Draw : e.MoverOutcome;
            ChessGraph.AppendMoveEdge(b, e.SubjectKey, e.ObjectKey, moverOutcome, games, _witnessWeight,
                sourceId: ChessVocabulary.SourceId);
        }

        var change = await b.BuildAsync(ct);
        await _writer.ApplyAsync(change, ct);
    }

    Task ITurnLearner.RecordPlyAsync(
        Hash128 gameId, int ply, string fromKey, string toKey, string moveToken,
        Hash128? moverPlayerId, CancellationToken ct)
        => throw new NotSupportedException("Use ChessLiveGameHost for per-ply live recording.");

    Task ITurnLearner.CompleteGameAsync(
        Hash128 gameId, GameOutcome result, bool adjudicated, CancellationToken ct)
        => throw new NotSupportedException("Use ChessLiveGameHost for terminal live recording.");
}
