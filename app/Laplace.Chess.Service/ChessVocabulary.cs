using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>
/// The chess source's abstract symbols — the ONLY things minted by name (OfCanonical + Vocabulary
/// tier): the source, the <c>Chess_Position</c> type, and the <c>MOVE</c> relation type. Position
/// instances are never declared here — they are composed from their canonical surface (routed through
/// <c>ContentEmitter</c>), so their id/tier/coordinate emerge. (When the substrate-wide anchor class is
/// removed, this vocab moves to composed content too — see the vocabulary-is-content direction.)
/// </summary>
public static class ChessVocabulary
{
    public const string SourceName = "ChessSelfPlay";

    // Type ids route through EntityTypeRegistry.Id (the centralized minter) instead of raw
    // OfCanonical here — behavior-preserving (Id(name) == OfCanonical($"substrate/type/{name}/v1"))
    // and conformant with the type-id law (TypeIdLawTests). SourceId is not a type, so it stays raw.
    public static readonly Hash128 SourceId         = Hash128.OfCanonical("substrate/source/ChessSelfPlay/v1");
    public static readonly Hash128 PositionType     = EntityTypeRegistry.Id("Chess_Position");
    public static readonly Hash128 SubstructureType = EntityTypeRegistry.Id("Chess_Substructure");
    public static readonly Hash128 MoveType         = EntityTypeRegistry.Id("MOVE");

    /// <summary>
    /// Unary relation: "with this node present, the side to move scored <i>result</i>." Folded over
    /// every substructure AND the whole position, so a position's value is the consensus over the
    /// substructures it shares with seen positions (the lookup-table fix) and the substrate <i>measures</i>
    /// which structural features predict winning — never hand-tuned weights.
    /// </summary>
    public static readonly Hash128 OutcomeType = EntityTypeRegistry.Id("OUTCOME");

    /// <summary>
    /// Sentinel object of every <see cref="OutcomeType"/> attestation, so the row is structurally
    /// identical to the proven scored MOVE edge (subject → type → object), folding the same way. The
    /// subject (the substructure / position) carries the identity; the object is constant.
    /// </summary>
    public static readonly Hash128 OutcomeObject = EntityTypeRegistry.Id("Chess_Result");

    // --- Provenance-distinct sources (was one shared id for everything). Evidence still folds — positions
    // are content-addressed — but each carries its own source + a REAL trust class (not trustClassId:
    // SourceId): a curated master game outweighs self-play jitter, and a user-prompt game is weighted as such.
    public static readonly Hash128 PgnSourceId        = Hash128.OfCanonical("substrate/source/ChessPgn/v1");
    public static readonly Hash128 UserPromptSourceId = Hash128.OfCanonical("substrate/source/ChessUserPrompt/v1");
    public static readonly Hash128 OpeningsSourceId   = Hash128.OfCanonical("substrate/source/ChessOpenings/v1");

    private static Hash128 TrustClass(string cls) => Hash128.OfCanonical($"substrate/trust_class/{cls}/v1");
    // Trust-class entity names MUST match the seeded set in 21_seed.sql.in (ResponseContent /
    // UserPromptContent, NOT the SourceTrust constant names Response/UserPrompt) or HAS_TRUST_CLASS
    // points at an unseeded entity with no weight.
    public static readonly Hash128 PgnTrustClass        = TrustClass("AcademicCurated");   // real player games
    public static readonly Hash128 SelfPlayTrustClass   = TrustClass("ResponseContent");   // substrate's own high-temp play
    public static readonly Hash128 UserPromptTrustClass = TrustClass("UserPromptContent"); // user-prompt games
    public static readonly Hash128 OpeningsTrustClass   = TrustClass("AcademicCurated");   // curated ECO book (openings decomposer)

