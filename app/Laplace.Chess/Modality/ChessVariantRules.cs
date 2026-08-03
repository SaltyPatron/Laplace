using System.Text;

namespace Laplace.Modality.Chess;

/// <summary>
/// The RULE SET a game is played under, content-addressed like everything else here.
///
/// THE DISTINCTION THIS TYPE EXISTS TO MAKE. Two unrelated things get called "a chess
/// variant" and conflating them is what turns variant engines into a pile of flags:
///
///   a different STARTING ARRANGEMENT, same rules — Chess960, Double Fischer Random, odds
///   games, any study position. The FEN already carries it. Nothing here changes, which is
///   exactly why Chess960 support was additive and needed no reseed.
///
///   different RULES — King of the Hill (new win condition), Three-check, Antichess
///   (inverted objective), Atomic (capture side effects), Crazyhouse (drops add state),
///   Capablanca/Grand (board and pieces). THIS is what a variant descriptor is for.
///
/// IDENTITY. Rules belong in a position's identity; the starting arrangement does not.
/// A King-of-the-Hill position and a standard position with identical placement have
/// different legal futures and different values — one id would let the fold mix two games.
/// But a Chess960 middlegame that transposes into a position reachable from the standard
/// array IS that position and must collide. So the surface carries the rules and never the
/// start.
///
/// <see cref="Standard"/> contributes NOTHING to the surface. Every position id already in
/// the substrate was minted under standard rules, so the default must be identity-neutral
/// or the entire corpus moves. Same discipline as Board.CastleString emitting KQkq.
///
/// NOT AN ENUM, AND THAT IS THE POINT. A closed list of variants is a list somebody has to
/// keep, and it is wrong the moment a source ships something not on it. These are RULE
/// AXES; a variant is a point in that space, its id is the hash of that point, and two
/// sources describing the same rules collide on the same id without anyone registering
/// anything. A rule set nobody has named is still a rule set, and it still gets an id.
/// </summary>
public sealed record ChessVariantRules
{
    /// <summary>Files and ranks. 8x8 for the chess family; Capablanca is 10x8, Grand 10x10.</summary>
    public int Files { get; init; } = 8;
    public int Ranks { get; init; } = 8;

    /// <summary>Piece letters in play, ordered. Extra letters are variant pieces.</summary>
    public string Pieces { get; init; } = "KQRBNP";

    /// <summary>Castling exists at all. False for Racing Kings, Horde-style setups.</summary>
    public bool Castling { get; init; } = true;

    /// <summary>What a pawn may become.</summary>
    public string PromotionPieces { get; init; } = "QRBN";

    /// <summary>Captured pieces return to the capturer's hand and may be dropped.</summary>
    public bool Drops { get; init; }

    /// <summary>A capture removes more than the captured piece (Atomic).</summary>
    public bool CaptureExplodes { get; init; }

    /// <summary>Captures are compulsory when available (Antichess).</summary>
    public bool CaptureCompulsory { get; init; }

    /// <summary>Losing all material WINS (Antichess). Inverts the objective.</summary>
    public bool BareKingWins { get; init; }

    /// <summary>Reaching one of these squares wins outright (King of the Hill).</summary>
    public string WinBySquares { get; init; } = "";

    /// <summary>Delivering this many checks wins (Three-check). 0 = not a condition.</summary>
    public int WinByCheckCount { get; init; }

    /// <summary>Giving check is illegal (Racing Kings).</summary>
    public bool CheckForbidden { get; init; }

    /// <summary>Ordinary chess. The identity-neutral default.</summary>
    public static readonly ChessVariantRules Standard = new();

    public bool IsStandard => Equals(Standard);

    /// <summary>
    /// The canonical rule surface, or EMPTY for standard chess.
    ///
    /// Empty is load-bearing: <see cref="PositionContent"/> appends this, so a standard
    /// position's surface is byte-identical to what it was before variants existed and
    /// every id in the substrate survives. Only axes that DIFFER from standard are named,
    /// so adding a new axis with a standard-matching default also cannot move existing ids.
    /// </summary>
    public string Surface()
    {
        if (IsStandard) return "";
        var sb = new StringBuilder(64);
        void Add(string k, string v) => sb.Append(sb.Length == 0 ? "" : ",").Append(k).Append(':').Append(v);

        if (Files != 8 || Ranks != 8) Add("dim", $"{Files}x{Ranks}");
        if (Pieces != Standard.Pieces) Add("pc", Pieces);
        if (!Castling) Add("nocastle", "1");
        if (PromotionPieces != Standard.PromotionPieces) Add("promo", PromotionPieces);
        if (Drops) Add("drops", "1");
        if (CaptureExplodes) Add("atomic", "1");
        if (CaptureCompulsory) Add("forcedcap", "1");
        if (BareKingWins) Add("antiwin", "1");
        if (WinBySquares.Length > 0) Add("winsq", WinBySquares);
        if (WinByCheckCount > 0) Add("winchk", WinByCheckCount.ToString());
        if (CheckForbidden) Add("nocheck", "1");
        return sb.ToString();
    }

