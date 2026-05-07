namespace Laplace.Pipeline.Abstractions;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Submit edges + edge_member rows. Edge type and role hashes reference
/// substrate entities (resolved via <c>IConceptEntityResolver</c>) — never
/// hardcoded English string codes.
/// </summary>
public interface IEdgeEmission
{
    ValueTask EmitEdgeAsync(EdgeRecord record, CancellationToken cancellationToken);

    ValueTask EmitMemberAsync(EdgeMemberRecord record, CancellationToken cancellationToken);
}
