using System.Globalization;
using System.Text.Json;
using Laplace.Chess.Service;
using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli;

internal static class ChessRecordedCorpusCommands
{
    internal const string Usage =
        "usage: laplace chess verify-recorded-corpus --selection-manifest /absolute/selection.json "
        + "--expected-sha256 <lowercase-sha256> --evidence-root /absolute/new-evidence-directory "
        + "[--deadline-seconds 3600]\n"
        + "  Verifies exactly the authenticated completed games and their zero-write replay; it does not qualify a throughput benchmark.";

    internal static ChessRecordedCorpusVerification.Options ParseArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            string flag = args[i];
            if (flag is not ("--selection-manifest" or "--expected-sha256" or "--evidence-root" or "--deadline-seconds")
                || i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])
                || args[i + 1].StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(flag, args[i + 1]))
                throw new ArgumentException("Unknown, duplicate, or incomplete verify-recorded-corpus option.");
        }
        string AbsolutePath(string key)
        {
            if (!values.TryGetValue(key, out string? value) || !Path.IsPathFullyQualified(value))
                throw new ArgumentException(key + " requires an explicit absolute path.");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        string manifest = AbsolutePath("--selection-manifest"), evidence = AbsolutePath("--evidence-root");
        if (string.Equals(manifest, evidence,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Selection manifest and evidence directory must differ.");
        string? digest = values.GetValueOrDefault("--expected-sha256");
        if (digest is null || digest.Length != 64
            || digest.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("--expected-sha256 requires exactly 64 lowercase hexadecimal characters.");
        int deadline = 3600;
        if (values.TryGetValue("--deadline-seconds", out string? seconds)
            && (!int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out deadline)
                || deadline is < 30 or > 86400))
            throw new ArgumentException("--deadline-seconds must be an integer between 30 and 86400.");
        return new ChessRecordedCorpusVerification.Options(manifest, digest, evidence, deadline);
    }

    internal const string ExportUsage =
        "usage: laplace chess export-recorded-pgn --selection-manifest /absolute/selection.json "
        + "--expected-sha256 <lowercase-sha256> --output-pgn /absolute/new-selected.pgn\n"
        + "  Exports the exact authenticated original game frames for ordinary admission.";

    internal sealed record ExportOptions(string ManifestPath, string ExpectedSha256, string OutputPath);

    internal static ExportOptions ParseExportArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var mapped = (string[])args.Clone();
        for (int i = 0; i < mapped.Length; i += 2)
        {
            if (mapped[i] is not ("--selection-manifest" or "--expected-sha256" or "--output-pgn"))
                throw new ArgumentException("Unknown export-recorded-pgn option.");
            if (mapped[i] == "--output-pgn") mapped[i] = "--evidence-root";
        }
        var parsed = ParseArguments(mapped);
        if (!parsed.EvidenceDirectory.EndsWith(".pgn", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--output-pgn requires a .pgn file.");
        return new(parsed.ManifestPath, parsed.ExpectedSha256, parsed.EvidenceDirectory);
    }

    internal static async Task<int> ExportAsync(string[] args)
    {
        ExportOptions options;
        try { options = ParseExportArguments(args); }
        catch (ArgumentException error) { return Fail(error.Message + "\n" + ExportUsage); }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var result = await ChessRecordedSelection.ExportPgnAsync(
                options.ManifestPath, options.ExpectedSha256, options.OutputPath, cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return 0;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        ChessRecordedCorpusVerification.Options options;
        try { options = ParseArguments(args); }
        catch (ArgumentException error) { return Fail(error.Message + "\n" + Usage); }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var result = await ChessRecordedCorpusVerification.RunAsync(options, cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return result.Completed ? 0 : 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
