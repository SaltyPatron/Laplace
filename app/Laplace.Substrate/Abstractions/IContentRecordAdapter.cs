namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Content→record adapter port. Tree-sitter is the text-alphabet adapter;
/// safetensors / binary codecs and future protocol decoders are other kinds
/// behind this one membrane. The alphabet is a <see cref="ISeedScope"/> instance.
/// Do not hard-bind extraction to tree-sitter.
/// </summary>
public interface IContentRecordAdapter
{
    /// <summary>Stable kind id (e.g. "tree-sitter-text", "safetensors", "raw-bytes").</summary>
    string Kind { get; }

    /// <summary>True when this adapter can unpack <paramref name="path"/>.</summary>
    bool CanHandle(string path);

    /// <summary>Open packaging and return a content handle for the valet.</summary>
    ValueTask<ContentAdapterHandle> OpenAsync(string path, CancellationToken ct = default);
}

/// <summary>Opened content handle — dispose releases underlying streams/maps.</summary>
public sealed class ContentAdapterHandle : IAsyncDisposable
{
    private readonly Func<ValueTask>? _dispose;

    public ContentAdapterHandle(
        string formatId,
        Stream content,
        IReadOnlyDictionary<string, string>? metadata = null,
        Func<ValueTask>? dispose = null)
    {
        FormatId = formatId;
        Content = content;
        Metadata = metadata ?? new Dictionary<string, string>();
        _dispose = dispose;
    }

    public string FormatId { get; }
    public Stream Content { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    public async ValueTask DisposeAsync()
    {
        if (_dispose is not null)
            await _dispose();
        await Content.DisposeAsync();
    }
}
