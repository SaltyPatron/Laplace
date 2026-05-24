using Microsoft.Extensions.Logging;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Per-decomposer context handed in at <see cref="IDecomposer.InitializeAsync"/>
/// + <see cref="IDecomposer.DecomposeAsync"/>. Per ADR 0051 lines 101–121.
/// </summary>
public interface IDecomposerContext
{
    /// <summary>Path to the source ecosystem on disk
    /// (e.g. <c>/vault/Data/Wordnet/</c>).</summary>
    string EcosystemPath { get; }

    /// <summary>Substrate write surface — every decomposer routes intents
    /// through this one writer. Per ADR 0050.</summary>
    ISubstrateWriter Writer { get; }

    /// <summary>Read access for verifying existing substrate state during
    /// bootstrap (e.g. "does the SubstrateCanonical source entity exist
    /// yet?"). Read-only.</summary>
    ISubstrateReader Reader { get; }

    /// <summary>Per-decomposer structured logger.</summary>
    ILogger Logger { get; }

    /// <summary>Substrate-version tag — informs deterministic content-addressed
    /// IDs for bootstrap entities (whose name includes substrate-version per
    /// ADR 0042).</summary>
    string SubstrateVersion { get; }
}
