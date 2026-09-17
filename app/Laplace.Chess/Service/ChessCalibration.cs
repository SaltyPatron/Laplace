using System.Security.Cryptography;
using System.Text.Json;

namespace Laplace.Chess.Service;

/// <summary>Read the measured calibration selected by the ordinary desktop installer.</summary>
public static class ChessCalibration
{
    public sealed record Snapshot(string Status, string? Message = null,
        string? ReportSha256 = null, string? SelectionStatus = null,
        bool? CurrentEngineMatches = null, JsonElement? Report = null);

    public static Task<Snapshot> ReadAsync(CancellationToken ct = default)
        => ReadInstalledAsync(ChessRuntimeConfiguration.InstallPrefix, ChessLabPaths.Stockfish.Path, ct);

    internal static async Task<Snapshot> ReadInstalledAsync(
        string? prefix, string? stockfish, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return new("not-selected", "No installed calibration has been selected.");
        var directory = Path.Combine(prefix, "share", "laplace");
        var manifestPath = Path.Combine(directory, "cutechess-desktop.json");
        if (!File.Exists(manifestPath))
            return new("not-selected", "No installed calibration has been selected.");
        try
        {
            using var manifest = JsonDocument.Parse(await ReadBoundedAsync(manifestPath, 1 << 20, ct));
            if (!manifest.RootElement.TryGetProperty("calibration", out var selection)
                || !selection.TryGetProperty("reportSha256", out var hashValue))
                return new("not-selected", "No measured calibration has been selected.");
            string? hash = hashValue.GetString();
            if (hash is null || hash.Length != 64
                || hash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                return new("invalid", "The selected calibration identifier is invalid.");
            // The hash selects a fixed installed location; no caller or manifest path is opened.
            var reportPath = Path.Combine(directory, "chess-calibrations", hash, "report.json");
            byte[] raw = await ReadBoundedAsync(reportPath, 32 << 20, ct);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(raw)), hash, StringComparison.Ordinal))
                return new("invalid", "The saved report does not match its selected checksum.", hash);
            using var document = JsonDocument.Parse(raw);
            var report = document.RootElement;
            if (Text(report, "schema") != "laplace.benchmark.chess-environment/v1")
                return new("unsupported", "This calibration report uses an unsupported format.", hash);
            string? selectionStatus = Text(selection, "status");
            bool? engineMatches = null;
            if (report.TryGetProperty("stockfish_identity", out var identity)
                && Text(identity, "sha256") is { } expected)
            {
                engineMatches = false;
                if (!string.IsNullOrWhiteSpace(stockfish) && File.Exists(stockfish))
                {
                    await using var executable = File.OpenRead(stockfish);
                    engineMatches = string.Equals(Convert.ToHexStringLower(
                        await SHA256.HashDataAsync(executable, ct)), expected, StringComparison.Ordinal);
                }
            }
            // Project documented measurement fields only. Process logs, environment and
            // unrelated runtime observations are never part of this browser response.
            var visible = Pick(report, "schema", "status", "started_utc", "finished_utc",
                "elapsed_wall_seconds", "evidence_invalid");
            visible["host"] = Pick(Property(report, "host"), "hostname", "cpu_models",
                "logical_cpus_reported", "physical_memory_bytes", "effective_cpu_capacity");
            visible["parameters"] = Pick(Property(report, "parameters"), "repeats", "bench_limit",
                "bench_limit_type", "match_depth", "max_moves", "match_threads", "match_hash_mb");
            visible["plan"] = Pick(Property(report, "plan"), "reserved_cpu_capacity",
                "games_per_match_sample", "match_threads_per_engine", "match_hash_mib_per_engine");
            visible["stockfish_identity"] = Pick(Property(report, "stockfish_identity"),
                "sha256", "source_commit", "source_networks");
            visible["recommendations"] = Pick(Property(report, "recommendations"),
                "bench_suite_latency", "search_node_throughput", "bounded_tournament_throughput");
            visible["stockfish_bench"] = Rows(report, "stockfish_bench", "status", "threads",
                "hash_mib", "steady_engine_seconds", "steady_nodes_per_second");
            visible["cutechess_matches"] = Rows(report, "cutechess_matches", "status", "concurrency",
                "steady_games_per_second", "steady_plies_per_second");
            return new("available", ReportSha256: hash, SelectionStatus: selectionStatus,
                CurrentEngineMatches: engineMatches, Report: JsonSerializer.SerializeToElement(visible));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or ArgumentException)
        {
            return new("unavailable", "The installed calibration report could not be read.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int limit, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        if (input.Length > limit) throw new IOException("Calibration evidence exceeds its size limit.");
        using var result = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (result.Length + count > limit) throw new IOException("Calibration evidence grew beyond its size limit.");
            await result.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return result.ToArray();
    }

    private static string? Text(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field)
            && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static JsonElement Property(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? field : default;

    private static Dictionary<string, object?> Pick(JsonElement value, params string[] keys)
        => keys.Where(key => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out _))
            .ToDictionary(key => key, key => (object?)value.GetProperty(key).Clone(), StringComparer.Ordinal);

    private static object[] Rows(JsonElement value, string key, params string[] fields)
        => Property(value, key) is { ValueKind: JsonValueKind.Array } rows
            ? rows.EnumerateArray().Select(row => (object)Pick(row, fields)).ToArray() : [];
}
