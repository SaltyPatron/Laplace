using System.Diagnostics;
using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public sealed class DocumentDecomposer : DecomposerMultiFile<ContentIngestRecord>, IIngestInventoryProvider,
    IIgnoresAmbientArtifactManifest
{
    public override Hash128 SourceId => UserPromptContent.Source;
    public override string SourceName => "UserPrompt";
    public override int LayerOrder => 2;
    public override Hash128 TrustClassId => UserPromptContent.TrustClass;
    protected override double SourceTrust => UserPromptContent.WitnessWeight;

    // Pillar 0 live: every file is its own provenance unit (source = content-DAG root,
    // completion marker + metadata DAG per file), so completion is per-file, not the
    // all-or-nothing source-level marker — new files in a completed directory just work.
    public override bool PerFileCompletion => true;

    public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => context.Writer.ApplyAsync(UserPromptContent.BuildBootstrapChange(), ct);

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options)
    {
        bool rootIsFile = File.Exists(ecosystemPath);
        return EnumerateInputFiles(ecosystemPath).Select(f =>
        {
            string rel = rootIsFile
                ? Path.GetFileName(f)
                : Path.GetRelativePath(ecosystemPath, f).Replace('\\', '/');
            return (f, $"document/{rel}");
        }).ToList();
    }

    protected override IAsyncEnumerable<ContentIngestRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options, CancellationToken ct)
    {
        string rel = fileLabel.StartsWith("document/", StringComparison.Ordinal)
            ? fileLabel["document/".Length..]
            : Path.GetFileName(filePath);
        return DocumentFileExtract.OpenAsync(filePath, rel, ct);
    }

    protected override IIngestRecordHandler<ContentIngestRecord> CreateHandlerForFile(
        string fileLabel, DecomposerOptions options) =>
        new DocumentIngestHandler(LayerOrder) { IgnoreCompletedFiles = options.ReObservePresent };

    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options)
    {
        return DocumentIngestSupport.PipelineConfig(fileLabel, reader, options);
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = EnumerateInputFiles(context.EcosystemPath).ToList();
        if (paths.Count == 0) return Task.FromResult<IngestInventory?>(null);
        if (options.MaxInputUnits > 0)
            return Task.FromResult(IngestInventory.FromFiles(
                "documents", paths, options.MaxInputUnits, ct, tracksFileCompletion: true));
        var specs = paths.Select(f => new IngestFileSpec(Path.GetFileName(f), f, 1)).ToList();
        return Task.FromResult<IngestInventory?>(
            new IngestInventory("documents", paths.Count, specs, TracksFileCompletion: true));
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long n = EnumerateInputFiles(context.EcosystemPath).LongCount();
        return Task.FromResult<long?>(n == 0 ? null : n);
    }

    internal static IEnumerable<string> EnumerateInputFiles(string path)
    {
        if (string.IsNullOrEmpty(path)) yield break;

        if (File.Exists(path))
        {
            yield return Path.GetFullPath(path);
            yield break;
        }

        if (!Directory.Exists(path)) yield break;

        // Provenance filter ONLY — not the source-code size heuristic. A 27 MB
        // dictionary is the corpus, not a build artifact. IsVendoredOrBuildPath
        // dropped webster-unabridged-dictionary-1913 and one Britannica volume
        // here, silently, before enumeration (GH #754).
        foreach (string file in Directory.EnumerateFiles(path, "*.txt", SearchOption.AllDirectories)
                                         .Where(f => !VendoredPathFilter.IsVendoredOrBuildLocation(f))
                                         .OrderBy(p => p, StringComparer.Ordinal))
            yield return file;
    }
}

/// <summary>Single-file document masticator — shared by multi-file workers and tests.</summary>
public static class DocumentFileExtract
{
    public static async IAsyncEnumerable<ContentIngestRecord> OpenAsync(
        string file, string relativePath, [EnumeratorCancellation] CancellationToken ct)
    {
        byte[] bytes = await ReadFileBytesAsync(file, ct);
        if (bytes.Length == 0) yield break;
        // Match RepoDecomposer / GH #596: one malformed-encoding file must skip with a
        // warning, not abort a multi-hundred-file document run (rc=1 for the process).
        Hash128? fileRoot = ContentTierSpine.ResolveRoot(bytes);
        if (fileRoot is null)
        {
            Trace.TraceWarning(
                "DocumentFileExtract: skipping '{0}' — unresolvable content root " +
                "(malformed encoding or native content_root_id rejection)",
                relativePath);
            yield break;
        }
        yield return new ContentIngestRecord(
            bytes, SourceId: fileRoot.Value, Metadata: FileMetadata.FromPath(file, relativePath));
    }

    private static async Task<byte[]> ReadFileBytesAsync(string file, CancellationToken ct)
    {
        var fi = new FileInfo(file);
        if (!fi.Exists)
            throw new FileNotFoundException($"document vanished between enumeration and open: {file}");
        if (fi.Length == 0) return Array.Empty<byte>();
        int contiguousBytes = IngestSizing.ResolveContiguousPayloadBytes();
        if (fi.Length > contiguousBytes)
            throw new InvalidOperationException(
                $"document '{file}' is {fi.Length:N0} bytes — exceeds the current "
                + $"{contiguousBytes:N0}-byte contiguous compose envelope; split the file into records");
        var bytes = new byte[(int)fi.Length];
        await using var fs = IngestIo.OpenSequentialRead(file, useAsync: true);
        int off = 0;
        while (off < bytes.Length)
        {
            int n = await fs.ReadAsync(bytes.AsMemory(off), ct);
            if (n == 0)
                throw new IOException(
                    $"document '{file}' truncated mid-read at {off:N0}/{bytes.Length:N0} bytes");
            off += n;
        }
        return bytes;
    }
}
