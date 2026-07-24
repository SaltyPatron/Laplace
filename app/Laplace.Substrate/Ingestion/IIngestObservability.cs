using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

public interface IIngestObservability
{
    void OnRunStart(string sourceName, int layerOrder, IngestInventory? inventory);

    void OnIntentApplied(string sourceName, ApplyResult result);

    void OnIntentFailed(string sourceName, IngestFailure failure);

    /// <summary>Terminal success-shaped exit; <paramref name="status"/> is the same value
    /// INGEST_COMPLETE logs (ok / failed / empty-noop / capped).</summary>
    void OnRunFinished(string sourceName, IngestRunResult result, string status);

    /// <summary>Terminal abnormal exit (exception or cancellation) — called when
    /// <see cref="OnRunFinished"/> was NOT reached; implementations that already
    /// finalized the run ignore it.</summary>
    void OnRunFailed(string sourceName, string status, string error) { }

    /// <summary>The run short-circuited on a completion marker before doing any work
    /// (this path reaches neither <see cref="OnRunStart"/> nor <see cref="OnRunFinished"/>).</summary>
    void OnRunSkipped(string sourceName, int layerOrder) { }

    /// <summary>Throttleable progress snapshot alongside each applied batch.</summary>
    void OnProgress(IngestProgress progress) { }
}

public sealed class NoOpObservability : IIngestObservability
{
    public static readonly NoOpObservability Instance = new();
    private NoOpObservability() { }
    public void OnRunStart(string sourceName, int layerOrder, IngestInventory? inventory) { }
    public void OnIntentApplied(string sourceName, ApplyResult result) { }
    public void OnIntentFailed(string sourceName, IngestFailure failure) { }
    public void OnRunFinished(string sourceName, IngestRunResult result, string status) { }
}
