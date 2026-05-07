namespace Laplace.Decomposers.Ud;

using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Text;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// F4 — Universal Dependencies treebank decomposer. Per token in each
/// CoNLL-U sentence: emits the surface form via F1, the lemma via F1
/// (cross-source dedup with WordNet/OMW/Wiktionary lemmas), and dependency
/// + UPOS edges between substrate entities.
///
/// Per substrate invariant 4: UD's UPOS tags are NOT mapped to anchor
/// "Noun"/"Verb"/etc. entities. They flip the corresponding bit in the
/// entity's prime_flags column (NOUN bit, VERB bit, etc.) and are
/// recorded as has_upos edges to UPOS-tag substrate entities (which are
/// themselves text compositions of "NOUN", "VERB", etc.). The bit-flag
/// path drives fast filter queries; the edge path lets the substrate
/// trace any per-entity attestation back to its source.
/// </summary>
public sealed class UdDecomposer
{
    private static readonly int[] BinaryEdgeRleCounts    = new[] { 1, 1, 1 };
    private static readonly int[] QuaternaryEdgeRleCounts = new[] { 1, 1, 1, 1 };

    private readonly TextDecomposer          _textDecomposer;
    private readonly IIdentityHashing        _hashing;
    private readonly IConceptEntityResolver  _conceptResolver;
    private readonly IEntityEmission         _entityEmission;
    private readonly IEdgeEmission           _edgeEmission;
    private readonly IProvenance             _provenance;

    public UdDecomposer(
        TextDecomposer         textDecomposer,
        IIdentityHashing       hashing,
        IConceptEntityResolver conceptResolver,
        IEntityEmission        entityEmission,
        IEdgeEmission          edgeEmission,
        IProvenance            provenance)
    {
        _textDecomposer  = textDecomposer;
        _hashing         = hashing;
        _conceptResolver = conceptResolver;
        _entityEmission  = entityEmission;
        _edgeEmission    = edgeEmission;
        _provenance      = provenance;
    }

    public async Task DecomposeTreebankAsync(string conlluPath, string treebankCanonicalName, CancellationToken cancellationToken)
    {
        var sourceHash = await _provenance.ResolveSourceAsync(
            $"ud_treebank_{treebankCanonicalName}", cancellationToken).ConfigureAwait(false);

        var hasLemmaEdge   = _conceptResolver.Resolve("has_lemma");
        var hasUposEdge    = _conceptResolver.Resolve("has_upos");
        var hasDeprelEdge  = _conceptResolver.Resolve("has_dependency_relation");
        var roleSubject    = _conceptResolver.Resolve("subject");
        var roleObject     = _conceptResolver.Resolve("object");
        var roleHead       = _conceptResolver.Resolve("head");
        var roleDependent  = _conceptResolver.Resolve("dependent");
        var roleLabel      = _conceptResolver.Resolve("label");

        if (!File.Exists(conlluPath)) { return; }

        foreach (var sentence in UdConlluParser.Parse(conlluPath))
        {
            var sentenceHash = await _textDecomposer.DecomposeAsync(
                sentence.Text, cancellationToken).ConfigureAwait(false);
            await _provenance.EmitEntityProvenanceAsync(
                new EntityProvenanceRecord(sentenceHash, sourceHash),
                cancellationToken).ConfigureAwait(false);

            // Pre-decompose every token so head links resolve.
            var tokenHashes = new System.Collections.Generic.Dictionary<string, AtomId>(sentence.Tokens.Count);
            foreach (var token in sentence.Tokens)
            {
                if (string.IsNullOrEmpty(token.Form)) { continue; }
                var formHash = await _textDecomposer.DecomposeAsync(token.Form, cancellationToken).ConfigureAwait(false);
                tokenHashes[token.Id] = formHash;

                if (!string.IsNullOrEmpty(token.Lemma) && token.Lemma != "_" && token.Lemma != token.Form)
                {
                    var lemmaHash = await _textDecomposer.DecomposeAsync(token.Lemma, cancellationToken).ConfigureAwait(false);
                    await EmitBinaryEdgeAsync(
                        hasLemmaEdge, roleSubject, formHash, roleObject, lemmaHash,
                        sourceHash, cancellationToken).ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(token.Upos) && token.Upos != "_")
                {
                    var uposEntityHash = await _textDecomposer.DecomposeAsync(token.Upos, cancellationToken).ConfigureAwait(false);
                    await EmitBinaryEdgeAsync(
                        hasUposEdge, roleSubject, formHash, roleObject, uposEntityHash,
                        sourceHash, cancellationToken).ConfigureAwait(false);
                }
            }

            // Dependency edges: dep → has_dependency_relation(label) → head
            foreach (var token in sentence.Tokens)
            {
                if (!tokenHashes.TryGetValue(token.Id, out var dependentHash)) { continue; }
                if (string.IsNullOrEmpty(token.Head) || token.Head == "0" || token.Head == "_") { continue; }
                if (!tokenHashes.TryGetValue(token.Head, out var headHash)) { continue; }
                if (string.IsNullOrEmpty(token.Deprel) || token.Deprel == "_") { continue; }

                var deprelLabelHash = await _textDecomposer.DecomposeAsync(token.Deprel, cancellationToken).ConfigureAwait(false);
                var edgeHash = _hashing.CompositionId(
                    new[] { hasDeprelEdge, dependentHash, headHash, deprelLabelHash },
                    QuaternaryEdgeRleCounts);

                await _edgeEmission.EmitEdgeAsync(
                    new EdgeRecord(EdgeTypeHash: hasDeprelEdge, Hash: edgeHash),
                    cancellationToken).ConfigureAwait(false);
                await _edgeEmission.EmitMemberAsync(
                    new EdgeMemberRecord(hasDeprelEdge, edgeHash, roleDependent, 0, dependentHash),
                    cancellationToken).ConfigureAwait(false);
                await _edgeEmission.EmitMemberAsync(
                    new EdgeMemberRecord(hasDeprelEdge, edgeHash, roleHead, 0, headHash),
                    cancellationToken).ConfigureAwait(false);
                await _edgeEmission.EmitMemberAsync(
                    new EdgeMemberRecord(hasDeprelEdge, edgeHash, roleLabel, 0, deprelLabelHash),
                    cancellationToken).ConfigureAwait(false);
                await _provenance.EmitEdgeProvenanceAsync(
                    new EdgeProvenanceRecord(hasDeprelEdge, edgeHash, sourceHash),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EmitBinaryEdgeAsync(
        AtomId edgeType, AtomId subjectRole, AtomId subject, AtomId objectRole, AtomId @object,
        AtomId sourceHash, CancellationToken cancellationToken)
    {
        var edgeHash = _hashing.CompositionId(
            new[] { edgeType, subject, @object }, BinaryEdgeRleCounts);

        await _edgeEmission.EmitEdgeAsync(
            new EdgeRecord(EdgeTypeHash: edgeType, Hash: edgeHash),
            cancellationToken).ConfigureAwait(false);
        await _edgeEmission.EmitMemberAsync(
            new EdgeMemberRecord(edgeType, edgeHash, subjectRole, 0, subject),
            cancellationToken).ConfigureAwait(false);
        await _edgeEmission.EmitMemberAsync(
            new EdgeMemberRecord(edgeType, edgeHash, objectRole, 0, @object),
            cancellationToken).ConfigureAwait(false);
        await _provenance.EmitEdgeProvenanceAsync(
            new EdgeProvenanceRecord(edgeType, edgeHash, sourceHash),
            cancellationToken).ConfigureAwait(false);
    }
}
