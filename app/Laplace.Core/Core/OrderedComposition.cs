namespace Laplace.Engine.Core;

/// <summary>
/// A constituent already realized by the substrate. The native ordered-composition
/// operation owns parent identity, altitude, geometry, trajectory, and staging.
/// </summary>
public readonly record struct OrderedCompositionComponent(
    Hash128 Id, byte Tier, double CoordX, double CoordY, double CoordZ, double CoordM,
    uint Atom = 0, bool HasAtom = false);

public readonly record struct OrderedCompositionResult(
    Hash128 Id, byte Tier, double CoordX, double CoordY, double CoordZ, double CoordM,
    Hilbert128 Hilbert);

/// <summary>
/// One declared ordered realization. <paramref name="TypeId"/> is supplied by
/// the governed caller; it is never inferred from altitude.
/// </summary>
public sealed record OrderedCompositionRequest(
    OrderedCompositionComponent[] Components,
    Hash128 TypeId,
    Hash128 SourceId,
    long ObservedAtUnixUs);

/// <summary>Thin managed transport over the native bulk composition/stage operation.</summary>
public static unsafe class OrderedComposition
{
    public static OrderedCompositionResult[] ComposeBatch(
        IReadOnlyList<OrderedCompositionRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var results = new OrderedCompositionResult[requests.Count];
        Invoke(requests, null, results);
        return results;
    }

    public static void StageBatch(
        IntentStage stage,
        IReadOnlyList<OrderedCompositionRequest> requests,
        Span<OrderedCompositionResult> results)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(requests);
        if (results.Length < requests.Count)
            throw new ArgumentException("results must hold one result per request", nameof(results));
        Invoke(requests, stage, results);
    }

    private static void Invoke(
        IReadOnlyList<OrderedCompositionRequest> requests,
        IntentStage? stage,
        Span<OrderedCompositionResult> results)
    {
        if (requests.Count == 0) return;

        int totalComponents = 0;
        for (int i = 0; i < requests.Count; i++)
        {
            OrderedCompositionRequest request = requests[i]
                ?? throw new ArgumentException("request must not be null", nameof(requests));
            if (request.Components is not { Length: > 0 })
                throw new ArgumentException("each request needs at least one component", nameof(requests));
            totalComponents = checked(totalComponents + request.Components.Length);
        }

        var nativeRequests = new NativeInterop.OrderedCompositionRequestNative[requests.Count];
        var nativeComponents = new NativeInterop.OrderedCompositionComponentNative[totalComponents];
        var nativeResults = new NativeInterop.OrderedCompositionResultNative[requests.Count];
        fixed (NativeInterop.OrderedCompositionRequestNative* native = nativeRequests)
        fixed (NativeInterop.OrderedCompositionComponentNative* components = nativeComponents)
        fixed (NativeInterop.OrderedCompositionResultNative* nativeResult = nativeResults)
        {
            int componentOffset = 0;
            for (int i = 0; i < requests.Count; i++)
            {
                OrderedCompositionRequest request = requests[i];
                for (int j = 0; j < request.Components.Length; j++)
                {
                    OrderedCompositionComponent component = request.Components[j];
                    components[componentOffset + j] = new NativeInterop.OrderedCompositionComponentNative
                    {
                        Id = component.Id,
                        Coord0 = component.CoordX,
                        Coord1 = component.CoordY,
                        Coord2 = component.CoordZ,
                        Coord3 = component.CoordM,
                        Atom = component.Atom,
                        Tier = component.Tier,
                        HasAtom = component.HasAtom ? (byte)1 : (byte)0,
                    };
                }
                native[i] = new NativeInterop.OrderedCompositionRequestNative
                {
                    Components = (IntPtr)(components + componentOffset),
                    ComponentCount = (nuint)request.Components.Length,
                    TypeId = request.TypeId,
                    SourceId = request.SourceId,
                    ObservedAtUnixUs = request.ObservedAtUnixUs,
                };
                componentOffset += request.Components.Length;
            }

            int rc = stage is null
                ? NativeInterop.OrderedCompositionComposeBatch(
                    native, (nuint)requests.Count, nativeResult)
                : NativeInterop.OrderedCompositionStageBatch(
                    stage.DangerousNativeHandle, native, (nuint)requests.Count, nativeResult);
            if (rc != 0)
                throw new InvalidOperationException($"laplace_ordered_composition batch returned {rc}");
        }
        for (int i = 0; i < requests.Count; i++)
            results[i] = new OrderedCompositionResult(
                nativeResults[i].Id, nativeResults[i].Tier,
                nativeResults[i].Coord0, nativeResults[i].Coord1,
                nativeResults[i].Coord2, nativeResults[i].Coord3,
                nativeResults[i].Hilbert);
    }
}
