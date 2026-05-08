namespace Laplace.Decomposers.Code;

using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Text;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// F6 — Code source-file decomposer (line-level slice).
///
/// Decomposes a source file into per-line F1 entities + a per-document
/// sequence (line-position → line-content-hash) record stream so the
/// recomposer can reconstitute the file in line order via O(1) per-line
/// lookups. Common lines ("    return result;", "<?xml version=...", etc.)
/// dedupe across all ingested code via F1 content addressing.
///
/// Per substrate invariant 4 (knowledge IS edges): the source-file entity
/// receives has_language (e.g., "python" / "rust" / "go"), has_filename,
/// has_extension edges; recomposers and queries traverse those edges to
/// answer "every Go file containing this line" or "Python files in
/// repository X" without scanning content blobs.
///
/// AST-level decomposition (tree-sitter, identifier extraction, operator
/// classification) is a follow-up F6 slice that attaches additional edges
/// to the same source-file entity. The line-level slice is load-bearing
/// for round-trip fidelity (Regime 1 lossless artifact recompose) on its
/// own.
///
/// Phase 4 / Track F6.
/// </summary>
public sealed class CodeFileDecomposer
{
    private static readonly int[] BinaryEdgeRleCounts = new[] { 1, 1, 1 };

    private readonly TextDecomposer         _textDecomposer;
    private readonly IIdentityHashing       _hashing;
    private readonly IConceptEntityResolver _conceptResolver;
    private readonly IEntityEmission        _entityEmission;
    private readonly IEntityChildEmission   _childEmission;
    private readonly IEdgeEmission          _edgeEmission;
    private readonly ISequenceEmission      _sequenceEmission;
    private readonly IProvenance            _provenance;

    public CodeFileDecomposer(
        TextDecomposer         textDecomposer,
        IIdentityHashing       hashing,
        IConceptEntityResolver conceptResolver,
        IEntityEmission        entityEmission,
        IEntityChildEmission   childEmission,
        IEdgeEmission          edgeEmission,
        ISequenceEmission      sequenceEmission,
        IProvenance            provenance)
    {
        _textDecomposer   = textDecomposer;
        _hashing          = hashing;
        _conceptResolver  = conceptResolver;
        _entityEmission   = entityEmission;
        _childEmission    = childEmission;
        _edgeEmission     = edgeEmission;
        _sequenceEmission = sequenceEmission;
        _provenance       = provenance;
    }

    /// <summary>
    /// Decompose one source file. Computes the source-file entity hash as
    /// the Merkle composition of per-line entity hashes, emits the
    /// composition + entity_child + sequence rows, and emits has_filename /
    /// has_extension / has_language edges on the source-file entity.
    /// </summary>
    /// <param name="filePath">Path to the source file on disk.</param>
    /// <param name="languageCanonicalName">Canonical programming-language
    /// concept name (e.g., "python", "rust", "go", "csharp"). Resolved via
    /// IConceptEntityResolver. May be empty to skip the has_language edge.</param>
    /// <param name="provenanceCanonicalName">Provenance source canonical
    /// name for this ingestion (e.g., "user_session", "github_corpus").</param>
    public async Task DecomposeAsync(
        string             filePath,
        string             languageCanonicalName,
        string             provenanceCanonicalName,
        CancellationToken  cancellationToken)
    {
        if (!File.Exists(filePath)) { return; }

        var sourceHash    = await _provenance.ResolveSourceAsync(
            provenanceCanonicalName, cancellationToken).ConfigureAwait(false);
        var hasFilename   = _conceptResolver.Resolve("has_filename");
        var hasExtension  = _conceptResolver.Resolve("has_extension");
        var hasLanguage   = _conceptResolver.Resolve("has_language");
        var subjectRole   = _conceptResolver.Resolve("subject");
        var objectRole    = _conceptResolver.Resolve("object");

        // Stream the file into per-line F1 entities. Empty lines and lines
        // with only whitespace dedupe into substrate via F1 content
        // addressing. Order is preserved via the sequence emission.
        var lineHashes = new System.Collections.Generic.List<AtomId>(capacity: 1024);
        using (var reader = new StreamReader(filePath))
        {
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
            {
                var lineHash = await _textDecomposer.DecomposeAsync(line, cancellationToken).ConfigureAwait(false);
                lineHashes.Add(lineHash);
            }
        }

        if (lineHashes.Count == 0) { return; }

        // Source-file entity = Merkle composition of line hashes (RLE 1).
        var rleCounts = new int[lineHashes.Count];
        for (int i = 0; i < rleCounts.Length; ++i) { rleCounts[i] = 1; }
        var fileHash = _hashing.CompositionId(lineHashes, rleCounts);

        // Emit the source-file composition entity at tier 2 (file =
        // composition of tier-1 line entities).
        await _entityEmission.EmitAsync(
            new EntityRecord(
                Hash:            fileHash,
                Tier:            2,
                ContentKindHash: fileHash,
                Content:         null,
                Centroid:        new Point4D(0, 0, 0, 0)),
            cancellationToken).ConfigureAwait(false);
        await _provenance.EmitEntityProvenanceAsync(
            new EntityProvenanceRecord(EntityHash: fileHash, SourceHash: sourceHash),
            cancellationToken).ConfigureAwait(false);

        // entity_child rows linking file → its lines in order.
        for (int ordinal = 0; ordinal < lineHashes.Count; ++ordinal)
        {
            await _childEmission.EmitAsync(
                new EntityChildRecord(
                    ParentHash: fileHash,
                    Ordinal:    ordinal,
                    RleCount:   1,
                    ChildHash:  lineHashes[ordinal]),
                cancellationToken).ConfigureAwait(false);
            await _sequenceEmission.EmitAsync(
                new SequenceRecord(
                    DocumentHash:  fileHash,
                    LeafPosition:  ordinal,
                    LeafAtomHash:  lineHashes[ordinal]),
                cancellationToken).ConfigureAwait(false);
        }

        // has_filename + has_extension (file's surface attestation —
        // recoverable by the recomposer for filesystem reconstitution).
        var fileName = Path.GetFileName(filePath);
        if (fileName.Length > 0)
        {
            var nameHash = await _textDecomposer.DecomposeAsync(fileName, cancellationToken).ConfigureAwait(false);
            await EmitBinaryEdgeAsync(
                hasFilename, subjectRole, fileHash, objectRole, nameHash, sourceHash, cancellationToken).ConfigureAwait(false);
        }
        var ext = Path.GetExtension(filePath);
        if (ext.Length > 0)
        {
            var extHash = await _textDecomposer.DecomposeAsync(ext, cancellationToken).ConfigureAwait(false);
            await EmitBinaryEdgeAsync(
                hasExtension, subjectRole, fileHash, objectRole, extHash, sourceHash, cancellationToken).ConfigureAwait(false);
        }

        // has_language (canonical programming-language concept entity).
        if (languageCanonicalName.Length > 0)
        {
            var langHash = _conceptResolver.Resolve(languageCanonicalName);
            await EmitBinaryEdgeAsync(
                hasLanguage, subjectRole, fileHash, objectRole, langHash, sourceHash, cancellationToken).ConfigureAwait(false);
        }
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
}
