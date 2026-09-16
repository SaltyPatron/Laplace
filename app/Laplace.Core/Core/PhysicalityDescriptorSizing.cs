using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// Source-local descriptor dimensions and native payload bounds. These bounds do
/// not predict database provider closure, elected views or complete admission.
/// Every actual native/SQL owner still enforces its unchanged allocation grant.
/// </summary>
public static class PhysicalityDescriptorSizing
{
    public readonly record struct Shape(ulong Forms, ulong StoredVertices, ulong MaximumVertices)
    {
        public Shape Add(Shape other) => new(
            checked(Forms + other.Forms),
            checked(StoredVertices + other.StoredVertices),
            Math.Max(MaximumVertices, other.MaximumVertices));

        public long PlanPayloadBound => PlanBound(this);
        public long CapturePayloadBound => CaptureBound(this);
    }

    public static unsafe Shape FromStages(IReadOnlyList<IntentStage> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        lock (LaplaceCoreGate.Native)
        {
            var pointers = new IntPtr[stages.Count];
            for (int i = 0; i < pointers.Length; i++)
            {
                var stage = stages[i] ?? throw new ArgumentException("null stage", nameof(stages));
                ObjectDisposedException.ThrowIf(stage.IsClosed || stage.IsInvalid, stage);
                pointers[i] = stage.DangerousNativeHandle;
            }
            PhysicalityDescriptorShapeNative result = default;
            fixed (IntPtr* values = pointers)
            {
                int status = NativeInterop.PhysicalityDescriptorStagesShape(
                    values, (nuint)pointers.Length, &result);
                GC.KeepAlive(stages);
                RequireSuccess(status, "source stage shape");
            }
            return new((ulong)result.Forms, (ulong)result.StoredVertices, (ulong)result.MaximumVertices);
        }
    }

    /// <summary>
    /// Constant-time framing-inclusive shape upper bound for a stage still being
    /// composed. This does not validate tuple framing or identity; completed intent
    /// sizing uses FromStages and ordinary admission performs the full validation.
    /// </summary>
    public static unsafe Shape FromStageBound(IntentStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        lock (LaplaceCoreGate.Native)
        {
            ObjectDisposedException.ThrowIf(stage.IsClosed || stage.IsInvalid, stage);
            PhysicalityDescriptorShapeNative result = default;
            int status = NativeInterop.PhysicalityDescriptorStageShapeBound(
                stage.DangerousNativeHandle, &result);
            GC.KeepAlive(stage);
            RequireSuccess(status, "growing source stage shape bound");
            return new((ulong)result.Forms, (ulong)result.StoredVertices, (ulong)result.MaximumVertices);
        }
    }

    public static unsafe long TuplePayloadBound(
        ulong entities, ulong physicalities, ulong storedVertices, ulong attestations)
    {
        nuint bytes = 0;
        int status = NativeInterop.IntentStageTuplePayloadBound(
            checked((nuint)entities), checked((nuint)physicalities),
            checked((nuint)storedVertices), checked((nuint)attestations), &bytes);
        RequireSuccess(status, "intent tuple payload bound");
        return checked((long)bytes);
    }

    private static unsafe long PlanBound(Shape shape)
    {
        nuint bytes = 0;
        int status = NativeInterop.PhysicalityDescriptorPlanPayloadBound(
            checked((nuint)shape.Forms), checked((nuint)shape.StoredVertices),
            checked((nuint)shape.MaximumVertices), &bytes);
        RequireSuccess(status, "descriptor plan payload bound");
        return checked((long)bytes);
    }

    private static unsafe long CaptureBound(Shape shape)
    {
        nuint bytes = 0;
        int status = NativeInterop.PhysicalityDescriptorCapturePayloadBound(
            checked((nuint)shape.Forms), checked((nuint)shape.StoredVertices), &bytes);
        RequireSuccess(status, "descriptor capture payload bound");
        return checked((long)bytes);
    }

    private static void RequireSuccess(int status, string operation)
    {
        if (status != 0)
            throw new InvalidOperationException($"native {operation} refused: {status}");
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PhysicalityDescriptorShapeNative
{
    internal nuint Forms;
    internal nuint StoredVertices;
    internal nuint MaximumVertices;
}

public static unsafe partial class NativeInterop
{
    [LibraryImport(Library, EntryPoint = "intent_stage_tuple_payload_bound")]
    internal static partial int IntentStageTuplePayloadBound(
        nuint entities, nuint physicalities, nuint storedVertices, nuint attestations, nuint* bytes);

    [LibraryImport(Library, EntryPoint = "physicality_descriptor_stages_shape")]
    internal static partial int PhysicalityDescriptorStagesShape(
        IntPtr* stages, nuint stageCount, PhysicalityDescriptorShapeNative* shape);

    [LibraryImport(Library, EntryPoint = "physicality_descriptor_stage_shape_bound")]
    internal static partial int PhysicalityDescriptorStageShapeBound(
        IntPtr stage, PhysicalityDescriptorShapeNative* shape);

    [LibraryImport(Library, EntryPoint = "physicality_descriptor_plan_payload_bound")]
    internal static partial int PhysicalityDescriptorPlanPayloadBound(
        nuint forms, nuint storedVertices, nuint maximumVertices, nuint* bytes);

    [LibraryImport(Library, EntryPoint = "physicality_descriptor_capture_payload_bound")]
    internal static partial int PhysicalityDescriptorCapturePayloadBound(
        nuint forms, nuint storedVertices, nuint* bytes);
}
