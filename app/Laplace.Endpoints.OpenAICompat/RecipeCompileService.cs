using Laplace.Decomposers.Model;
using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed record RecipeCompileResult(
    string RecipeIdHex,
    string Name,
    string Structure,
    string HiddenSize,
    int NumLayers,
    string CompileMode);

internal interface IRecipeCompileService
{
    RecipeCompileResult Compile(string recipeJson);
}

internal sealed class RecipeCompileService : IRecipeCompileService
{
    public RecipeCompileResult Compile(string recipeJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeJson);
        var desc = RecipeDescriptor.Parse(recipeJson.Trim());
        return new RecipeCompileResult(
            RecipeIdHex: Convert.ToHexStringLower(desc.RecipeId.ToBytes()),
            Name: desc.Name,
            Structure: desc.Structure,
            HiddenSize: desc.HiddenSize,
            NumLayers: desc.NumLayers,
            CompileMode: desc.Compile);
    }
}
