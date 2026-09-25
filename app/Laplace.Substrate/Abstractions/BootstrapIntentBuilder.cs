using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using System.Collections.Immutable;

namespace Laplace.Decomposers.Abstractions;

public sealed class BootstrapIntentBuilder
{
    private readonly Hash128 _sourceId;
    private readonly string _sourceName;
    private readonly Hash128 _substrateCanonicalTypeId;
    private readonly Hash128 _sourceCanonicalSource;
    private readonly SubstrateChangeBuilder _inner;

    public static readonly Hash128 SourceTypeId = EntityTypeRegistry.Id("Source");
    public static readonly Hash128 TypeMetaTypeId = EntityTypeRegistry.Id("Type");
    public static readonly Hash128 RelationTypeMetaTypeId = EntityTypeRegistry.Id("RelationType");

    public BootstrapIntentBuilder(Hash128 sourceId, string sourceName, Hash128 trustClassId)
        : this(sourceId, sourceName, trustClassId, witness: null) { }

    /// <param name="witness">A source generation's self-description: the source is the
    /// content composition [authority, release] (<see cref="SourceWitness"/>), staged as
    /// ordinary content instead of a named identity.</param>
    public BootstrapIntentBuilder(Hash128 sourceId, string sourceName, Hash128 trustClassId,
        (string Authority, string Release)? witness)
    {
        _sourceId = sourceId;
        _sourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
        // A source never testifies to its own trust class: its prior is declared on the
        // change, and calculated evidence is qualified on each claim (CalculationSources).
        _ = trustClassId;
        _substrateCanonicalTypeId = SourceTypeId;
        _sourceCanonicalSource = sourceId;
        _inner = new SubstrateChangeBuilder(
            sourceId, $"bootstrap/{sourceName}", parentIntentId: null)
            .DeclareSourcePrior(SourceTrust.SubstrateMandate);

        if (witness is { } w)
        {
            if (SourceWitness.Stage(_inner, w.Authority, w.Release) != sourceId)
                throw new InvalidOperationException(
                    $"source {sourceName} is not the witness [{w.Authority}, {w.Release}]");
            return;
        }
        CanonicalNamedIdentity.Declare(
            _inner, sourceId, EntityTier.Word, SourceTypeId, sourceName, sourceId);

        // The source names ITSELF, by the same law AddType uses for type nodes:
        // HAS_NAME_ALIAS → the name's content root. Canonical-string sources are
        // unaffected on the read side (realize.render() prefers canonical_names); content-
        // hash sources (models) stop rendering as raw hex, and name → source-id
        // resolution becomes a consensus lookup (seed-step verify depends on it).
        if (ContentEmitter.Emit(_inner, sourceName, sourceId) is { } sourceNameId)
            _inner.AddAttestation(NativeAttestation.Categorical(
                sourceId, "HAS_NAME_ALIAS", sourceNameId, sourceId, null,
                SourceTrust.SubstrateMandate));
    }







    public Hash128 AddType(string canonicalTypeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalTypeName);
        var id = EntityTypeRegistry.Id(canonicalTypeName);
        // type_id is structural metadata on an entity row. The registry key is not
        // itself content and must not be materialized as a fake Entity/Physicality.
        // Semantic category endpoints are ordinary content entities witnessed by
        // the source that actually makes the claim.
        return id;
    }

    public Hash128 AddRelationType(string canonicalRelationTypeName)
    {
        var r = RelationTypeRegistry.Resolve(canonicalRelationTypeName);
        // Relation keys belong to the native relation/operator registry and highway
        // perfcache. They are not content entities and receive no physicality.
        return r.Id;
    }

    public Hash128 AddRelationType(string canonicalRelationTypeName, double typeRank, double sourceTrust)
        => AddRelationType(canonicalRelationTypeName);

    public void AddEntity(EntityRow row) => _inner.AddEntity(row);

    public void AddAttestation(AttestationRow row) => _inner.AddAttestation(row);

    public SubstrateChange Build()
    {
        RelationTypeRegistry.SeedCanonical(_inner, _sourceId);


        PosReference.SeedCanonical(_inner, _sourceId);
        return _inner.Build();
    }
}
