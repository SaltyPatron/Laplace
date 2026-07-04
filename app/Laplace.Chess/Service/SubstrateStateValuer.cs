using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;
using Laplace.Modality;

namespace Laplace.Chess.Service;

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
                if (!stats.TryGetValue(e, out var st)) continue;
                double dev = st.EffMu - GlickoPriors.NeutralMu;
                double conf = GlickoPriors.InitialRd / (GlickoPriors.InitialRd + st.Rd);
                double w = Math.Abs(dev) * conf * st.Witness;
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
            "SELECT id, eff_mu, rd, witness_count FROM laplace.consensus_by_ids($1)";
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
