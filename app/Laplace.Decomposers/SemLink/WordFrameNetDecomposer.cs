using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.SemLink;

public sealed class WordFrameNetDecomposer : DecomposerMultiFile<CategoryCorrespondenceRecord, WordFrameNetSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = WordFrameNetSource.SourceId;
    public static readonly Hash128 TrustClass = WordFrameNetSource.TrustClass;

    public override int LayerOrder => 3;
    protected override double SourceTrust => TC.AcademicCurated;

    protected override Task OnBeforeRegisterAsync(IDecomposerContext context, CancellationToken ct)
    {
        SourceEntityIdConventions.EnsureCiliMapForIngest(context.Logger, SourceName);
        return Task.CompletedTask;
    }

    protected override IMultiFileRecordStream<CategoryCorrespondenceRecord> CreateMultiFileStream(
        string ecosystemPath, DecomposerOptions options)
    {
        var files = WordFrameNetIngest.ResolvePaths(ecosystemPath)
            .Select(WordFrameNetIngest.DescribeFile)
            .ToList();
        return new WordFrameNetMultiFileStream(files);
    }

    protected override IIngestRecordHandler<CategoryCorrespondenceRecord> CreateHandlerForFile(string fileLabel) =>
        new CategoryCorrespondenceHandler(Source, SourceTrust);

    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options) =>
        IngestPipelineDefaults.ApplyMaxInputUnits(
            IngestPipelineDefaults.CategoryCorrespondence(
                Source, fileLabel, DefaultBatchSize, options, reader),
            options);

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = WordFrameNetIngest.ResolvePaths(context.EcosystemPath).ToList();
        if (paths.Count == 0) return Task.FromResult<IngestInventory?>(null);
        return Task.FromResult(IngestInventory.FromFiles("records", paths, options.MaxInputUnits, ct));
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long total = 0;
        foreach (string path in WordFrameNetIngest.ResolvePaths(context.EcosystemPath))
        {
            long? lines = await WordFrameNetIngest.EstimateLineCountAsync(path, ct);
            if (lines is not null) total += lines.Value;
        }
        return total > 0 ? total : null;
    }
}
