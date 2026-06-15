using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Model;
using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli;

/// <summary>
/// No-DB benchmark commands: <c>svd-exact-bench</c> (prove tensor_svd_truncate is fp-exact on a real
/// tensor) and <c>model-bench</c> (run the whole-model FFN/relation ETL on a real model or hub).
/// </summary>
internal static class BenchCommands
{
    public static int SvdExactBenchCmd(string[] rest)
    {
        string modelDir = rest.Length > 0 && !string.IsNullOrEmpty(rest[0])
            ? rest[0]
            : ResolveTinyLlamaDir();
        string? tensor = rest.Length > 1 ? rest[1] : null;

        if (string.IsNullOrEmpty(modelDir) || !Directory.Exists(modelDir))
            return Fail("usage: laplace svd-exact-bench [model-dir] [tensor]\n" +
                        "  set $LAPLACE_TINYLLAMA_DIR or pass a model dir; none resolved.");

        bool pass = SvdExactBench.Run(modelDir, tensor);
        return pass ? 0 : 1;
    }

    // Benches one model dir, or every model in a hub root. With no arg, walks the default hub
    // ($LAPLACE_MODEL_HUB or D:\Models\hub) and benches each models--*/snapshots/* that has weights.
    public static async Task<int> ModelBenchCmd(string[] rest)
    {
        var log = ConsoleLoggerProvider.Factory().CreateLogger("model-bench");
        var arg = rest.Length > 0 ? rest[0] : null;

        List<string> models;
        if (!string.IsNullOrEmpty(arg) && IsModelDir(arg))
            models = new() { arg! };
        else
        {
            string hub = !string.IsNullOrEmpty(arg) ? arg!
                : Environment.GetEnvironmentVariable("LAPLACE_MODEL_HUB")
                  ?? (Directory.Exists(@"D:\Models\hub") ? @"D:\Models\hub" : "");
            if (string.IsNullOrEmpty(hub) || !Directory.Exists(hub))
                return Fail("usage: laplace model-bench [model-dir | hub-root]\n" +
                            "  pass a model dir, a hub root, or set $LAPLACE_MODEL_HUB; none resolved.");
            models = EnumerateHubModels(hub).ToList();
            if (models.Count == 0)
                return Fail($"model-bench: no models with weights found under {hub}");
            Console.WriteLine($"model-bench: {models.Count} model(s) under {hub}");
            Console.WriteLine();
        }

        bool allOk = true;
        var failures = new List<string>();
        foreach (var dir in models)
        {
            Console.WriteLine(new string('=', 78));
            bool ok;
            try { ok = await ModelFfnBench.RunAsync(dir, log); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"model-bench: {dir} threw: {ex.Message}");
                ok = false;
            }
            if (!ok) failures.Add(dir);
            allOk &= ok;
            Console.WriteLine();
        }

        if (models.Count > 1)
        {
            Console.WriteLine(new string('=', 78));
            Console.WriteLine($"model-bench: {models.Count - failures.Count}/{models.Count} passed");
            foreach (var f in failures) Console.WriteLine($"  FAIL {f}");
        }
        return allOk ? 0 : 1;
    }

    private static bool IsModelDir(string dir) => SafetensorSnapshotWitness.IsComplete(dir);

    // HF cache layout: <hub>/models--ORG--NAME/snapshots/<rev>/, weights resolved via blob symlinks.
    private static IEnumerable<string> EnumerateHubModels(string hub)
    {
        // direct: hub root is itself a model
        if (IsModelDir(hub)) { yield return hub; yield break; }

        foreach (var fam in Directory.GetDirectories(hub, "models--*").OrderBy(f => f, StringComparer.Ordinal))
        {
            var snapsDir = Path.Combine(fam, "snapshots");
            if (!Directory.Exists(snapsDir)) continue;
            // newest snapshot that actually carries weights
            var snap = Directory.GetDirectories(snapsDir)
                                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                                .FirstOrDefault(IsModelDir);
            if (snap != null) yield return snap;
        }

        // also allow plain (non-HF) model dirs sitting directly under the hub
        foreach (var d in Directory.GetDirectories(hub).OrderBy(f => f, StringComparer.Ordinal))
            if (!Path.GetFileName(d).StartsWith("models--", StringComparison.Ordinal) && IsModelDir(d))
                yield return d;
    }

    private static string ResolveTinyLlamaDir()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_TINYLLAMA_DIR");
        if (!string.IsNullOrEmpty(env)) return env;

        const string root = "/vault/models";
        if (!Directory.Exists(root)) return "";
        var families = Directory.GetDirectories(root, "models--TinyLlama--*");
        foreach (var fam in families.OrderBy(f => f, StringComparer.Ordinal))
        {
            var snapsDir = Path.Combine(fam, "snapshots");
            if (!Directory.Exists(snapsDir)) continue;
            foreach (var snap in Directory.GetDirectories(snapsDir)
                                          .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                if (SafetensorSnapshotWitness.IsComplete(snap)) return snap;
            }
        }
        return "";
    }
}
