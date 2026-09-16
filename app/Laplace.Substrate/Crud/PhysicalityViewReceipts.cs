using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

/// <summary>Validates the native PostgreSQL result before retaining its optional-view receipts.
/// This transports native identity and selection; it does not compute either.</summary>
internal static class PhysicalityViewReceipts
{
    internal static long RetainedPayloadBytes(long forms, long missing)
        => checked(forms * Unsafe.SizeOf<PhysicalityFormReceipt>() + missing * Unsafe.SizeOf<Hash128>());

    internal static (ImmutableArray<PhysicalityFormReceipt> Forms, ImmutableArray<Hash128> Missing)
        Decode(byte[][] descriptors, byte[]?[] views, short[] states, long[] first, long[] count,
            byte[][] missing, long maximumBytes, long maximumLogicalWork)
    {
        int length = descriptors.Length;
        if (views.Length != length || states.Length != length || first.Length != length || count.Length != length)
            throw new InvalidOperationException("physicality view receipt arrays do not align");
        if (maximumBytes < 0 || RetainedPayloadBytes(length, missing.LongLength) > maximumBytes)
            throw new InvalidOperationException("physicality view receipts exceed their retained payload grant");
        if (maximumLogicalWork < 0)
            throw new InvalidOperationException("physicality view receipt has an invalid work grant");

        // Validate every byte payload and slice before making retained copies.
        foreach (byte[]? id in missing)
            if (id is not { Length: 16 })
                throw new InvalidOperationException("physicality view receipt contains an invalid missing reference");
        long inspected = 0;
        long covered = 0;
        for (int i = 0; i < length; ++i)
        {
            if (descriptors[i] is not { Length: 16 }
                || first[i] < 0 || first[i] > missing.LongLength
                || count[i] < 0 || count[i] > missing.LongLength - first[i])
                throw new InvalidOperationException("physicality view receipt contains an invalid descriptor or missing slice");
            switch ((PhysicalityViewState)states[i])
            {
                case PhysicalityViewState.Available:
                    if (views[i] is not { Length: 16 } || count[i] != 0)
                        throw new InvalidOperationException("available physicality view must have an ID and no missing references");
                    break;
                case PhysicalityViewState.MissingReference:
                    if (views[i] is not null || count[i] == 0)
                        throw new InvalidOperationException("unavailable physicality view must have a null ID and an exact missing frontier");
                    break;
                default:
                    throw new InvalidOperationException("physicality view receipt contains an unknown disposition");
            }
            if (count[i] > maximumLogicalWork - inspected)
                throw new InvalidOperationException("physicality missing-reference validation exceeds its work grant");
            inspected += count[i];
            // Native publishes newly encountered ranges in source-form order;
            // repeated forms may reuse earlier ranges. This proves full coverage
            // without allocating a bitmap proportional to the frontier.
            if (count[i] != 0)
            {
                if (first[i] > covered)
                    throw new InvalidOperationException("physicality missing frontier has an unreferenced gap");
                covered = Math.Max(covered, first[i] + count[i]);
            }
            int end = checked((int)(first[i] + count[i]));
            for (int j = checked((int)first[i]) + 1; j < end; ++j)
                if (missing[j - 1].AsSpan().SequenceCompareTo(missing[j]) >= 0)
                    throw new InvalidOperationException("physicality missing frontier must be sorted and unique");
        }
        if (covered != missing.LongLength)
            throw new InvalidOperationException("physicality missing frontier has unreferenced entries");

        var missingIds = ImmutableArray.CreateBuilder<Hash128>(missing.Length);
        foreach (var id in missing) missingIds.Add(Hash128.FromBytes(id));
        var forms = ImmutableArray.CreateBuilder<PhysicalityFormReceipt>(length);
        for (int i = 0; i < length; ++i)
            forms.Add(new PhysicalityFormReceipt(Hash128.FromBytes(descriptors[i]),
                views[i] is { } view ? Hash128.FromBytes(view) : null,
                (PhysicalityViewState)states[i], first[i], count[i]));
        return (forms.MoveToImmutable(), missingIds.MoveToImmutable());
    }
}
