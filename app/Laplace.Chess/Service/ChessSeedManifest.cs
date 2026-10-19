using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>Governed chess type and relation vocabulary, declared for a chess source through an <see cref="ISourceManifest"/>.</summary>
public static class ChessSeedManifest
{
    public static readonly IReadOnlyList<string> TypeNodeNames =
    [
        "Chess_Position", "Chess_Substructure", "Chess_Result", "Chess_Player",
        "Chess_Game", "Chess_Event", "Chess_Playing", "Chess_AnalysisMarker",
        "Chess_Eval", "Chess_BookLine",
    ];

    // Relation surfaces other chess code must name. Each literal is spelled once, here in
    // the declared list, and callers reference the constant rather than retype it.
    internal const string OpeningName    = "Opening";
    internal const string HasEco         = "ECO";
    internal const string GameHasOpening = "Opening";
    internal const string GameHasEco     = "ECO";
    public static readonly IReadOnlyList<string> Relations =
    [
        "MOVE", "OUTCOME", "PLAYED_BY", "HAS_RATING", "WhiteElo", "BlackElo", OpeningName, HasEco,
        // Playing → line is the stored binding of a playing to the line it played.
        "PLAYS_LINE",
        "FEN", "ANALYZED_AT",
        "White", "Black", "Event", "Site", "Date", "Round", "TimeControl",
        "Termination", "Result", "HAS_EVAL", "MOVE_QUALITY",
        "HAS_THINK_CLASS",
        // Exact tablebase verdicts on witnessed positions: five-valued WDL token (side to
        // move's view) and distance-to-zeroing scalar.
        "HAS_WDL", "HAS_DTZ",
        // HAS_MOTIF is the position-grain child of GAME_HAS_MOTIF. Declaring a child pulls
        // its root through ExpandRelationsWithFamily but not the converse, so both are listed.
        "GAME_HAS_MOTIF", "HAS_MOTIF", "EXPLAINS", "IS_EXAMPLE_OF", "HAS_DEFINITION",
        // PGN tags kept on the game: clock of the playing, titles, and FIDE ids.
        // Site and Round are declared above.
        "UTCDate", "UTCTime", "WhiteTitle", "BlackTitle", "WhiteFideId", "BlackFideId",
        // Attested by ChessPgnDecomposer (CORRESPONDS_TO game ↔ lichess id) and
        // ChessVocabulary.EmitPlayer (HAS_NAME {name/alias}). Both are family roots, which
        // family expansion never pulls, so they are declared explicitly.
        "HAS_EXTERNAL_ID", "HAS_FEATURE", "CORRESPONDS_TO", "HAS_NAME",
    ];

    public static ISourceManifest ForLane(Hash128 sourceId, string sourceName, Hash128 trustClass) =>
        new LaneManifest(sourceId, sourceName, trustClass);

    private sealed class LaneManifest : ISourceManifest
    {
        public LaneManifest(Hash128 sourceId, string sourceName, Hash128 trustClass)
        {
            SourceId = sourceId;
            SourceName = sourceName;
            TrustClass = trustClass;
        }

        public Hash128 SourceId { get; }
        public string SourceName { get; }
        public Hash128 TrustClass { get; }
        public IReadOnlyList<string> Relations => ChessSeedManifest.Relations;
        public IReadOnlyList<string>? TypeNodeNames => ChessSeedManifest.TypeNodeNames;
        public SourceLicense License => SourceLicense.Unknown;
        public IngestSourceProfile Profile => IngestSourceProfile.ChessPgn;
    }
}
