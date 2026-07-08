using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

public sealed record RecipeCompileResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("recipe_id_hex")] string RecipeIdHex,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("structure")] string Structure,
    [property: JsonPropertyName("hidden_size")] string HiddenSize,
    [property: JsonPropertyName("num_layers")] int NumLayers,
    [property: JsonPropertyName("compile_mode")] string CompileMode,
    [property: JsonPropertyName("billing")] BillingReceipt? Billing);

public sealed record SynthesisExportResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("output_path")] string OutputPath,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("billing")] BillingReceipt? Billing);
