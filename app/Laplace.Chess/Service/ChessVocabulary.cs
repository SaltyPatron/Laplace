using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

public static class ChessVocabulary
{
    public const string SourceName = "ChessSelfPlay";




    public static readonly Hash128 SourceId = Hash128.OfCanonical("substrate/source/ChessSelfPlay/v1");
    public static readonly Hash128 PositionType = EntityTypeRegistry.Id("Chess_Position");
    public static readonly Hash128 SubstructureType = EntityTypeRegistry.Id("Chess_Substructure");
    public static readonly Hash128 MoveType = EntityTypeRegistry.Id("MOVE");

    public static readonly Hash128 OutcomeType = EntityTypeRegistry.Id("OUTCOME");

    public static readonly Hash128 OutcomeObject = EntityTypeRegistry.Id("Chess_Result");




    public static readonly Hash128 PgnSourceId = Hash128.OfCanonical("substrate/source/ChessPgn/v1");
    public static readonly Hash128 EvalPgnSourceId = Hash128.OfCanonical("substrate/source/ChessEvalPgn/v1");
    public static readonly Hash128 ReviewSourceId = Hash128.OfCanonical("substrate/source/ChessReview/v1");
    public static readonly Hash128 UserPromptSourceId = Hash128.OfCanonical("substrate/source/ChessUserPrompt/v1");
    public static readonly Hash128 OpeningsSourceId = Hash128.OfCanonical("substrate/source/ChessOpenings/v1");
    public static readonly Hash128 BookSourceId = Hash128.OfCanonical("substrate/source/ChessBook/v1");

    private static Hash128 TrustClass(string cls) => Hash128.OfCanonical($"substrate/trust_class/{cls}/v1");



    public static readonly Hash128 PgnTrustClass = TrustClass("AcademicCurated");
    public static readonly Hash128 EvalPgnTrustClass = TrustClass("StructuredCorpus");
    public static readonly Hash128 ReviewTrustClass = TrustClass("UserPromptContent");
    public static readonly Hash128 SelfPlayTrustClass = TrustClass("ResponseContent");
    public static readonly Hash128 UserPromptTrustClass = TrustClass("UserPromptContent");
    public static readonly Hash128 OpeningsTrustClass = TrustClass("AcademicCurated");
    public static readonly Hash128 BookTrustClass = TrustClass("AcademicCurated");



    public static readonly Hash128 PlayerType = EntityTypeRegistry.Id("Chess_Player");
    public static readonly Hash128 PlayedByType = EntityTypeRegistry.Id("PLAYED_BY");
    public static readonly Hash128 HasRatingType = EntityTypeRegistry.Id("HAS_RATING");




    public static readonly Hash128 OpeningNameType = EntityTypeRegistry.Id("OPENING_NAME");
    public static readonly Hash128 EcoCodeType = EntityTypeRegistry.Id("HAS_ECO");




    public static readonly Hash128 GameType = EntityTypeRegistry.Id("Chess_Game");
    // Witnessed ply anchor: a per-move node carrying the recorded SAN + annotations, with a
    // deterministic id (game+ply) the analyzer can reconstruct without a reverse lookup.
    public static readonly Hash128 PlyType = EntityTypeRegistry.Id("Chess_Ply");
    public static readonly Hash128 HasMovetextType = EntityTypeRegistry.Id("HAS_MOVETEXT");
    public static readonly Hash128 HasPlyType = EntityTypeRegistry.Id("HAS_PLY");
    public static readonly Hash128 HasSanType = EntityTypeRegistry.Id("HAS_SAN");
    public static readonly Hash128 HasCommentType = EntityTypeRegistry.Id("HAS_COMMENT");
    public static readonly Hash128 HasSetupType = EntityTypeRegistry.Id("HAS_SETUP");
    // Analysis watermark: analyzer stamps each game once it has derived at a given version.
    public static readonly Hash128 AnalyzedAtType = EntityTypeRegistry.Id("ANALYZED_AT");
    public static readonly Hash128 AnalysisMarkerType = EntityTypeRegistry.Id("Chess_AnalysisMarker");
    public static readonly Hash128 AnalysisSourceId = Hash128.OfCanonical("substrate/source/ChessAnalysis/v1");
    public static readonly Hash128 AnalysisTrustClass = TrustClass("DerivedCalculation");

