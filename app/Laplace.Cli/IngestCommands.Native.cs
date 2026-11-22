using Laplace.Data;
using Laplace.Engine.Core;

namespace Laplace.Cli;

internal static partial class IngestCommands
{
    public static async Task<int> StatsAsync(string? source)
    {
        await using var data = LaplaceDataSource.Create(SubstrateAccess.Serving, CliRuntime.ConnString);
        await using var conn = await data.OpenConnectionAsync();
        var counts = await NpgsqlSubstrateReads.SubstrateCountsAsync(conn, CancellationToken.None);
        Console.WriteLine("substrate totals:");
        foreach (var row in counts)
            Console.WriteLine($"  {row.Metric,-32}: {row.Value?.ToString("N0") ?? "Not measured",12}");
        var sources = await NpgsqlSubstrateReads.SourceStatusAsync(data, source, CancellationToken.None);
        if (source is not null && sources.All(row => !row.Known))
            return CliRuntime.Fail($"No registered source or witness matches '{source}'.");
        foreach (var row in sources)
            Console.WriteLine($"  {row.Source}: {row.EvidenceApprox?.ToString("N0") ?? "Not measured"} attestations; ingested={row.Ingested.ToString().ToLowerInvariant()}");
        return 0;
    }

    internal static Task<int> IngestNativeRecipeAsync(string source, IngestCliArgs cli, CancellationToken ct = default)
        => RunNativeIngestAsync(source, cli, ct);

    internal static Task<int> IngestNativeFilesAsync(string lane, IngestCliArgs cli, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cli.Path))
            return Task.FromResult(CliRuntime.Fail($"usage: laplace ingest {lane} <file-or-directory>"));
        if (!File.Exists(cli.Path) && !Directory.Exists(cli.Path))
            return Task.FromResult(CliRuntime.Fail($"ingest {lane}: path not found: {cli.Path}"));
        return RunNativeIngestAsync(null, cli, ct);
    }

    internal static Task<int> IngestSafetensorSnapshotAsync(string modelDir, IngestCliArgs cli, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(modelDir))
            return Task.FromResult(CliRuntime.Fail("usage: laplace ingest safetensors <snapshot-dir>"));
        if (!Directory.Exists(modelDir) && !File.Exists(modelDir))
            return Task.FromResult(CliRuntime.Fail($"ingest safetensors: path not found: {modelDir}"));
        return RunNativeIngestAsync(null, cli with { Path = Path.GetFullPath(modelDir) }, ct);
    }

    internal static Task<int> CorroborateSafetensorSnapshotsAsync(IngestCliArgs cli)
        => Task.FromResult(CliRuntime.Fail("Model corroboration runs via native `laplace model` / `laplace ingest`."));

    internal static Task<int> IngestRecipeAsync(IngestCliArgs cli, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(cli.Path))
            return Task.FromResult(CliRuntime.Fail("usage: laplace ingest recipe <recipe-path>"));
        if (!File.Exists(cli.Path))
            return Task.FromResult(CliRuntime.Fail($"ingest recipe: file not found: {cli.Path}"));
        return RunNativeIngestAsync(null, cli with { Path = Path.GetFullPath(cli.Path) }, ct);
    }

    internal static Task<int> IngestRepoAsync(IngestCliArgs cli, CancellationToken ct = default)
    {
        ValidateVerifiedGitArguments(cli);
        if (string.IsNullOrWhiteSpace(cli.Path))
            return Task.FromResult(CliRuntime.Fail("usage: laplace ingest repo <repository-root>"));
        return IngestNativeFilesAsync("repo", cli, ct);
    }

    internal static Task<int> IngestParquetAsync(IngestCliArgs cli, CancellationToken ct = default)
        => IngestNativeFilesAsync("parquet", cli, ct);

    private static Task<int> RunNativeIngestAsync(string? source, IngestCliArgs cli, CancellationToken ct)
    {
        if (cli.Langs is not null)
            throw new ArgumentException("Native source ingestion admits the languages declared by its recipe. Remove --langs or select a language-scoped recipe.");
        if (cli.EmitCrossLanguageLinks is not null
            || cli.RegisterOnly || cli.Force || cli.NoAnalyze || cli.Recursive
            || cli.AnalyzeDepth != 0 || cli.AnalyzeNodes != 0 || cli.SecondPath.Length != 0
            || cli.GitCorpusSelection is not null || cli.GitCorpusReceipt is not null)
            throw new ArgumentException("Native ingestion reads through the Engine's recipes. Select input paths or a recipe preview mode.");

        var argv = new List<string> { "ingest", "-r", NativeRecipeCatalog.RecipesRoot, "-t", LaplaceInstall.ResolveT0Perfcache() };
        int threads = CpuTopology.ResolveCpuBoundWorkers();
        if (threads > 0)
        {
            argv.Add("-j");
            argv.Add(threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        switch (cli.NativeMode)
        {
            case NativeRecipeMode.Plan: argv.Add("--plan"); break;
            case NativeRecipeMode.Claims: argv.Add("--claims-tsv"); break;
            case NativeRecipeMode.NoLoad: argv.Add("--no-load"); break;
        }
        if (source is not null && !string.IsNullOrWhiteSpace(cli.Path))
        {
            argv.Add("-s");
            argv.Add(source);
            argv.Add(Path.GetFullPath(cli.Path));
        }
        else if (!string.IsNullOrWhiteSpace(cli.Path))
        {
            argv.Add(Path.GetFullPath(cli.Path));
        }
        else if (source is not null)
        {
            argv.Add(source);
        }

        string? connInfo = cli.NativeMode == NativeRecipeMode.Ingest
            ? NativeRecipeCatalog.ConnectionInfoFromNpgsql(CliRuntime.ConnString)
            : null;
        return NativeRecipeCatalog.RunNativeAsync(argv, connInfo, cli.NativeExpandPrerequisites, ct);
    }
}
