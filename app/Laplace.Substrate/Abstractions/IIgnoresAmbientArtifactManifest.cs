namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Marks a direct-content ingest lane whose operator-selected path is the input authority.
///
/// A MANIFEST.tsv found in that path may describe a curated dataset estate that happens to
/// share the directory. It must not silently replace the decomposer's own file discovery.
/// Explicit production-manifest mode (<c>RequireArtifactManifest=true</c>) still wins.
/// </summary>
public interface IIgnoresAmbientArtifactManifest
{
}
