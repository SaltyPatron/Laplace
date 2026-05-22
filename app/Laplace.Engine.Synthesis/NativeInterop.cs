using System.Runtime.InteropServices;

namespace Laplace.Engine.Synthesis;

/// <summary>
/// P/Invoke bindings to liblaplace_synthesis (per ADR 0024 + 0026).
///
/// Recipe parsing, architecture-template materialization, and GGUF
/// emission live here. Real bindings land Chunks 7-8 (Stories 7.1
/// onward).
/// </summary>
public static partial class NativeInterop
{
    private const string Library = "laplace_synthesis";

    [LibraryImport(Library, EntryPoint = "laplace_synthesis_version", StringMarshalling = StringMarshalling.Utf8)]
    public static partial string LaplaceSynthesisVersion();

    // TODO Chunk 7.16: recipe_parse / recipe_get_field / recipe_free
    // TODO Chunk 7.1-7.2: arch_template_load / arch_template_required_tensors / arch_template_free
    // TODO Chunk 7.3-7.10: feature_extractor_load / extract / output_dim / free
    // TODO Chunk 7.15: gguf_writer_create / add_metadata / add_tensor / finalize / free
}
