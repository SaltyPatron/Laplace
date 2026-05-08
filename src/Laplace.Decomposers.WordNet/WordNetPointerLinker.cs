namespace Laplace.Decomposers.WordNet;

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Text;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// F4 — WordNet pointer linker. Second-pass companion to
/// <see cref="WordNetDecomposer"/>. Emits the inter-synset typed edges
/// (hypernym, hyponym, member_holonym, substance_meronym, antonym,
/// derivationally_related_form, entailment, cause, similar_to, etc.) that
/// the first-pass decomposer cannot emit because target synset hashes
/// aren't known until every synset in the file family has been hashed.
///
/// Per substrate invariant 4 (knowledge IS edges + intersections), the
/// substrate's concept hierarchy IS this edge graph; without the pointer
/// edges, OMW cross-language has_sense attachments would land on isolated
/// synset entities with no relational density. The pointer linker is what
/// makes "find concepts hypernymically related to cat" a substrate query.
///
/// Pointer-symbol → concept mapping mirrors <see cref="WordNetDecomposer"/>'s
/// MapPointerSymbolToConcept. The first WordNet pass is responsible for
/// emitting synset entity rows; this linker is responsible for inter-synset
/// edges; together they constitute the WordNet seed contribution.
///
/// Phase 4 / Track F4.
/// </summary>
public sealed class WordNetPointerLinker
{
    private static readonly int[] BinaryEdgeRleCounts = new[] { 1, 1, 1 };

    private readonly TextDecomposer         _textDecomposer;
    private readonly IIdentityHashing       _hashing;
    private readonly IConceptEntityResolver _conceptResolver;
    private readonly IEdgeEmission          _edgeEmission;
    private readonly IProvenance            _provenance;

    public WordNetPointerLinker(
        TextDecomposer         textDecomposer,
        IIdentityHashing       hashing,
        IConceptEntityResolver conceptResolver,
        IEdgeEmission          edgeEmission,
        IProvenance            provenance)
    {
        _textDecomposer  = textDecomposer;
        _hashing         = hashing;
        _conceptResolver = conceptResolver;
        _edgeEmission    = edgeEmission;
        _provenance      = provenance;
    }

