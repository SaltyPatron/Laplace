using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.Model;

/// <summary>Binary checkpoint adapter — safetensors / model dir packaging.</summary>
public sealed class SafetensorsContentAdapter : IContentRecordAdapter
{
    public string Kind => "safetensors";

    public bool CanHandle(string path)
    {
        if (Directory.Exists(path))
        {
            return Directory.EnumerateFiles(path, "*.safetensors").Any()
                || File.Exists(Path.Combine(path, "config.json"));
        }
        return File.Exists(path) && path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase);
    }

    public ValueTask<ContentAdapterHandle> OpenAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string full = Path.GetFullPath(path);
        if (Directory.Exists(full))
        {
            Stream empty = new MemoryStream(Array.Empty<byte>());
            return ValueTask.FromResult(new ContentAdapterHandle(
                "safetensors-dir", empty,
                new Dictionary<string, string> { ["path"] = full, ["kind"] = "model-dir" }));
        }

        var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ValueTask.FromResult(new ContentAdapterHandle(
            "safetensors-file", fs,
            new Dictionary<string, string> { ["path"] = full, ["kind"] = "file" }));
    }
}