    public override string ToString() => IsStandard ? "standard" : Surface();
}

/// <summary>
/// The conventional rule sets, PRE-SEEDED — not a closed list, a starting vocabulary.
///
/// These exist so the common cases have names the moment a corpus mentions them. A game
/// whose observed rules match none of them is not an error: it mints its own id from its
/// own rule surface and is recorded as the variant it is. That is the feature — the
/// substrate learns a rule set by being shown one, exactly as it learns a word.
/// </summary>
public static class ChessVariants
{
    public static readonly ChessVariantRules Standard = ChessVariantRules.Standard;

    /// <summary>Chess960 / Freestyle / DFRC are STANDARD RULES. Only the array differs, and
    /// the array lives in the FEN. Named here because operators expect the name, and it
    /// resolving to standard is the correct, and slightly surprising, answer.</summary>
    public static readonly ChessVariantRules Chess960 = ChessVariantRules.Standard;

    public static readonly ChessVariantRules KingOfTheHill =
        ChessVariantRules.Standard with { WinBySquares = "d4,d5,e4,e5" };

    public static readonly ChessVariantRules ThreeCheck =
        ChessVariantRules.Standard with { WinByCheckCount = 3 };

    public static readonly ChessVariantRules Antichess = ChessVariantRules.Standard with
    {
        Castling = false, CaptureCompulsory = true, BareKingWins = true,
        PromotionPieces = "QRBNK",
    };

    public static readonly ChessVariantRules Atomic =
        ChessVariantRules.Standard with { CaptureExplodes = true };

    public static readonly ChessVariantRules Crazyhouse =
        ChessVariantRules.Standard with { Drops = true };

    public static readonly ChessVariantRules RacingKings = ChessVariantRules.Standard with
    {
        Castling = false, CheckForbidden = true, WinBySquares = "a8,b8,c8,d8,e8,f8,g8,h8",
    };

    public static readonly ChessVariantRules Capablanca = ChessVariantRules.Standard with
    {
        Files = 10, Pieces = "KQRBNACP", PromotionPieces = "QRBNAC",
    };

    /// <summary>
    /// The names a PGN <c>[Variant "..."]</c> tag is likely to carry, mapped to rules.
    /// A tag is a CLAIM by the source, not proof — it is one witness, and where the played
    /// moves contradict it the moves win. Lookup is case- and separator-insensitive because
    /// sites disagree ("King of the Hill", "kingOfTheHill", "koth").
    /// </summary>
    public static ChessVariantRules? ByName(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string k = new(tag.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return k switch
        {
            "standard" or "chess" or "fromposition" => Standard,
            "chess960" or "fischerandom" or "fischerrandom" or "freestyle" or "960"
                or "doublefischerandom" or "dfrc" => Chess960,
            "kingofthehill" or "koth" => KingOfTheHill,
            "threecheck" or "3check" or "threechecks" => ThreeCheck,
            "antichess" or "giveaway" or "suicide" or "losers" => Antichess,
            "atomic" => Atomic,
            "crazyhouse" or "zh" => Crazyhouse,
            "racingkings" or "racing" => RacingKings,
            "capablanca" => Capablanca,
            _ => null,
        };
    }

    /// <summary>Every pre-seeded rule set, for bootstrap and for narrowing.</summary>
    public static IReadOnlyList<(string Name, ChessVariantRules Rules)> Conventional =>
    [
        ("Standard", Standard),
        ("KingOfTheHill", KingOfTheHill),
        ("ThreeCheck", ThreeCheck),
        ("Antichess", Antichess),
        ("Atomic", Atomic),
        ("Crazyhouse", Crazyhouse),
        ("RacingKings", RacingKings),
        ("Capablanca", Capablanca),
    ];
}