    // Deterministic per-(game, analysis version) marker. The analyzer scan bulk-probes these for
    // existence (EntitiesExistBitmapAsync) to skip games already derived at the current version —
    // same fast novelty-probe the recorder uses on game ids.
    public static Hash128 AnalysisMarkerId(Hash128 gameId, int version)
        => Hash128.OfCanonical($"chess/analyzed/{gameId}/{version}");
    public static readonly Hash128 HasWhiteType = EntityTypeRegistry.Id("HAS_WHITE");
    public static readonly Hash128 HasBlackType = EntityTypeRegistry.Id("HAS_BLACK");
    public static readonly Hash128 HasEventType = EntityTypeRegistry.Id("HAS_EVENT");
    public static readonly Hash128 OnDateType = EntityTypeRegistry.Id("ON_DATE");
    public static readonly Hash128 HasTimeControlType = EntityTypeRegistry.Id("HAS_TIME_CONTROL");
    public static readonly Hash128 HasTcClassType = EntityTypeRegistry.Id("HAS_TC_CLASS");
    public static readonly Hash128 HasTerminationType = EntityTypeRegistry.Id("HAS_TERMINATION");
    public static readonly Hash128 HasResultType = EntityTypeRegistry.Id("HAS_RESULT");
    public static readonly Hash128 GameMoveType = EntityTypeRegistry.Id("GAME_AT");
    public static readonly Hash128 GameAtPlyType = EntityTypeRegistry.Id("GAME_AT_PLY");
    public static readonly Hash128 HasEvalType = EntityTypeRegistry.Id("HAS_EVAL");
    public static readonly Hash128 HasEvalObject = EntityTypeRegistry.Id("Chess_Eval");
    public static readonly Hash128 MoveQualityType = EntityTypeRegistry.Id("MOVE_QUALITY");
    public static readonly Hash128 HasClockType = EntityTypeRegistry.Id("HAS_CLOCK");
    public static readonly Hash128 HasEvalTokenType = EntityTypeRegistry.Id("HAS_EVAL_TOKEN");
    public static readonly Hash128 HasThinkClassType = EntityTypeRegistry.Id("HAS_THINK_CLASS");
    public static readonly Hash128 GameHasOpeningType = EntityTypeRegistry.Id("GAME_HAS_OPENING");
    public static readonly Hash128 GameHasEcoType = EntityTypeRegistry.Id("GAME_HAS_ECO");
    public static readonly Hash128 GameHasMotifType = EntityTypeRegistry.Id("GAME_HAS_MOTIF");
    public static readonly Hash128 ConceptType = EntityTypeRegistry.Id("Chess_Concept");
    public static readonly Hash128 ExplainsType = EntityTypeRegistry.Id("EXPLAINS");
    public static readonly Hash128 IsExampleOfType = EntityTypeRegistry.Id("IS_EXAMPLE_OF");
    // Reuses the manifest's existing HAS_DEFINITION relation (same one WordNet/Wiktionary glosses
    // use) rather than minting a chess-only "DEFINES" duplicate, so a chess term's definition and
    // a dictionary gloss for the same content-addressed term land on the same relation type.
    public static readonly Hash128 DefinesType = EntityTypeRegistry.Id("HAS_DEFINITION");

    public static Hash128 GameId(string white, string black, string date, IReadOnlyList<string> moves)
    => Hash128.OfCanonical($"chess/game/{white}|{black}|{date}|{string.Join(' ', moves)}");

