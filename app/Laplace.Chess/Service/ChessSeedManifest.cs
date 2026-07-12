using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>Shared chess-lane vocabulary as an <see cref="ISourceManifest"/> factory.</summary>
public static class ChessSeedManifest
{
    public static readonly IReadOnlyList<string> TypeNodeNames =
    [
        "Chess_Position", "Chess_Substructure", "Chess_Result", "Chess_Player",
        "Chess_Game", "Chess_Ply", "Chess_AnalysisMarker", "Chess_Eval",
        "Chess_Concept", "Chess_BookLine",
    ];

    public static readonly IReadOnlyList<string> Relations =
    [
        "MOVE", "OUTCOME", "PLAYED_BY", "HAS_RATING", "OPENING_NAME", "HAS_ECO",
        "HAS_MOVETEXT", "HAS_PLY", "HAS_SAN", "HAS_COMMENT", "HAS_SETUP", "ANALYZED_AT",
        "HAS_WHITE", "HAS_BLACK", "HAS_EVENT", "ON_DATE", "HAS_TIME_CONTROL", "HAS_TC_CLASS",
        "HAS_TERMINATION", "HAS_RESULT", "GAME_AT", "GAME_AT_PLY", "HAS_EVAL", "MOVE_QUALITY",
        "HAS_CLOCK", "HAS_EVAL_TOKEN", "HAS_THINK_CLASS", "GAME_HAS_OPENING", "GAME_HAS_ECO",
        "GAME_HAS_MOTIF", "EXPLAINS", "IS_EXAMPLE_OF", "HAS_DEFINITION",
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
