namespace Laplace.Decomposers.Abstractions;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Drive an external inference runtime (Python subprocess to Transformers /
/// vLLM with per-layer hooks; or in-process ONNX Runtime) to capture
/// activations on a probe corpus. Used by per-tensor extractors in the AI
/// model decomposer family for weight-as-edge extraction.
///
/// The probe corpus is drawn from substrate seed data (Tatoeba sentences for
/// general text, code samples for code models, audio clips for audio models,
/// etc.) so observations carry the trust-rated provenance of the seed source.
/// </summary>
public interface IProbeRunner
{
    /// <summary>Run a probe corpus through the model and return per-(layer, position) activations.</summary>
    Task<IReadOnlyList<ProbeActivation>> RunAsync(
        string modelDirectory,
        IReadOnlyList<string> probeCorpus,
        CancellationToken cancellationToken);
}

public record ProbeActivation(
    int Layer,
    string LayerKind,
    int Position,
    int? HeadIndex,
    float[] AttentionScores,
    float[] FfnActivations);
