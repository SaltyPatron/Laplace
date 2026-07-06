using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public sealed class BootstrapIntentBuilder
{
    private readonly Hash128 _sourceId;
    private readonly string _sourceName;
    private readonly Hash128 _trustClassId;
    private readonly Hash128 _substrateCanonicalTypeId;
    private readonly Hash128 _sourceCanonicalSource;
    private readonly SubstrateChangeBuilder _inner;

    public static readonly Hash128 SourceTypeId = EntityTypeRegistry.Id("Source");
    public static readonly Hash128 TypeMetaTypeId = EntityTypeRegistry.Id("Type");
    public static readonly Hash128 RelationTypeMetaTypeId = EntityTypeRegistry.Id("RelationType");
    public static readonly Hash128 HasTrustClassTypeId = EntityTypeRegistry.Id("HAS_TRUST_CLASS");

    public BootstrapIntentBuilder(Hash128 sourceId, string sourceName, Hash128 trustClassId)
    {
        _sourceId = sourceId;
        _sourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
        _trustClassId = trustClassId;
        _substrateCanonicalTypeId = SourceTypeId;
        _sourceCanonicalSource = sourceId;
        _inner = new SubstrateChangeBuilder(
            sourceId, $"bootstrap/{sourceName}", parentIntentId: null);

        _inner.AddEntity(sourceId, EntityTier.Word, SourceTypeId, sourceId);
    }






    public IReadOnlyCollection<string> CanonicalNames => _canonicalNames;
    private readonly HashSet<string> _canonicalNames = new(StringComparer.Ordinal);

    public Hash128 AddType(string canonicalTypeName)
    {
        var id = EntityTypeRegistry.Id(canonicalTypeName);
        _canonicalNames.Add(canonicalTypeName);
        _inner.AddEntity(id, EntityTier.Word, TypeMetaTypeId, _sourceId);



        if (ContentEmitter.Emit(_inner, canonicalTypeName, _sourceId) is { } nameId)
            _inner.AddAttestation(NativeAttestation.Categorical(
                id, "HAS_NAME_ALIAS", nameId, _sourceId, null, SourceTrust.SubstrateMandate));
        return id;
    }

    public Hash128 AddRelationType(string canonicalRelationTypeName)
    {
        var r = RelationTypeRegistry.Resolve(canonicalRelationTypeName);
        var id = r.Id;
        _canonicalNames.Add(r.Canonical);
        _inner.AddEntity(id, EntityTier.Word, RelationTypeMetaTypeId, _sourceId);
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
