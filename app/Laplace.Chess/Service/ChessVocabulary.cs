using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

public static class ChessVocabulary
{
    public const string SourceName = "ChessSelfPlay";




    public static readonly Hash128 SourceId = SubstrateCanonicalIds.Source("ChessSelfPlay");
    public static readonly Hash128 PositionType = EntityTypeRegistry.Id("Chess_Position");
    public static readonly Hash128 SubstructureType = EntityTypeRegistry.Id("Chess_Substructure");
    public static readonly Hash128 MoveType = EntityTypeRegistry.Id("MOVE");

    public static readonly Hash128 OutcomeType = EntityTypeRegistry.Id("OUTCOME");

    public static readonly Hash128 OutcomeObject = EntityTypeRegistry.Id("Chess_Result");




    public static readonly Hash128 PgnSourceId = SubstrateCanonicalIds.Source("ChessPgn");
    public static readonly Hash128 EvalPgnSourceId = SubstrateCanonicalIds.Source("ChessEvalPgn");
    public static readonly Hash128 ReviewSourceId = SubstrateCanonicalIds.Source("ChessReview");
    public static readonly Hash128 UserPromptSourceId = SubstrateCanonicalIds.Source("ChessUserPrompt");
    public static readonly Hash128 OpeningsSourceId = SubstrateCanonicalIds.Source("ChessOpenings");
    public static readonly Hash128 BookSourceId = SubstrateCanonicalIds.Source("ChessBook");

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




    // GH #736: the game CONTENT entity — the LINE, content-addressed from the ordered
    // position ids it passes through (ChessCompose.LineId). One entity per distinct line
    // ever played, no matter who played it or when. The type name stays Chess_Game: the
    // game-as-content IS the line.
    public static readonly Hash128 GameType = EntityTypeRegistry.Id("Chess_Game");
    // GH #736: the playing EVENT — a slim provenance handle (who/when/where a line was
    // played). Exists as an entity row solely so the novelty gate can bitmap-probe it;
    // it is the attestation CONTEXT for every per-playing fact and the subject of
    // exactly one record edge, (event, PLAYS_LINE, line).
    public static readonly Hash128 EventType = EntityTypeRegistry.Id("Chess_Event");
    public static readonly Hash128 PlaysLineType = EntityTypeRegistry.Id("PLAYS_LINE");
    public static readonly Hash128 HasMovetextType = EntityTypeRegistry.Id("HAS_MOVETEXT");
    /// <summary>Entity type of a composed movetext document — a game's verbatim token sequence.</summary>
    public static readonly Hash128 MovetextType = EntityTypeRegistry.Id("Chess_Movetext");
    public static readonly Hash128 HasPlyType = EntityTypeRegistry.Id("HAS_PLY");
    public static readonly Hash128 HasSanType = EntityTypeRegistry.Id("HAS_SAN");
    public static readonly Hash128 HasCommentType = EntityTypeRegistry.Id("HAS_COMMENT");
    public static readonly Hash128 HasSetupType = EntityTypeRegistry.Id("HAS_SETUP");
    // Analysis watermark: analyzer stamps each game once it has derived at a given version.
    public static readonly Hash128 AnalyzedAtType = EntityTypeRegistry.Id("ANALYZED_AT");
    public static readonly Hash128 AnalysisMarkerType = EntityTypeRegistry.Id("Chess_AnalysisMarker");
    public static readonly Hash128 AnalysisSourceId = SubstrateCanonicalIds.Source("ChessAnalysis");
    public static readonly Hash128 AnalysisTrustClass = TrustClass("DerivedCalculation");
    // GH #736 lane/source split: the trajectory backfill writes physicalities under its
    // OWN source so source-grain eviction (evict_source, #508) never conflates it with
    // ChessAnalysis testimony. One lane = one source = one evictable unit.
    public static readonly Hash128 TrajectorySourceId = SubstrateCanonicalIds.Source("ChessTrajectory");

    // Deterministic per-(EVENT, analysis version) marker (GH #736: the analyzer deposits
    // per-playing testimony — outcome/clock/think/eval contexts — so its unit is the
    // event; two playings of one line each fold their own outcome). The scan bulk-probes
    // these (EntitiesExistBitmapAsync) to skip events already derived at this version.
    public static Hash128 AnalysisMarkerId(Hash128 eventId, int version)
        => Hash128.OfCanonical($"chess/analyzed/{eventId}/{version}");
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
    // GH #736: a book's grounded prose line IS the shared line entity (ChessCompose.LineId
    // of its replayed positions) — two books teaching the same trap collide, which is the
    // point. The idempotency the old (title|sans)-salted id provided moves to a MARKER,
    // exactly like every calculated lane: probed by the extractor so re-ingesting a book
    // never re-witnesses its lines, while a DIFFERENT book adds witnesses to the shared line.
    public static readonly Hash128 BookLineType = EntityTypeRegistry.Id("Chess_BookLine");
    public static Hash128 BookLineMarkerId(Hash128 bookTitleContentId, Hash128 lineId)
        => Hash128.OfCanonical($"chess/bookline-marker/{bookTitleContentId}/{lineId}");
    public static readonly Hash128 ExplainsType = EntityTypeRegistry.Id("EXPLAINS");
    public static readonly Hash128 IsExampleOfType = EntityTypeRegistry.Id("IS_EXAMPLE_OF");
    // Reuses the manifest's existing HAS_DEFINITION relation (same one WordNet/Wiktionary glosses
    // use) rather than minting a chess-only "DEFINES" duplicate, so a chess term's definition and
    // a dictionary gloss for the same content-addressed term land on the same relation type.
    public static readonly Hash128 DefinesType = EntityTypeRegistry.Id("HAS_DEFINITION");

    // GH #736: the playing-event handle for a PGN-corpus record — the Seven-Tag-Roster
    // fields the source asserts, CLOSED OVER the verbatim movetext content id. Including
    // movetextId makes the handle exactly "this record": re-ingesting the same file (or a
    // second corpus carrying the byte-identical game) is idempotent — one event — while
    // garbage tag rosters ("?", "-") cannot collide two different games, because their
    // verbatim movetexts differ. This is PROVENANCE-shaped by design: it names an event,
    // never content; it appears only as attestation context and as PLAYS_LINE's subject.
    public static Hash128 PgnEventId(
        string white, string black, string date, string @event, string round, string site,
        Hash128 movetextId)
        => Hash128.OfCanonical($"chess/event/{white}|{black}|{date}|{@event}|{round}|{site}|{movetextId}");

    // Live/lab playing-event handle: a live occurrence is unique by construction, so the
    // session GUID is the whole identity (determinism-for-re-ingest does not apply — the
    // cutechess PGN written afterwards is the replayable record). Lichess games keep their
    // source-asserted external id (ChessLiveGameHost.LichessGameId).
    public static Hash128 PlayEventId(Guid sessionGame)
        => Hash128.OfCanonical($"chess/play/{sessionGame:N}");

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
        foreach (var t in ChessSeedManifest.TypeNodeNames)
            boot.AddType(t);
        foreach (var r in SourceVocabularyBootstrap.ExpandRelationsWithFamily(ChessSeedManifest.Relations))
            boot.AddRelationType(r);
        await writer.ApplyAsync(boot.Build(), ct);
        return boot.CanonicalNames;
    }
}
