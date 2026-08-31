namespace Laplace.Engine.Synthesis;

public static partial class NativeInterop
{
    /// <summary>
    /// Keeps the native recipe/template that own <see cref="TensorSpec.Name"/> alive
    /// for exactly as long as the materialized managed manifest is consumed.
    /// </summary>
    public sealed class ArchTemplateManifestLease : IDisposable
    {
        private IntPtr _template;
        private IntPtr _recipe;

        internal ArchTemplateManifestLease(IntPtr template, IntPtr recipe, TensorSpec[] specs)
        {
            _template = template;
            _recipe = recipe;
            Specs = specs;
        }

        public TensorSpec[] Specs { get; }
        public int Count => Specs.Length;

        public void Dispose()
        {
            if (_template != IntPtr.Zero)
            {
                ArchTemplateFree(_template);
                _template = IntPtr.Zero;
            }
            if (_recipe != IntPtr.Zero)
            {
                RecipeFree(_recipe);
                _recipe = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Materializes the complete architecture tensor manifest from the exact config
    /// bytes while retaining the native owners of pointer-backed tensor names.
    /// </summary>
    public static ArchTemplateManifestLease? ArchTemplateManifestMaterialize(
        byte[] configJson, string architecture, out string? error)
    {
        ArgumentNullException.ThrowIfNull(configJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(architecture);

        IntPtr recipe;
        unsafe
        {
            fixed (byte* p = configJson)
                recipe = RecipeParse(p, (nuint)configJson.Length);
        }
        if (recipe == IntPtr.Zero)
        {
            error = "recipe_parse returned null";
            return null;
        }

        IntPtr template = ArchTemplateLoad(architecture);
        if (template == IntPtr.Zero)
        {
            RecipeFree(recipe);
            error = "arch_template_load returned null";
            return null;
        }

        int count = ArchTemplateRequiredTensorsComplete(template, recipe, out var specs);
        if (count <= 0)
        {
            ArchTemplateFree(template);
            RecipeFree(recipe);
            error = $"arch_template_required_tensors returned {count}";
            return null;
        }

        error = null;
        return new ArchTemplateManifestLease(template, recipe, specs);
    }

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
