namespace Laplace.Engine.Synthesis;

public static partial class NativeInterop
{
    /// <summary>
    /// Materializes the complete architecture tensor manifest without imposing a
    /// caller-side tensor-count ceiling.
    ///
    /// The native ABI intentionally returns the required count without filling the
    /// buffer when the supplied capacity is too small. Callers that interpret any
    /// positive return as populated data therefore consume zero/default TensorSpec
    /// values. Probe with one real slot, allocate the exact required capacity, and
    /// retry so the returned count and populated array are one contract.
    /// </summary>
    public static int ArchTemplateRequiredTensorsComplete(
        IntPtr tmpl, IntPtr recipe, out TensorSpec[] specs)
    {
        specs = new TensorSpec[1];
        int required;
        unsafe
        {
            fixed (TensorSpec* p = specs)
                required = ArchTemplateRequiredTensorsNative(tmpl, recipe, p, 1);
        }

        if (required <= 0)
        {
            specs = [];
            return required;
        }

        // A one-tensor manifest fits the probe and was populated by the first call.
        if (required == 1)
            return 1;

        specs = new TensorSpec[required];
        int filled;
        unsafe
        {
            fixed (TensorSpec* p = specs)
                filled = ArchTemplateRequiredTensorsNative(
                    tmpl, recipe, p, (nuint)specs.Length);
        }

        // Template + parsed recipe are immutable across the two calls. A different
        // positive count means the ABI violated its capacity-query contract; never
        // hand a partially trustworthy manifest to synthesis.
        if (filled != required)
        {
            specs = [];
            return filled <= 0 ? filled : -4;
        }

        return filled;
    }
}
