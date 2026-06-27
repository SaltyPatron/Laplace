using System.Text.Json;
using global::Npgsql;
using Laplace.Cli.Provenance;
using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli;

// laplace document <recipe.json> [out-dir]
//
// Extracts the provenance record for a synthesized model and writes it to out-dir/provenance.json.
// The JSON is the CANONICAL SOURCE MATERIAL — every other output format (markdown, html, pdf, csv)
// is a pure renderer over this file. Renderers never touch the substrate. If a fact is not in the
// record, it cannot be rendered; that rule forces this extractor to be complete.
//
// Recipe path also determines the model dir (the containing directory, i.e. where config.json lives).
// Out-dir defaults to <model-dir>/laplace-provenance/.
internal static class DocumentCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
            return Fail(
                "usage: laplace document <recipe.json> [out-dir]\n"
                + "  recipe.json   the config.json / recipe file (same path used with 'synthesize substrate')\n"
                + "  out-dir       where to write provenance.json (default: <recipe-dir>/laplace-provenance/)");

        string recipePath = Path.GetFullPath(args[0]);
        if (!File.Exists(recipePath))
            return Fail($"recipe not found: {recipePath}");

        string defaultOut = Path.Combine(Path.GetDirectoryName(recipePath) ?? ".", "laplace-provenance");
        string outDir = args.Length > 1 ? Path.GetFullPath(args[1]) : defaultOut;
        Directory.CreateDirectory(outDir);

        Console.WriteLine($"document: extracting provenance");
        Console.WriteLine($"  recipe: {recipePath}");
        Console.WriteLine($"  output: {outDir}");

        await using var ds = NpgsqlDataSource.Create(ConnString);
        var record = await ProvenanceExtractor.ExtractAsync(ds, recipePath);

        string outPath = Path.Combine(outDir, "provenance.json");
        var opts = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(record, opts);
        await File.WriteAllTextAsync(outPath, json);

        PrintSummary(record, outPath);
        return 0;
    }

    private static void PrintSummary(Provenance.ProvenanceRecord r, string outPath)
    {
        Console.WriteLine($"  → {outPath}");
        Console.WriteLine($"  model:       {r.Identity.Name}");
        Console.WriteLine($"  recipe hash: {r.Identity.RecipeHash[..16]}…");
        Console.WriteLine($"  layers:      {r.Identity.Config.GetValueOrDefault("num_hidden_layers", "?")}  "
            + $"heads: {r.Identity.Config.GetValueOrDefault("num_attention_heads", "?")}  "
            + $"vocab: {r.Identity.Config.GetValueOrDefault("vocab_size", "?")}");

        Console.WriteLine($"  sources:     {r.Sources.Count}");
        foreach (var s in r.Sources)
            Console.WriteLine($"    [{s.Kind}] {s.Label ?? s.Domain}"
                + (s.EntityCount.HasValue ? $" ({s.EntityCount:N0} entities)" : ""));

        Console.WriteLine($"  tensors:     {r.Tensors.Count}");

        int resolved = r.Circuits.Count(c => c.EncodesRelation != null);
        Console.WriteLine($"  circuits:    {r.Circuits.Count}  resolved: {resolved}"
            + $" ({100.0 * resolved / Math.Max(1, r.Circuits.Count):F0}%)");
        if (resolved > 0)
        {
            var topRelations = r.Circuits
                .Where(c => c.EncodesRelation != null)
                .GroupBy(c => c.EncodesRelation!)
                .OrderByDescending(g => g.Count())
                .Take(5);
            foreach (var g in topRelations)
                Console.WriteLine($"    {g.Key}: {g.Count()} circuit(s)");
        }
    }
}
