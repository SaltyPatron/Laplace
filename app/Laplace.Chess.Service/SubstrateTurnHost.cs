using global::Npgsql;
using NpgsqlTypes;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Bridges the generic <see cref="ModalityEngine{TState,TAction}"/> to the Laplace substrate:
/// <list type="bullet">
/// <item><see cref="IContentAddresser"/> — composes a position's canonical surface to its content id
///   (<c>ContentEmitter.RootId</c>); the engine never mints ids itself.</item>
/// <item><see cref="IEdgeRatings"/> — reads <c>eff_mu</c> of candidate MOVE edges from
///   <c>laplace.consensus</c>.</item>
/// <item><see cref="ITurnLearner"/> — composes the visited positions as content entities, writes one
///   <c>MOVE</c> attestation per ply whose <i>score is the game result</i>, then folds the touched
///   consensus edges in place (online, no drain).</item>
/// </list>
/// </summary>
public sealed class SubstrateTurnHost : IContentAddresser, IEdgeRatings, IStateValuer, ITurnLearner
{
    private readonly NpgsqlDataSource _ds;
    private readonly ConsensusAccumulatingWriter _writer;
    private readonly ISubstrateReader _reader;
    private readonly double _witnessWeight;
    private readonly SubstrateStateValuer _valuer;

    public SubstrateTurnHost(
        NpgsqlDataSource ds, ConsensusAccumulatingWriter writer, ISubstrateReader reader,
        double witnessWeight)
    {
        _ds = ds ?? throw new ArgumentNullException(nameof(ds));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _witnessWeight = witnessWeight;
        _valuer = new SubstrateStateValuer(ds);
    }

    // IContentAddresser: a position id is the Merkle root of composing its bounded SUBSTRUCTURE tokens
    // (ChessCompose) — NOT the text-decomposition of the whole surface string (that was the lookup
    // table). PositionId computes the id without staging (rating lookups); LearnGameAsync /
    // ChessPgnDecomposer stage the same substructures via ChessGraph, yielding the identical root.
    public Hash128 Address(string canonicalSurface)
        => ChessCompose.PositionId(canonicalSurface);

    public async Task<double[]> EffMuAsync(IReadOnlyList<Hash128> edgeIds, CancellationToken ct = default)
    {
        var raw = new byte[edgeIds.Count][];
        for (int i = 0; i < edgeIds.Count; i++) raw[i] = edgeIds[i].ToBytes();

        var map = new Dictionary<Hash128, (double Mu, double W)>(edgeIds.Count);
        await using (var conn = await _ds.OpenConnectionAsync(ct))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id, (rating - 2*rd)::double precision, witness_count::double precision " +
                "FROM laplace.consensus WHERE id = ANY($1)";
            cmd.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                Value = raw,
            });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                map[Hash128.FromBytes((byte[])r[0])] = (r.GetDouble(1), r.GetDouble(2));
        }

        var outv = new double[edgeIds.Count];
        for (int i = 0; i < edgeIds.Count; i++)
            outv[i] = map.TryGetValue(edgeIds[i], out var v) ? Shrink(v.Mu, v.W) : GlickoPriors.UnratedEffMu;
        return outv;
    }

    // Empirical-Bayes confidence shrinkage: pull a move's eff_mu toward neutral μ by its evidence, so a
    // high rating from few games (a rare master win, esp. with rd deflated by the Elo→game-count trick)
    // cannot outrank a converged value from many games. Measured: without this, greedy selection opens
    // a2a4 (1.8k games); with it, the bot opens g1f3/d2d4/e2e4. K0 = prior-equivalent sample size.
    private const double ShrinkK0 = 15000d;
    private static double Shrink(double effMu, double witness)
        => GlickoPriors.NeutralMu + (effMu - GlickoPriors.NeutralMu) * (witness / (witness + ShrinkK0));

    // IStateValuer: the substructure-fold (folds OUTCOME consensus over a position's substructures + the
    // position itself, generalizing to novel positions). Shared with the search root-bias via
    // SubstrateStateValuer — one implementation, one SQL.
    public Task<double[]> ValueStatesAsync(
        IReadOnlyList<string> stateSurfaces, CancellationToken ct = default)
        => _valuer.ValueStatesAsync(stateSurfaces, ct);

    // A CHECKMATE is earned chess testimony — its moves get more observation weight; a ply-cap ADJUDICATION
    // (the self-play analog of flagging) is NOT a win, so its moves are credited as a neutral draw, never as a
    // win. So self-play learns from decisive play, not from games that merely timed/ran out.
    private const long CheckmateGames = 3;

    /// <summary>ITurnLearner entry — adjudication state unknown, so treat conservatively as a real game.</summary>
    public Task LearnGameAsync(IReadOnlyList<RecordedEdge> edges, CancellationToken ct = default)
        => LearnGameAsync(edges, adjudicated: false, ct);

    /// <summary>Learn a finished self-play game, weighting by HOW it ended: a real checkmate up-weights the
    /// winner's moves (earned win); an adjudicated/flagged game credits every move as a draw (no real result).</summary>
    public async Task LearnGameAsync(
        IReadOnlyList<RecordedEdge> edges, bool adjudicated, CancellationToken ct = default)
    {
        if (edges.Count == 0) return;

        var b = new SubstrateChangeBuilder(ChessVocabulary.SourceId, "chess/selfplay/game")
            .EnableDeferredContent(_reader);

        // The self-play mover is the substrate itself — every move is PLAYED_BY the Laplace player, under
        // the self-play source (low trust: high-temp exploration, not authoritative play).
        ChessVocabulary.EmitPlayer(b, ChessVocabulary.LaplacePlayerId, "Laplace", ChessVocabulary.SourceId);

        // Decisive checkmate (someone actually won, not a cutoff) ⇒ up-weight; flagged/adjudicated ⇒ draw-only.
        bool hasWin = false;
        foreach (var e in edges) if (e.MoverOutcome == PlyOutcome.Win) { hasWin = true; break; }
        bool checkmate = !adjudicated && hasWin;
        long games = checkmate ? CheckmateGames : 1;

        foreach (var e in edges)
        {
            var moverOutcome = adjudicated ? PlyOutcome.Draw : e.MoverOutcome;  // flagging ≠ winning
            ChessGraph.AppendMoveEdge(b, e.SubjectKey, e.ObjectKey, moverOutcome, games, _witnessWeight,
                sourceId: ChessVocabulary.SourceId, moverPlayerId: ChessVocabulary.LaplacePlayerId);
        }

        var change = await b.BuildAsync(ct);
        await _writer.ApplyAsync(change, ct);
        await _writer.FoldIncrementalAsync(ct);
    }
}
