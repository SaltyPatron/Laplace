namespace Laplace.Decomposers.Abstractions;

using System.IO;

/// <summary>
/// Route an input artifact to the appropriate per-modality decomposer based on
/// magic bytes / extension / declared MIME / content sniffing. Files with no
/// detected modality fall back to opaque-content + provenance-only ingestion
/// with a flag indicating semantic decomposition is unavailable for this
/// artifact (NOT a substitute for semantic decomposition where it could apply).
/// </summary>
public interface IModalityRouter
{
    /// <summary>Inspect the artifact and return the canonical modality decomposer name to dispatch to.</summary>
    string Route(string artifactPath, Stream content);
}
