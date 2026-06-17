using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

// Ingests a single build-a-bear recipe JSON (the simulated UI POST — see docs/invention/recipe-schema.md)
// into the substrate as a content-addressed Model_Recipe entity, so export can fetch it via
// laplace.model_recipes() / --recipe-from. The recipe is stored content, never read from disk at export.
public sealed class RecipeDecomposer : IDecomposer
{
    // A recipe is a user-curated build-a-bear artifact (composed in the UI). Must be a
    // pre-registered trust class (see extension bootstrap) or referential integrity rejects it.
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/UserCuratedResource/v1");

    private static readonly Hash128 HasHiddenSizeTypeId = RelationTypeRegistry.RelationTypeId("HAS_HIDDEN_SIZE");
    private static readonly Hash128 HasNumLayersTypeId  = RelationTypeRegistry.RelationTypeId("HAS_NUM_LAYERS");

    private readonly string _recipePath;
    private readonly RecipeExtractor.RecipeInfo _recipe;
    private readonly Hash128 _source;
    private readonly string  _sourceName;

    public RecipeDecomposer(string recipePath)
    {
        _recipePath = recipePath ?? throw new ArgumentNullException(nameof(recipePath));
        _recipe = RecipeExtractor.Parse(_recipePath);
        _sourceName = $"recipe/{_recipe.Name}";
        _source = Hash128.OfCanonical($"substrate/source/recipe/{_recipe.Name}/v1");
    }

    public Hash128 SourceId     => _source;
    public string  SourceName   => _sourceName;
    public int     LayerOrder   => 5;
    public Hash128 TrustClassId => TrustClass;

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(_source, _sourceName, TrustClass);
        boot.AddType("Model_Recipe");
        boot.AddType("Scalar");
        boot.AddRelationType("HAS_HIDDEN_SIZE");
        boot.AddRelationType("HAS_NUM_LAYERS");
        return context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        context.Logger.LogInformation(
            "phase=recipe parsed: name={Name} structure={Structure} hidden={Hidden} layers={Layers}",
            _recipe.Name, _recipe.Structure, _recipe.HiddenSize, _recipe.NumLayers);
        yield return RecipeExtractor.BuildChange(
            _recipe, _source, EntityTypeRegistry.ModelRecipe,
            HasHiddenSizeTypeId, HasNumLayersTypeId);
        await Task.CompletedTask;
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(1);

    // Registered into canonical_names by IngestCommands.RegisterDynamicCanonicalsAsync. The recipe
    // entity's id == Blake3(canonical JSON), so this name resolves it in model_recipes().
    public IReadOnlyCollection<string> CanonicalNamesForReadback => new[]
    {
        RecipeExtractor.CanonicalName(_recipe),
        _recipe.HiddenSize,
        _recipe.NumLayers.ToString(),
    };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
