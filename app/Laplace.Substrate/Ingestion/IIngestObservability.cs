using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

public interface IIngestObservability
{
    void OnRunStart(string sourceName, int layerOrder, IngestInventory? inventory);

    void OnRunStart(
        string sourceName,
        int layerOrder,
        IngestInventory? inventory,
        IngestArtifactGraph? artifactGraph) => OnRunStart(sourceName, layerOrder, inventory);

    void OnIntentApplied(string sourceName, ApplyResult result);

    void OnIntentFailed(string sourceName, IngestFailure failure);

    void OnRunFinished(string sourceName, IngestRunResult result, string status, string? error = null);

    void OnRunFailed(string sourceName, string status, string error) { }

    void OnRunSkipped(string sourceName, int layerOrder) { }

    void OnProgress(IngestProgress progress) { }

    void OnCompletionPhase(string sourceName, BulkRunCompletionPhase phase) { }

    void OnBulkCompletion(
        string sourceName, TimeSpan foldDrain, TimeSpan writerMaintenance,
        TimeSpan foldSpan, TimeSpan consensusBackendWork, TimeSpan highwayMaskBackendWork,
        long consensusCalls, long highwayMaskCalls, long highwayMaskPairs) { }

    /// <summary>Compatibility file-start callback retained for existing observers.</summary>
    void OnFileStarted(string sourceName, string fileLabel, long bytes = 0) { }

    /// <summary>
    /// File-start callback with observational filesystem metadata. Size and mtime are not
    /// file-entity identity; the persistent ingest journal is where those observations live.
    /// The default forwards to the old callback so existing observers continue to receive it.
    /// </summary>
    void OnFileStarted(
        string sourceName,
        string fileLabel,
        long bytes,
        DateTimeOffset? modifiedAt)
        => OnFileStarted(sourceName, fileLabel, bytes);

    void OnFileProgress(
        string sourceName, string fileLabel,
        long records, long entities, long physicalities, long attestations) { }

    void OnFileComposed(
        string sourceName, string fileLabel, Hash128? fileId = null,
        long records = 0, long entities = 0, long physicalities = 0, long attestations = 0,
        Hash128? resumeFingerprint = null) { }

    void OnFileFinished(
        string sourceName, string fileLabel, string status, string? error = null) { }
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
