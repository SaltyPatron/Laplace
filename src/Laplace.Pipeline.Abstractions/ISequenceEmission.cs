namespace Laplace.Pipeline.Abstractions;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Submit denormalized fast-offset reconstruction rows: per document, the
/// (leaf_position → leaf_atom_hash) mapping. Used by recomposers to walk a
/// document's leaves in O(1) per position rather than O(tier-depth) per
/// recursive descent through the composition tree.
/// </summary>
public interface ISequenceEmission
{
    ValueTask EmitAsync(SequenceRecord record, CancellationToken cancellationToken);
}
