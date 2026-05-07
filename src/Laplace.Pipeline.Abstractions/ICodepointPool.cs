namespace Laplace.Pipeline.Abstractions;

using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;

/// <summary>
/// In-memory codepoint → entity_hash lookup. Loaded once at startup from
/// the SeedTableGenerator's seed_db_rows.tsv (or computed on-the-fly via
/// IIdentityHashing as a fallback). Decomposers hit this for tier-0 atom
/// hashes without round-tripping the database — the substrate's content-
/// addressed identity invariant means same codepoint always produces the
/// same hash regardless of which lookup mechanism resolves it.
/// </summary>
public interface ICodepointPool
{
    /// <summary>Load the codepoint → hash mapping from a generator TSV (idempotent).</summary>
    Task LoadFromTsvAsync(string seedDbRowsTsvPath, CancellationToken cancellationToken);

    /// <summary>
    /// Resolve a codepoint to its substrate entity_hash. Falls back to
    /// computing the hash if the codepoint is not in the pool yet.
    /// </summary>
    AtomId AtomIdFor(int codepoint);
}
