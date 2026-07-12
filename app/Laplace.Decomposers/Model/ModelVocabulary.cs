using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Model;

/// <summary>Shared model-lane vocabulary (types + relations). Per-model SourceId is runtime.</summary>
public static class ModelVocabulary
{
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AIModelProbe/v1");

    public static readonly IReadOnlyList<string> TypeNodeNames =
    [
        "Model_Recipe", "Model_Tokenizer", "Scalar", "Architecture",
        "Ngram", "Model_Layer", "Model_Circuit", "Model_Plane", "Model_AnalysisMarker",
        "Model_Tensor", "Model_Checkpoint",
    ];

    public static readonly IReadOnlyList<string> Relations =
    [
        "MERGES_WITH", "SIMILAR_TO", "ATTENDS", "OV_RELATES",
        "COMPLETES_TO", "CONTINUES_TO", "ENCODES", "TOKEN_MAPS_TO", "APPEARS_IN",
        "CONTAINS", "PRECEDES",
        "HAS_HIDDEN_SIZE", "HAS_NUM_LAYERS", "HAS_NUM_HEADS", "HAS_NUM_KV_HEADS",
        "HAS_INTERMEDIATE_SIZE", "HAS_VOCAB_SIZE", "IS_A",
    ];
}

/// <summary>Per-checkpoint instance manifest (SourceId is content-addressed from model dir).</summary>
public sealed class ModelRuntimeManifest : ISourceManifest
{
    public ModelRuntimeManifest(Hash128 sourceId, string sourceName)
    {
        SourceId = sourceId;
        SourceName = sourceName;
    }

    public Hash128 SourceId { get; }
    public string SourceName { get; }
    public Hash128 TrustClass => ModelVocabulary.TrustClass;
    public IReadOnlyList<string> Relations => ModelVocabulary.Relations;
    public IReadOnlyList<string>? TypeNodeNames => ModelVocabulary.TypeNodeNames;
    public SourceLicense License => SourceLicense.Unknown;
    public IngestSourceProfile Profile => IngestSourceProfile.Default;
}
