namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Marker for ingest lanes whose input is ordinary operator-selected digital content rather
/// than a curated source estate. A MANIFEST.tsv that merely happens to share the selected
/// directory must not replace the operator's file enumeration.
/// </summary>
public interface IIgnoresArtifactManifest
{
}
