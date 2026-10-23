using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Cli;

internal static partial class IngestCommands
{
    internal static async Task<int> IngestNativeRecipeAsync(string source, IngestCliArgs cli, CancellationToken ct = default)
    {
        if (cli.LangOverride is not null || cli.EmitCrossLanguageLinks is not null
            || cli.RegisterOnly || cli.Force || cli.NoAnalyze || cli.Recursive
            || cli.AnalyzeDepth != 0 || cli.AnalyzeNodes != 0 || cli.SecondPath.Length != 0
            || cli.GitCorpusSelection is not null || cli.GitCorpusReceipt is not null)
            throw new ArgumentException("Canonical source ingestion uses its native recipe. Select input paths or a recipe preview mode.");

        var execution = NativeRecipeExecution.Configured();
        string[] paths = string.IsNullOrWhiteSpace(cli.Path) ? [] : [cli.Path];
        var request = new NativeRecipeRequest(cli.NativeMode, source, paths,
            ConnectionInfo: NativeConnectionInfo(CliRuntime.ConnString), Threads: CpuTopology.ResolveCpuBoundWorkers());
        using var output = Console.OpenStandardOutput();
        using var error = Console.OpenStandardError();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void Cancel(object? sender, ConsoleCancelEventArgs args) { args.Cancel = true; cancellation.Cancel(); }
        Console.CancelKeyPress += Cancel;
        try { return await execution.ExecuteAsync(request, output, error, cancellation.Token).ConfigureAwait(false); }
        finally { Console.CancelKeyPress -= Cancel; }
    }

    internal static string NativeConnectionInfo(string connectionString)
        => NativeRecipeExecution.ConnectionInfoFromNpgsql(connectionString);
}
