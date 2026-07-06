using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Microsoft.Extensions.Logging.Abstractions;

namespace Laplace.Decomposers.Tests;

/// <summary>
/// Shared test doubles for decomposer tests. Previously copy-pasted as private
/// nested classes in every per-source test file.
/// </summary>
internal sealed class FakeContext(ISubstrateWriter writer) : IDecomposerContext
{
    public FakeContext(string ecosystemPath, ISubstrateWriter writer) : this(writer)
        => EcosystemPath = ecosystemPath;

    public string EcosystemPath { get; init; } = TestIngestPaths.Root;
    public ISubstrateWriter Writer { get; } = writer;
    public ISubstrateReader Reader { get; init; } = new NullReader();
    public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
    public string SubstrateVersion => "test";
}

internal sealed class NullWriter : ISubstrateWriter
{
    public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
        => Task.FromResult(new ApplyResult(0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, false));
}

internal sealed class CapturingWriter : ISubstrateWriter
{
    public List<SubstrateChange> Captured { get; } = new();
    public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
    {
        Captured.Add(change);
        return Task.FromResult(new ApplyResult(
            change.Entities.Length, change.Entities.Length,
            change.Physicalities.Length, change.Physicalities.Length,
            change.Attestations.Length, change.Attestations.Length, 4, TimeSpan.Zero, false));
    }
}

internal sealed class NullReader : ISubstrateReader
{
    public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
        => Task.FromResult(0L);
    public Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
        => Task.FromResult(new byte[(candidates.Count + 7) / 8]);
}
