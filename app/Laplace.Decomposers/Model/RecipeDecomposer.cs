using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Model;

public sealed class RecipeDecomposer : ComposeDecomposer<RecipeExtractor.RecipeInfo>
{
    private static readonly Hash128 HasHiddenSizeTypeId = RelationTypeRegistry.RelationTypeId("HAS_HIDDEN_SIZE");
    private static readonly Hash128 HasNumLayersTypeId = RelationTypeRegistry.RelationTypeId("HAS_NUM_LAYERS");

    private readonly RecipeExtractor.RecipeInfo _recipe;
    private readonly Hash128 _source;
    private readonly string _sourceName;
    private readonly RecipeRuntimeManifest _manifest;

    public RecipeDecomposer(string recipePath)
    {
        _ = recipePath ?? throw new ArgumentNullException(nameof(recipePath));
        _recipe = RecipeExtractor.Parse(recipePath);
        _sourceName = $"recipe/{_recipe.Name}";
        _source = Hash128.OfCanonical($"substrate/source/recipe/{_recipe.Name}/v1");
        _manifest = new RecipeRuntimeManifest(_source, _sourceName);
    }

    public override Hash128 SourceId => _source;
    public override string SourceName => _sourceName;
    public override int LayerOrder => 5;
    public override Hash128 TrustClassId => _manifest.TrustClass;
    protected override double SourceTrust => TC.UserCuratedResource;
    protected override string BatchLabelPrefix => "laplace.recipe";
    protected override int DefaultBatchSize => 1;

    public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SourceVocabularyBootstrap.RegisterManifestAsync(context, _manifest, ct: ct);

    protected override async IAsyncEnumerable<RecipeExtractor.RecipeInfo> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        yield return _recipe;
        await Task.CompletedTask;
    }

    protected override void Compose(RecipeExtractor.RecipeInfo rec, SubstrateChangeBuilder b) =>
        RecipeExtractor.StageRecipe(
            b, rec, _source, EntityTypeRegistry.ModelRecipe,
            HasHiddenSizeTypeId, HasNumLayersTypeId);

    protected override async IAsyncEnumerable<SubstrateChange> RunDecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        context.Logger.LogInformation(
            "phase=recipe parsed: name={Name} structure={Structure} hidden={Hidden} layers={Layers}",
            _recipe.Name, _recipe.Structure, _recipe.HiddenSize, _recipe.NumLayers);
        await foreach (var change in base.RunDecomposeAsync(context, options, ct))
            yield return change;
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(1);

    public IReadOnlyCollection<string> CanonicalNamesForReadback => new[]
    {
        RecipeExtractor.CanonicalName(_recipe),
        _recipe.HiddenSize,
        _recipe.NumLayers.ToString(),
    };
}
