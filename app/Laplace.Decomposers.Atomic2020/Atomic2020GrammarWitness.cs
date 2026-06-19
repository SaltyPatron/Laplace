using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Atomic2020;

internal sealed class Atomic2020GrammarWitness : IGrammarWitness
{
    private static readonly Hash128 NoneId = Hash128.OfCanonical("substrate/atomic/none/v1");

    public string ModalityId => "tsv";

    public void WalkRow(in GrammarComposeContext composed, in RowContext ctx, SubstrateChangeBuilder b)
    {
        if (composed.Composer is null) return;
        var fields = composed.Composer.FieldSpans();
        if (fields.Count < 3) return;

        ReadOnlySpan<byte> utf8 = composed.Utf8;
        ReadOnlySpan<byte> head = Slice(utf8, fields[0]);
        ReadOnlySpan<byte> rel = Slice(utf8, fields[1]);
        ReadOnlySpan<byte> tail = Slice(utf8, fields[2]);
        if (head.IsEmpty || rel.IsEmpty) return;

        if (!ContentWitnessBatch.TryAppendToBuilder(
                b, head, Atomic2020Decomposer.Source, out var headId))
            return;

        string relName = Encoding.UTF8.GetString(rel).Trim();
        if (!Atomic2020Decomposer.RelTypeId.TryGetValue(relName, out var typeName))
            return;

        Hash128 tailId;
        string tailText = Encoding.UTF8.GetString(tail).Trim();
        if (tailText.Length == 0 || tailText.Equals("none", StringComparison.OrdinalIgnoreCase))
            tailId = NoneId;
        else if (!ContentWitnessBatch.TryAppendToBuilder(
                     b, tail, Atomic2020Decomposer.Source, out tailId))
            return;

        b.AddAttestation(NativeAttestation.Categorical(
            headId, typeName, tailId, Atomic2020Decomposer.Source, TC.StructuredCorpus,
            contextId: ctx.ContextId));
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> utf8, (uint Start, uint End) sp) =>
        utf8[(int)sp.Start..(int)sp.End];
}
