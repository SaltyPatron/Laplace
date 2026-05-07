namespace Laplace.Decomposers.Abstractions;

using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// Decompose a number literal into a substrate composition entity made of
/// digit-codepoint atoms. The number 255 becomes the composition [2, 5, 5]
/// of the digit-2 and digit-5 codepoint atoms (one entity, references the
/// digit atoms with rle_count=2 for the trailing pair). The number 3.14
/// becomes [3, '.', 1, 4]. The number 440 in 440Hz is the same entity as
/// the number 440 anywhere else — port number, calorie count, line number,
/// pixel value &lt;255 if applicable.
///
/// Numbers are NOT separate atom types — they are tier-2+ compositions of
/// digit codepoints. This is what makes "how many things intersect with
/// 3.14?" a first-class query across math, code, text, sensor data, etc.
/// </summary>
public interface INumberDecomposition
{
    /// <summary>Decompose a number expressed as a string (preserves negative sign, decimal, exponent).</summary>
    Task<AtomId> DecomposeAsync(string numberLiteral, AtomId provenanceSource, CancellationToken cancellationToken);
}
