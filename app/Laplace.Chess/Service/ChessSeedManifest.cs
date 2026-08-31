using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>Shared chess-lane vocabulary as an <see cref="ISourceManifest"/> factory.</summary>
public static class ChessSeedManifest
{
    public static readonly IReadOnlyList<string> TypeNodeNames =
    [
        "Chess_Position", "Chess_Substructure", "Chess_Result", "Chess_Player",
        "Chess_Game", "Chess_Event", "Chess_Playing", "Chess_AnalysisMarker",
        "Chess_Eval", "Chess_BookLine",
        // Chess_Concept retired from the seed manifest (GH #577): zero entities
        // on the live box; no emitter. Relation registry bits are untouched.
    ];

    // Named constants for the relation surfaces other chess code needs to NAME rather
    // than merely declare. The literal stays where it always was — in this list, which
    // is the chess lanes' canonical vocabulary — and callers reference the constant
    // instead of retyping the string. The ISA literalism gate counts a retyped
    // relation name in a new file as a new violation, and it is right to: a name typed
    // in two places is a name that can disagree with itself.
    internal const string OpeningName    = "OPENING_NAME";
    internal const string HasEco         = "HAS_ECO";
    internal const string GameHasOpening = "GAME_HAS_OPENING";
    internal const string GameHasEco     = "GAME_HAS_ECO";
    internal const string HasExternalId  = "HAS_EXTERNAL_ID";
    internal const string HasFeature     = "HAS_FEATURE";
    internal const string CorrespondsTo  = "CORRESPONDS_TO";
    internal const string HasNameAlias   = "HAS_NAME_ALIAS";

    public static readonly IReadOnlyList<string> Relations =
    [
        "MOVE", "OUTCOME", "PLAYED_BY", "HAS_RATING", OpeningName, HasEco,
        // GH #736: the event→line record edge; every chess lane that records playings emits it.
        "PLAYS_LINE",
        "HAS_SETUP", "ANALYZED_AT",
        "HAS_WHITE", "HAS_BLACK", "HAS_EVENT", "ON_DATE", "HAS_TIME_CONTROL", "HAS_TC_CLASS",
        "HAS_TERMINATION", "HAS_RESULT", "HAS_EVAL", "MOVE_QUALITY",
        "HAS_THINK_CLASS", GameHasOpening, GameHasEco,
        // GAME_AT / GAME_AT_PLY retired from the seed manifest (GH #577): ChessGraph
        // removed the ply-grain emitters; live evidence_count is 0 for both.
        // Append-only relation_types.toml keeps the bits — do not renumber.
        // Syzygy probe lane (campaign PR-8): exact endgame verdicts on witnessed
        // positions — five-valued WDL token (STM POV) + distance-to-zeroing scalar.
        "HAS_WDL", "HAS_DTZ",
        // GH #736: HAS_MOTIF is the position-grain sibling (family child of GAME_HAS_MOTIF —
        // declaring the CHILD pulls the root via ExpandRelationsWithFamily; the converse
        // does not hold, so both stay listed).
        "GAME_HAS_MOTIF", "HAS_MOTIF", "EXPLAINS", "IS_EXAMPLE_OF", "HAS_DEFINITION",
        // GH #577: emitted by ChessPgnDecomposer (CORRESPONDS_TO game↔lichess-id bridge) and
        // ChessVocabulary.EmitPlayer (HAS_NAME_ALIAS). Both are family_roots, so family
        // expansion never pulls them — an undeclared emit is the 0xC0000005 class, previously
        // masked only by global foundation seeding.
        CorrespondsTo, HasNameAlias, HasExternalId, HasFeature,
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
