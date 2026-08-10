using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

public interface IIngestObservability
{
    void OnRunStart(string sourceName, int layerOrder, IngestInventory? inventory);

    void OnIntentApplied(string sourceName, ApplyResult result);

    void OnIntentFailed(string sourceName, IngestFailure failure);

    /// <summary>Terminal exit; <paramref name="status"/> is the same value
    /// INGEST_COMPLETE logs (ok / failed / empty-noop / capped, or a decomposer-supplied
    /// expected-empty status via IIngestNoOpExplainer: already-present, already-complete, …).
    ///
    /// <paramref name="error"/> carries the reason whenever <paramref name="status"/> is a
    /// failure. It cannot be supplied by a later <see cref="OnRunFailed"/> call: that method
    /// no-ops once the run is terminal, which is why runs reached this method with
    /// status=failed and a NULL error column — a ledger row saying the run failed and
    /// nothing at all about why.</summary>
    void OnRunFinished(string sourceName, IngestRunResult result, string status, string? error = null);

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
    public void OnRunFinished(string sourceName, IngestRunResult result, string status, string? error = null) { }
}
