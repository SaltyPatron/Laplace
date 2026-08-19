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




    public Task LearnGameAsync(IReadOnlyList<RecordedEdge> edges, CancellationToken ct = default)
    => LearnGameAsync(edges, adjudicated: false, ct);

    public async Task LearnGameAsync(
    IReadOnlyList<RecordedEdge> edges, bool adjudicated, CancellationToken ct = default)
    {
        if (edges.Count == 0) return;

        var b = new SubstrateChangeBuilder(ChessVocabulary.SourceId, _learnContext);

        ChessVocabulary.EmitPlayer(
            b, ChessVocabulary.LaplacePlayerId, "Laplace", ChessVocabulary.SourceId, SourceTrust.Response);

        var line = new List<ChessNode>(edges.Count + 1);
        foreach (var e in edges)
        {
            var from = ChessGraph.EmitComposed(b, e.SubjectKey, ChessVocabulary.SourceId);
            var to = ChessGraph.EmitComposed(b, e.ObjectKey, ChessVocabulary.SourceId);
            if (line.Count == 0) line.Add(from.Position);
            line.Add(to.Position);
        }

        var lineId = ChessCompose.LineId(line.Select(static n => n.Id).ToArray());
        b.AddEntity(lineId, EntityTier.Document, ChessVocabulary.GameType, ChessVocabulary.SourceId);
        PlyOutcome whiteOutcome = WhiteOutcome(edges, adjudicated);
        string resultToken = whiteOutcome switch
        {
            PlyOutcome.Win => "1-0",
            PlyOutcome.Loss => "0-1",
            _ => "1/2-1/2",
        };
        var playingId = ChessVocabulary.LivePlayingId(
            null, null, _learnContext, lineId, resultToken);
        b.AddEntity(
            playingId, EntityTier.Document, ChessVocabulary.PlayingType,
            ChessVocabulary.SourceId);
        b.AddAttestation(NativeAttestation.Aggregated(
            subject: playingId,
            typeId: ChessVocabulary.PlaysLineType,
            obj: lineId,
            sourceId: ChessVocabulary.SourceId,
            contextId: null,
            games: 1,
            sumScoreFp1e9: ChessGraph.ScoreFp1e9(whiteOutcome),
            witnessWeight: _witnessWeight));
        ChessGraph.AppendLineOutcome(
            b, lineId, whiteOutcome, _witnessWeight, ChessVocabulary.SourceId, playingId);
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        ChessGraph.AppendGameTrajectory(
            b, lineId, line, ChessVocabulary.TrajectorySourceId, nowUs);
        b.AddEntity(
            ChessTrajectoryDecomposer.MarkerId(lineId), EntityTier.Document,
            ChessVocabulary.AnalysisMarkerType, ChessVocabulary.TrajectorySourceId);

        var change = await b.BuildAsync(ct);
        await _writer.ApplyAsync(change, ct);
    }

    private static PlyOutcome WhiteOutcome(
        IReadOnlyList<RecordedEdge> edges, bool adjudicated)
    {
        if (adjudicated) return PlyOutcome.Draw;
        for (int i = 0; i < edges.Count; i++)
        {
            if (edges[i].MoverOutcome == PlyOutcome.Draw) continue;
            bool whiteMoved = (i & 1) == 0;
            return edges[i].MoverOutcome == PlyOutcome.Win
                ? (whiteMoved ? PlyOutcome.Win : PlyOutcome.Loss)
                : (whiteMoved ? PlyOutcome.Loss : PlyOutcome.Win);
        }
        return PlyOutcome.Draw;
    }

    Task ITurnLearner.RecordPlyAsync(
        Hash128 gameId, int ply, string fromKey, string toKey, string moveToken,
        Hash128? moverPlayerId, CancellationToken ct)
        => throw new NotSupportedException("Use ChessLiveGameHost for per-ply live recording.");

    Task ITurnLearner.CompleteGameAsync(
        Hash128 gameId, GameOutcome result, bool adjudicated, CancellationToken ct)
        => throw new NotSupportedException("Use ChessLiveGameHost for terminal live recording.");
}
