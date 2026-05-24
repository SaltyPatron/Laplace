namespace Laplace.Decomposers.Containers.Abstractions;

/// <summary>
/// Base record for items produced by a static structural container parse
/// per ADR 0055 — "we're literally semantically dissecting them to an
/// exploded view". A container's bytes never get loaded by a framework
/// runtime; the parser walks the format's static structure and emits one
/// of these per discovered element.
///
/// <para>
/// Concrete subrecords map to the kinds of things a container can hold:
/// directory entries, tensor references, Python class references (from
/// pickle opcodes — never executed), embedded text blobs, embedded
/// containers (e.g. a .tar inside a .zip).
/// </para>
/// </summary>
public abstract record ExplodedViewItem(string Path);

/// <summary>A generic named entry within the container — file, directory,
/// member, etc. without a more specific shape.</summary>
public sealed record Entry(string Path, long Offset, long Length, string? MimeHint = null)
    : ExplodedViewItem(Path);

/// <summary>A tensor / weight buffer with shape + dtype + on-disk extent.
/// Never loaded; the substrate consumes (shape, dtype, offset, length).</summary>
public sealed record TensorReference(
    string                Path,
    string                Dtype,           // e.g. "F32", "BF16", "F64", "I8"
    IReadOnlyList<long>   Shape,
    long                  ByteOffset,
    long                  ByteLength,
    string?               Source = null)   // e.g. "safetensors", "pytorch.storage"
    : ExplodedViewItem(Path);

/// <summary>A reference to a Python class encountered in a pickle stream —
/// resolved STATICALLY from the GLOBAL opcode (module + qualname) per
/// ADR 0055. Never invoked. The downstream TreeSitterDecomposer resolves
/// the actual class definition from the Python source corpus.</summary>
public sealed record PythonClassReference(string Path, string Module, string Qualname)
    : ExplodedViewItem(Path);

/// <summary>An embedded text blob — README, license, config.json, model
/// card, etc. The substrate's text-bearing decomposers (when running on
/// the exploded view) hand the bytes through TextDecomposer for normal
/// tier-tree decomposition.</summary>
public sealed record EmbeddedText(string Path, string Encoding, ReadOnlyMemory<byte> Bytes)
    : ExplodedViewItem(Path);

/// <summary>An embedded container — e.g. a .tar inside a .zip. The
/// substrate recurses by feeding the embedded bytes back into the
/// magic-byte registry to find the right inner parser.</summary>
public sealed record EmbeddedContainer(string Path, string MagicHint, ReadOnlyMemory<byte> Bytes)
    : ExplodedViewItem(Path);
