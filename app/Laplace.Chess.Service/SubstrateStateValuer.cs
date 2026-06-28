using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;
using Laplace.Modality;

namespace Laplace.Chess.Service;

/// <summary>
/// The read-only substructure-fold: values a chess position by folding the <c>OUTCOME</c> consensus over
/// its composed substructures (<see cref="ChessCompose.Position"/>) <b>plus the position itself</b>,
/// weighted by predictiveness <c>|eff_mu−neutral|·conf(rd)·witness</c>. A seen position's own OUTCOME node
/// carries high witness so it dominates (the exact case); a novel position relies on the substructures it
/// shares with seen ones (the generalization) — so this is the read that lets learned knowledge transfer
/// to positions never in the corpus. Side-to-move-relative (OUTCOME is credited to the side to move where
/// the substructure occurred).
///
/// <para>Extracted from <see cref="SubstrateTurnHost"/> so the self-play engine and the search root-bias
/// (<see cref="SubstructureFoldBias"/>) share ONE implementation and one SQL. No write path — just the
/// data source. The native compose loop is serialized under <see cref="ChessCompose.Gate"/>; the DB read
/// runs unlocked, so parallel callers still overlap their queries.</para>
/// </summary>
public sealed class SubstrateStateValuer : IStateValuer
{
    private readonly NpgsqlDataSource _ds;

    public SubstrateStateValuer(NpgsqlDataSource ds)
        => _ds = ds ?? throw new ArgumentNullException(nameof(ds));

    public async Task<double[]> ValueStatesAsync(
        IReadOnlyList<string> stateSurfaces, CancellationToken ct = default)
    {
        int n = stateSurfaces.Count;
        var result = new double[n];
        if (n == 0) return result;

        // Compose each state; collect the distinct OUTCOME edge ids (substructures + position). The native
        // compose primitive is not thread-safe — serialize the whole compose pass; it is fast + memoized.
        var perState = new Hash128[n][];
        var distinct = new HashSet<Hash128>();
        lock (ChessCompose.Gate)
        {
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
                // the MOVE-edge/tiebreak (sound). Real fix is interaction effects, not re-weighting.
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
}