    // Deterministic per-ply anchor id — reconstructable by the analyzer from (game, ply) alone,
    // so witnessed ply annotations attach without a reverse content lookup.
    public static Hash128 PlyId(Hash128 gameId, int ply)
    => Hash128.OfCanonical($"chess/ply/{gameId}/{ply}");

    public static Hash128 PlayerId(string name) => Hash128.OfCanonical($"chess/player/{PlayerAlias.Canonical(name)}");

    public static Hash128 LegacyPlayerId(string rawName) => Hash128.OfCanonical($"chess/player/{rawName.Trim()}");

    public static readonly Hash128 LaplacePlayerId = PlayerId("Laplace");

    public static Hash128 EmitPlayer(
        SubstrateChangeBuilder b, Hash128 playerId, string name, Hash128 sourceId,
        double witnessWeight = SourceTrust.AcademicCurated)
    {
        b.AddEntity(playerId, EntityTier.Word, PlayerType, sourceId);
        if (ContentEmitter.Emit(b, name, sourceId) is { } nameId)
            b.AddAttestation(NativeAttestation.Categorical(
                playerId, "HAS_NAME_ALIAS", nameId, sourceId, null, witnessWeight));
        return playerId;
    }

    public const double Trust = SourceTrust.StructuredCorpus;

    public static Task<IReadOnlyCollection<string>> BootstrapAsync(
    ISubstrateWriter writer, CancellationToken ct = default)
    => BootstrapAsync(writer, SourceId, SourceName, SelfPlayTrustClass, ct);

    public static async Task<IReadOnlyCollection<string>> BootstrapAsync(
    ISubstrateWriter writer, Hash128 sourceId, string sourceName, Hash128 trustClassId,
    CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(sourceId, sourceName, trustClassId);
        boot.AddType("Chess_Position");
        boot.AddType("Chess_Substructure");
        boot.AddType("Chess_Result");
        boot.AddType("Chess_Player");
        boot.AddRelationType("MOVE");
        boot.AddRelationType("OUTCOME");
        boot.AddRelationType("PLAYED_BY");
        boot.AddRelationType("HAS_RATING");
        boot.AddRelationType("OPENING_NAME");
        boot.AddRelationType("HAS_ECO");
        boot.AddType("Chess_Game");
        boot.AddType("Chess_Ply");
        boot.AddRelationType("HAS_MOVETEXT");
        boot.AddRelationType("HAS_PLY");
        boot.AddRelationType("HAS_SAN");
        boot.AddRelationType("HAS_COMMENT");
        boot.AddRelationType("HAS_SETUP");
        boot.AddRelationType("ANALYZED_AT");
        boot.AddType("Chess_AnalysisMarker");
        boot.AddRelationType("HAS_WHITE");
        boot.AddRelationType("HAS_BLACK");
        boot.AddRelationType("HAS_EVENT");
        boot.AddRelationType("ON_DATE");
        boot.AddRelationType("HAS_TIME_CONTROL");
        boot.AddRelationType("HAS_TC_CLASS");
        boot.AddRelationType("HAS_TERMINATION");
        boot.AddRelationType("HAS_RESULT");
        boot.AddRelationType("GAME_AT");
        boot.AddRelationType("GAME_AT_PLY");
        boot.AddType("Chess_Eval");
        boot.AddRelationType("HAS_EVAL");
        boot.AddRelationType("MOVE_QUALITY");
        boot.AddRelationType("HAS_CLOCK");
        boot.AddRelationType("HAS_EVAL_TOKEN");
        boot.AddRelationType("HAS_THINK_CLASS");
        boot.AddRelationType("GAME_HAS_OPENING");
        boot.AddRelationType("GAME_HAS_ECO");
        boot.AddRelationType("GAME_HAS_MOTIF");
        boot.AddType("Chess_Concept");
        boot.AddRelationType("EXPLAINS");
        boot.AddRelationType("IS_EXAMPLE_OF");
        boot.AddRelationType("HAS_DEFINITION");
        await writer.ApplyAsync(boot.Build(), ct);
        return boot.CanonicalNames;
    }
}
