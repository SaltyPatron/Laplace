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
        Hash128.OfCanonical("substrate/type/Kind/v1");

    public static readonly Hash128 HasTrustClassTypeId =
        Hash128.OfCanonical("substrate/kind/HAS_TRUST_CLASS/v1");

    public BootstrapIntentBuilder(Hash128 sourceId, string sourceName, Hash128 trustClassId)
    {
        _sourceId = sourceId;
        _sourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
        _trustClassId = trustClassId;
        _substrateCanonicalTypeId = SourceTypeId;
        _sourceCanonicalSource = sourceId;
        _inner = new SubstrateChangeBuilder(
            sourceId, $"bootstrap/{sourceName}", parentIntentId: null);

        _inner.AddEntity(sourceId, (byte)MetaTier.Meta, SourceTypeId, sourceId);
    }

    public Hash128 AddType(string canonicalTypeName)
    {
        var id = Hash128.OfCanonical($"substrate/type/{canonicalTypeName}/v1");
        _inner.AddEntity(id, (byte)MetaTier.Meta, TypeMetaTypeId, _sourceId);
        return id;
    }

    public Hash128 AddRelationType(string canonicalKindName)
    {
        var id = Hash128.OfCanonical($"substrate/kind/{canonicalKindName}/v1");
        _inner.AddEntity(id, (byte)MetaTier.RelationType, RelationTypeMetaTypeId, _sourceId);
        return id;
    }

    public Hash128 AddRelationType(string canonicalKindName, double kindRank, double sourceTrust)
        => AddRelationType(canonicalKindName);

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
