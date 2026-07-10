using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Laplace.Chess.Service;

public static partial class CutechessRunner
{
    [GeneratedRegex(@"Score of .*?:\s*(\d+)\s*-\s*(\d+)\s*-\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ScoreRegex();

    [GeneratedRegex(@"Elo difference:\s*([+-]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex EloRegex();

    public static bool ProbeCatalog(out bool cutechessOk, out bool stockfishOk, out bool qtOk)
    {
        var catalog = ChessLabPaths.Catalog;
        cutechessOk = catalog["cutechess"].Found;
        stockfishOk = catalog["stockfish"].Found;
        qtOk = catalog["qt"].Found;
        return cutechessOk && stockfishOk && qtOk;
    }

    public static async IAsyncEnumerable<ChessLabEvent> RunAsync(
        int rounds, int depth, string pgnOut,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var catalog = ChessLabPaths.Catalog;
        var cc = catalog["cutechess"];
        var sf = catalog["stockfish"];
        var qt = catalog["qt"];
        var uci = catalog["laplaceUci"];

        if (!cc.Found || !sf.Found || !uci.Found)
        {
            yield return new ChessLabLogEvent("error",
                $"binaries missing: cutechess={cc.Found} stockfish={sf.Found} qt={qt.Found} laplaceUci={uci.Found}");
            yield return new ChessLabDoneEvent(ChessLabJobState.Failed, "missing binaries");
            yield break;
        }

        var psi = new ProcessStartInfo
        {
            FileName = cc.Path!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (qt.Found)
        {
            var prior = psi.Environment.TryGetValue("PATH", out var existing) ? existing
                : Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = string.IsNullOrEmpty(prior)
                ? qt.Path!
                : qt.Path! + Path.PathSeparator + prior;
        }

        psi.ArgumentList.Add("-engine");
        psi.ArgumentList.Add($"name=Laplace cmd={uci.Path}");
        psi.ArgumentList.Add("-engine");
        psi.ArgumentList.Add($"name=Stockfish cmd={sf.Path} arg=\"setoption name UCI_Elo value 2000\" arg=\"setoption name UCI_LimitStrength value true\"");
        psi.ArgumentList.Add("-each");
        psi.ArgumentList.Add($"tc=inf/depth={depth}");
        psi.ArgumentList.Add("-rounds");
        psi.ArgumentList.Add(rounds.ToString());
        psi.ArgumentList.Add("-pgnout");
        psi.ArgumentList.Add(pgnOut);

        yield return new ChessLabLogEvent("info", $"cutechess: {rounds} rounds depth {depth}");

        using var proc = Process.Start(psi)!;
        int done = 0;
        bool sawScore = false;
        await foreach (var line in ReadLinesAsync(proc, ct))
        {
            yield return new ChessLabLogEvent("info", line);
            var score = ScoreRegex().Match(line);
            if (score.Success)
            {
                sawScore = true;
                done = int.Parse(score.Groups[1].Value) + int.Parse(score.Groups[2].Value) + int.Parse(score.Groups[3].Value);
                yield return new ChessLabProgressEvent(done, rounds * 2);
            }
            var elo = EloRegex().Match(line);
            if (elo.Success && double.TryParse(elo.Groups[1].Value, out var eloVal))
                yield return new ChessLabMetricEvent("elo_diff", eloVal);
        }

        try { proc.Kill(entireProcessTree: true); } catch { }
        await proc.WaitForExitAsync(ct);
        // Exit code alone isn't sufficient: a 0 exit with zero parsed "Score of ..." lines means
        // cutechess-cli's output didn't match what we expect (version bump, localization, or a
        // run that produced no games) — that's a real failure, not a silent "Completed" with no
        // metrics.
        yield return proc.ExitCode == 0 && sawScore
            ? new ChessLabDoneEvent(ChessLabJobState.Completed)
            : new ChessLabDoneEvent(ChessLabJobState.Failed,
                proc.ExitCode == 0
                    ? "cutechess exited 0 but no \"Score of ...\" line was ever parsed from its output"
                    : $"cutechess exited with code {proc.ExitCode}");
    }

    internal static IEnumerable<ChessLabEvent> ParseLinesForTest(IEnumerable<string> lines)
    {
        int rounds = 10;
        foreach (var line in lines)
        {
            yield return new ChessLabLogEvent("info", line);
            var score = ScoreRegex().Match(line);
            if (score.Success)
            {
                int done = int.Parse(score.Groups[1].Value) + int.Parse(score.Groups[2].Value) + int.Parse(score.Groups[3].Value);
                yield return new ChessLabProgressEvent(done, rounds * 2);
            }
            var elo = EloRegex().Match(line);
            if (elo.Success && double.TryParse(elo.Groups[1].Value, out var eloVal))
                yield return new ChessLabMetricEvent("elo_diff", eloVal);
        }
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(Process proc, [EnumeratorCancellation] CancellationToken ct)
    {
        while (!proc.HasExited)
        {
            string? line = await proc.StandardOutput.ReadLineAsync(ct);
            if (line is not null) yield return line;
            else await Task.Delay(50, ct);
        }
        while (proc.StandardOutput.ReadLine() is { } rest) yield return rest;
    }
}
