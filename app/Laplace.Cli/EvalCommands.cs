using Laplace.SubstrateCRUD.Npgsql;
using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli;

internal static class EvalCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] == "generation")
            return await GenerationAsync(args);
        if (args.Length == 0 || args[0] != "ingest-fidelity")
            return Fail("usage: laplace eval ingest-fidelity [relation] [ground-truth] [n] | laplace eval generation [prompt] [steps] [seeds-csv]");

        string relation = args.Length > 1 ? args[1] : "SIMILAR_TO";
        string gt = args.Length > 2 ? args[2] : "IS_SYNONYM_OF";
        int n = args.Length > 3 && int.TryParse(args[3], out var v) && v > 0 ? v : 3000;

        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);

        var pos = (await NpgsqlSubstrateReads.IngestFidelityPositiveScoresAsync(ds, relation, gt, n)).ToList();
        var neg = (await NpgsqlSubstrateReads.IngestFidelityNegativeScoresAsync(ds, relation, gt, n)).ToList();

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

    // W5 seed-variance measurement through the installed surface
    // (generation.probe): both lanes over one prompt and a seed set,
    // one row per (lane, seed). Replay — the failure converse_compose's header
    // gates wiring on — shows up mechanically as distinct==1 for a lane.
    private static async Task<int> GenerationAsync(string[] args)
    {
        string prompt = args.Length > 1 ? args[1] : "dog";
        int steps = args.Length > 2 && int.TryParse(args[2], out var st) && st > 0 ? st : 30;
        long[] seeds;
        try
        {
            seeds = (args.Length > 3 ? args[3] : "7,991,12345")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(long.Parse).ToArray();
        }
        catch (FormatException)
        {
            return Fail("eval generation: seeds must be a comma-separated list of integers");
        }

        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        await using var conn = await ds.OpenConnectionAsync();
        var rows = await NpgsqlIngestOps.GenerationProbeAsync(conn, prompt, seeds, steps);

        var replies = new Dictionary<string, HashSet<string>>();
        foreach (var (lane, seed, reply) in rows)
        {
            if (!replies.TryGetValue(lane, out var set)) replies[lane] = set = new();
            set.Add(reply ?? "");
            Console.WriteLine($"  [{lane} seed={seed}] {reply}");
        }
        foreach (var (lane, set) in replies)
            Console.WriteLine($"  {lane}: distinct={set.Count}/{seeds.Length}"
                + (set.Count == 1 && seeds.Length > 1 ? "  REPLAY" : ""));
        return 0;
    }

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
