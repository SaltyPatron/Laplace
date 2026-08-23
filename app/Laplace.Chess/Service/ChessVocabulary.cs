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
    // Chess_Event = the tournament / named event (PGN [Event], optionally Site/Date).
    // ONE event contains MANY games. Never mint this from white|black|source serialization — that
    // conflates event with a single playing (operator law 2026-08-03).
    public static readonly Hash128 EventType = EntityTypeRegistry.Id("Chess_Event");
    // Per-game playing occurrence (novelty / attestation context). Not Chess_Event.
    public static readonly Hash128 PlayingType = EntityTypeRegistry.Id("Chess_Playing");
    public static readonly Hash128 PlaysLineType = EntityTypeRegistry.Id("PLAYS_LINE");
    public static readonly Hash128 HasSetupType = EntityTypeRegistry.Id("HAS_SETUP");
    // Analysis watermark: analyzer stamps each game once it has derived at a given version.
    //
    // GAME METADATA HANGS OFF THE GAME TRUNK; IT IS NOT RATED TESTIMONY.
    //
    // This was emitted as the manifest relation ANALYZED_AT, so it FOLDED: measured
    // 2026-08-23, 12,891,661 attestations produced 12,863,059 consensus cells, 100%
    // single-witness with 2 distinct ratings across the lot. It cannot ever be anything
    // else -- the subject is one game and the object is the analyzer version, so the
    // triple is unique by construction and no second witness can exist to rate it
    // against. 12.8M cells in the table whose entire purpose is adjudicating competing
    // testimony, none of which can compete.
    //
    // The substrate already has the right shape for this and uses it elsewhere:
    // FileEntity.MetadataRelationTypeId (HasFileMetadata) and LayerCompletion's
    // HasLayerCompleted are minted inline as substrate meta-types, never entered in
    // relation_types.toml, never given a highway bit, and therefore never folded --
    // verified live: HasFileMetadata 209 attestations / 0 consensus,
    // HasLayerCompleted/2 8,995 attestations / 0 consensus. Provenance hangs off the
    // trunk node and is FETCHED when asked, exactly as a file's name/size/mtime is.
    //
    // A game's analysis version is that, not a claim about the world.
    public static readonly Hash128 AnalysisVersionMetaTypeId =
        SubstrateCanonicalIds.OfVersioned("type", "HasAnalysisVersion");

    // Retained: relation bits are an append-only registry (ADR 0001), so ANALYZED_AT
    // cannot be withdrawn from the manifest. It is simply no longer emitted.
    public static readonly Hash128 AnalyzedAtType = EntityTypeRegistry.Id("ANALYZED_AT");
    public static readonly Hash128 AnalysisMarkerType = EntityTypeRegistry.Id("Chess_AnalysisMarker");
    public static readonly Hash128 AnalysisSourceId = SubstrateCanonicalIds.Source("ChessAnalysis");
    public static readonly Hash128 AnalysisTrustClass = TrustClass("DerivedCalculation");
    // GH #736 lane/source split: the trajectory backfill writes physicalities under its
    // OWN source so source-grain eviction (evict_source, #508) never conflates it with
    // ChessAnalysis testimony. One lane = one source = one evictable unit.
    public static readonly Hash128 TrajectorySourceId = SubstrateCanonicalIds.Source("ChessTrajectory");

    // GH #736 source split: the position-id opening matcher writes under its OWN source so
    // its verdict can be read, trusted and evicted separately from the analyzer's
    // SAN-prefix guess. Three witnesses name a game's opening; only this one does it by
    // board identity.
    public static readonly Hash128 OpeningMatchSourceId = SubstrateCanonicalIds.Source("ChessOpeningMatch");
    // Syzygy probe lane (campaign PR-8): an exact mathematical oracle rides the
    // StandardsDerived band — high witness weight, still one voice among many.
    public static readonly Hash128 SyzygyTrustClass = TrustClass("StandardsDerived");

    // Deterministic per-(PLAYING, analysis version) marker (GH #736). The analyzer deposits
    // per-playing testimony — outcome/clock/think/eval contexts — so its unit is the
    // PLAYING, not the tournament event: two playings of one line each fold their own
    // outcome, and one event holds many playings. The scan bulk-probes these
    // (EntitiesExistBitmapAsync) to skip playings already derived at this version.
    //
    // The argument must be the same id ChessAnalyze stamps with, or the probe silently
    // never matches and the watermark stops skipping — every re-run re-analyzes the whole
    // corpus at full cost while still looking correct.
    public static Hash128 AnalysisMarkerId(Hash128 playingId, int version)
        => Hash128.OfCanonical($"chess/analyzed/{playingId}/{version}");
    public static readonly Hash128 HasWhiteType = EntityTypeRegistry.Id("HAS_WHITE");
    public static readonly Hash128 HasBlackType = EntityTypeRegistry.Id("HAS_BLACK");
    public static readonly Hash128 HasEventType = EntityTypeRegistry.Id("HAS_EVENT");
    public static readonly Hash128 OnDateType = EntityTypeRegistry.Id("ON_DATE");
    public static readonly Hash128 HasTimeControlType = EntityTypeRegistry.Id("HAS_TIME_CONTROL");
    public static readonly Hash128 HasTcClassType = EntityTypeRegistry.Id("HAS_TC_CLASS");
    public static readonly Hash128 HasTerminationType = EntityTypeRegistry.Id("HAS_TERMINATION");
    public static readonly Hash128 HasResultType = EntityTypeRegistry.Id("HAS_RESULT");
    public static readonly Hash128 HasEvalType = EntityTypeRegistry.Id("HAS_EVAL");
    public static readonly Hash128 HasEvalObject = EntityTypeRegistry.Id("Chess_Eval");
    public static readonly Hash128 MoveQualityType = EntityTypeRegistry.Id("MOVE_QUALITY");
    // Syzygy tablebase verdicts (ChessSyzygy source): five-valued WDL token
    // (side-to-move POV) and distance-to-zeroing scalar, on witnessed positions.
    public static readonly Hash128 HasWdlType = EntityTypeRegistry.Id("HAS_WDL");
    public static readonly Hash128 HasDtzType = EntityTypeRegistry.Id("HAS_DTZ");
    public static readonly Hash128 HasThinkClassType = EntityTypeRegistry.Id("HAS_THINK_CLASS");
    public static readonly Hash128 GameHasOpeningType = EntityTypeRegistry.Id("GAME_HAS_OPENING");
    public static readonly Hash128 GameHasEcoType = EntityTypeRegistry.Id("GAME_HAS_ECO");
    public static readonly Hash128 GameHasMotifType = EntityTypeRegistry.Id("GAME_HAS_MOTIF");
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

    /// <summary>
    /// Tournament / named event id from PGN tags. Same [Event] (+ Site, Date) → one id
    /// shared by every game in that event. Not a game id; not a playing id.
    /// </summary>
    public static Hash128 PgnEventId(string @event, string site, string date)
        => Hash128.OfCanonical($"chess/event/{@event}|{site}|{date}");

    /// <summary>
    /// One playing of a line (one PGN game record). Novelty gate and attestation context.
    /// Closed over the decomposed line so formatting-equivalent PGNs converge, and over the
    /// witnessed result so two source records are two playings. Never Chess_Event.
    ///
    /// GH #736 rules this handle provenance-shaped and SOURCE-RECORD-derived, precisely so
    /// re-ingest is idempotent while distinct records stay distinct. Closing it over the line
    /// alone made it a pure function of content, so the same players/date/event replaying the
    /// same moves to a DIFFERENT result collapsed onto one playing — and HAS_RESULT is
    /// subjected on the line with this id as its context, so the two results became
    /// indistinguishable rather than separately recoverable. The result token restores the
    /// record grain without reintroducing a dependency on PGN spelling.
    /// </summary>
    public static Hash128 PgnPlayingId(
        string white, string black, string date, string @event, string round, string site,
        Hash128 lineId, string resultToken)
        => Hash128.OfCanonical(
            $"chess/playing/{white}|{black}|{date}|{@event}|{round}|{site}|{lineId}|{resultToken}");

    /// <summary>
    /// One playing of a live/lab game. Content-derived exactly like <see cref="PgnPlayingId"/>:
    /// the line is the Merkle of the ordered position ids, so it already carries the whole
    /// move sequence; players, learn context and result close over the rest. Two identical
    /// self-plays therefore mint ONE playing whose observation count folds, which is the
    /// designed behaviour — testimony accumulates, rows do not duplicate.
    ///
    /// Replaces minting the playing from a session GUID. A random id is not a function of
    /// what it identifies: the same game replayed produced a different entity every time, so
    /// re-ingest could never dedupe it and the substrate accumulated a fresh playing per run.
    /// There was no speed argument either — OfCanonical stack-allocates the UTF-8 and calls
    /// the native SIMD blake3 (NativeInterop.Hash128Blake3), which beats Guid.NewGuid().
    /// </summary>
    public static Hash128 LivePlayingId(
        Hash128? whitePlayer, Hash128? blackPlayer, string learnContext,
        Hash128 lineId, string resultToken)
        => Hash128.OfCanonical(
            $"chess/playing/live/{whitePlayer}|{blackPlayer}|{learnContext}|{lineId}|{resultToken}");

    // IN-MEMORY SESSION HANDLE ONLY — never an entity id. A live game needs a key to route
    // plies to a session before any content exists; that key is not identity and no longer
    // reaches the substrate. The playing entity is minted by LivePlayingId at completion,
    // when the content it names finally exists. Lichess games keep their source-asserted
    // external id (ChessLiveGameHost.LichessGameId), which IS deterministic.
    public static Hash128 PlaySessionHandle(Guid sessionGame)
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
    ISubstrateWriter writer, CancellationToken ct = default, ISubstrateReader? reader = null)
    => BootstrapAsync(writer, SourceId, SourceName, SelfPlayTrustClass, ct, reader);

    public static async Task<IReadOnlyCollection<string>> BootstrapAsync(
    ISubstrateWriter writer, Hash128 sourceId, string sourceName, Hash128 trustClassId,
    CancellationToken ct = default, ISubstrateReader? reader = null)
    {
        var boot = new BootstrapIntentBuilder(sourceId, sourceName, trustClassId);
        foreach (var t in ChessSeedManifest.TypeNodeNames)
            boot.AddType(t);
        foreach (var r in SourceVocabularyBootstrap.ExpandRelationsWithFamily(ChessSeedManifest.Relations))
            boot.AddRelationType(r);
        // Source entity already named ⇒ vocabulary for this lane was deposited. Skip the
        // multi-second present-verify apply that was eating the process envelope on every
        // re-ingest (measured ~3s × 2 bootstraps before INGEST_START).
        if (reader is not null)
        {
            if (reader.IsProvenPresent(sourceId))
                return boot.CanonicalNames;
            byte[] bm = await reader.EntitiesExistBitmapAsync(new[] { sourceId }, ct)
                .ConfigureAwait(false);
            if (BitmapBits.IsSet(bm, 0))
            {
                reader.MarkProven(new[] { sourceId });
                return boot.CanonicalNames;
            }
        }
        await writer.ApplyAsync(boot.Build(), ct).ConfigureAwait(false);
        reader?.MarkProven(new[] { sourceId });
        return boot.CanonicalNames;
    }
}
