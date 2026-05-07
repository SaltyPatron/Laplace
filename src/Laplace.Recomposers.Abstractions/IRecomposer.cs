namespace Laplace.Recomposers.Abstractions;

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// Base contract for per-modality recomposers. Walks the substrate composition
/// Merkle tree starting at a root entity hash and emits the original artifact
/// (text bytes, audio PCM, image grid, video frames + audio, AI model in a
/// chosen target format).
///
/// Foundational seed data does NOT recompose — there are no recomposers for
/// UCD, UCA, Unihan, ISO 639, WordNet, OMW, UD, Wiktionary, Tatoeba, ATOMIC,
/// ArXiv. User content + AI models DO recompose.
/// </summary>
public interface IRecomposer
{
    /// <summary>Canonical modality name (e.g. "text", "audio", "image", "video", "model").</summary>
    string Modality { get; }

    /// <summary>
    /// Walk the substrate from <paramref name="rootEntity"/> and write the
    /// reconstructed artifact to <paramref name="output"/>. For lossless
    /// modalities the byte-for-byte equality check via
    /// <c>fc.exe /b</c> against the original ingestion source must succeed
    /// (covered by convergence gate G8).
    /// </summary>
    Task WriteAsync(
        AtomId rootEntity,
        Stream output,
        RecompositionOptions options,
        CancellationToken cancellationToken);
}

/// <summary>
/// Per-modality recomposer options (target format, threshold for distillation,
/// quantization precision, etc.). Concrete recomposers extend with derived
/// records.
/// </summary>
public abstract record RecompositionOptions;
