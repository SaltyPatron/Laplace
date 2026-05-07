namespace Laplace.Decomposers.Tatoeba;

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Text;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// F4 — Tatoeba parallel-sentence corpus decomposer. Demonstrates cross-
/// language equivalence emerging from parallel-sentence cooccurrence
/// edges WITHOUT any anchor language: cat (English sentence) and 猫
/// (Japanese sentence) become substrate entities via F1, and the link
/// between them is a parallel_translation edge weighted by the source's
/// Glicko-2 rating.
///
/// Per substrate invariant 4: cross-language equivalence is graph-emergent
/// from this kind of edge density. No mapping to a privileged anchor.
/// </summary>
public sealed class TatoebaDecomposer
{
    private static readonly int[] BinaryEdgeRleCounts = new[] { 1, 1, 1 };

    private readonly TextDecomposer          _textDecomposer;
    private readonly IIdentityHashing        _hashing;
    private readonly IConceptEntityResolver  _conceptResolver;
    private readonly IEntityEmission         _entityEmission;
    private readonly IEdgeEmission           _edgeEmission;
    private readonly IProvenance             _provenance;

    public TatoebaDecomposer(
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

    public async Task DecomposeAsync(string tatoebaDirectory, CancellationToken cancellationToken)
    {
        var sourceHash = await _provenance.ResolveSourceAsync(
            "tatoeba_corpus", cancellationToken).ConfigureAwait(false);

        var hasLanguageEdge        = _conceptResolver.Resolve("has_language");
        var parallelTranslationEdge = _conceptResolver.Resolve("parallel_translation");
        var roleSubject            = _conceptResolver.Resolve("subject");
        var roleObject             = _conceptResolver.Resolve("object");

        // Pass 1: sentences → substrate entities + has_language edges.
        // Per invariant 1: language is referenced by entity_hash (the
        // language entity from Iso639Decomposer), not by language_id integer.
        var sentencesPath = Path.Combine(tatoebaDirectory, "sentences.csv");
        var sentenceIdToHash = new Dictionary<long, AtomId>();

        if (File.Exists(sentencesPath))
        {
            foreach (var sentence in TatoebaParser.ParseSentences(sentencesPath))
            {
                var sentenceHash = await _textDecomposer.DecomposeAsync(
                    sentence.Text, cancellationToken).ConfigureAwait(false);
                sentenceIdToHash[sentence.Id] = sentenceHash;

                var languageHash = await _textDecomposer.DecomposeAsync(
                    sentence.LanguageIso6393, cancellationToken).ConfigureAwait(false);

                await EmitBinaryEdgeAsync(
                    hasLanguageEdge, roleSubject, sentenceHash, roleObject, languageHash,
                    sourceHash, cancellationToken).ConfigureAwait(false);

                await _provenance.EmitEntityProvenanceAsync(
                    new EntityProvenanceRecord(EntityHash: sentenceHash, SourceHash: sourceHash),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // Pass 2: parallel-translation links between sentence entities.
        var linksPath = Path.Combine(tatoebaDirectory, "links.csv");
        if (File.Exists(linksPath))
        {
            foreach (var link in TatoebaParser.ParseLinks(linksPath))
            {
                if (!sentenceIdToHash.TryGetValue(link.SourceId, out var sourceSentence)) { continue; }
                if (!sentenceIdToHash.TryGetValue(link.TargetId, out var targetSentence)) { continue; }
                await EmitBinaryEdgeAsync(
                    parallelTranslationEdge, roleSubject, sourceSentence, roleObject, targetSentence,
                    sourceHash, cancellationToken).ConfigureAwait(false);
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
