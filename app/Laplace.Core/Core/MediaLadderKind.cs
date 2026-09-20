namespace Laplace.Engine.Core;

/// <summary>
/// Selects the currently installed legacy media-ladder recipe for
/// <c>laplace_modality_witness_emit_tree</c> (Image vs Audio labels).
/// The historical "every modality T0 is Unicode" rule is retired. These ABI
/// values describe current implementation state only; GH #1134 and
/// docs/invention/modality-ladder-law.md own the corrected representation.
/// </summary>
public enum MediaLadderKind
{
    Image = 1,
    Audio = 2,
}
