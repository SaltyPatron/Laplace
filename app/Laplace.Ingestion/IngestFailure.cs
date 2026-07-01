using Laplace.Engine.Core;

namespace Laplace.Ingestion;

public sealed record IngestFailure(
    Hash128 IntentId,
    string SourceContentUnitName,
    string ExceptionType,
    string Message,
    bool WasTransient,
    int RetryAttempts,
    DateTimeOffset OccurredAt);
