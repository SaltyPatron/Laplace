namespace Laplace.Decomposers.Abstractions;

using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// Decompose a unit-bearing quantity (e.g., 440Hz, 1024B, 3.14m, 60BPM) into
/// (number-composition entity, unit-composition entity) plus a quantity edge
/// joining them. Units are themselves substrate composition entities (e.g.,
/// "Hz" is [H, z]). Dimensional-analysis edges relate compatible units
/// (Hz ↔ THz ↔ BPM ↔ s⁻¹) so cross-modal frequency intersections work.
/// </summary>
public interface IUnitDecomposition
{
    Task<AtomId> DecomposeAsync(string quantityLiteral, AtomId provenanceSource, CancellationToken cancellationToken);
}
