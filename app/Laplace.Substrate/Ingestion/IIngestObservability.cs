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

    /// <summary>A file was opened for ingest. The file boundary is already a real unit
    /// with a real identity — per-file resume (GH #898) deposits a HasLayerCompleted
    /// marker on the file's content identity and a restart true-skips it — but this
    /// contract had no file granularity, so the ledger could only ever hold
    /// files_done/files_total for the whole run and never which files those were.
    ///
    /// <paramref name="bytes"/> is recorded because input size is the quantity the file
    /// pump is NOT bounded by: file_channel and file_workers are counts, and a source's
    /// files can span five orders of magnitude (UD: 4,577 B to 360,217,466 B).</summary>
    void OnFileStarted(string sourceName, string fileLabel, long bytes = 0) { }

    /// <summary>A file reached a terminal state. <paramref name="status"/> is one of
    /// ok / skipped-complete / failed, matching ingest_file_journal's CHECK; a file that
    /// never reaches here stays 'running' and is reconciled to 'cancelled' with its run.</summary>
    void OnFileFinished(
        string sourceName, string fileLabel, string status,
        long records = 0, long entities = 0, long physicalities = 0, long attestations = 0,
        string? error = null) { }
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
