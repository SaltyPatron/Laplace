using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

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
        long cap = options.MaxInputUnits;
        long consumed = 0;
        int bn = 0;

        // Rule #8 working-set mode: one builder spans the stream (the
        // builder's own id-dedup and attestation pre-merge ARE the
        // client-side working-set dedup for this lane); the only mid-stream
        // yield is the memory-budget valve. The content bank survives builds
        // — the runner resets it at the working-set boundary after the apply
        // commits. Per-batch mode remains only behind LAPLACE_WORKING_SET=0.
        bool workingSet = WorkingSetMode.Enabled;
        var builder = NewBuilder(sourceId, labelPrefix, bn, batchSize, reader);
        int inBatch = 0;
        int sinceBudgetCheck = 0;

        await foreach (var record in records.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (cap > 0 && consumed >= cap) break;

            compose(record, builder);
            consumed++;
            inBatch++;

            if (workingSet)
            {
                if (++sinceBudgetCheck >= 8_192)
                {
                    sinceBudgetCheck = 0;
                    if (builder.StagedBytesEstimate >= WorkingSetMode.BudgetBytes && !options.DryRun)
                    {
                        yield return await builder.SetInputUnitsConsumed(inBatch).BuildAsync(ct);
                        builder = NewBuilder(sourceId, labelPrefix, ++bn, batchSize, reader);
                        inBatch = 0;
                    }
                }
                continue;
            }

            if (inBatch >= batchSize)
            {
                if (!options.DryRun)
                {
                    yield return await builder.SetInputUnitsConsumed(inBatch).BuildAsync(ct);
                    IntentStage.ResetContentBank();
                }
                builder = NewBuilder(sourceId, labelPrefix, ++bn, batchSize, reader);
                inBatch = 0;
            }
        }

        if (inBatch > 0 && !options.DryRun)
        {
            yield return await builder.SetInputUnitsConsumed(inBatch).BuildAsync(ct);
            if (!workingSet) IntentStage.ResetContentBank();
        }
    }

    private static SubstrateChangeBuilder NewBuilder(
        Hash128 sourceId, string prefix, int bn, int batchSize, ISubstrateReader? _)
        => new SubstrateChangeBuilder(sourceId, $"{prefix}/{bn}", null,
                entityCapacity: batchSize * 4,
                physicalityCapacity: batchSize * 2,
                attestationCapacity: batchSize * 8);
}
