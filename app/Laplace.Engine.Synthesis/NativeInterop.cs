using System.Runtime.InteropServices;

namespace Laplace.Engine.Synthesis;

/// <summary>
/// P/Invoke bindings to liblaplace_synthesis (per ADR 0024 + 0026).
///
/// Recipe parsing, architecture-template materialization, native package
/// emission, and proof/compatibility exports such as GGUF live here. Real
/// bindings land Chunks 7-8 (Stories 7.1 onward).
/// </summary>
public static partial class NativeInterop
{
    private const string Library = "laplace_synthesis";

    // Returns IntPtr (not string): C side returns .rodata string literal,
    // so `string` + StringMarshalling.Utf8 would have the source generator
    // call NativeMemory.Free() on the returned pointer post-copy and
    // crash with `free(): invalid pointer`.
    [LibraryImport(Library, EntryPoint = "laplace_synthesis_version")]
    private static partial IntPtr LaplaceSynthesisVersionPtr();

    public static string LaplaceSynthesisVersion() =>
        Marshal.PtrToStringUTF8(LaplaceSynthesisVersionPtr()) ?? string.Empty;

    // TODO Chunk 7.16: recipe_parse / recipe_get_field / recipe_free
    // TODO Chunk 7.1-7.2: arch_template_load / arch_template_required_tensors / arch_template_free
    // TODO Chunk 7.3-7.10: feature_extractor_load / extract / output_dim / free
    // TODO Chunk 7.15: native package writer + gguf proof writer bindings
}
