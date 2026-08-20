using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Media;

/// <summary>Packaging adapter for <c>*.rgba</c> planar packages (see <see cref="RgbaFileCodec"/>).</summary>
public sealed class RgbaContentAdapter : IContentRecordAdapter
{
    public string Kind => "rgba-planar";

    public bool CanHandle(string path) =>
        path.EndsWith(".rgba", StringComparison.OrdinalIgnoreCase);

    public async ValueTask<ContentAdapterHandle> OpenAsync(string path, CancellationToken ct = default)
    {
        var stream = IngestIo.OpenSequentialRead(path, useAsync: true);
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["format"] = "rgba",
            ["path"] = path,
        };
        return new ContentAdapterHandle("rgba", stream, meta);
    }
}
