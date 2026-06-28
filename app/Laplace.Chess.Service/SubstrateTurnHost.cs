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

    public SubstrateTurnHost(
        NpgsqlDataSource ds, ConsensusAccumulatingWriter writer, ISubstrateReader reader,
        double witnessWeight)
    {
        _ds = ds ?? throw new ArgumentNullException(nameof(ds));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _witnessWeight = witnessWeight;
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

    // IStateValuer: value each state by folding the OUTCOME consensus over its substructures (+ the
    // position itself), weighted by predictiveness |eff_mu−neutral|·conf(rd)·witness. A seen position's
    // own OUTCOME node carries high witness so it dominates (the exact case); a novel position relies on
    // the substructures it shares with seen ones (the generalization). Side-to-move-relative, since
    // OUTCOME is credited to the side to move where the substructure occurred.
    public async Task<double[]> ValueStatesAsync(
        IReadOnlyList<string> stateSurfaces, CancellationToken ct = default)
    {
        int n = stateSurfaces.Count;
        var result = new double[n];
        if (n == 0) return result;

        // Compose each state; collect the distinct OUTCOME edge ids (substructures + position).
        var perState = new Hash128[n][];
        var distinct = new HashSet<Hash128>();
        for (int i = 0; i < n; i++)
        {
            var c = ChessCompose.Position(stateSurfaces[i]);
            var ids = new Hash128[c.Substructures.Count + 1];
            for (int j = 0; j < c.Substructures.Count; j++)
            {
                var e = ConsensusKeys.EdgeId(
                    c.Substructures[j].Id, ChessVocabulary.OutcomeType, ChessVocabulary.OutcomeObject);
                ids[j] = e; distinct.Add(e);
            }
            var pe = ConsensusKeys.EdgeId(
                c.Position.Id, ChessVocabulary.OutcomeType, ChessVocabulary.OutcomeObject);
            ids[^1] = pe; distinct.Add(pe);
            perState[i] = ids;
        }

        var stats = await ReadOutcomeStatsAsync(distinct, ct).ConfigureAwait(false);

        for (int i = 0; i < n; i++)
        {
            double wsum = 0d, acc = 0d;
            foreach (var e in perState[i])
            {
                if (!stats.TryGetValue(e, out var st)) continue;       // unrated → no contribution
                double dev  = st.EffMu - GlickoPriors.NeutralMu;        // signed: above/below draw
                double conf = GlickoPriors.InitialRd / (GlickoPriors.InitialRd + st.Rd); // →1 as rd→0
                // NOTE: weighting by |dev|·conf alone (dropping ×witness) was MEASURED to regress play —
                // it sharpens the fold toward spurious CORRELATIONS (e.g. Ph3 occurs in won games), so the
                // bot confidently picks weak moves (g1f3→h2h3 on a novel position). The marginal OUTCOME is
                // correlational, not causal; ×witness keeps the fold near-neutral so selection defers to
                // the MOVE-edge/tiebreak (sound). Real fix is interaction effects (P4), not re-weighting.
                double w    = Math.Abs(dev) * conf * st.Witness;
                if (w <= 0d) continue;
                wsum += w; acc += w * dev;
            }
            result[i] = wsum > 0d ? GlickoPriors.NeutralMu + acc / wsum : GlickoPriors.NeutralMu;
        }
        return result;
    }

    private readonly record struct OutcomeStat(double EffMu, double Rd, double Witness);

    private async Task<Dictionary<Hash128, OutcomeStat>> ReadOutcomeStatsAsync(
        IReadOnlyCollection<Hash128> ids, CancellationToken ct)
    {
        var raw = new byte[ids.Count][];
        int k = 0; foreach (var id in ids) raw[k++] = id.ToBytes();

        var map = new Dictionary<Hash128, OutcomeStat>(ids.Count);
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, (rating - 2*rd)::double precision, rd::double precision, " +
            "witness_count::double precision FROM laplace.consensus WHERE id = ANY($1)";
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            Value = raw,
        });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            map[Hash128.FromBytes((byte[])r[0])] = new OutcomeStat(r.GetDouble(1), r.GetDouble(2), r.GetDouble(3));
        return map;
    }

    public async Task LearnGameAsync(IReadOnlyList<RecordedEdge> edges, CancellationToken ct = default)
    {
        if (edges.Count == 0) return;

        var b = new SubstrateChangeBuilder(ChessVocabulary.SourceId, "chess/selfplay/game")
            .EnableDeferredContent(_reader);

        // The self-play mover is the substrate itself — every move is PLAYED_BY the Laplace player, under
        // the self-play source (low trust: high-temp exploration, not authoritative play).
        ChessVocabulary.EmitPlayer(b, ChessVocabulary.LaplacePlayerId, "Laplace", ChessVocabulary.SourceId);

        foreach (var e in edges)
            ChessGraph.AppendMoveEdge(b, e.SubjectKey, e.ObjectKey, e.MoverOutcome, games: 1, _witnessWeight,
                sourceId: ChessVocabulary.SourceId, moverPlayerId: ChessVocabulary.LaplacePlayerId);

        var change = await b.BuildAsync(ct);
        await _writer.ApplyAsync(change, ct);
        await _writer.FoldIncrementalAsync(ct);
    }
}
