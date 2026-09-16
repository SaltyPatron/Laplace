using System.Globalization;
using System.Text.Json;
using Laplace.Chess.Service;
using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli;

internal static class ChessCorpusCommands
{
    internal const string Usage =
        "usage: laplace chess measure-corpus --pgn /absolute/games.pgn --evidence-root /absolute/new-evidence-directory\n"
        + "  [--games 75000] [--minimum-seconds 30] [--replays 1] [--deadline-seconds 3600]\n"
        + "  [--expected-sha256 lowercase-64-character-SHA256]\n"
        + "  Measures complete authentic PGNs through ordinary recording, readback, and replay.\n"
        + "  Input must be a plain .pgn file; the runtime validates source bytes and the new evidence directory.";

    internal static ChessCorpusBenchmark.Options ParseArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            string flag = args[i];
            if (flag is not ("--pgn" or "--evidence-root" or "--games" or "--minimum-seconds"
                or "--replays" or "--deadline-seconds" or "--expected-sha256"))
                throw new ArgumentException($"Unknown measure-corpus argument '{flag}'.");
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])
                || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"measure-corpus {flag} requires a value.");
            if (!values.TryAdd(flag, args[i + 1]))
                throw new ArgumentException($"measure-corpus {flag} may be specified only once.");
        }

        string pgnPath = AbsolutePath("--pgn");
        string evidenceDirectory = AbsolutePath("--evidence-root");
        if (!string.Equals(Path.GetExtension(pgnPath), ".pgn", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("measure-corpus requires a plain .pgn file; compressed input is not supported by this measurement.");
        if (string.Equals(pgnPath, evidenceDirectory,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("measure-corpus input and evidence directory must be different paths.");

        int games = Integer("--games", 75_000, 1, 1_000_000);
        int replays = Integer("--replays", 1, 1, 4);
        int deadline = Integer("--deadline-seconds", 3600, 30, 86_400);
        double minimum = 30;
        if (values.TryGetValue("--minimum-seconds", out string? minimumText)
            && (!double.TryParse(minimumText, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out minimum)
                || !double.IsFinite(minimum) || minimum is < 30 or > 3600))
            throw new ArgumentException("measure-corpus --minimum-seconds must be finite and between 30 and 3600.");
        if (minimum > deadline)
            throw new ArgumentException("measure-corpus --minimum-seconds cannot exceed --deadline-seconds.");

        string? expectedSha256 = values.GetValueOrDefault("--expected-sha256");
        if (expectedSha256 is not null
            && (expectedSha256.Length != 64 || expectedSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new ArgumentException("measure-corpus --expected-sha256 must contain exactly 64 lowercase hexadecimal characters.");

        return new ChessCorpusBenchmark.Options(pgnPath, evidenceDirectory, games, minimum, replays, deadline, expectedSha256);

        string AbsolutePath(string flag)
        {
            if (!values.TryGetValue(flag, out string? value) || !Path.IsPathFullyQualified(value))
                throw new ArgumentException($"measure-corpus {flag} requires an explicit absolute path.");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }

        int Integer(string flag, int fallback, int minimumValue, int maximumValue)
        {
            if (!values.TryGetValue(flag, out string? value)) return fallback;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                || parsed < minimumValue || parsed > maximumValue)
                throw new ArgumentException($"measure-corpus {flag} must be an integer between {minimumValue} and {maximumValue}.");
            return parsed;
        }
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        ChessCorpusBenchmark.Options options;
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
            var result = await ChessCorpusBenchmark.RunAsync(options, cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return result.Completed ? 0 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }
}
