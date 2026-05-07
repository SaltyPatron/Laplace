namespace Laplace.Pipeline.Abstractions;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Submit entity records to the bounded ingestion channel. Entities are
/// content-addressed; <c>ON CONFLICT DO NOTHING</c> dedupes within-session
/// and across-session.
/// </summary>
public interface IEntityEmission
{
    ValueTask EmitAsync(EntityRecord record, CancellationToken cancellationToken);
}
