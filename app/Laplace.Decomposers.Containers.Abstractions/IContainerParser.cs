namespace Laplace.Decomposers.Containers.Abstractions;

/// <summary>
/// Static structural parse contract per ADR 0055. Implementations dissect
/// a container format into a stream of <see cref="ExplodedViewItem"/>s
/// WITHOUT executing any code in the container (no pickle unpickling, no
/// framework loaders, no <c>__reduce__</c> dispatch, no callbacks).
///
/// <para>
/// Concrete parsers land per-format (SafetensorsContainerParser,
/// PyTorchPickleContainerParser, OnnxProtobufContainerParser,
/// TfSavedModelContainerParser, Hdf5ContainerParser,
/// JupyterContainerParser, etc.) alongside the ModelDecomposer
/// (#191 / ADR 0043) — outside the scope of this abstractions project.
/// </para>
/// </summary>
public interface IContainerParser
{
    /// <summary>Human-readable parser name for logs / observability
    /// (e.g. <c>"safetensors"</c>, <c>"pytorch-pickle"</c>).</summary>
    string FormatName { get; }

    /// <summary>Returns true iff this parser recognises the given
    /// magic-bytes prefix. Implementations typically check 2–16 bytes;
    /// no I/O.</summary>
    bool CanParse(ReadOnlySpan<byte> magic);

    /// <summary>Dissect <paramref name="content"/> into a stream of
    /// exploded-view items. Yield-as-you-go — large containers don't
    /// have to fit in memory. The stream MUST NOT invoke any code from
    /// inside the container (per ADR 0055).</summary>
    IAsyncEnumerable<ExplodedViewItem> DissectAsync(Stream content, CancellationToken ct = default);
}
