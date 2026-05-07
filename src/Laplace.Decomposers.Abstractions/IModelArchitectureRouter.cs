namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Inspect an AI model's <c>config.json</c> and route to the matching
/// architecture-family decomposer (DecoderOnly / EncoderDecoder /
/// EncoderOnly / MoE / MoeMla / VisionEncoder / AudioEncoder / AudioDecoder /
/// Diffusion / Multimodal / Reranker).
/// </summary>
public interface IModelArchitectureRouter
{
    /// <summary>
    /// Returns the canonical architecture-family code for the model in
    /// <paramref name="modelDirectory"/> based on its config.json.
    /// </summary>
    string Route(string modelDirectory);
}
