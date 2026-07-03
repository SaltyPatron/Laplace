using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public interface IDecomposer : IAsyncDisposable
{
    Hash128 SourceId { get; }

    string SourceName { get; }

    int LayerOrder { get; }

    Hash128 TrustClassId { get; }

    Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default);

    IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        CancellationToken ct = default);

    Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default);

    IReadOnlyCollection<string> CanonicalNamesForReadback => Array.Empty<string>();
}
