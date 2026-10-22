using System.Globalization;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Npgsql;

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

    // Values pass through the child environment, never the argument vector or a
    // shell. libpq's quoted connection values escape backslashes and apostrophes.
    internal static string NativeConnectionInfo(string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database))
            throw new ArgumentException("The substrate host and database must be declared.", nameof(connectionString));
        static string Pair(string name, string value) => name + "='"
            + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
        var fields = new List<string>
        {
            Pair("host", settings.Host),
            Pair("port", settings.Port.ToString(CultureInfo.InvariantCulture)),
            Pair("dbname", settings.Database),
            Pair("connect_timeout", settings.Timeout.ToString(CultureInfo.InvariantCulture)),
            Pair("sslmode", settings.SslMode switch
            {
                SslMode.Disable => "disable", SslMode.Allow => "allow", SslMode.Prefer => "prefer",
                SslMode.Require => "require", SslMode.VerifyCA => "verify-ca", SslMode.VerifyFull => "verify-full",
                _ => throw new ArgumentOutOfRangeException(nameof(connectionString)),
            }),
        };
        if (!string.IsNullOrEmpty(settings.Username)) fields.Add(Pair("user", settings.Username));
        if (!string.IsNullOrEmpty(settings.Password)) fields.Add(Pair("password", settings.Password));
        if (!string.IsNullOrEmpty(settings.RootCertificate)) fields.Add(Pair("sslrootcert", settings.RootCertificate));
        if (!string.IsNullOrEmpty(settings.SslCertificate)) fields.Add(Pair("sslcert", settings.SslCertificate));
        if (!string.IsNullOrEmpty(settings.SslKey)) fields.Add(Pair("sslkey", settings.SslKey));
        if (!string.IsNullOrEmpty(settings.Options)) fields.Add(Pair("options", settings.Options));
        return string.Join(" ", fields);
    }
}
