using System.Collections.Immutable;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Shared source-grammar semantic witness lowering. Grammar queries identify source spans;
/// the active composer remains the sole authority for the entities those spans denote.
/// </summary>
public static class GrammarTagWitness
{
    public static void Emit(
        SubstrateChangeBuilder builder,
        byte[] utf8,
        GrammarAst ast,
        GrammarRowComposer composer,
        string modality,
        Hash128 sourceId,
        double weight)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(utf8);
        ArgumentNullException.ThrowIfNull(ast);
        ArgumentNullException.ThrowIfNull(composer);

        IntPtr recipe = GrammarDecomposer.LookupById(modality);
        byte[]? tags = GrammarTags.TagsSource(modality);
        if (recipe == IntPtr.Zero || tags is null) return;

        var spanIds = new Dictionary<(uint Start, uint End), Hash128>();
        for (int i = 0; i < ast.NodeCount; ++i)
        {
            LaplaceAstNode node = ast.GetNode(i);
            if (composer.TrySpanEntity(node.StartByte, node.EndByte, out Hash128 id))
                spanIds.TryAdd((node.StartByte, node.EndByte), id);
        }

        foreach (AttestationRow attestation in Build(
                     utf8, recipe, tags, spanIds, sourceId, weight))
            builder.AddAttestation(attestation);
    }

    internal static ImmutableArray<AttestationRow> Build(
        byte[] utf8,
        IntPtr recipe,
        byte[]? tags,
        IReadOnlyDictionary<(uint Start, uint End), Hash128> spanIds,
        Hash128 sourceId,
        double weight)
    {
        if (recipe == IntPtr.Zero || tags is null)
            return ImmutableArray<AttestationRow>.Empty;

        IReadOnlyList<TagCapture> captures = GrammarTags.Run(recipe, tags, utf8);
        if (captures.Count == 0) return ImmutableArray<AttestationRow>.Empty;

        var rows = ImmutableArray.CreateBuilder<AttestationRow>();
        foreach (var group in captures.GroupBy(static capture => capture.MatchId))
        {
            Hash128? name = null;
            Hash128? definition = null;
            Hash128? call = null;
            Hash128? reference = null;
            foreach (TagCapture capture in group)
            {
                if (!spanIds.TryGetValue((capture.StartByte, capture.EndByte), out Hash128 id))
                    continue;
                switch (capture.Type)
                {
                    case TagType.Name:
                        name = id;
                        break;
                    case TagType.DefFunction:
                    case TagType.DefType:
                    case TagType.DefVar:
                        definition = id;
                        break;
                    case TagType.RefCall:
                        call = id;
                        break;
                    case TagType.RefType:
                        reference = id;
                        break;
                }
            }

            if (name is not { } nameId) continue;
            if (definition is { } definitionId)
                rows.Add(NativeAttestation.Categorical(
                    definitionId, "DEFINES", nameId, sourceId, null, weight));
            if (call is { } callId)
                rows.Add(NativeAttestation.Categorical(
                    callId, "CALLS", nameId, sourceId, null, weight));
            if (reference is { } referenceId)
                rows.Add(NativeAttestation.Categorical(
                    referenceId, "REFERENCES", nameId, sourceId, null, weight));
        }
        return rows.ToImmutable();
    }
}
