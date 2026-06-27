using System.Text.Json.Serialization;

namespace Laplace.Cli.Provenance;

// THE SOURCE MATERIAL.
//
// This is the canonical, self-contained provenance record for one exported model. It is extracted
// ONCE from the substrate + recipe + gguf, serialized to provenance.json, and is the SOLE input to
// every renderer (markdown/HF card, html viz, pdf, docx, csv). Renderers never touch the substrate —
// if a fact is not in this record, it cannot be documented. That rule is what makes every output
// reproducible from the record alone, and forces the extractor to be the single place that must be
// complete.
//
// Design rule: this type is a SERIALIZATION CONTRACT, not a view over internal engine types. It holds
// strings/primitives (hashes as hex), never Hash128 / ModelManifest / etc., so the schema is stable
// across engine refactors and the JSON is portable (a third party with no Laplace build can read it).

public sealed record ProvenanceRecord
{
    // Schema version of THIS record format, so old provenance.json files remain machine-readable as
    // the contract evolves. Bump on any breaking field change.
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("generated_by")]
    public required string GeneratedBy { get; init; }   // tool + version that produced this record

    [JsonPropertyName("identity")]
    public required ModelIdentity Identity { get; init; }

    [JsonPropertyName("sources")]
    public required IReadOnlyList<SourceProvenance> Sources { get; init; }

    [JsonPropertyName("tensors")]
    public required IReadOnlyList<TensorProvenance> Tensors { get; init; }

    [JsonPropertyName("circuits")]
    public required IReadOnlyList<CircuitProvenance> Circuits { get; init; }
}

// WHO this model is. Grounded in: RecipeExtractor.RecipeEntityId (Blake3 of canonical recipe JSON),
// WriteGgufMetadata stamps (FoundryCommands.cs), and the mold's origin.
public sealed record ModelIdentity
{
    [JsonPropertyName("recipe_hash")]
    public required string RecipeHash { get; init; }            // Blake3(canonical recipe json), hex

    [JsonPropertyName("architecture")]
    public required string Architecture { get; init; }          // e.g. "llama"

    [JsonPropertyName("name")]
    public string? Name { get; init; }                          // general.name from gguf metadata

    // How the mold was obtained: "recipe-file" | "discovered" (deposed from an ingested model) |
    // "substrate-native" (vocab trained over substrate words). Mirrors FoundryCommands materialize-*.
    [JsonPropertyName("mold_origin")]
    public required string MoldOrigin { get; init; }

    // The config scalars that define the shape (vocab/hidden/layers/heads/kv/intermediate, tied, moe…).
    // Kept as an open string→string map so any architecture's recipe round-trips without schema churn.
    [JsonPropertyName("config")]
    public required IReadOnlyDictionary<string, string> Config { get; init; }

    // Raw gguf general.* metadata KVs as written, for an exact echo of the artifact header.
    [JsonPropertyName("gguf_metadata")]
    public required IReadOnlyDictionary<string, string> GgufMetadata { get; init; }
}

// WHERE the knowledge came from. One entry per dataset/source that fed the consensus this model was
// poured from. Grounded in: SourceEntityIdConventions source ids + the per-source completion ledger
// (HasSourceCompletedAsync) + CountEntitiesByTypeAsync.
public sealed record SourceProvenance
{
    [JsonPropertyName("source_id")]
    public required string SourceId { get; init; }              // Hash128 hex (ContentHash/ModelContent/…)

    [JsonPropertyName("domain")]
    public required string Domain { get; init; }                // e.g. "substrate/source/model/v1"

    // "lexical-resource" (WordNet/FrameNet/VerbNet/PropBank/…) | "ingested-model" | "corpus" | …
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }                         // human name if known (e.g. "WordNet 3.0")

    [JsonPropertyName("completed")]
    public required bool Completed { get; init; }               // per-source completion ledger

    [JsonPropertyName("entity_count")]
    public long? EntityCount { get; init; }                     // CountEntitiesByTypeAsync, when applicable

    [JsonPropertyName("trust_class")]
    public string? TrustClass { get; init; }                    // e.g. "AcademicCurated", "AIModelProbe"
}

// HOW the artifact was built, tensor by tensor. Each gguf tensor slot is poured from a consensus
// plane selected by rank band (FoundryCommands.cs:529: equivalence→embed, taxonomic→V/O, causal→FFN,
// associative→attention) then SVD-factored. This is the "what filled this weight" layer.
public sealed record TensorProvenance
{
    [JsonPropertyName("tensor_name")]
    public required string TensorName { get; init; }            // gguf tensor name (e.g. blk.3.attn_q.weight)

    [JsonPropertyName("role")]
    public required string Role { get; init; }                  // TensorRoleKind (embed/q/k/v/o/gate/up/down/…)

    [JsonPropertyName("layer")]
    public int? Layer { get; init; }

    [JsonPropertyName("expert")]
    public int? Expert { get; init; }                           // MoE expert index, if any

    // The consensus plane / relation family this tensor was poured from.
    [JsonPropertyName("plane")]
    public required string Plane { get; init; }                 // "equivalence"|"taxonomic"|"causal"|"associative"|…

    [JsonPropertyName("rank_band")]
    public string? RankBand { get; init; }                      // relation_types.toml rank class

    [JsonPropertyName("svd_rank")]
    public int? SvdRank { get; init; }                          // k kept by tensor_svd_truncate

    // Source ids that contributed folds to this tensor's plane (subset of Sources above, by id).
    [JsonPropertyName("contributing_sources")]
    public required IReadOnlyList<string> ContributingSources { get; init; }
}

// WHAT each layer/head means — the "crystal ball" layer. From the ingest-time decoder ring:
// Model_Circuit —ENCODES→ <relation> attestations (HeadClassifier.ClassifyAsync), i.e. each
// (layer, head) circuit resolved to the seed relation its strongest token pairs already hold.
public sealed record CircuitProvenance
{
    [JsonPropertyName("circuit_id")]
    public required string CircuitId { get; init; }             // embeds model+layer+head+plane

    [JsonPropertyName("layer")]
    public required int Layer { get; init; }

    [JsonPropertyName("head")]
    public int? Head { get; init; }                             // null for layer-wide circuits (OV/MLP)

    [JsonPropertyName("plane")]
    public required string Plane { get; init; }                 // ATTENDS/OV_RELATES/COMPLETES_TO/…

    // The relation this circuit was classified as encoding, and how strongly / how witnessed.
    [JsonPropertyName("encodes_relation")]
    public string? EncodesRelation { get; init; }               // e.g. "IS_A", "PRECEDES" (null = unresolved)

    [JsonPropertyName("confidence")]
    public double? Confidence { get; init; }                    // eff_mu of the ENCODES meta-attestation

    [JsonPropertyName("witnesses")]
    public long? Witnesses { get; init; }

    // A few exemplar token pairs that drove the classification, so a reader can SEE the evidence.
    [JsonPropertyName("exemplars")]
    public required IReadOnlyList<CircuitExemplar> Exemplars { get; init; }
}

public sealed record CircuitExemplar
{
    [JsonPropertyName("subject")]
    public required string Subject { get; init; }               // rendered token text

    [JsonPropertyName("object")]
    public required string Object { get; init; }

    [JsonPropertyName("strength")]
    public required double Strength { get; init; }
}
