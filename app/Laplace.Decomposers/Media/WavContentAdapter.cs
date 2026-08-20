using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Media;

/// <summary>Packaging adapter for PCM16 mono WAV.</summary>
public sealed class WavContentAdapter : IContentRecordAdapter
{
    public string Kind => "wav-pcm16";

    public bool CanHandle(string path) =>
        path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);

    public ValueTask<ContentAdapterHandle> OpenAsync(string path, CancellationToken ct = default)
    {
        var stream = IngestIo.OpenSequentialRead(path, useAsync: true);
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["format"] = "wav-pcm16",
            ["path"] = path,
        };
        return ValueTask.FromResult(new ContentAdapterHandle("wav", stream, meta));
    }
}
