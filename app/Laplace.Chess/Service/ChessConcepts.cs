using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>
/// One chess concept for a motif word. Lexical sense, opening trajectories, book
/// explanations, and board deltas are members of that concept. Domain membership
/// selects the chess sense; a display switch does not.
/// </summary>
public static class ChessConcepts
{
    public enum Domain
    {
        Lexical,
        Opening,
        Book,
        Board,
    }

    public readonly record struct Concept(
        string Key,
        string Name,
        string BoardTag,
        string LexicalSense)
    {
        public Hash128 Id => Hash128.OfCanonical("chess/concept/" + Key);

        public Hash128 Member(Domain domain) =>
            Hash128.OfCanonical("chess/concept/" + Key + "/" + domain.ToString().ToLowerInvariant());

        public bool MatchesOpening(string? openingName) =>
            openingName is not null
            && openingName.Contains(Name, StringComparison.OrdinalIgnoreCase);

        public bool MatchesBook(string? text) =>
            !string.IsNullOrWhiteSpace(text)
            && text.Contains(Name, StringComparison.OrdinalIgnoreCase);

        public bool MatchesBoard(string? motif)
        {
            if (string.IsNullOrWhiteSpace(motif)) return false;
            return TryResolve(motif, chessDomain: true, out var hit) && hit.Id == Id;
        }
    }

    private static readonly Concept[] Catalog =
    [
        new("gambit", "Gambit", "gambit", "A pawn offered in the opening so a line of play opens."),
        new("fork", "Fork", "fork", "One piece attacks two or more enemy pieces at once."),
        new("pin", "Pin", "pin", "A piece cannot move without exposing a more valuable piece behind it."),
        new("skewer", "Skewer", "skewer", "An attack through a valuable piece onto a piece behind it."),
        new("discovered attack", "Discovered attack", "discovered_check", "A moving piece uncovers an attack from the piece behind it."),
        new("sacrifice", "Sacrifice", "sacrifice", "Material given for a concrete line, not for its nominal value."),
        new("zugzwang", "Zugzwang", "zugzwang", "Every legal move weakens the side that has to move."),
        new("fianchetto", "Fianchetto", "fianchetto", "A bishop developed on the long diagonal behind a knight pawn."),
        new("outpost", "Outpost", "outpost", "A minor piece on a square the opponent's pawns cannot attack."),
        new("minority attack", "Minority attack", "minority_attack", "A pawn minority advances against a queenside pawn majority."),
        new("opposition", "Opposition", "opposition", "Kings stand so the side that has to move yields the key squares."),
    ];

    private static readonly Dictionary<string, Concept> BySurface = Build();

    public static IReadOnlyList<Concept> All => Catalog;

    public static bool TryResolve(string surface, bool chessDomain, out Concept concept)
    {
        concept = default;
        if (!chessDomain || string.IsNullOrWhiteSpace(surface)) return false;
        string key = surface.Trim().Replace('_', ' ').ToLowerInvariant();
        return BySurface.TryGetValue(key, out concept);
    }

    private static Dictionary<string, Concept> Build()
    {
        var map = new Dictionary<string, Concept>(StringComparer.Ordinal);
        foreach (var concept in Catalog)
        {
            map[concept.Key] = concept;
            map[concept.BoardTag.Replace('_', ' ')] = concept;
            map[concept.Name.ToLowerInvariant()] = concept;
        }
        return map;
    }
}
