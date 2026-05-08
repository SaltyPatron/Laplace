namespace Laplace.Decomposers.Model;

/// <summary>
/// One of the three observed HuggingFace package layouts on disk. Determines
/// where the package's actual content (config.json, tokenizer files, weights,
/// etc.) lives relative to the path the user supplied.
/// </summary>
public enum HuggingFacePackageLayout
{
    /// <summary>Content is at the supplied path directly. README/LICENSE +
    /// config.json + tokenizer.json + model.safetensors + etc. are all in the
    /// top-level directory. Example: D:\Models\hub\Florence-2-base.</summary>
    DirectDirectory,

    /// <summary>HuggingFace cache layout: <c>models--&lt;org&gt;--&lt;name&gt;/</c>
    /// containing <c>refs/</c> + <c>snapshots/&lt;sha&gt;/</c>. Content lives
    /// inside the snapshot directory pointed to by refs/main. Example:
    /// D:\Models\hub\models--sentence-transformers--all-MiniLM-L6-v2.</summary>
    HuggingFaceCache,

    /// <summary>Diffusers multi-component layout: package root contains
    /// <c>model_index.json</c> declaring named subcomponents (text_encoder,
    /// transformer, vae, scheduler, tokenizer). Each subcomponent is itself
    /// a package (config + weights). Example: FLUX.2-dev, Stable Diffusion XL.</summary>
    DiffusersMultiComponent,
}
