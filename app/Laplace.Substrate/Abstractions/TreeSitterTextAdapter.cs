namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Text-alphabet adapter: file/dir of source text unpacked for grammar/compose valets.
/// Tree-sitter grammars remain the default extractor behind this port; the adapter
/// itself does not bind callers to a specific grammar engine.
/// </summary>
public sealed class TreeSitterTextAdapter : IContentRecordAdapter
{
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".jsonl", ".xml", ".conllu", ".tsv", ".csv",
        ".py", ".cs", ".cpp", ".c", ".h", ".rs", ".go", ".js", ".ts",
    };

    public string Kind => "tree-sitter-text";

    public bool CanHandle(string path)
    {
        if (Directory.Exists(path)) return true;
        if (!File.Exists(path)) return false;
        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) || TextExt.Contains(ext);
    }

    public ValueTask<ContentAdapterHandle> OpenAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(path))
        {
            // Directory roots are enumerated by the valet; expose a marker stream.
            Stream empty = new MemoryStream(Array.Empty<byte>());
            return ValueTask.FromResult(new ContentAdapterHandle(
                "text-dir", empty,
                new Dictionary<string, string> { ["path"] = Path.GetFullPath(path), ["kind"] = "directory" }));
        }

        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ValueTask.FromResult(new ContentAdapterHandle(
            "text-file", fs,
            new Dictionary<string, string> { ["path"] = Path.GetFullPath(path), ["kind"] = "file" }));
    }
}
