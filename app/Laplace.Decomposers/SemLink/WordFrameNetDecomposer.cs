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

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options) =>
        WordFrameNetIngest.ResolvePaths(ecosystemPath)
            .Select((p, i) =>
            {
                var spec = WordFrameNetIngest.DescribeFile(p);
                return (spec.Path, $"{spec.Label}/{i}/{Path.GetFileName(spec.Path)}");
            })
            .ToList();

    protected override IAsyncEnumerable<CategoryCorrespondenceRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options, CancellationToken ct)
    {
        var spec = WordFrameNetIngest.DescribeFile(filePath);
        return spec.NativeFormat
            ? FnLuSynsetBridgeIngest.EnumerateWfnNativeRecordsAsync(
                filePath, FnLuSynsetBridgeIngest.MultiWordNetVersion, 0, ct)
            : FnLuSynsetBridgeIngest.EnumerateTabRecordsAsync(
                filePath, FnLuSynsetBridgeIngest.MultiWordNetVersion, 0, ct);
    }

    protected override IIngestRecordHandler<CategoryCorrespondenceRecord> CreateHandlerForFile(
        string fileLabel, DecomposerOptions options) =>
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
        return Task.FromResult(IngestInventory.FromFiles(
            "records", paths, options.MaxInputUnits, ct, tracksFileCompletion: true));
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long total = 0;
        foreach (string path in WordFrameNetIngest.ResolvePaths(context.EcosystemPath))
            total += EtlInventory.EstimateNewlineCount(path, ct);
        return Task.FromResult<long?>(total > 0 ? total : null);
    }
}
