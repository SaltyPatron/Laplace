using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Atomic2020;

internal static class Atomic2020Witness
{
    private static readonly Hash128 NoneId = Hash128.OfCanonical("substrate/atomic/none/v1");

    public static void WalkRow(in Atomic2020TsvRow row, SubstrateChangeBuilder b)
    {
        if (!ContentWitnessBatch.TryAppendToBuilder(
                b, row.Head, Atomic2020Decomposer.Source, out var headId))
            return;

        string rel = row.RelationText();
        if (!Atomic2020Decomposer.RelTypeId.TryGetValue(rel, out var typeName))
            return;

        Hash128 tailId;
        string tailText = row.TailText();
        if (tailText.Length == 0 || tailText.Equals("none", StringComparison.OrdinalIgnoreCase))
            tailId = NoneId;
        else if (!ContentWitnessBatch.TryAppendToBuilder(
                     b, row.Tail, Atomic2020Decomposer.Source, out tailId))
            return;

        b.AddAttestation(NativeAttestation.Categorical(
            headId, typeName, tailId, Atomic2020Decomposer.Source, SourceTrust.StructuredCorpus));
    }
}
