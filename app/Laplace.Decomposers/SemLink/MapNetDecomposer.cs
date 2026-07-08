using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.SemLink;

public sealed class MapNetDecomposer : DecomposerMultiFile<CategoryCorrespondenceRecord>, IIngestInventoryProvider
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/MapNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    public override Hash128 SourceId => Source;
    public override string SourceName => "MapNetDecomposer";
    public override int LayerOrder => 3;
    public override Hash128 TrustClassId => TrustClass;
    protected override double SourceTrust => TC.AcademicCurated;

    public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        SourceEntityIdConventions.EnsureCiliMapForIngest(context.Logger, SourceName);
        return SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["FrameNet_Frame", "FrameNet_LU"],
            relationNodeNames: ["CORRESPONDS_TO"],
            ct: ct);
    }

    protected override IMultiFileRecordStream<CategoryCorrespondenceRecord> CreateMultiFileStream(
        string ecosystemPath, DecomposerOptions options)
    {
        var files = MapNetIngest.ResolvePaths(ecosystemPath)
            .Select(MapNetIngest.DescribeFile)
            .ToList();
        return new MapNetMultiFileStream(files);
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
        var paths = MapNetIngest.ResolvePaths(context.EcosystemPath).ToList();
        if (paths.Count == 0) return Task.FromResult<IngestInventory?>(null);
        return Task.FromResult(IngestInventory.FromFiles("records", paths, options.MaxInputUnits, ct));
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long total = 0;
        foreach (string path in MapNetIngest.ResolvePaths(context.EcosystemPath))
        {
            long? lines = await MapNetIngest.EstimateLineCountAsync(path, ct);
            if (lines is not null) total += lines.Value;
        }
        return total > 0 ? total : null;
    }
}
