using System.Buffers;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>One native projection of a complete, explicitly witnessed UD parse.</summary>
public static class NativeUdWitness
{
    public static unsafe void Project(
        SubstrateChangeBuilder builder, ReadOnlySpan<Hash128> flat,
        Hash128 source, Hash128 parseOccurrence, Hash128 languageMiscKey,
        double witnessWeight)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (flat.IsEmpty)
            throw new ArgumentException("A complete parse trajectory is required.", nameof(flat));
        if (!double.IsFinite(witnessWeight) || witnessWeight < 0 || witnessWeight > 1)
            throw new ArgumentOutOfRangeException(nameof(witnessWeight));

        // The native bound follows from the parse layout, not a source size or
        // arbitrary per-token cap. Each annotation needs at least one flat ID.
        var staged = ArrayPool<AttestationStagedNative>.Shared.Rent(flat.Length);
        try
        {
            nuint count = 0;
            fixed (Hash128* input = flat)
            fixed (AttestationStagedNative* output = staged)
            {
                int rc = NativeInterop.UdWitnessBuild(
                    input, (nuint)flat.Length, &source, &parseOccurrence,
                    &languageMiscKey, witnessWeight, 0,
                    output, (nuint)flat.Length, &count);
                if (rc != 0)
                    throw new InvalidDataException($"Native UD witness projection failed: {rc}");
            }
            if (count > (nuint)flat.Length)
                throw new InvalidDataException("Native UD witness projection exceeded its output capacity.");

            // No partially decoded native prefix is publishable. This loop only
            // stages already-built rows into the common dedup/COPY/fold path;
            // annotation interpretation, identity and aggregation stay native.
            for (int i = 0; i < (int)count; ++i)
                builder.AddAttestation(NativeAttestation.Row(in staged[i]));
        }
        finally
        {
            ArrayPool<AttestationStagedNative>.Shared.Return(staged);
        }
    }
}