    // --- Players as first-class entities (the move-CHOICE authority axis): a move weighted by WHO chose it,
    // so "Magnus toying with me" still carries Magnus-level authority on the move he picked.
    public static readonly Hash128 PlayerType    = EntityTypeRegistry.Id("Chess_Player");
    public static readonly Hash128 PlayedByType  = EntityTypeRegistry.Id("PLAYED_BY");   // position/move PLAYED_BY player
    public static readonly Hash128 HasRatingType = EntityTypeRegistry.Id("HAS_RATING");  // player HAS_RATING (per game)

    /// <summary>Player entity id from the raw PGN name (alias-merge is a later canonicalization pass).</summary>
    public static Hash128 PlayerId(string name) => Hash128.OfCanonical($"chess/player/{name.Trim()}");

    /// <summary>The self-play mover: the substrate playing itself. Self-play moves are attributed to this
    /// player (<c>PLAYED_BY Laplace</c>) — low corpus trust (<see cref="SelfPlayTrustClass"/>), but a real
    /// identity so the substrate's own play is tracked and distinguishable from humans. (Openings remain
    /// anonymous — a null mover — since book theory has no player.)</summary>
    public static readonly Hash128 LaplacePlayerId = PlayerId("Laplace");

    /// <summary>Mint (dedup) a player entity and bind its display name via HAS_NAME_ALIAS. Shared by the
    /// PGN decomposer (named humans) and self-play (the <c>Laplace</c> player), so players render by name
    /// and are queryable. Content-addressed, so repeats across games/batches converge to one entity.</summary>
    public static Hash128 EmitPlayer(SubstrateChangeBuilder b, Hash128 playerId, string name, Hash128 sourceId)
    {
        b.AddEntity(playerId, EntityTier.Vocabulary, PlayerType, sourceId);
        if (ContentEmitter.Emit(b, name, sourceId) is { } nameId)
            b.AddAttestation(NativeAttestation.Categorical(
                playerId, "HAS_NAME_ALIAS", nameId, sourceId, null, SourceTrust.AcademicCurated));
        return playerId;
    }

    /// <summary>Trust of the self-play corpus (the substrate's own play); tune as needed.</summary>
    public const double Trust = SourceTrust.StructuredCorpus;

    /// <summary>Bootstraps the chess type/relation vocabulary and returns the canonical name strings it
    /// declared, so a caller without the IDecomposer ingest path (the self-play host) can register them
    /// into <c>canonical_names</c> — otherwise the types render only via the slow HAS_NAME_ALIAS
    /// traversal and aren't queryable by name.</summary>
    /// <summary>Legacy/self-play bootstrap: the self-play source at its real trust class (no longer the
    /// degenerate <c>trustClassId: SourceId</c>).</summary>
    public static Task<IReadOnlyCollection<string>> BootstrapAsync(
        ISubstrateWriter writer, CancellationToken ct = default)
        => BootstrapAsync(writer, SourceId, SourceName, SelfPlayTrustClass, ct);

    /// <summary>Bootstraps the chess vocabulary FOR a specific provenance source with its real trust class,
    /// returning the canonical names declared. Types are content-addressed, so declaring them under each
    /// source is idempotent — the shared graph converges regardless of which source seeded a given type.</summary>
    public static async Task<IReadOnlyCollection<string>> BootstrapAsync(
        ISubstrateWriter writer, Hash128 sourceId, string sourceName, Hash128 trustClassId,
        CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(sourceId, sourceName, trustClassId);
        boot.AddType("Chess_Position");
        boot.AddType("Chess_Substructure");
        boot.AddType("Chess_Result");      // the OUTCOME sentinel object
        boot.AddType("Chess_Player");      // named movers (PGN) + the self-play "Laplace" player
        boot.AddRelationType("MOVE");
        boot.AddRelationType("OUTCOME");
        boot.AddRelationType("PLAYED_BY");
        boot.AddRelationType("HAS_RATING");
        await writer.ApplyAsync(boot.Build(), ct);
        return boot.CanonicalNames;
    }
}
