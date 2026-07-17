using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public sealed class DocumentDecomposer : DecomposerMultiFile<ContentIngestRecord>, IIngestInventoryProvider
{
    public override Hash128 SourceId => UserPromptContent.Source;
    public override string SourceName => "UserPrompt";
    public override int LayerOrder => 2;
    public override Hash128 TrustClassId => UserPromptContent.TrustClass;
    protected override double SourceTrust => UserPromptContent.WitnessWeight;

    public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => context.Writer.ApplyAsync(UserPromptContent.BuildBootstrapChange(), ct);

    // Each document is its own content DAG with no cross-file state or ordering, so the file
    // pool may ingest N documents concurrently (see DecomposerMultiFile.FilesAreIndependent).
    protected override bool FilesAreIndependent => true;

    protected override IMultiFileRecordStream<ContentIngestRecord> CreateMultiFileStream(
        string ecosystemPath, DecomposerOptions options) =>
        new DocumentMultiFileStream(ecosystemPath);

    protected override IIngestRecordHandler<ContentIngestRecord> CreateHandlerForFile(string fileLabel) =>
        new DocumentIngestHandler();

    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options)
    {
        int batchSize = BatchConfigDefaults.Resolve(options, BatchConfigDefaults.Document);
        return DocumentIngestSupport.PipelineConfig(fileLabel, reader, batchSize);
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = EnumerateInputFiles(context.EcosystemPath).ToList();
        if (paths.Count == 0) return Task.FromResult<IngestInventory?>(null);
        if (options.MaxInputUnits > 0)
            return Task.FromResult(IngestInventory.FromFiles("documents", paths, options.MaxInputUnits, ct));
        var specs = paths.Select(f => new IngestFileSpec(Path.GetFileName(f), f, 1)).ToList();
        return Task.FromResult<IngestInventory?>(new IngestInventory("documents", paths.Count, specs));
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
                                         .OrderBy(p => p, StringComparer.Ordinal))
            yield return file;
    }
}
