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

    
    
    
    
    
    public IReadOnlyCollection<string> CanonicalNames => _canonicalNames;
    private readonly HashSet<string> _canonicalNames = new(StringComparer.Ordinal);

    public Hash128 AddType(string canonicalTypeName)
    {
        var id = Hash128.OfCanonical($"substrate/type/{canonicalTypeName}/v1");
        _canonicalNames.Add($"substrate/type/{canonicalTypeName}/v1");
        _inner.AddEntity(id, EntityTier.Vocabulary, TypeMetaTypeId, _sourceId);
        // Substrate-native legibility: name the entity type via a codepoint-walk content entity +
        // HAS_NAME_ALIAS, so even unregistered types (e.g. WordNet_Sense) render from their own
        // codepoints instead of a bare hash. See render() COALESCE in 15_readback.sql.in.
        if (ContentWitnessBatch.Emit(_inner, canonicalTypeName, _sourceId) is { } nameId)
            _inner.AddAttestation(NativeAttestation.Categorical(
                id, "HAS_NAME_ALIAS", nameId, _sourceId, null, SourceTrust.SubstrateMandate));
        return id;
    }

    public Hash128 AddRelationType(string canonicalRelationTypeName)
    {
        var id = Hash128.OfCanonical($"substrate/type/{canonicalRelationTypeName}/v1");
        _canonicalNames.Add($"substrate/type/{canonicalRelationTypeName}/v1");
        
        
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
        _inner.AddAttestation(NativeAttestation.CategoricalResolved(
            subject: _sourceId,
            typeId: HasTrustClassTypeId,
            obj: _trustClassId,
            sourceId: _sourceId,
            contextId: null,
            witnessWeight: 1.0,
            confirm: true));
    }

    public SubstrateChange Build()
    {
        AddTrustClassAttestation();
        RelationTypeRegistry.SeedCanonical(_inner, _sourceId);
        
        
        PosReference.SeedCanonical(_inner, _sourceId);
        return _inner.Build();
    }
}
