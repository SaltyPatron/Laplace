namespace Laplace.Pipeline.Abstractions;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Submit geometric records into the partitioned physicality table. The
/// partition routing key is <c>physicality_type_hash</c> — substrate
/// codepoint atom S³ position lives in one partition; AI model fireflies
/// (per-token-per-model S³ position) live in a SEPARATE partition;
/// composition centroids (4-ball positions) live in another; per-modality
/// geometries (audio waveform, image patch grid, protein backbone 3D, etc.)
/// each have their own partitions.
///
/// Physicality types are themselves substrate entities (composed of their
/// codepoint LINESTRING names) — open vocabulary, not enum.
/// </summary>
public interface IPhysicalityEmission
{
    ValueTask EmitAsync(PhysicalityRecord record, CancellationToken cancellationToken);
}
