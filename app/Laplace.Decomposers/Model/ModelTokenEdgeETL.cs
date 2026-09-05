using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Reserved evidence boundary for a future native model contraction. Checkpoint
/// ingestion retains OP0-OP3 source structure;
/// it cannot manufacture calibrated OP9 token evidence from salience rankings.
/// </summary>
public sealed class ModelTokenEdgeETL
{
    public static int TestimonyWidthPerCircuit => 0;

    public static string ResolvePlanesMode()
    {
        var value = Environment.GetEnvironmentVariable("LAPLACE_MODEL_PLANES");
        string mode = string.IsNullOrWhiteSpace(value) ? "structure" : value.Trim().ToLowerInvariant();
        if (mode == "structure") return mode;
        throw new InvalidOperationException(
            $"LAPLACE_MODEL_PLANES='{mode}' is not a valid model-ingest mode; " +
            "calibrated model contraction has not been admitted.");
    }

    private readonly ModelManifest _manifest;
    private readonly ILogger _log;

    public ModelTokenEdgeETL(string modelDir, ModelManifest manifest,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens, Hash128 sourceId,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(modelDir);
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>
    /// Deliberately emits no attestations. A checkpoint score or top-k selection is
    /// not an observed, calibrated token-to-token outcome. Numeric tensors remain transient native inputs until a governed OP3 candidate scan and OP4 contraction can emit calibrated outcomes.
    /// </summary>
    public async IAsyncEnumerable<SubstrateChange> EmitAsync(
        int commitEpoch,
        ISubstrateReader? reader,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _log.LogInformation(
            "phase=edges: deferred for {Name}; no calibrated token-to-token evidence", _manifest.ModelName);
        await Task.CompletedTask;
        yield break;
    }
}
