using global::Npgsql;
using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli;













internal static class EvalCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "ingest-fidelity")
            return Fail("usage: laplace eval ingest-fidelity [relation] [ground-truth] [n]");

        string relation = args.Length > 1 ? args[1] : "SIMILAR_TO";
        string gt = args.Length > 2 ? args[2] : "IS_SYNONYM_OF";
        int n = args.Length > 3 && int.TryParse(args[3], out var v) && v > 0 ? v : 3000;

        await using var ds = NpgsqlDataSource.Create(ConnString);

        var pos = await ScoresAsync(ds, PositivesSql, relation, gt, n);
        var neg = await ScoresAsync(ds, NegativesSql, relation, gt, n);

        Console.WriteLine($"eval ingest-fidelity: relation={relation} ground-truth={gt} (two-hop synonym join)");
        if (pos.Count == 0)
        {
            Console.WriteLine($"  no positive pairs — is a model ingested? (consensus '{relation}' edges over "
                + "vocab that shares a seed sense). Ingest a model, then re-run.");
            return 0;
        }

        double auc = RocAuc(pos, neg);
        double pAtK = PrecisionAtK(pos, neg);
        Console.WriteLine($"  positives: n={pos.Count}  mean μ={Mean(pos):F4}  nonzero={pos.Count(x => x > 0)}");
        Console.WriteLine($"  negatives: n={neg.Count}  mean μ={Mean(neg):F4}  nonzero={neg.Count(x => x > 0)}");
        Console.WriteLine($"  ROC-AUC          = {auc:F4}   (0.5 = chance; higher = plane recovers '{gt}')");
        Console.WriteLine($"  precision@|P|    = {pAtK:F4}");
        return 0;
    }

    private static async Task<List<double>> ScoresAsync(
        NpgsqlDataSource ds, string sql, string relation, string gt, int n)
    {
        await using var cmd = ds.CreateCommand(sql);
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("rel", relation);
        cmd.Parameters.AddWithValue("gt", gt);
        cmd.Parameters.AddWithValue("n", n);
        var outv = new List<double>(n);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync()) outv.Add(rdr.GetDouble(0));
        return outv;
    }



    private const string PositivesSql = @"
        WITH vocab AS (
          SELECT DISTINCT subject_id AS id FROM laplace.consensus
          WHERE type_id = laplace.relation_type_id(@rel)
        ),
        syn AS (
          SELECT subject_id AS w, object_id AS sense FROM laplace.consensus
          WHERE type_id = laplace.relation_type_id(@gt)
            AND subject_id IN (SELECT id FROM vocab)
        ),
        pairs AS (
          SELECT DISTINCT a.w AS w1, b.w AS w2
          FROM syn a JOIN syn b ON a.sense = b.sense AND a.w < b.w
        )
        SELECT GREATEST(
          COALESCE(laplace.eff_mu_display(c1.rating, c1.rd), 0),
          COALESCE(laplace.eff_mu_display(c2.rating, c2.rd), 0))::float8
        FROM (SELECT w1, w2 FROM pairs ORDER BY random() LIMIT @n) p
        LEFT JOIN laplace.consensus c1 ON c1.id = laplace.consensus_id(p.w1, laplace.relation_type_id(@rel), p.w2)
        LEFT JOIN laplace.consensus c2 ON c2.id = laplace.consensus_id(p.w2, laplace.relation_type_id(@rel), p.w1)";


    private const string NegativesSql = @"
        WITH vocab AS (
          SELECT DISTINCT subject_id AS id FROM laplace.consensus
          WHERE type_id = laplace.relation_type_id(@rel)
        ),
        samp AS (SELECT id, row_number() OVER (ORDER BY random()) AS rn FROM vocab),
        cnt  AS (SELECT count(*) AS c FROM samp),
        neg  AS (
          SELECT a.id AS w1, b.id AS w2
          FROM samp a JOIN samp b ON b.rn = a.rn + (SELECT c/2 FROM cnt)
        )
        SELECT GREATEST(
          COALESCE(laplace.eff_mu_display(c1.rating, c1.rd), 0),
          COALESCE(laplace.eff_mu_display(c2.rating, c2.rd), 0))::float8
        FROM (SELECT w1, w2 FROM neg ORDER BY random() LIMIT @n) p
        LEFT JOIN laplace.consensus c1 ON c1.id = laplace.consensus_id(p.w1, laplace.relation_type_id(@rel), p.w2)
        LEFT JOIN laplace.consensus c2 ON c2.id = laplace.consensus_id(p.w2, laplace.relation_type_id(@rel), p.w1)";

    private static double Mean(List<double> xs) => xs.Count == 0 ? 0 : xs.Average();


    private static double RocAuc(List<double> pos, List<double> neg)
    {
        if (pos.Count == 0 || neg.Count == 0) return double.NaN;
        var all = new List<(double v, bool isPos)>(pos.Count + neg.Count);
        foreach (var v in pos) all.Add((v, true));
        foreach (var v in neg) all.Add((v, false));
        all.Sort((a, b) => a.v.CompareTo(b.v));

        double rankSumPos = 0; int i = 0; int n = all.Count;
        while (i < n)
        {
            int j = i;
            while (j < n && all[j].v == all[i].v) j++;
            double avgRank = (i + 1 + j) / 2.0;
            for (int k = i; k < j; k++) if (all[k].isPos) rankSumPos += avgRank;
            i = j;
        }
        double u = rankSumPos - pos.Count * (pos.Count + 1) / 2.0;
        return u / ((double)pos.Count * neg.Count);
    }


    private static double PrecisionAtK(List<double> pos, List<double> neg)
    {
        int k = pos.Count;
        if (k == 0) return double.NaN;
        var all = new List<(double v, bool isPos)>(pos.Count + neg.Count);
        foreach (var v in pos) all.Add((v, true));
        foreach (var v in neg) all.Add((v, false));
        all.Sort((a, b) => b.v.CompareTo(a.v));
        int hits = 0;
        for (int t = 0; t < k && t < all.Count; t++) if (all[t].isPos) hits++;
        return (double)hits / k;
    }
}
