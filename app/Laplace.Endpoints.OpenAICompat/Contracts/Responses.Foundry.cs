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
    [property: JsonPropertyName("billing")] BillingReceipt? Billing,
    [property: JsonPropertyName("n_vocab"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? NVocab = null,
    [property: JsonPropertyName("n_edges"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? NEdges = null,
    [property: JsonPropertyName("hidden_dim"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? HiddenDim = null,
    [property: JsonPropertyName("raw_weight_bytes_retained"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ulong? RawWeightBytesRetained = null,
    [property: JsonPropertyName("scheme"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scheme = null);
