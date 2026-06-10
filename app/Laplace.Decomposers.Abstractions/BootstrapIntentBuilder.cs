using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public sealed class BootstrapIntentBuilder
{
    private readonly Hash128 _sourceId;
    private readonly string  _sourceName;
    private readonly Hash128 _trustClassId;
    private readonly Hash128 _substrateCanonicalTypeId;
    private readonly Hash128 _sourceCanonicalSource;
    private readonly SubstrateChangeBuilder _inner;

    public static readonly Hash128 SourceTypeId =
        Hash128.OfCanonical("substrate/type/Source/v1");

    public static readonly Hash128 TypeMetaTypeId =
        Hash128.OfCanonical("substrate/type/Type/v1");

    public static readonly Hash128 RelationTypeMetaTypeId =
        Hash128.OfCanonical("substrate/type/RelationType/v1");

    public static readonly Hash128 HasTrustClassTypeId =
        Hash128.OfCanonical("substrate/type/HAS_TRUST_CLASS/v1");

    public BootstrapIntentBuilder(Hash128 sourceId, string sourceName, Hash128 trustClassId)
    {
        _sourceId = sourceId;
        _sourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
        _trustClassId = trustClassId;
        _substrateCanonicalTypeId = SourceTypeId;
        _sourceCanonicalSource = sourceId;
        _inner = new SubstrateChangeBuilder(
            sourceId, $"bootstrap/{sourceName}", parentIntentId: null);

        _inner.AddEntity(sourceId, EntityTier.Vocabulary, SourceTypeId, sourceId);
    }

    /// <summary>
    /// Canonical names declared through this builder (types and relation types, plus the
    /// resolved canonical of aliased relations). The declaration site is the single source
    /// of truth — decomposers feed this to CanonicalNamesForReadback instead of retyping names.
    /// </summary>
    public IReadOnlyCollection<string> CanonicalNames => _canonicalNames;
    private readonly HashSet<string> _canonicalNames = new(StringComparer.Ordinal);

    public Hash128 AddType(string canonicalTypeName)
    {
        var id = Hash128.OfCanonical($"substrate/type/{canonicalTypeName}/v1");
        _canonicalNames.Add($"substrate/type/{canonicalTypeName}/v1");
        _inner.AddEntity(id, EntityTier.Vocabulary, TypeMetaTypeId, _sourceId);
        return id;
    }

    public Hash128 AddRelationType(string canonicalRelationTypeName)
    {
        var id = Hash128.OfCanonical($"substrate/type/{canonicalRelationTypeName}/v1");
        _canonicalNames.Add($"substrate/type/{canonicalRelationTypeName}/v1");
        // Aliased relations (e.g. DEFINES → HAS_DEFINITION) attest under their resolved
        // canonical id, so name that relation too — readback then matches consensus.
        var r = RelationTypeRegistry.Resolve(canonicalRelationTypeName);
        _canonicalNames.Add($"substrate/type/{r.Canonical}/v1");
        _inner.AddEntity(id, EntityTier.Vocabulary, RelationTypeMetaTypeId, _sourceId);
        return id;
    }

    public Hash128 AddRelationType(string canonicalRelationTypeName, double typeRank, double sourceTrust)
        => AddRelationType(canonicalRelationTypeName);

    public void AddEntity(EntityRow row) => _inner.AddEntity(row);

    public void AddAttestation(AttestationRow row) => _inner.AddAttestation(row);

    private void AddTrustClassAttestation()
    {
        _inner.AddAttestation(AttestationFactory.CreateCategorical(
            subject:    _sourceId,
            typeId:     HasTrustClassTypeId,
            obj:        _trustClassId,
            sourceId:   _sourceId,
            contextId:  null,
            confirm:    true,
            witnessWeight: 1.0));
    }

    public SubstrateChange Build()
    {
        AddTrustClassAttestation();
        RelationTypeRegistry.SeedCanonical(_inner, _sourceId);
        return _inner.Build();
    }
}
