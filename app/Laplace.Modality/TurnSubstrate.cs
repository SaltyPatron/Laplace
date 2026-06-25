using Laplace.Engine.Core;

namespace Laplace.Modality;

/// <summary>
/// One learned action edge produced by playing/analysing a game: <c>subject —[type]→ object</c>, with
/// the mover's per-ply credit and an optional context (e.g. the move's own content id, retained for
/// provenance even though consensus is keyed only by subject/type/object).
/// </summary>
public readonly record struct RecordedEdge(
    Hash128    Subject,
    Hash128    Type,
    Hash128    Object,
    Hash128?   Context,
    PlyOutcome MoverOutcome);

/// <summary>
/// Content-addressing for states and action edges. The edge id MUST match the substrate's
/// <c>consensus_id(subject,type,object)</c> = BLAKE3(subject ‖ type ‖ COALESCE(object, 0¹⁶)), so the
/// host can look up a candidate move's rating without a per-move round trip to compute the id
/// server-side. (Verified against <c>laplace.consensus_id</c> at startup — see the engine's parity
/// check.)
/// </summary>
public static class ConsensusKeys
{
    private static readonly byte[] ZeroObject = new byte[16];

    /// <summary>16-byte content id for a state from its modality canonical key.</summary>
    public static Hash128 StateId<TState, TAction>(ITurnModality<TState, TAction> modality, TState state)
        => Hash128.OfCanonical($"{modality.Name}/state/{modality.StateKey(state)}");

    /// <summary>16-byte content id for a move from its modality canonical key.</summary>
    public static Hash128 ActionId<TState, TAction>(
        ITurnModality<TState, TAction> modality, TState state, TAction action)
        => Hash128.OfCanonical($"{modality.Name}/action/{modality.ActionKey(state, action)}");

    /// <summary>Edge (consensus) id = BLAKE3(subject ‖ type ‖ object). Mirrors laplace.consensus_id.</summary>
    public static Hash128 EdgeId(Hash128 subject, Hash128 type, Hash128 obj)
    {
        Span<byte> buf = stackalloc byte[48];
        subject.WriteBytes(buf[..16]);
        type.WriteBytes(buf.Slice(16, 16));
        obj.WriteBytes(buf.Slice(32, 16));
        return Hash128.Blake3(buf);
    }

    /// <summary>Edge id for a nullable object, matching the COALESCE(object, 0¹⁶) in the SQL.</summary>
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
/// consensus edges by id. Unrated edges return <see cref="UnratedEffMu"/> — the Glicko-2 prior lower
/// bound (neutral μ, max rd) — so a never-seen move sits on the same scale as rated ones: below a
/// confirmed win, above a refuted move. That is the explore/exploit gradient, for free.
/// </summary>
public interface IEdgeRatings
{
    /// <summary>eff_mu per edge id, in the same order; <see cref="UnratedEffMu"/> where absent.</summary>
    Task<double[]> EffMuAsync(IReadOnlyList<Hash128> edgeIds, CancellationToken ct = default);
}

/// <summary>Writes a finished game's edges into the substrate and folds them online (immediately).</summary>
public interface ITurnLearner
{
    /// <summary>
    /// Persist the game's action edges as attestations and fold the touched consensus edges in place
    /// (no batch drain / table rebuild), so the updated ratings are queryable on the next move.
    /// </summary>
    Task LearnGameAsync(IReadOnlyList<RecordedEdge> edges, CancellationToken ct = default);
}

public static class GlickoPriors
{
    // Ratings/rd are fixed-point ×1e9: a real Glicko value of 1500 is stored as 1500e9 = 1.5e12.
    // These mirror glicko2_neutral_mu() and glicko2_initial_rd() in 13_mu_law.sql.in, in the same
    // raw storage units the consensus query returns for (rating - 2*rd).

    /// <summary>glicko2_neutral_mu — real 1500, stored 1500×1e9.</summary>
    public const double NeutralMu = 1_500_000_000_000d;
    /// <summary>glicko2_initial_rd — real 350, stored 350×1e9.</summary>
    public const double InitialRd = 350_000_000_000d;
    /// <summary>eff_mu of an unseen edge = neutral μ − 2·initial rd (the prior lower-confidence bound).</summary>
    public const double UnratedEffMu = NeutralMu - 2d * InitialRd; // 800×1e9
}
