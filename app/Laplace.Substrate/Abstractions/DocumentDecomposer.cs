using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Abstractions;

public sealed class DocumentDecomposer : DecomposerMultiFile<ContentIngestRecord>, IIngestInventoryProvider,
    IIngestArtifactGraphProvider, IIgnoresAmbientArtifactManifest
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
        var selected = context.SelectedArtifacts;
        var paths = context.HasArtifactGraph
            ? selected.Select(static artifact => artifact.Path).ToList()
            : EnumerateInputFiles(context.EcosystemPath).ToList();
        if (paths.Count == 0) return Task.FromResult<IngestInventory?>(null);
        var specs = context.HasArtifactGraph
            ? selected.Select(static artifact =>
                    new IngestFileSpec(artifact.FileLabel, artifact.Path, 1))
                .ToList()
            : paths.Select(f => new IngestFileSpec(Path.GetFileName(f), f, 1)).ToList();
        return Task.FromResult<IngestInventory?>(
            new IngestInventory(
                "documents",
                options.MaxInputUnits > 0 ? Math.Min(paths.Count, options.MaxInputUnits) : paths.Count,
                specs,
                TracksFileCompletion: true));
    }

    public Task<IngestArtifactGraph?> DescribeArtifactsAsync(
        string ecosystemPath,
        DecomposerOptions options,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(BuildArtifactGraph(ecosystemPath));
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
            if (string.Equals(Path.GetExtension(path), ".txt", StringComparison.Ordinal))
                yield return Path.GetFullPath(path);
            yield break;
        }

        if (!Directory.Exists(path)) yield break;

        foreach (string file in Directory.EnumerateFiles(path, "*.txt", SearchOption.AllDirectories)
                                         .Where(f => !VendoredPathFilter.IsVendoredOrBuildLocation(f))
                                         .OrderBy(p => p, StringComparer.Ordinal))
            yield return file;
    }

    internal static IngestArtifactGraph? BuildArtifactGraph(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        if (File.Exists(path))
        {
            string full = Path.GetFullPath(path);
            bool supported = string.Equals(Path.GetExtension(full), ".txt", StringComparison.Ordinal);
            return new IngestArtifactGraph(
                [BuildArtifact(
                    full,
                    Path.GetFileName(full),
                    supported ? IngestArtifactDisposition.Admitted : IngestArtifactDisposition.Unsupported,
                    supported ? "" : "DocumentDecomposer currently admits plain-text .txt files only")]);
        }

        if (!Directory.Exists(path)) return null;

        string root = Path.GetFullPath(path);
        var artifacts = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(static file => file, StringComparer.Ordinal)
            .Select(file =>
            {
                string full = Path.GetFullPath(file);
                string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                if (VendoredPathFilter.IsVendoredOrBuildLocation(full))
                    return BuildArtifact(
                        full,
                        relative,
                        IngestArtifactDisposition.ExcludedWithReason,
                        "vendored or build-tree artifact is outside DocumentDecomposer source ownership");
                if (!string.Equals(Path.GetExtension(full), ".txt", StringComparison.Ordinal))
                    return BuildArtifact(
                        full,
                        relative,
                        IngestArtifactDisposition.Unsupported,
                        "DocumentDecomposer currently admits plain-text .txt files only");
                return BuildArtifact(full, relative, IngestArtifactDisposition.Admitted, "");
            })
            .ToArray();
        return new IngestArtifactGraph(artifacts);
    }

    private static IngestArtifact BuildArtifact(
        string fullPath,
        string relativePath,
        IngestArtifactDisposition disposition,
        string reason)
    {
        var info = new FileInfo(fullPath);
        return new IngestArtifact(
            DocumentSource.SourceName,
            "local",
            relativePath,
            relativePath,
            fullPath,
            disposition,
            UpstreamUrl: "",
            FetchedAtUtc: "",
            Bytes: info.Length,
            Sha256: "",
            UpstreamChecksum: "",
            MediaType: disposition == IngestArtifactDisposition.Admitted ? "text/plain" : "",
            License: "",
            Citation: "",
            Language: "",
            Split: "",
            AnnotationOrigin: "local-filesystem",
            Notes: reason,
            JournalLabel: $"document/{relativePath}",
            ModifiedAt: info.LastWriteTimeUtc);
    }
}

/// <summary>Single-file document masticator — shared by multi-file workers and tests.</summary>
public static class DocumentFileExtract
{
    public static async IAsyncEnumerable<ContentIngestRecord> OpenAsync(
        string file, string relativePath, [EnumeratorCancellation] CancellationToken ct)
    {
        byte[] bytes = await ReadFileBytesAsync(file, ct);
        if (bytes.Length == 0)
            throw new InvalidDataException(
                $"document '{relativePath}' is empty; an admitted document must have content");

        Hash128? contentRoot = ContentTierSpine.ResolveRoot(bytes);
        if (contentRoot is null)
            throw new InvalidDataException(
                $"document '{relativePath}' has invalid UTF-8 or failed canonical content identity");

        var metadata = FileMetadata.FromPath(file, relativePath) with
        {
            FormatMetadata = ProjectGutenbergMetadata.Extract(bytes),
        };
        FileIdentity fileIdentity;
        try
        {
            fileIdentity = FileEntity.Resolve(bytes, metadata);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException(
                $"document '{relativePath}' failed canonical file identity: {ex.Message}", ex);
        }

        // A plain-text document introduces no extra composition around its content.
        Hash128 documentId = contentRoot.Value;
        yield return new ContentIngestRecord(
            CanonicalUtf8: bytes,
            SourceId: contentRoot.Value,
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
