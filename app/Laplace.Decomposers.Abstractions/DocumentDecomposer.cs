using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;





public sealed class DocumentDecomposer : IDecomposer, IIngestInventoryProvider
{
    public Hash128 SourceId => UserPromptContent.Source;



    public string SourceName => "UserPrompt";
    public int LayerOrder => 2;
    public Hash128 TrustClassId => UserPromptContent.TrustClass;

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => context.Writer.ApplyAsync(UserPromptContent.BuildBootstrapChange(), ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string root = context.EcosystemPath;
        if (string.IsNullOrEmpty(root)) yield break;
        if (options.DryRun) yield break;

        ISubstrateReader? reader = context.Reader;

        int batchSize = options.BatchSize > 1 ? options.BatchSize : 32;

        await foreach (var change in IngestBatchPipeline.RunMultiFileAsync(
            new DocumentMultiFileStream(root),
            _ => new DocumentIngestHandler(),
            label => DocumentIngestSupport.PipelineConfig(label, reader, batchSize),
            maxTotalUnits: options.MaxInputUnits,
            ct))
            yield return change;
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

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long n = EnumerateInputFiles(context.EcosystemPath).LongCount();
        return Task.FromResult<long?>(n == 0 ? null : n);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static IEnumerable<string> EnumerateInputFiles(string path)
    {
        if (string.IsNullOrEmpty(path)) yield break;

        if (File.Exists(path))
        {
            yield return Path.GetFullPath(path);
            yield break;
        }

        if (!Directory.Exists(path)) yield break;

        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                                         .OrderBy(p => p, StringComparer.Ordinal))
            yield return file;
    }
}
