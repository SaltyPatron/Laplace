using System.IO;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>
/// Seed FENs for the substrate "fair test" — drawn from the ALREADY-INGESTED Lichess ECO openings
/// (<c>openings/*.tsv</c>), the same rows <see cref="ChessOpeningsDecomposer"/> folds into the graph (no
/// hardcoded lines). Streams the TSVs, parses movetext with the <c>pgn</c> grammar
/// (<see cref="ChessOpeningsDecomposer.ExtractSans"/>), replays a bounded prefix through the perft-verified
/// movegen (<see cref="San.Resolve"/>), and returns the distinct book positions — so matches start exactly
/// where the substrate has data. A line with an unresolved token is skipped (not emitted as a bad FEN).
/// </summary>
public static class OpeningSeed
{
    /// <summary>Default openings directory (under <c>$INGEST\Games\Chess\openings</c>).</summary>
    public static string DefaultDir => Path.Combine(
        Environment.GetEnvironmentVariable("INGEST") ?? @"D:\Data\Ingest", "Games", "Chess", "openings");

    /// <summary>Distinct book FENs from the openings TSVs at <paramref name="path"/> (dir or single file),
    /// each replayed up to <paramref name="plies"/> half-moves; <paramref name="max"/> &gt; 0 caps the count.</summary>
    public static IReadOnlyList<string> Fens(string? path = null, int plies = 10, int max = 0)
    {
        path ??= DefaultDir;
        var m = new ChessModality();
        var seen = new HashSet<string>();
        var fens = new List<string>();
        foreach (var file in Files(path))
        foreach (var line in File.ReadLines(file))
        {
            if (ChessOpeningsDecomposer.ParseRow(line) is not { } row) continue;
            if (!TryReplay(m, ChessOpeningsDecomposer.ExtractSans(row.Movetext), plies, out var fen)) continue;
            if (seen.Add(fen)) fens.Add(fen);
            if (max > 0 && fens.Count >= max) return fens;
        }
        return fens;
    }

    private static bool TryReplay(ChessModality m, List<string> sans, int plies, out string fen)
    {
        var s = m.Initial();
        int n = 0;
        foreach (var san in sans)
        {
            if (n >= plies) break;
            var mv = San.Resolve(s.Board, m.LegalActions(s), san);
            if (mv is null) { fen = ""; return false; }      // malformed/illegal → drop the line
            s = m.Apply(s, mv.Value);
            n++;
        }
        fen = s.Board.ToFen();
        return n > 0;
    }

    private static IEnumerable<string> Files(string path)
    {
        if (File.Exists(path)) return new[] { path };
        if (Directory.Exists(path))
            return Directory.EnumerateFiles(path, "*.tsv", SearchOption.AllDirectories)
                            .OrderBy(p => p, StringComparer.Ordinal);
        return Array.Empty<string>();
    }
}
