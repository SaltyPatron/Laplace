using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>
/// Chess API, CLI and corpus jobs use the same installed settings. The API receives
/// laplace-api.env through its service environment; direct CLI processes read that
/// file when the caller has not supplied an explicit environment override.
/// </summary>
internal static class ChessRuntimeConfiguration
{
    internal static string? InstallPrefix
        => Environment.GetEnvironmentVariable("LAPLACE_INSTALL_PREFIX") is { Length: > 0 } prefix
            ? Path.GetFullPath(prefix.Trim()) : OperatingSystem.IsWindows() ? null : "/opt/laplace";

    internal static string? Read(string key)
    {
        var environment = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(environment)) return environment.Trim();
        // A caller selecting another source checkout must not inherit the old
        // deployed binary path. An explicit executable still wins above.
        if (key == "LAPLACE_STOCKFISH"
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LAPLACE_STOCKFISH_SOURCE")))
            return null;

        if (InstallPrefix is { } prefix)
        {
            // First file with a value wins; the last assignment within that file
            // wins, matching the service's EnvironmentFile assignment semantics.
            foreach (var path in new[]
                     {
                         Path.Combine(prefix, "app", "laplace-api.env"),
                         Path.Combine(prefix, "app", "chess-lab.env"),
                         Path.Combine(prefix, "chess-lab.env"),
                         Path.Combine(prefix, "secrets", "chess-lab.env"),
                     })
            {
                var value = ReadFile(path, key);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        return LaplaceInstall.TryReadDeploySecret("chess-lab.env", key);
    }

    private static string? ReadFile(string path, string key)
    {
        if (!File.Exists(path)) return null;
        string? selected = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.TrimStart().StartsWith('#')) continue;
            int separator = line.IndexOf('=');
            if (separator <= 0 || !string.Equals(line[..separator].Trim(), key, StringComparison.Ordinal)) continue;
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"') value = value[1..^1];
            selected = value;
        }
        return selected;
    }
}
