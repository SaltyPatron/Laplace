using System.Buffers;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Structured;

/// <summary>One compiled source recipe, native parsing/composition, and bulk writer handoffs.</summary>
public sealed class NativeSourceRecipe
{
    private readonly byte[] _program;
    public SemanticSourceRecipe Recipe { get; }

    public NativeSourceRecipe(SemanticSourceRecipe recipe, int recordDepth = 2)
    {
        Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        _program = NativeRecipeCompiler.Compile(recipe, recordDepth);
    }

    public async IAsyncEnumerable<SubstrateChange> ReadChangesAsync(
        Stream input,
        Hash128 sourceId,
        double sourceTrust,
        string artifactLabel,
        int maximumRows,
        long maximumBytes,
        int readBufferBytes,
        int commitEpoch = 0,
        Func<Stream>? openPrescan = null,
        OrderedCompositionComponent? fileHead = null,
        Action<OrderedCompositionComponent>? onFileRoot = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactLabel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readBufferBytes);
        using NativeRecipeStream native = NativeRecipeStream.Open(_program, sourceId, sourceTrust);
        if (fileHead is { } head) native.SetFile(head);
        // Drain parser events and tuple output between transport windows. A large
        // I/O envelope must not queue an entire source's expanded syntax tree.
        int feedBytes = Math.Min(readBufferBytes, 64 * 1024);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(feedBytes);
        long batch = 0;
        try
        {
            if (native.RequiresPrescan)
            {
                // The source's own id tables (synset -> ILI, sense -> word) precede
                // lowering, so every reference resolves to what it denotes.
                if (openPrescan is null)
                    throw new InvalidDataException(
                        $"Recipe {Recipe.Authority}/{Recipe.Release} declares identity tables; '{artifactLabel}' must be readable twice.");
                await using Stream scan = openPrescan();
                bool scanned = false;
                while (!scanned)
                {
                    int count = await scan.ReadAsync(buffer.AsMemory(0, feedBytes), ct).ConfigureAwait(false);
                    scanned = count == 0;
                    native.Prescan(buffer.AsSpan(0, count), scanned);
                }
            }
            bool final = false;
            while (!final)
            {
                int count = await input.ReadAsync(buffer.AsMemory(0, feedBytes), ct)
                    .ConfigureAwait(false);
                final = count == 0;
                native.Feed(buffer.AsSpan(0, count), final);
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    IntentStage? stage = native.Drain(maximumRows, maximumBytes, out ulong completed);
                    if (stage is null) break;
                    // Ownership transfers to the common writer with the returned change.
                    // No entity, field, or source record is marshalled individually.
                    SubstrateChange change;
                    try
                    {
                        using var builder = new SubstrateChangeBuilder(
                            sourceId, $"{artifactLabel}/recipe/{Recipe.RecipeId}/batch/{batch++}",
                            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0);
                        builder.AddIntentStage(stage, sourceId);
                        builder.DeclareSourcePrior(sourceTrust)
                            .SetInputUnitsConsumed(checked((long)completed))
                            .SetCommitEpoch(commitEpoch);
                        change = builder.Build();
                    }
                    catch
                    {
                        stage.Dispose();
                        throw;
                    }
                    yield return change;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (native.FileRoot is { } root) onFileRoot?.Invoke(root);
    }
}
