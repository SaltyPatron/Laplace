using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

[StructLayout(LayoutKind.Sequential)]
internal struct PhysicalityObservationNative
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
        ReadOnlySpan<PhysicalityObservationNative> observations);

    /// <summary>Copy native tuple bodies into a bounded native snapshot, then
    /// visit it while owned. Trajectory pointers must not escape the visitor.
    /// This transport does not perform descriptor admission.</summary>
    internal unsafe void VisitPhysicalityRows(long maximumBytes, PhysicalityRowsVisitor visitor)
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
            nuint inputsCount = 0, observationCount = 0;
            var inputs = NativeInterop.PhysicalityDescriptorCaptureInputs(capture, &inputsCount);
            var observations = NativeInterop.PhysicalityDescriptorCaptureObservations(capture, &observationCount);
            if (inputsCount != (nuint)expected || observationCount != inputsCount
                || (inputsCount != 0 && (inputs == null || observations == null)))
                throw new InvalidOperationException("native physicality row export count differs from its source stage");
            visitor(new ReadOnlySpan<PhysicalityDescriptorInputNative>(inputs, expected),
                new ReadOnlySpan<PhysicalityObservationNative>(observations, expected));
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
    internal static partial PhysicalityObservationNative* PhysicalityDescriptorCaptureObservations(
        IntPtr capture, nuint* count);

    [LibraryImport(Library, EntryPoint = "physicality_descriptor_capture_free")]
    internal static partial void PhysicalityDescriptorCaptureFree(IntPtr capture);
}
