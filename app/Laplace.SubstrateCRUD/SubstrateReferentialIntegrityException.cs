namespace Laplace.SubstrateCRUD;

/// <summary>
/// Thrown by the writer's SET-BASED referential proof when a staged row
/// references an entity id that is neither staged in the same batch nor
/// present in <c>laplace.entities</c>. Raised BEFORE any COPY is issued —
/// nothing is written.
///
/// This is the bulk-path enforcement of the same invariant the schema's FK
/// constraints express (the constraints remain in place, guarding every
/// non-bulk write path; <c>scripts/verify-fk.sql</c> remains the independent
/// audit). A miss here is a decomposer bug — content-addressed emit must
/// stage every entity it references — so it is FATAL, never transient-retried.
/// </summary>
public sealed class SubstrateReferentialIntegrityException : Exception
{
    /// <summary>How many distinct referenced ids failed to resolve.</summary>
    public int MissingCount { get; }

    public SubstrateReferentialIntegrityException(int missingCount, string firstMissingHex)
        : base($"referential proof failed: {missingCount} referenced entity id(s) neither staged in this batch "
             + $"nor present in laplace.entities (decomposer bug — first missing: {firstMissingHex}); "
             + "nothing was written")
    {
        MissingCount = missingCount;
    }
}
