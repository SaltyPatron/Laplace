namespace Laplace.Cognition.Abstractions;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// Detect when current substrate state is insufficient to answer a query —
/// empty Voronoi cells (no model has an opinion on this entity), low-rated
/// paths (all candidate edges have high RD), dead-end traversals (no edges
/// from current frontier), frayed edges encountered during traversal.
///
/// Signals are surfaced to the Gödel Engine for refinement, source-ingestion
/// proposal, or honest abstention (NEVER fabrication — the substrate has no
/// token-sampling layer that could hallucinate; missing knowledge surfaces
/// as a structured "here's why I can't answer yet, here's what would be
/// needed").
/// </summary>
public interface IIncompletenessSignal
{
    Task<IReadOnlyList<IncompletenessSignalRecord>> DetectAsync(
        AtomId queryContext,
        CancellationToken cancellationToken);
}

public record IncompletenessSignalRecord(
    string Kind,
    string Detail,
    AtomId? RelatedEntity,
    string? SuggestedRemediation);
