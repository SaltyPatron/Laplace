using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Atomic2020;

internal sealed class Atomic2020Witness : IGrammarWitness
{
    public string ModalityId => "tsv";

    private static readonly Hash128 NoneId = Hash128.OfCanonical("substrate/atomic/none/v1");

    public void WalkRow(in GrammarComposeContext composed, in RowContext ctx, SubstrateChangeBuilder b)
    {
        if (composed.Composer is null) return;
        var fields = composed.Composer.FieldSpans();
        if (fields.Count < 3) return;

        if (!composed.Composer.TrySpanEntity(fields[0].Start, fields[0].End, out var headId))
            return;

        string rel = Encoding.UTF8.GetString(
            composed.Utf8.AsSpan((int)fields[1].Start, (int)(fields[1].End - fields[1].Start))).Trim();
        if (!Atomic2020Decomposer.RelTypeId.TryGetValue(rel, out var typeName))
            return;

        Hash128 tailId;
        var tailText = Encoding.UTF8.GetString(
            composed.Utf8.AsSpan((int)fields[2].Start, (int)(fields[2].End - fields[2].Start))).Trim();
        if (tailText.Length == 0 || tailText.Equals("none", StringComparison.OrdinalIgnoreCase))
            tailId = NoneId;
        else if (!composed.Composer.TrySpanEntity(fields[2].Start, fields[2].End, out tailId))
            return;

        b.AddAttestation(RelationTypeRegistry.Attest(
            headId, typeName, tailId, Atomic2020Decomposer.Source, SourceTrust.StructuredCorpus,
            contextId: ctx.ContextId));
    }
}
