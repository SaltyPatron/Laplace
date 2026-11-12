using System.Text;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Chess.Service;

/// <summary>
/// The opening book's rows (the Lichess openings TSV: ECO, name, movetext) as the search's book reads them.
/// The substrate's openings are the Engine's (recipes/openings); this is the local book a search consults.
/// </summary>
internal static class OpeningRows
{
    internal static List<string> ExtractSans(string movetext)
    {
        // TSV supplies a movetext fragment. The fixed header/unknown termination frame it for the PGN grammar.
        var tail = movetext.AsSpan().TrimEnd();
        bool terminated = tail.EndsWith("*", StringComparison.Ordinal)
            || tail.EndsWith("1-0", StringComparison.Ordinal)
            || tail.EndsWith("0-1", StringComparison.Ordinal)
            || tail.EndsWith("1/2-1/2", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes("[Event \"Opening fragment\"]\n\n" + movetext + (terminated ? "\n" : "\n*"));
        using var ast = GrammarDecomposer.Parse(bytes, "pgn");
        if (!ast.Diagnostics.SyntaxComplete)
            throw new InvalidDataException($"Opening movetext has incomplete native PGN syntax (errors={ast.Diagnostics.ErrorNodeCount}, missing={ast.Diagnostics.MissingNodeCount}).");
        return PgnMovetext.Extract(ast, bytes).Moves;
    }

    internal static (string Eco, string Name, string Movetext)? ParseRow(string line, string? path = null, int lineNumber = 0)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var cols = line.Split('\t');
        if (cols.Length < 3)
            throw new InvalidDataException($"Opening TSV {path ?? "input"}:{lineNumber} requires ECO, name and movetext columns.");
        string eco = cols[0].Trim(), name = cols[1].Trim(), movetext = cols[2].Trim();
        if (string.Equals(eco, "eco", StringComparison.OrdinalIgnoreCase)) return null;
        if (eco.Length == 0 || movetext.Length == 0)
            throw new InvalidDataException($"Opening TSV {path ?? "input"}:{lineNumber} has an empty ECO or movetext field.");
        return (eco, name, movetext);
    }
}
