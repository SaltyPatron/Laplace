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


internal sealed record EntityEvidence(
    string EntityIdHex,
    string EntityLabel,
    IReadOnlyList<Laplace.Api.Contracts.LabeledEvidenceItem> Items);
