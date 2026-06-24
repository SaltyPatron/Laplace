namespace Laplace.Endpoints.OpenAICompat;




internal sealed record ConverseRow(string Reply, decimal EffectiveMu, long Witnesses);

internal sealed record CompletionRow(
    string ObjectIdHex,
    string TypeIdHex,
    decimal EffectiveMu,
    long Witnesses,
    string ObjectLabel);

internal sealed record GenerateToken(int Step, string Token, decimal Mu);

internal sealed record EmbeddingVector(bool Resolved, IReadOnlyList<double> Values);

internal sealed record StructuralNeighbor(string Neighbor, double Geodesic, double? Frechet);

// Laplace embeddings are TWO levels, never one conflated blob:
//   FORM    = the S³ geometry coordinate (a real dense vector; orthographic/compositional locality).
//   MEANING = the Glicko-2 consensus neighbourhood (salience-filtered), expressed as ranked neighbours
//             rather than a dense vector, because meaning is the witnessed relational field, not a point.
internal sealed record EmbeddingForm(double X, double Y, double Z, double M, double Radius, int Constituents);

internal sealed record MeaningNeighbor(string Relation, string ObjectLabel, decimal EffMu, long Witnesses);

internal sealed record EmbeddingResult(
    string? EntityIdHex,
    EmbeddingForm? Form,
    IReadOnlyList<MeaningNeighbor> Meaning);


internal sealed record EntityEvidence(
    string EntityIdHex,
    string EntityLabel,
    IReadOnlyList<Laplace.Api.Contracts.LabeledEvidenceItem> Items);
