namespace Laplace.Decomposers.Containers.Abstractions;

public abstract record ExplodedViewItem(string Path);

public sealed record Entry(string Path, long Offset, long Length, string? MimeHint = null)
    : ExplodedViewItem(Path);

public sealed record TensorReference(
    string                Path,
    string                Dtype,
    IReadOnlyList<long>   Shape,
    long                  ByteOffset,
    long                  ByteLength,
    string?               Source = null)
    : ExplodedViewItem(Path);

public sealed record PythonClassReference(string Path, string Module, string Qualname)
    : ExplodedViewItem(Path);

public sealed record EmbeddedText(string Path, string Encoding, ReadOnlyMemory<byte> Bytes)
    : ExplodedViewItem(Path);

public sealed record EmbeddedContainer(string Path, string MagicHint, ReadOnlyMemory<byte> Bytes)
    : ExplodedViewItem(Path);
