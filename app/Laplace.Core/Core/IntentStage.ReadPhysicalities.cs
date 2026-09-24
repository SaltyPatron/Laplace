using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>Native origin of one exported physicality row: its placement id and
/// the stage/row it was read from, in source order.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PhysicalityRowOriginNative
{
    internal Hash128 PlacementId;
    internal nuint SourceStageIndex;
    internal nuint SourceRowIndex;
    internal long ObservedAtUnixUs;
}

public sealed partial class IntentStage
{
    internal delegate void PhysicalityRowsVisitor(
        ReadOnlySpan<PhysicalityDescriptorInputNative> inputs,
        ReadOnlySpan<PhysicalityRowOriginNative> origins);

    internal delegate void PhysicalityRowsBudgetVisitor(
        ReadOnlySpan<PhysicalityDescriptorInputNative> inputs,
        ReadOnlySpan<PhysicalityRowOriginNative> origins,
        long retainedCaptureBytes);

    /// <summary>Copy native tuple bodies into a bounded native snapshot, then
    /// visit it while owned. Trajectory pointers must not escape the visitor.</summary>
    internal unsafe void VisitPhysicalityRows(long maximumBytes, PhysicalityRowsVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        VisitPhysicalityRows(maximumBytes,
            (inputs, origins, _) => visitor(inputs, origins));
    }

    /// <summary>The visitor receives the capture's actual retained allocation so
    /// managed row exports can account for its overlapping lifetime.</summary>
    internal unsafe void VisitPhysicalityRows(long maximumBytes, PhysicalityRowsBudgetVisitor visitor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentNullException.ThrowIfNull(visitor);
        IntPtr capture = IntPtr.Zero;
        try
        {
            int expected;
            lock (LaplaceCoreGate.Native)
            {
                ThrowIfDisposed();
                expected = PhysicalityCount;
                IntPtr stage = handle;
                int status = NativeInterop.PhysicalityDescriptorCaptureStageRows(
                    &stage, 1, checked((nuint)maximumBytes), &capture);
                GC.KeepAlive(this);
                if (status != 0 || capture == IntPtr.Zero)
                    throw new InvalidOperationException($"native physicality row export failed: {status}");
            }
            nuint inputsCount = 0, originCount = 0;
            var inputs = NativeInterop.PhysicalityDescriptorCaptureInputs(capture, &inputsCount);
            var origins = NativeInterop.PhysicalityDescriptorCaptureOrigins(capture, &originCount);
            if (inputsCount != (nuint)expected || originCount != inputsCount
                || (inputsCount != 0 && (inputs == null || origins == null)))
                throw new InvalidOperationException("native physicality row export count differs from its source stage");
            visitor(new ReadOnlySpan<PhysicalityDescriptorInputNative>(inputs, expected),
                new ReadOnlySpan<PhysicalityRowOriginNative>(origins, expected),
                checked((long)NativeInterop.PhysicalityDescriptorCaptureRetainedBytes(capture)));
        }
        finally
        {
            if (capture != IntPtr.Zero) NativeInterop.PhysicalityDescriptorCaptureFree(capture);
        }
    }
}

public static unsafe partial class NativeInterop
{
    [LibraryImport(Library, EntryPoint = "physicality_descriptor_capture_stage_rows")]
    internal static partial int PhysicalityDescriptorCaptureStageRows(
        IntPtr* stages, nuint stageCount, nuint maximumBytes, IntPtr* capture);

    [LibraryImport(Library, EntryPoint = "physicality_descriptor_capture_inputs")]
    internal static partial PhysicalityDescriptorInputNative* PhysicalityDescriptorCaptureInputs(
        IntPtr capture, nuint* count);

    [LibraryImport(Library, EntryPoint = "physicality_descriptor_capture_observations")]
    internal static partial PhysicalityRowOriginNative* PhysicalityDescriptorCaptureOrigins(
        IntPtr capture, nuint* count);

    [LibraryImport(Library, EntryPoint = "physicality_descriptor_capture_bytes")]
    internal static partial nuint PhysicalityDescriptorCaptureRetainedBytes(IntPtr capture);

    [LibraryImport(Library, EntryPoint = "physicality_descriptor_capture_free")]
    internal static partial void PhysicalityDescriptorCaptureFree(IntPtr capture);
}
