using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

public interface IIngestObservability
{
    void OnRunStart(string sourceName, int layerOrder, long? estimatedUnitCount);

    void OnIntentApplied(string sourceName, ApplyResult result);

    void OnIntentFailed(string sourceName, IngestFailure failure);

    void OnRunFinished(string sourceName, IngestRunResult result);
}

public sealed class NoOpObservability : IIngestObservability
{
    public static readonly NoOpObservability Instance = new();
    private NoOpObservability() { }
    public void OnRunStart(string sourceName, int layerOrder, long? estimatedUnitCount) { }
    public void OnIntentApplied(string sourceName, ApplyResult result) { }
    public void OnIntentFailed(string sourceName, IngestFailure failure) { }
    public void OnRunFinished(string sourceName, IngestRunResult result) { }
}
