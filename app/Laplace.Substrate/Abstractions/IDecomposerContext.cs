using Microsoft.Extensions.Logging;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public interface IDecomposerContext
{
    string EcosystemPath { get; }

    ISubstrateWriter Writer { get; }

    ISubstrateReader Reader { get; }

    ILogger Logger { get; }

    string SubstrateVersion { get; }
}
