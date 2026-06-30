using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Single batching loop shared by every record-based decomposer.
/// Handles: bounded-memory streaming, deferred-content descent probe,
/// DryRun, MaxInputUnits, SetInputUnitsConsumed, and content-bank reset per batch.
///
/// Each call to BuildAsync drains the current batch from native memory; the content
/// bank is reset immediately after so that the next batch starts with a clean slate.
/// This keeps peak native heap proportional to ONE batch, not the entire source file.
/// </summary>
public static class DecomposerBatch
{
    public static async IAsyncEnumerable<SubstrateChange> RunAsync<T>(
        IAsyncEnumerable<T> records,
        Action<T, SubstrateChangeBuilder> compose,
        Hash128 sourceId,
        string labelPrefix,
        int batchSize,
        ISubstrateReader? reader,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        long cap     = options.MaxInputUnits;
        long consumed = 0;
        int  bn      = 0;

        var builder  = NewBuilder(sourceId, labelPrefix, bn, batchSize, reader);
        int  inBatch = 0;

        await foreach (var record in records.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (cap > 0 && consumed >= cap) break;

            compose(record, builder);
            consumed++;
            inBatch++;

            if (inBatch >= batchSize)
            {
                if (!options.DryRun)
                {
                    yield return await builder.SetInputUnitsConsumed(inBatch).BuildAsync(ct);
                    IntentStage.ResetContentBank();
                }
                builder  = NewBuilder(sourceId, labelPrefix, ++bn, batchSize, reader);
                inBatch  = 0;
            }
        }

        if (inBatch > 0 && !options.DryRun)
        {
            yield return await builder.SetInputUnitsConsumed(inBatch).BuildAsync(ct);
            IntentStage.ResetContentBank();
        }
    }

    private static SubstrateChangeBuilder NewBuilder(
        Hash128 sourceId, string prefix, int bn, int batchSize, ISubstrateReader? reader)
        => new SubstrateChangeBuilder(sourceId, $"{prefix}/{bn}", null,
                entityCapacity:      batchSize * 4,
                physicalityCapacity: batchSize * 2,
                attestationCapacity: batchSize * 8)
            .EnableDeferredContent(IntentStage.IsBulkFreshBypass ? null : reader);
}
