using Microsoft.Extensions.Logging;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public interface IDecomposerContext
{
    string EcosystemPath { get; }

    IReadOnlyList<IngestArtifact> SelectedArtifacts => Array.Empty<IngestArtifact>();

    bool HasArtifactGraph => SelectedArtifacts.Count > 0;

    ISubstrateWriter Writer { get; }

    ISubstrateReader Reader { get; }

    ILogger Logger { get; }

    string SubstrateVersion { get; }
}
