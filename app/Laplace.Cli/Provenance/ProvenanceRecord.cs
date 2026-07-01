using System.Text.Json.Serialization;

namespace Laplace.Cli.Provenance;














public sealed record ProvenanceRecord
{


    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("generated_by")]
    public required string GeneratedBy { get; init; }

    [JsonPropertyName("identity")]
    public required ModelIdentity Identity { get; init; }

    [JsonPropertyName("sources")]
    public required IReadOnlyList<SourceProvenance> Sources { get; init; }

    [JsonPropertyName("tensors")]
    public required IReadOnlyList<TensorProvenance> Tensors { get; init; }

    [JsonPropertyName("circuits")]
    public required IReadOnlyList<CircuitProvenance> Circuits { get; init; }
}



public sealed record ModelIdentity
{
    [JsonPropertyName("recipe_hash")]
    public required string RecipeHash { get; init; }

    [JsonPropertyName("architecture")]
    public required string Architecture { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }



    [JsonPropertyName("mold_origin")]
    public required string MoldOrigin { get; init; }



    [JsonPropertyName("config")]
    public required IReadOnlyDictionary<string, string> Config { get; init; }


    [JsonPropertyName("gguf_metadata")]
    public required IReadOnlyDictionary<string, string> GgufMetadata { get; init; }
}




public sealed record SourceProvenance
{
    [JsonPropertyName("source_id")]
    public required string SourceId { get; init; }

    [JsonPropertyName("domain")]
    public required string Domain { get; init; }


    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("completed")]
    public required bool Completed { get; init; }

    [JsonPropertyName("entity_count")]
    public long? EntityCount { get; init; }

    [JsonPropertyName("trust_class")]
    public string? TrustClass { get; init; }
}




public sealed record TensorProvenance
{
    [JsonPropertyName("tensor_name")]
    public required string TensorName { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("layer")]
    public int? Layer { get; init; }

    [JsonPropertyName("expert")]
    public int? Expert { get; init; }


    [JsonPropertyName("plane")]
    public required string Plane { get; init; }

    [JsonPropertyName("rank_band")]
    public string? RankBand { get; init; }

    [JsonPropertyName("svd_rank")]
    public int? SvdRank { get; init; }


    [JsonPropertyName("contributing_sources")]
    public required IReadOnlyList<string> ContributingSources { get; init; }
}




public sealed record CircuitProvenance
{
    [JsonPropertyName("circuit_id")]
    public required string CircuitId { get; init; }

    [JsonPropertyName("layer")]
    public required int Layer { get; init; }

    [JsonPropertyName("head")]
    public int? Head { get; init; }

    [JsonPropertyName("plane")]
    public required string Plane { get; init; }


    [JsonPropertyName("encodes_relation")]
    public string? EncodesRelation { get; init; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; init; }

    [JsonPropertyName("witnesses")]
    public long? Witnesses { get; init; }


    [JsonPropertyName("exemplars")]
    public required IReadOnlyList<CircuitExemplar> Exemplars { get; init; }
}

public sealed record CircuitExemplar
{
    [JsonPropertyName("subject")]
    public required string Subject { get; init; }

    [JsonPropertyName("object")]
    public required string Object { get; init; }

    [JsonPropertyName("strength")]
    public required double Strength { get; init; }
}
