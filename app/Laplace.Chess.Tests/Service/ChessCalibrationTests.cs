using System.Security.Cryptography;
using System.Text.Json;
using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessCalibrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chess-calibration-" + Guid.NewGuid().ToString("N"));
    private string _report = "";
    private string _engine = "";

    private string Install(string selection = "selected")
    {
        Directory.CreateDirectory(_root);
        _engine = Path.Combine(_root, "stockfish");
        File.WriteAllText(_engine, "measured engine");
        string engineHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_engine)));
        byte[] report = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "laplace.benchmark.chess-environment/v1", status = "complete",
            stockfish_identity = new { sha256 = engineHash, source_commit = "measured-commit" },
            parameters = new { repeats = 3, max_moves = 0, bench_limit = 12, bench_limit_type = "depth" },
            host = new { hostname = "measured-host", cpu_models = new[] { "measured-cpu" } },
            stockfish_bench = new[] { new { status = "complete", threads = 8, hash_mib = 16,
                steady_engine_seconds = new { median = 1.6 } } },
            cutechess_matches = new[] { new { status = "complete", concurrency = 10,
                steady_games_per_second = new { median = 5.7 }, process = new { log = "private-process-log" } } },
            runtime_capabilities = new { private_test_value = "must-not-be-exposed" }
        });
        string hash = Convert.ToHexStringLower(SHA256.HashData(report));
        string directory = Path.Combine(_root, "share", "laplace");
        _report = Path.Combine(directory, "chess-calibrations", hash, "report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_report)!);
        File.WriteAllBytes(_report, report);
        File.WriteAllText(Path.Combine(directory, "cutechess-desktop.json"), JsonSerializer.Serialize(new
        {
            calibration = new { status = selection, reportSha256 = hash, report = "/untrusted-path-is-ignored" }
        }));
        return hash;
    }

    [Fact]
    public async Task SelectedReportIsAuthenticatedAndProjectedWithoutRuntimeLogs()
    {
        string hash = Install();
        var result = await ChessCalibration.ReadInstalledAsync(_root, _engine);
        Assert.Equal("available", result.Status);
        Assert.Equal(hash, result.ReportSha256);
        Assert.True(result.CurrentEngineMatches);
        var report = Assert.IsType<JsonElement>(result.Report);
        Assert.Equal(1.6, report.GetProperty("stockfish_bench")[0]
            .GetProperty("steady_engine_seconds").GetProperty("median").GetDouble());
        Assert.False(report.TryGetProperty("runtime_capabilities", out _));
        Assert.False(report.GetProperty("cutechess_matches")[0].TryGetProperty("process", out _));
        Assert.False(report.TryGetProperty("recorded_games_per_second", out _));
    }

    [Fact]
    public async Task ModifiedReportIsNotPresentedAsMeasuredEvidence()
    {
        Install();
        File.AppendAllText(_report, " ");
        var result = await ChessCalibration.ReadInstalledAsync(_root, _engine);
        Assert.Equal("invalid", result.Status);
        Assert.Null(result.Report);
    }

    [Fact]
    public async Task ChangedEngineRetainsHistoryButReportsMismatch()
    {
        Install();
        File.WriteAllText(_engine, "different engine");
        var result = await ChessCalibration.ReadInstalledAsync(_root, _engine);
        Assert.Equal("available", result.Status);
        Assert.False(result.CurrentEngineMatches);
        Assert.NotNull(result.Report);
    }

    [Fact]
    public async Task StaleInstallerSelectionIsPreserved()
    {
        Install("stale");
        var result = await ChessCalibration.ReadInstalledAsync(_root, _engine);
        Assert.Equal("stale", result.SelectionStatus);
        Assert.Equal("available", result.Status);
    }

    [Fact]
    public async Task MissingCalibrationHasExplicitUnmeasuredState()
    {
        var result = await ChessCalibration.ReadInstalledAsync(_root, null);
        Assert.Equal("not-selected", result.Status);
        Assert.Null(result.Report);
        Assert.Null(result.CurrentEngineMatches);
    }

    [Fact]
    public async Task MalformedSelectedIdentityCannotSelectAnotherFile()
    {
        Install();
        File.WriteAllText(Path.Combine(_root, "share", "laplace", "cutechess-desktop.json"),
            "{\"calibration\":{\"reportSha256\":\"../../elsewhere\"}}");
        var result = await ChessCalibration.ReadInstalledAsync(_root, _engine);
        Assert.Equal("invalid", result.Status);
        Assert.Null(result.Report);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
