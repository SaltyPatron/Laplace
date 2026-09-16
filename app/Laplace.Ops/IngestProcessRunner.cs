using System.Diagnostics;
using Laplace.Engine.Core;

namespace Laplace.Ops;

/// <summary>
/// Starts the canonical <c>Laplace.Cli ingest</c> lane without teaching every
/// operator surface a second source registry. The CLI remains the authority for
/// source names, defaults, argument validation and ingest semantics.
/// </summary>
public static class IngestProcessRunner
{
    public sealed record StartReceipt(
        int ProcessId,
        string Source,
        string? Path,
        string CliPath,
        IReadOnlyList<string> Arguments);

    public static StartReceipt Start(
        string source,
        string? path = null,
        IReadOnlyList<string>? extraArguments = null)
    {
        source = (source ?? string.Empty).Trim();
        path = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        if (source.Length == 0)
            throw new ArgumentException("Ingest source is required.", nameof(source));
        if (source.Any(char.IsControl))
            throw new ArgumentException("Ingest source contains control characters.", nameof(source));
        if (path is not null && !File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException($"Ingest path not found: {path}", path);

        var cliPath = ResolveCliBinary()
            ?? throw new FileNotFoundException(
                "Laplace.Cli binary not found. Set LAPLACE_CLI_BIN or install/build the canonical CLI.");

        var args = new List<string> { "ingest", source };
        if (path is not null) args.Add(path);
        if (extraArguments is not null)
        {
            foreach (var value in extraArguments)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (value.Any(char.IsControl))
                    throw new ArgumentException("An ingest argument contains control characters.", nameof(extraArguments));
                args.Add(value);
            }
        }

        var start = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var value in args) start.ArgumentList.Add(value);

        var process = Process.Start(start)
            ?? throw new InvalidOperationException("Laplace.Cli ingest process did not start.");
        try
        {
            return new StartReceipt(process.Id, source, path, cliPath, args);
        }
        finally
        {
            // The ingest owns its own database/run journal lifetime. Disposing the local
            // Process handle does not terminate the child; the operator follows progress
            // through the canonical ingest journal instead of holding an HTTP/MCP request.
            process.Dispose();
        }
    }

    public static string? ResolveCliBinary()
    {
        var fromEnv = Environment.GetEnvironmentVariable("LAPLACE_CLI_BIN");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return Path.GetFullPath(fromEnv);

        if (!LaplaceInstall.TryRepoRoot(out var root)) return null;
        var exeName = OperatingSystem.IsWindows() ? "Laplace.Cli.exe" : "Laplace.Cli";
        foreach (var candidate in Candidates(root, exeName))
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static IEnumerable<string> Candidates(string root, string exeName)
    {
        // ReadyToRun is the production ingest binary when present; normal build output
        // remains a development fallback. Keep discovery here so MCP/HTTP/other operator
        // clients cannot drift into different binaries.
        foreach (var config in new[] { "Release", "Debug" })
        {
            yield return Path.Combine(root, "app", "bin", "Laplace.Cli", config, "net10.0-r2r", exeName);
            yield return Path.Combine(root, "app", "Laplace.Cli", "bin", config, "net10.0", exeName);
        }
    }
}
