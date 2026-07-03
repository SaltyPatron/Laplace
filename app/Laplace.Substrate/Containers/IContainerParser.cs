namespace Laplace.Decomposers.Containers.Abstractions;

public interface IContainerParser
{
    string FormatName { get; }

    bool CanParse(ReadOnlySpan<byte> magic);

    IAsyncEnumerable<ExplodedViewItem> DissectAsync(Stream content, CancellationToken ct = default);
}
