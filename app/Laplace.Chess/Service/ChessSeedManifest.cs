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
    internal const string OpeningName    = "OPENING_NAME";
    internal const string HasEco         = "HAS_ECO";
    internal const string GameHasOpening = "GAME_HAS_OPENING";
    internal const string GameHasEco     = "GAME_HAS_ECO";
    public static readonly IReadOnlyList<string> Relations =
    [
        "MOVE", "OUTCOME", "PLAYED_BY", "HAS_RATING", OpeningName, HasEco,
        // Event → line: every source that records playings attests it.
        "PLAYS_LINE",
        "HAS_SETUP", "ANALYZED_AT",
        // PGN White/Black are its surfaces: HAS_PLAYER {side/white|black}.
        "HAS_PLAYER", "HAS_EVENT", "ON_DATE", "HAS_TIME_CONTROL", "HAS_TC_CLASS",
        "HAS_TERMINATION", "HAS_RESULT", "HAS_EVAL", "MOVE_QUALITY",
        "HAS_THINK_CLASS", GameHasOpening, GameHasEco,
        // Exact tablebase verdicts on witnessed positions: five-valued WDL token (side to
        // move's view) and distance-to-zeroing scalar.
        "HAS_WDL", "HAS_DTZ",
        // HAS_MOTIF is the position-grain child of GAME_HAS_MOTIF. Declaring a child pulls
        // its root through ExpandRelationsWithFamily but not the converse, so both are listed.
        "GAME_HAS_MOTIF", "HAS_MOTIF", "EXPLAINS", "IS_EXAMPLE_OF", "HAS_DEFINITION",
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
