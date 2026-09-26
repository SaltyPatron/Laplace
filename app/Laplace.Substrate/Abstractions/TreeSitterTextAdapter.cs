using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Content adapter for a file or directory of source text, handed to grammar parsing and
/// composition. It binds callers to no grammar engine; tree-sitter grammars are the
/// default extractor behind it.
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
            // A directory root is enumerated by the caller; expose an empty marker stream.
            Stream empty = new MemoryStream(Array.Empty<byte>());
            return ValueTask.FromResult(new ContentAdapterHandle(
                "text-dir", empty,
                new Dictionary<string, string> { ["path"] = Path.GetFullPath(path), ["kind"] = "directory" }));
        }

        var fs = IngestIo.OpenSequentialRead(path, useAsync: true);
        return ValueTask.FromResult(new ContentAdapterHandle(
            "text-file", fs,
            new Dictionary<string, string> { ["path"] = Path.GetFullPath(path), ["kind"] = "file" }));
    }
}
