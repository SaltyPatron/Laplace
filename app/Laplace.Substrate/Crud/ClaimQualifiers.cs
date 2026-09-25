using System.Collections.Concurrent;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

/// <summary>Governed claim qualifiers (engine/manifest/qualifiers.toml) as attestation mask
/// bits, resolved through the native qualifier law.</summary>
public static class ClaimQualifiers
{
    public static Mask256 Of(string family, string value)
    {
        int bit = NativeInterop.QualifierBit(family, value);
        if (bit < 0)
            throw new InvalidOperationException($"Undeclared claim qualifier {family}/{value}.");
        return Mask256.Zero.Set((byte)bit);
    }
}

/// <summary>
/// Sources whose claims are deterministic calculations (an engine's evaluation, outcome
/// tallies over recorded games). Every claim such a source writes carries
/// derivation/calculation, so readers tell calculated evidence from observed testimony
/// by the claim itself; the source never testifies to a trust class about itself.
/// The calculating modules register their sources when they load.
/// </summary>
public static class CalculationSources
{
    private static readonly ConcurrentDictionary<Hash128, byte> Sources = new();

    public static Mask256 Qualifier { get; } = ClaimQualifiers.Of("derivation", "calculation");

    public static void Register(Hash128 sourceId) => Sources.TryAdd(sourceId, 0);

    public static bool Contains(Hash128 sourceId) => !Sources.IsEmpty && Sources.ContainsKey(sourceId);
}
