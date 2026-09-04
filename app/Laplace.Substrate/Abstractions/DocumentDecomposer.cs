using System.Diagnostics;
using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Abstractions;

public sealed class DocumentDecomposer : DecomposerMultiFile<ContentIngestRecord>, IIngestInventoryProvider,
    IIgnoresAmbientArtifactManifest
{
    private static readonly ISourceManifest Manifest = SeedSourceManifest<DocumentSource>.Instance;

    public override Hash128 SourceId => DocumentSource.SourceId;
    public override string SourceName => DocumentSource.SourceName;
    public override int LayerOrder => 2;
    public override Hash128 TrustClassId => DocumentSource.TrustClass;
    protected override double SourceTrust => TC.StructuredCorpus;

    public override bool PerFileCompletion => true;

    // Document files now use their semantic file-composition id for completion. The generic
    // resume fingerprint is intentionally disabled here because it is an execution hash, not
    // the file entity defined by Pillar 0.
    public override bool PerFileResume => false;

    public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => SourceVocabularyBootstrap.RegisterManifestAsync(context, Manifest, ct: ct);

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
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options) =>
        DocumentIngestSupport.PipelineConfig(fileLabel, reader, options);

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

        Hash128? contentRoot = ContentTierSpine.ResolveRoot(bytes);
        if (contentRoot is null)
        {
            Trace.TraceWarning(
                "DocumentFileExtract: skipping '{0}' — unresolvable content root " +
                "(malformed encoding or native content_root_id rejection)",
                relativePath);
            yield break;
        }

        var metadata = FileMetadata.FromPath(file, relativePath);
        FileIdentity fileIdentity;
        try
        {
            fileIdentity = FileEntity.Resolve(bytes, metadata);
        }
        catch (InvalidOperationException ex)
        {
            Trace.TraceWarning(
                "DocumentFileExtract: skipping '{0}' — unresolvable file identity: {1}",
                relativePath, ex.Message);
            yield break;
        }

        Hash128 documentId = DocumentEntity.Resolve(contentRoot.Value);
        yield return new ContentIngestRecord(
            CanonicalUtf8: bytes,
            SourceId: documentId,
            Metadata: metadata,
            ContentRootId: contentRoot.Value,
            DocumentId: documentId,
            FileId: fileIdentity.FileId);
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
