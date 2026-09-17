using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;

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
            using var manifest = JsonAstDocument.TryParse(
                await ReadBoundedAsync(manifestPath, 1 << 20, ct));
            if (manifest is null || !manifest.SyntaxComplete)
                throw new InvalidDataException("Installed calibration selection is not complete JSON.");
            var selection = manifest.Root.Property("calibration");
            if (!selection.IsObject)
                return new("not-selected", "No measured calibration has been selected.");
            var hashValue = selection.Property("reportSha256");
            if (!hashValue.IsValid)
                return new("not-selected", "No measured calibration has been selected.");
            string? hash = hashValue.AsString();
            if (hash is null || hash.Length != 64
                || hash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                return new("invalid", "The selected calibration identifier is invalid.");
            // The hash selects a fixed installed location; no caller or manifest path is opened.
            var reportPath = Path.Combine(directory, "chess-calibrations", hash, "report.json");
            byte[] raw = await ReadBoundedAsync(reportPath, 32 << 20, ct);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(raw)), hash, StringComparison.Ordinal))
                return new("invalid", "The saved report does not match its selected checksum.", hash);
            using var document = JsonAstDocument.TryParse(raw);
            if (document is null || !document.SyntaxComplete)
                throw new InvalidDataException("Installed calibration report is not complete JSON.");
            var report = document.Root;
            if (report.String("schema") != "laplace.benchmark.chess-environment/v1")
                return new("unsupported", "This calibration report uses an unsupported format.", hash);
            string? selectionStatus = selection.String("status");
            bool? engineMatches = null;
            var identity = report.Property("stockfish_identity");
            if (identity.IsObject && identity.String("sha256") is { } expected)
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
            visible["host"] = Pick(report.Property("host"), "hostname", "cpu_models",
                "logical_cpus_reported", "physical_memory_bytes", "effective_cpu_capacity");
            visible["parameters"] = Pick(report.Property("parameters"), "repeats", "bench_limit",
                "bench_limit_type", "match_depth", "max_moves", "match_threads", "match_hash_mb");
            visible["plan"] = Pick(report.Property("plan"), "reserved_cpu_capacity",
                "games_per_match_sample", "match_threads_per_engine", "match_hash_mib_per_engine");
            visible["stockfish_identity"] = Pick(identity,
                "sha256", "source_commit", "source_networks");
            visible["recommendations"] = Pick(report.Property("recommendations"),
                "bench_suite_latency", "search_node_throughput", "bounded_tournament_throughput");
            visible["stockfish_bench"] = Rows(report, "stockfish_bench", "status", "threads",
                "hash_mib", "steady_engine_seconds", "steady_nodes_per_second");
            visible["cutechess_matches"] = Rows(report, "cutechess_matches", "status", "concurrency",
                "steady_games_per_second", "steady_plies_per_second");
            return new("available", ReportSha256: hash, SelectionStatus: selectionStatus,
                CurrentEngineMatches: engineMatches, Report: JsonSerializer.SerializeToElement(visible));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
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

    private static Dictionary<string, object?> Pick(JsonAstCursor value, params string[] keys)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!value.IsObject) return result;
        foreach (string key in keys)
        {
            var field = value.Property(key);
            if (field.IsValid) result[key] = Materialize(field);
        }
        return result;
    }

    private static object[] Rows(JsonAstCursor value, string key, params string[] fields)
    {
        var rows = value.Property(key);
        return rows.IsArray
            ? rows.Items().Select(row => (object)Pick(row, fields)).ToArray()
            : [];
    }

    private static object? Materialize(JsonAstCursor value) => value.Kind switch
    {
        JsonAstKind.Object => value.Pairs().ToDictionary(
            pair => pair.Key, pair => Materialize(pair.Value), StringComparer.Ordinal),
        JsonAstKind.Array => value.Items().Select(Materialize).ToArray(),
        JsonAstKind.String => value.AsString(),
        JsonAstKind.Number => MaterializeNumber(value),
        JsonAstKind.True => true,
        JsonAstKind.False => false,
        JsonAstKind.Null => null,
        _ => throw new InvalidDataException("Calibration JSON contains an unsupported recovered node."),
    };

    private static object MaterializeNumber(JsonAstCursor value)
    {
        string raw = value.RawText()
            ?? throw new InvalidDataException("Calibration JSON number has no source text.");
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
            return integer;
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal exact))
            return exact;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double approximate)
            && double.IsFinite(approximate))
            return approximate;
        throw new InvalidDataException("Calibration JSON number cannot be represented safely.");
    }
}
