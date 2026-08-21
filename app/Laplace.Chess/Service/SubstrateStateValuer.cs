using global::Npgsql;
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
                var ids = new Hash128[c.Substructures.Count];
                for (int j = 0; j < c.Substructures.Count; j++)
                {
                    var e = ConsensusKeys.EdgeId(
                        c.Substructures[j].Id, ChessVocabulary.OutcomeType, ChessVocabulary.OutcomeObject);
                    ids[j] = e; distinct.Add(e);
                }
                perState[i] = ids;
            }
        }

        // MEASURED 2026-08-21: this returns neutral for everything today -- zero consensus
        // rows and zero attestations carry a position-constituent subject. An earlier
        // rewrite of this comment claimed the promotion pass "has never existed in this
        // tree". That was FALSE and is retracted: ChessStockfishEval.DeriveGame IS the
        // pass -- it materializes each position (ChessGraph.EmitComposed) and deposits
        // HAS_EVAL / MOVE_QUALITY -- dispatched as ingest source "chess-eval"
        // (IngestDispatchTable) and by seed-chess-eval.yml. AppendEval and
        // AppendMoveQuality each have exactly one caller, both in that lane, and the
        // ingest_run_journal holds no ChessStockfish run, so the lane has simply never
        // been dispatched for this corpus. Neutral-until-census is the design working;
        // the original comment here was accurate all along.
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
        var byId = await Laplace.SubstrateCRUD.Npgsql.NpgsqlConsensusByIds.ReadAsync(
            _ds, ids, ChessVocabulary.OutcomeType, ct).ConfigureAwait(false);
        var map = new Dictionary<Hash128, OutcomeStat>(ids.Count);
        foreach (var (id, row) in byId)
            map[id] = new OutcomeStat(row.EffMu, row.Rd, row.Witnesses);
        return map;
    }
}
