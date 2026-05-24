using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

/// <summary>
/// Per-intent observability seam — concrete impl emits Prometheus metrics
/// (laplace_ingest_*) and / or OpenTelemetry traces. The IngestRunner
/// calls these synchronously on the hot path; impls MUST be cheap.
///
/// Default <see cref="NoOpObservability.Instance"/> drops every event.
/// </summary>
public interface IIngestObservability
{
    /// <summary>Called once per IngestRunner.RunAsync invocation.</summary>
    void OnRunStart(string sourceName, int layerOrder, long? estimatedUnitCount);

    /// <summary>Called per applied intent (after successful ApplyAsync).</summary>
    void OnIntentApplied(string sourceName, ApplyResult result);

    /// <summary>Called per intent that was skipped via checkpoint resume.</summary>
    void OnIntentSkipped(string sourceName);

    /// <summary>Called per intent that failed after retries exhausted.</summary>
    void OnIntentFailed(string sourceName, IngestFailure failure);

    /// <summary>Called once per IngestRunner.RunAsync return.</summary>
    void OnRunFinished(string sourceName, IngestRunResult result);
}

/// <summary>No-op observability — drops every event. Used by default
/// when the caller doesn't supply one.</summary>
public sealed class NoOpObservability : IIngestObservability
{
    public static readonly NoOpObservability Instance = new();
    private NoOpObservability() { }
    public void OnRunStart(string sourceName, int layerOrder, long? estimatedUnitCount) { }
    public void OnIntentApplied(string sourceName, ApplyResult result) { }
    public void OnIntentSkipped(string sourceName) { }
    public void OnIntentFailed(string sourceName, IngestFailure failure) { }
    public void OnRunFinished(string sourceName, IngestRunResult result) { }
}
