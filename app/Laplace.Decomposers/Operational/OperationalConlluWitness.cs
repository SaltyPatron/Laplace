using System.Collections.Concurrent;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.UD;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Operational;

/// <summary>
/// Authored operational annotations use the existing CoNLL-U parser and UD
/// composer. They are testimony by the operational source, not by upstream UD.
/// The unchanged full-file grammar remains their physical admission boundary.
/// </summary>
internal sealed class OperationalConlluWitness(
    IReadOnlyList<UdIngestRecord> records,
    string fileLabel,
    ConcurrentDictionary<string, byte> canonicalNames,
    ConcurrentIdSet seenSourceDeclarations) : IGrammarWitness
{
    public string ModalityId => "markdown";

    public void WalkRow(in GrammarComposeContext composed, in RowContext ctx,
        SubstrateChangeBuilder builder)
    {
        Hash128 file = ctx.ContextId is { } id && id != default
            ? id
            : throw new InvalidDataException("Authored CoNLL-U requires an admitted source-file context.");
        var handler = new UdIngestHandler(new UdWitnessContract(
            OperationalSource.SourceId, SourceTrust.SubstrateMandate, file),
            canonicalNames, fileLabel, seenSourceDeclarations);
        foreach (UdIngestRecord record in records)
        {
            // The same source record handler stages native content and complete
            // UD structure into this file's shared builder. It never applies a
            // change or creates an independent ingestion/file-completion loop.
            using IIngestDeferredUnit unit = handler.CreateDeferredUnit(record);
            Hash128 root = unit.DrainInto(builder, SourceTrust.SubstrateMandate, (byte[]?)null);
            handler.WalkWitness(record, root, builder, unit);
        }
    }
}
