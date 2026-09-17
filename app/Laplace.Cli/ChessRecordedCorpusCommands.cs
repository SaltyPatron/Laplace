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
