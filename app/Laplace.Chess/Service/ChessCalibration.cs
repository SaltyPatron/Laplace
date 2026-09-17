using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

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
            var manifest = JsonSerializer.Deserialize<DesktopManifest>(
                await ReadBoundedAsync(manifestPath, 1 << 20, ct), DesktopJson);
            var selection = manifest?.Calibration;
            if (selection?.ReportSha256 is not { } hash)
                return new("not-selected", "No measured calibration has been selected.");
            if (hash.Length != 64
                || hash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                return new("invalid", "The selected calibration identifier is invalid.");
            // The hash selects a fixed installed location; no caller or manifest path is opened.
            var reportPath = Path.Combine(directory, "chess-calibrations", hash, "report.json");
            byte[] raw = await ReadBoundedAsync(reportPath, 32 << 20, ct);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(raw)), hash, StringComparison.Ordinal))
                return new("invalid", "The saved report does not match its selected checksum.", hash);
            var report = JsonSerializer.Deserialize<CalibrationReport>(raw, ReportJson);
            if (report?.Schema != "laplace.benchmark.chess-environment/v1")
                return new("unsupported", "This calibration report uses an unsupported format.", hash);
            bool? engineMatches = null;
            if (report.StockfishIdentity?.Sha256 is { } expected)
            {
                engineMatches = false;
                if (!string.IsNullOrWhiteSpace(stockfish) && File.Exists(stockfish))
                {
                    await using var executable = File.OpenRead(stockfish);
                    engineMatches = string.Equals(Convert.ToHexStringLower(
                        await SHA256.HashDataAsync(executable, ct)), expected, StringComparison.Ordinal);
                }
            }
            return new("available", ReportSha256: hash, SelectionStatus: selection.Status,
                CurrentEngineMatches: engineMatches,
                Report: JsonSerializer.SerializeToElement(report, ReportJson));
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

    // These are the application-owned measurement envelopes emitted by
    // benchmark-chess-environment.py and the desktop installer.
    // Like ChessExperimentEvidence, this boundary reads typed receipt fields;
    // source chess containers continue through their registered grammar.
    // Omitted members (process logs, environment, runtime observations) are never
    // deserialized into the browser projection, including inside nested records.
    private static readonly JsonSerializerOptions DesktopJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record DesktopManifest(CalibrationSelection? Calibration);
    private sealed record CalibrationSelection(string? Status, string? ReportSha256);

    private sealed record CalibrationReport(string? Schema, string? Status,
        string? StartedUtc, string? FinishedUtc, double? ElapsedWallSeconds,
        bool? EvidenceInvalid, MeasuredHost? Host, Parameters? Parameters,
        ResourcePlan? Plan, EngineIdentity? StockfishIdentity,
        Recommendations? Recommendations, BenchCase[]? StockfishBench,
        MatchCase[]? CutechessMatches);

    private sealed record MeasuredHost(string? Hostname, string[]? CpuModels,
        int? LogicalCpusReported, long? PhysicalMemoryBytes, double? EffectiveCpuCapacity);
    private sealed record Parameters(int? Repeats, int? BenchLimit, string? BenchLimitType,
        int? MatchDepth, int? MaxMoves, int? MatchThreads, int? MatchHashMb);
    private sealed record ResourcePlan(double? ReservedCpuCapacity, int? GamesPerMatchSample,
        int? MatchThreadsPerEngine, int? MatchHashMibPerEngine);
    private sealed record EngineIdentity(string? Sha256, string? SourceCommit,
        NetworkIdentity[]? SourceNetworks);
    private sealed record NetworkIdentity(string? Name, string? Path, bool? Present,
        long? SizeBytes, string? Sha256);

    private sealed record Distribution(int? Count, double? Median, double? Min,
        double? Max, double? Mean, double? Stdev, double? RelativeRange);
    private sealed record BenchCase(string? Status, int? Threads, int? HashMib,
        Distribution? SteadyEngineSeconds, Distribution? SteadyNodesPerSecond);
    private sealed record MatchCase(string? Status, int? Concurrency,
        Distribution? SteadyGamesPerSecond, Distribution? SteadyPliesPerSecond);

    private sealed record Recommendations(BenchLatency? BenchSuiteLatency,
        SearchThroughput? SearchNodeThroughput, TournamentThroughput? BoundedTournamentThroughput);
    private sealed record BenchLatency(int? Threads, int? HashMib, double? MedianSeconds,
        string? Scope);
    private sealed record SearchThroughput(int? Threads, int? HashMib,
        double? MedianNodesPerSecond, bool? ObservedRangeOverlapsAnotherConfiguration);
    private sealed record TournamentThroughput(int? Concurrency, int? ThreadsPerEngine,
        int? HashMibPerEngine, double? MedianPliesPerSecond,
        bool? ObservedRangeOverlapsAnotherConfiguration, string? Scope);
}
