using Laplace.Engine.Core;

namespace Laplace.Ingestion;

/// <summary>One per-intent failure record. Aggregated into
/// <see cref="IngestRunResult.Failures"/>.</summary>
public sealed record IngestFailure(
    Hash128   IntentId,
    string    SourceContentUnitName,
    string    ExceptionType,
    string    Message,
    bool      WasTransient,
    int       RetryAttempts,
    DateTimeOffset OccurredAt);