    public async Task LinkAsync(
        string             wordnetDictionaryDirectory,
        CancellationToken  cancellationToken)
    {
        var sourceHash = await _provenance.ResolveSourceAsync(
            "princeton_wordnet_3_1", cancellationToken).ConfigureAwait(false);

        // Two passes over the WordNet data files:
        //   Pass 1 — build (offset, ss_type) → synset_hash for every synset.
        //   Pass 2 — for each synset, iterate pointers and emit edges to
        //            target synset hashes resolved via the map.
        // We don't load both passes into memory at once for the synsets
        // themselves; only the hash index (small) is held across passes.
        var synsetIndex = await BuildSynsetIndexAsync(
            wordnetDictionaryDirectory, cancellationToken).ConfigureAwait(false);

        var subjectRole = _conceptResolver.Resolve("subject");
        var objectRole  = _conceptResolver.Resolve("object");

        foreach (var (file, defaultType) in WordNetFileFamily)
        {
            var path = Path.Combine(wordnetDictionaryDirectory, file);
            if (!File.Exists(path)) { continue; }
            var parser = new WordNetDataParser(defaultType);
            foreach (var synset in parser.Parse(path))
            {
                if (synset.Pointers.Count == 0) { continue; }
                if (!synsetIndex.TryGetValue((synset.SynsetOffset, synset.Type), out var sourceSynsetHash)) { continue; }

                foreach (var ptr in synset.Pointers)
                {
                    if (!synsetIndex.TryGetValue((ptr.TargetOffset, ptr.TargetType), out var targetSynsetHash)) { continue; }

                    var conceptName = MapPointerSymbolToConcept(ptr.Symbol);
                    var edgeType    = _conceptResolver.Resolve(conceptName);

                    await EmitBinaryEdgeAsync(
                        edgeType:    edgeType,
                        subjectRole: subjectRole,
                        subjectHash: sourceSynsetHash,
                        objectRole:  objectRole,
                        objectHash:  targetSynsetHash,
                        sourceHash:  sourceHash,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<Dictionary<(long Offset, WordNetSynsetType Type), AtomId>> BuildSynsetIndexAsync(
        string             wordnetDictionaryDirectory,
        CancellationToken  cancellationToken)
    {
        var map = new Dictionary<(long, WordNetSynsetType), AtomId>();
        foreach (var (file, defaultType) in WordNetFileFamily)
        {
            var path = Path.Combine(wordnetDictionaryDirectory, file);
            if (!File.Exists(path)) { continue; }
            var parser = new WordNetDataParser(defaultType);
            foreach (var synset in parser.Parse(path))
            {
                if (synset.Lemmas.Count == 0) { continue; }
                var lemmaHashes = new AtomId[synset.Lemmas.Count];
                for (int i = 0; i < synset.Lemmas.Count; ++i)
                {
                    lemmaHashes[i] = await _textDecomposer.DecomposeAsync(
                        synset.Lemmas[i].SurfaceForm, cancellationToken).ConfigureAwait(false);
                }
                var counts = new int[lemmaHashes.Length];
                for (int i = 0; i < counts.Length; ++i) { counts[i] = 1; }
                map[(synset.SynsetOffset, synset.Type)] =
                    _hashing.CompositionId(lemmaHashes, counts);
            }
        }
        return map;
    }

    private async Task EmitBinaryEdgeAsync(
        AtomId            edgeType,
        AtomId            subjectRole,
        AtomId            subjectHash,
        AtomId            objectRole,
        AtomId            objectHash,
        AtomId            sourceHash,
        CancellationToken cancellationToken)
    {
        var edgeHash = _hashing.CompositionId(
            new[] { edgeType, subjectHash, objectHash }, BinaryEdgeRleCounts);

        await _edgeEmission.EmitEdgeAsync(
            new EdgeRecord(EdgeTypeHash: edgeType, Hash: edgeHash),
            cancellationToken).ConfigureAwait(false);
        await _edgeEmission.EmitMemberAsync(
            new EdgeMemberRecord(edgeType, edgeHash, subjectRole, 0, subjectHash),
            cancellationToken).ConfigureAwait(false);
        await _edgeEmission.EmitMemberAsync(
            new EdgeMemberRecord(edgeType, edgeHash, objectRole, 0, objectHash),
            cancellationToken).ConfigureAwait(false);
        await _provenance.EmitEdgeProvenanceAsync(
            new EdgeProvenanceRecord(edgeType, edgeHash, sourceHash),
            cancellationToken).ConfigureAwait(false);
    }

    private static readonly (string File, WordNetSynsetType DefaultType)[] WordNetFileFamily =
    {
        ("data.noun", WordNetSynsetType.Noun),
        ("data.verb", WordNetSynsetType.Verb),
        ("data.adj",  WordNetSynsetType.Adjective),
        ("data.adv",  WordNetSynsetType.Adverb),
    };

    /// <summary>
    /// Princeton WordNet 3.1 wndb(5) pointer symbol → concept canonical name.
    /// Mirrors <see cref="WordNetDecomposer"/>'s mapping verbatim so the same
    /// pointer in either pass resolves to the same substrate concept entity.
    /// </summary>
    private static string MapPointerSymbolToConcept(string symbol) => symbol switch
    {
        "!"  => "antonym",
        "@"  => "hypernym",
        "@i" => "instance_hypernym",
        "~"  => "hyponym",
        "~i" => "instance_hyponym",
        "#m" => "member_holonym",
        "#s" => "substance_holonym",
        "#p" => "part_holonym",
        "%m" => "member_meronym",
        "%s" => "substance_meronym",
        "%p" => "part_meronym",
        "="  => "attribute",
        "+"  => "derivationally_related_form",
        ";c" => "domain_topic",
        "-c" => "member_of_domain_topic",
        ";r" => "domain_region",
        "-r" => "member_of_domain_region",
        ";u" => "domain_usage",
        "-u" => "member_of_domain_usage",
        "*"  => "entailment",
        ">"  => "cause",
        "^"  => "also_see",
        "$"  => "verb_group",
        "&"  => "similar_to",
        "<"  => "participle_of_verb",
        "\\" => "pertainym",
        _    => "wordnet_relation",
    };
}
