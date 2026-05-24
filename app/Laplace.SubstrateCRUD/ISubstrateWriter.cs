using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

/// <summary>
/// The single substrate write surface every decomposer routes through —
/// per ADR 0050 + RULES R5/R6/R16. There is exactly one implementation
/// (<see cref="Npgsql.NpgsqlSubstrateWriter"/>); per-source bespoke insert
/// code is forbidden.
/// </summary>
public interface ISubstrateWriter
{
    /// <summary>Apply one <see cref="SubstrateChange"/> intent. Idempotent on
    /// repeat application of the same intent (ON CONFLICT DO NOTHING per RULES
    /// R5). Race-tolerant under concurrent writers of overlapping intents.
    /// </summary>
    Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default);
}
