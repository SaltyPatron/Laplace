namespace Laplace.Decomposers.Iso639;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Text;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// E5 / F4 — ISO 639-3 language decomposer. The smallest end-to-end
/// substrate ingestion pipeline: takes ISO 639-3's 7,800-language
/// registry, decomposes each language's alpha-3 code + reference name +
/// names list into substrate entities, emits has_iso_639_3_code /
/// has_reference_name / has_alternate_name / has_macrolanguage edges
/// between them.
///
/// Per substrate invariant 1: languages are content-addressed entities,
/// referenced by entity_hash everywhere (NOT by integer language_id).
/// Per invariant 4: language identity emerges from the graph of edges
/// (alpha-3 code, names across surfaces, macrolanguage hierarchy), not
/// from any anchor.
///
/// Phase 3 / Track E5.
/// </summary>
public sealed class Iso639Decomposer
{
    private readonly TextDecomposer          _textDecomposer;
    private readonly IIdentityHashing        _hashing;
    private readonly IConceptEntityResolver  _conceptResolver;
    private readonly IEntityEmission         _entityEmission;
    private readonly IEdgeEmission           _edgeEmission;
    private readonly IProvenance             _provenance;

    public Iso639Decomposer(
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

    public async Task DecomposeAsync(string iso639Directory, CancellationToken cancellationToken)
    {
        var sourceHash = await _provenance.ResolveSourceAsync(
            "iso_639_3_registry_sil", cancellationToken).ConfigureAwait(false);

        var hasCodeEdge          = _conceptResolver.Resolve("has_iso_639_3_code");
        var hasReferenceNameEdge = _conceptResolver.Resolve("has_reference_name");
        var hasAlternateNameEdge = _conceptResolver.Resolve("has_alternate_name");
        var hasMacrolangEdge     = _conceptResolver.Resolve("has_macrolanguage");
        var roleSubject          = _conceptResolver.Resolve("subject");
        var roleObject           = _conceptResolver.Resolve("object");

        // Pass 1: every language record → substrate entity, plus has_code +
        // has_reference_name edges to the alpha-3 code and reference name.
        var languagesPath = Path.Combine(iso639Directory, "iso-639-3.tab");
        if (!File.Exists(languagesPath)) { return; }

        foreach (var lang in Iso639TabParser.ParseLanguages(languagesPath))
        {
            // Language entity = composition of the alpha-3 code's codepoint LINESTRING.
            var langHash = await _textDecomposer.DecomposeAsync(lang.Id, cancellationToken).ConfigureAwait(false);
            // Reference name = composition of the name's codepoint LINESTRING.
            var nameHash = await _textDecomposer.DecomposeAsync(
                lang.ReferenceName, cancellationToken).ConfigureAwait(false);

            await EmitBinaryEdgeAsync(
                hasReferenceNameEdge, roleSubject, langHash, roleObject, nameHash,
                sourceHash, cancellationToken).ConfigureAwait(false);

            // The alpha-3 code itself is the language entity, so has_iso_639_3_code
            // is reflexive at this layer — it gets meaningful when other sources
            // (BCP-47, GlotLog, Ethnologue) attest the same language with a
            // different identifier and edges link those alternative identifiers
            // to the same substrate language entity. Emitting the reflexive
            // edge here lets future ingest paths join through it.
            await EmitBinaryEdgeAsync(
                hasCodeEdge, roleSubject, langHash, roleObject, langHash,
                sourceHash, cancellationToken).ConfigureAwait(false);

            await _provenance.EmitEntityProvenanceAsync(
                new EntityProvenanceRecord(EntityHash: langHash, SourceHash: sourceHash),
                cancellationToken).ConfigureAwait(false);
        }

        // Pass 2: alternate names (one language can have many surface names).
        var namesPath = Path.Combine(iso639Directory, "iso-639-3_Name_Index.tab");
        if (File.Exists(namesPath))
        {
            foreach (var alt in Iso639TabParser.ParseNames(namesPath))
            {
                var langHash = await _textDecomposer.DecomposeAsync(alt.LanguageId, cancellationToken).ConfigureAwait(false);
                var nameHash = await _textDecomposer.DecomposeAsync(alt.PrintName,  cancellationToken).ConfigureAwait(false);
                await EmitBinaryEdgeAsync(
                    hasAlternateNameEdge, roleSubject, langHash, roleObject, nameHash,
                    sourceHash, cancellationToken).ConfigureAwait(false);
            }
        }

        // Pass 3: macrolanguage hierarchy (M_Id contains I_Id member languages).
        var macroPath = Path.Combine(iso639Directory, "iso-639-3-macrolanguages.tab");
        if (File.Exists(macroPath))
        {
            foreach (var rel in Iso639TabParser.ParseMacrolanguages(macroPath))
            {
                if (rel.Status != Iso639MacrolanguageStatus.Active) { continue; }
                var macroHash      = await _textDecomposer.DecomposeAsync(rel.MacrolanguageId, cancellationToken).ConfigureAwait(false);
                var individualHash = await _textDecomposer.DecomposeAsync(rel.IndividualId,    cancellationToken).ConfigureAwait(false);
                await EmitBinaryEdgeAsync(
                    hasMacrolangEdge, roleSubject, individualHash, roleObject, macroHash,
                    sourceHash, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static readonly int[] BinaryEdgeRleCounts = new[] { 1, 1, 1 };

    private async Task EmitBinaryEdgeAsync(
        AtomId edgeType, AtomId subjectRole, AtomId subject, AtomId objectRole, AtomId @object,
        AtomId sourceHash, CancellationToken cancellationToken)
    {
        var edgeHash = _hashing.CompositionId(
            new[] { edgeType, subject, @object },
            BinaryEdgeRleCounts);

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
