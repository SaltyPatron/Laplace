namespace Laplace.Decomposers.Model;

// A path is one trajectory type a model computes between source entities and target entities.
// The ETL streams one Glicko-2 matchup per (source, target) pair above the noise floor.
// PathSpec subclasses encode the tensor chain and contraction shape; the ETL dispatches on type.
// New modalities (vision patches, audio frames, cross-modal) add new subclasses here.
public abstract record PathSpec(string RelationName, bool PerLayer);

// Cosine similarity in raw embedding space: no projection, source and target are the same entities.
// Used for SIMILAR_TO on token embeddings, patch embeddings, etc.
public record SelfSimilarityPath(
    string RelationName,
    string EmbedPattern)           // tensor name, no {L}
    : PathSpec(RelationName, PerLayer: false);

// Two projections whose outputs are dot-producted: left_proj(x) · right_proj(y).
// Used for ATTENDS (Q·K), and any other bilinear scoring path.
// When RightIsKv=true the right projection is in KV-head space and needs expansion to full head dim.
public record BilinearPath(
    string RelationName,
    string LeftPattern,            // tensor pattern, {L} for per-layer
    string RightPattern,
    bool RightIsKv = false)
    : PathSpec(RelationName, PerLayer: true);

// V then O projection, output scored against the un-embedding.
// Used for OV_RELATES: shows which output tokens a given token "writes toward" via the OV circuit.
public record ProjectionPath(
    string RelationName,
    string VPattern,               // first projection (KV space)
    string OPattern)               // second projection (back to model dim)
    : PathSpec(RelationName, PerLayer: true);

// Gate/Up/Down contraction: the intermediate dimension is contracted away inside the kernel.
// Used for COMPLETES_TO (FFN): source token → output token through the feed-forward pathway.
// Gate is optional (null for non-gated architectures like vanilla MLP).
public record ContractionPath(
    string RelationName,
    string? GatePattern,           // optional gating branch
    string UpPattern,
    string DownPattern)
    : PathSpec(RelationName, PerLayer: true);
