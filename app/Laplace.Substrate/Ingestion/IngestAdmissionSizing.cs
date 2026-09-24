using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

/// <summary>
/// Payload one intent holds until its working set is applied: the managed row
/// estimate and the native stage allocations it owns. Physicality count is the
/// number of typed realizations the intent carries.
/// </summary>
internal readonly record struct IngestAdmissionSizing(
    long SerializedBytes,
    long HeldNativeBytes,
    long Physicalities)
{
    internal IngestAdmissionSizing Add(IngestAdmissionSizing next) => new(
        checked(SerializedBytes + next.SerializedBytes),
        checked(HeldNativeBytes + next.HeldNativeBytes),
        checked(Physicalities + next.Physicalities));

    internal long ModeledSourcePayloadBytes => Math.Max(SerializedBytes, HeldNativeBytes);

    internal static IngestAdmissionSizing Measure(
        SubstrateChange change, long serializedBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ct.ThrowIfCancellationRequested();
        var stages = change.IntentStages.IsDefaultOrEmpty
            ? Array.Empty<IntentStage>()
            : change.IntentStages.Where(stage => !stage.IsInvalid).ToArray();
        return Of(serializedBytes, stages, change.Physicalities.Length);
    }

    internal static IngestAdmissionSizing MeasureGrowingBuilder(
        long serializedBytes, IReadOnlyList<IntentStage> stages, long managedPhysicalities) =>
        Of(serializedBytes, stages, managedPhysicalities);

    private static IngestAdmissionSizing Of(
        long serializedBytes, IReadOnlyList<IntentStage> stages, long managedPhysicalities)
    {
        long physicalities = managedPhysicalities;
        foreach (var stage in stages)
            if (!stage.IsInvalid) physicalities = checked(physicalities + stage.PhysicalityCount);
        return new(serializedBytes, HeldNativeStageBytes(stages), physicalities);
    }

    // These are the borrowed producer stages, which stay alive until apply.
    internal static long HeldNativeStageBytes(IEnumerable<IntentStage> stages)
    {
        long bytes = 0;
        var seen = new HashSet<IntentStage>(ReferenceEqualityComparer.Instance);
        foreach (var stage in stages)
            if (!stage.IsInvalid && seen.Add(stage))
                bytes = checked(bytes + stage.AllocatedBytes);
        return bytes;
    }
}

/// <summary>
/// Groups existing complete intents before apply. It never changes an intent or
/// retries an apply. An over-bound singleton still reaches the actual native
/// grant: a conservative no-reuse estimate is not evidence that it cannot fit.
/// </summary>
internal sealed class IngestAdmissionWindow(long maximumModeledBytes)
{
    internal long MaximumModeledBytes { get; } = maximumModeledBytes > 0
        ? maximumModeledBytes
        : throw new ArgumentOutOfRangeException(nameof(maximumModeledBytes));
    internal int Count { get; private set; }
    internal IngestAdmissionSizing Sizing { get; private set; }
    internal long ModeledSourcePayloadBytes => Sizing.ModeledSourcePayloadBytes;

    internal bool ShouldFlushBefore(IngestAdmissionSizing next) =>
        Count != 0 && Sizing.Add(next).ModeledSourcePayloadBytes > MaximumModeledBytes;

    internal void Add(IngestAdmissionSizing next)
    {
        if (ShouldFlushBefore(next))
            throw new InvalidOperationException("admission window must drain before another complete intent");
        Sizing = Sizing.Add(next);
        Count = checked(Count + 1);
    }

    internal void Reset()
    {
        Count = 0;
        Sizing = default;
    }
}
