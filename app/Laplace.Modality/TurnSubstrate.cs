using Laplace.Engine.Core;

namespace Laplace.Modality;

/// <summary>
/// One action edge produced by playing/analysing a game, expressed in the modality's <b>canonical
/// surfaces</b> (not pre-hashed ids): <c>SubjectKey —[move]→ ObjectKey</c>, with the mover's per-ply
/// credit. The host turns these surfaces into substrate entities by <b>composing</b> them
/// (<c>ContentEmitter</c>) — so a state/move becomes a content node with emergent tier/coordinate/id,
/// never a blind-hashed opaque atom. The engine deliberately does not mint ids itself.
/// </summary>
public readonly record struct RecordedEdge(
    string     SubjectKey,
    string     ObjectKey,
    string?    MoveKey,
    PlyOutcome MoverOutcome);

/// <summary>
/// Content-addresses a modality canonical surface to its substrate id by <b>composing</b> it (the
/// host implements this with <c>ContentEmitter.RootId</c>, so the id is the Merkle root of the
/// codepoint→…→ tree, with tier/coordinate emergent). This is the only way the engine obtains a
/// state/move id — it never calls <c>OfCanonical</c> on an instance.
/// </summary>
public interface IContentAddresser
{
    /// <summary>Composed content id (Merkle root) of a canonical surface string.</summary>
    Hash128 Address(string canonicalSurface);
}

/// <summary>
/// The substrate's edge (consensus) key. Mirrors <c>laplace.consensus_id(subject,type,object)</c> =
/// BLAKE3(subject ‖ type ‖ COALESCE(object, 0¹⁶)), so a candidate move's rating can be looked up by id
/// without a per-move round trip. This is a <i>relation key</i>, not a content node — it is correctly a
/// hash of the three ids, unlike a state/move which must be composed.
/// </summary>
public static class ConsensusKeys
{
    private static readonly byte[] ZeroObject = new byte[16];

    public static Hash128 EdgeId(Hash128 subject, Hash128 type, Hash128 obj)
    {
        Span<byte> buf = stackalloc byte[48];
        subject.WriteBytes(buf[..16]);
        type.WriteBytes(buf.Slice(16, 16));
        obj.WriteBytes(buf.Slice(32, 16));
        return Hash128.Blake3(buf);
    }

    public static Hash128 EdgeId(Hash128 subject, Hash128 type, Hash128? obj)
    {
        if (obj is { } o) return EdgeId(subject, type, o);
        Span<byte> buf = stackalloc byte[48];
        subject.WriteBytes(buf[..16]);
        type.WriteBytes(buf.Slice(16, 16));
        ZeroObject.CopyTo(buf.Slice(32, 16));
        return Hash128.Blake3(buf);
    }
}

/// <summary>
/// Reads the confidence-discounted strength (<c>eff_mu = rating − 2·rd</c>, fixed-point ×1e9) of
/// consensus edges by id. Unrated edges return <see cref="GlickoPriors.UnratedEffMu"/> — the Glicko-2
/// prior lower bound — so a never-seen move sits on the same scale as rated ones: below a confirmed
/// win, above a refuted move. That is the explore/exploit gradient, for free.
/// </summary>
public interface IEdgeRatings
{
    /// <summary>eff_mu per edge id, in the same order; the unrated prior where absent.</summary>
    Task<double[]> EffMuAsync(IReadOnlyList<Hash128> edgeIds, CancellationToken ct = default);
}

/// <summary>Writes a finished game's edges into the substrate and folds them online (immediately).</summary>
public interface ITurnLearner
{
    /// <summary>
    /// Compose the visited states/moves as content entities and persist the game's action edges as
    /// attestations whose <i>score is the game result</i>, then fold the touched consensus edges in
    /// place (no batch drain / table rebuild) so the updated ratings are queryable on the next move.
    /// </summary>
    Task LearnGameAsync(IReadOnlyList<RecordedEdge> edges, CancellationToken ct = default);
}

public static class GlickoPriors
{
    // Ratings/rd are fixed-point ×1e9: a real Glicko value of 1500 is stored as 1500e9 = 1.5e12.
    // Mirrors glicko2_neutral_mu() and glicko2_initial_rd() in 13_mu_law.sql.in, in the same raw
    // storage units the consensus query returns for (rating - 2*rd).

    /// <summary>glicko2_neutral_mu — real 1500, stored 1500×1e9.</summary>
    public const double NeutralMu = 1_500_000_000_000d;
    /// <summary>glicko2_initial_rd — real 350, stored 350×1e9.</summary>
    public const double InitialRd = 350_000_000_000d;
    /// <summary>eff_mu of an unseen edge = neutral μ − 2·initial rd (the prior lower-confidence bound).</summary>
    public const double UnratedEffMu = NeutralMu - 2d * InitialRd; // 800×1e9
}
