namespace Laplace.Engine.Core;

/// <summary>
/// Selects the modality ladder entity-type floor for
/// <c>laplace_modality_witness_emit_tree</c> (Image vs Audio type labels).
/// Not a private tier-0 alphabet — T0 is always Unicode codepoints
/// (<see cref="CodepointPerfcache"/>). ABI values match historic
/// <c>laplace_modality_t</c> Image=1 / Audio=2 until native rips that enum.
/// </summary>
public enum MediaLadderKind
{
    Image = 1,
    Audio = 2,
}
