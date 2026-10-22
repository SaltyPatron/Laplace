namespace Laplace.Engine.Core;

/// <summary>
/// The single client-side implementation of the substrate's consensus edge id.
/// Native Merkle composition of the strand's actual vertices, matching SQL
/// laplace.consensus_id and the native fold writer. A missing object contributes
/// no vertex; it must not mint an invented zero-filled constituent.
/// </summary>
public static class ConsensusKeys
{
    public static Hash128 EdgeId(Hash128 subject, Hash128 type, Hash128 obj)
    {
        ReadOnlySpan<Hash128> vertices = stackalloc Hash128[] { subject, type, obj };
        return Hash128.Merkle(0, vertices);
    }

    public static Hash128 EdgeId(Hash128 subject, Hash128 type, Hash128? obj)
    {
        if (obj is { } o) return EdgeId(subject, type, o);
        ReadOnlySpan<Hash128> vertices = stackalloc Hash128[] { subject, type };
        return Hash128.Merkle(0, vertices);
    }
}
