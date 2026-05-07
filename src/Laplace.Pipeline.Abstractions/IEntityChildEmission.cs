namespace Laplace.Pipeline.Abstractions;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Submit composition child relationships (parent → ordered children with
/// per-run RLE count). RLE counts are non-trivial — same content adjacent
/// shows up as one row with rle_count &gt; 1, never as multiple rows.
/// </summary>
public interface IEntityChildEmission
{
    ValueTask EmitAsync(EntityChildRecord record, CancellationToken cancellationToken);
}
