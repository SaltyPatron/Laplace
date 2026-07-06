using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;




public sealed class RecipeDecomposer : DecomposerOrchestrator
{


    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/UserCuratedResource/v1");

    private static readonly Hash128 HasHiddenSizeTypeId = RelationTypeRegistry.RelationTypeId("HAS_HIDDEN_SIZE");
    private static readonly Hash128 HasNumLayersTypeId = RelationTypeRegistry.RelationTypeId("HAS_NUM_LAYERS");

    private readonly string _recipePath;
    private readonly RecipeExtractor.RecipeInfo _recipe;
    private readonly Hash128 _source;
    private readonly string _sourceName;

    public RecipeDecomposer(string recipePath)
    {
        _recipePath = recipePath ?? throw new ArgumentNullException(nameof(recipePath));
        _recipe = RecipeExtractor.Parse(_recipePath);
        _sourceName = $"recipe/{_recipe.Name}";
        _source = Hash128.OfCanonical($"substrate/source/recipe/{_recipe.Name}/v1");
    }

    public override Hash128 SourceId => _source;
    public override string SourceName => _sourceName;
    public override int LayerOrder => 5;
    public override Hash128 TrustClassId => TrustClass;

    public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SourceVocabularyBootstrap.RegisterAsync(context, _source, _sourceName, TrustClass,
            typeNodeNames: ["Model_Recipe", "Scalar"],
            relationNodeNames: ["HAS_HIDDEN_SIZE", "HAS_NUM_LAYERS"],
            ct: ct);

    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        context.Logger.LogInformation(
            "phase=recipe parsed: name={Name} structure={Structure} hidden={Hidden} layers={Layers}",
            _recipe.Name, _recipe.Structure, _recipe.HiddenSize, _recipe.NumLayers);
        await foreach (var batch in RunComposePhaseAsync(
            SingleRecipeAsync(_recipe, ct),
            (rec, b) => RecipeExtractor.StageRecipe(
                b, rec, _source, EntityTypeRegistry.ModelRecipe,
                HasHiddenSizeTypeId, HasNumLayersTypeId),
            "laplace.recipe", SourceTrust.UserCuratedResource, 1, context, options, ct))
            yield return batch;
    }

    private static async IAsyncEnumerable<RecipeExtractor.RecipeInfo> SingleRecipeAsync(
        RecipeExtractor.RecipeInfo recipe, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        yield return recipe;
        await Task.CompletedTask;
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
