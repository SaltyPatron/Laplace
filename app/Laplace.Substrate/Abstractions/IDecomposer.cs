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

    /// <summary>Rough average bytes per input record — sizes batches/buffers to the real
    /// data shape instead of a global 512-byte fiction. A unicode codepoint is tens of
    /// bytes, a UD sentence a few KB, a wiktionary record tens of KB. Override for sources
    /// far from the neutral default; the sizer clamps round-trip counts at both extremes.</summary>
    int EstimatedBytesPerRecord => 512;

    IReadOnlyCollection<string> CanonicalNamesForReadback => Array.Empty<string>();
}
