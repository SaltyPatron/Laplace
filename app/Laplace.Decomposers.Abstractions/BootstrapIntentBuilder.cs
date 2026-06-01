using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Helper for <see cref="IDecomposer.InitializeAsync"/> to build the bootstrap
/// <see cref="SubstrateChange"/> that registers the source entity + the
/// decomposer's type vocabulary + the decomposer's attestation-kind vocabulary
/// + the source-trust-class meta-attestation. Per ADR 0042 + ADR 0044 + ADR 0051.
///
/// <para>
/// Centralises a shape every IDecomposer.InitializeAsync would otherwise
/// reinvent (forbidden by ADR 0016). Idempotent — re-running InitializeAsync
/// against an already-bootstrapped substrate is a no-op via SubstrateCRUD
/// ON CONFLICT (RULES R5).
/// </para>
///
/// <para>
/// Conventions:
/// <list type="bullet">
///   <item>Type entity id = <c>BLAKE3("substrate/type/&lt;Name&gt;/v1")</c></item>
///   <item>Kind entity id = <c>BLAKE3("substrate/kind/&lt;Name&gt;/v1")</c></item>
///   <item>Trust-class entity id = <c>BLAKE3("substrate/trust_class/&lt;ClassName&gt;/v1")</c></item>
///   <item>Source entity id = <c>BLAKE3("substrate/source/&lt;DecomposerName&gt;/v1")</c></item>
/// </list>
/// </para>
/// </summary>
public sealed class BootstrapIntentBuilder
{
    private readonly Hash128 _sourceId;
    private readonly string  _sourceName;
    private readonly Hash128 _trustClassId;
    private readonly Hash128 _substrateCanonicalTypeId;
    private readonly Hash128 _sourceCanonicalSource;
    private readonly SubstrateChangeBuilder _inner;

    /// <summary>The canonical "Source" type entity all source entities are
    /// declared as. Hash of <c>"substrate/type/Source/v1"</c>.</summary>
    public static readonly Hash128 SourceTypeId =
        Hash128.OfCanonical("substrate/type/Source/v1");

    /// <summary>The canonical "Type" meta-type entity. Hash of
    /// <c>"substrate/type/Type/v1"</c>.</summary>
    public static readonly Hash128 TypeMetaTypeId =
        Hash128.OfCanonical("substrate/type/Type/v1");

    /// <summary>The canonical "Kind" meta-type entity. Hash of
    /// <c>"substrate/type/Kind/v1"</c>.</summary>
    public static readonly Hash128 KindMetaTypeId =
        Hash128.OfCanonical("substrate/type/Kind/v1");

    /// <summary>The HAS_TRUST_CLASS kind attesting source ↔ trust-class.
    /// Hash of <c>"substrate/kind/HAS_TRUST_CLASS/v1"</c>.</summary>
    public static readonly Hash128 HasTrustClassKindId =
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

        // Register the source entity itself. Declared as a Source-typed
        // entity, first_observed_by self (the source bootstraps itself).
        _inner.AddEntity(sourceId, /*tier*/ 0, SourceTypeId, sourceId);
    }

    /// <summary>Register a Type entity the decomposer uses (e.g.
    /// WordNet_Synset, WordNet_Sense). Idempotent across decomposers — if
    /// two decomposers register the same canonical type name the entity
    /// IDs collide via content-addressing and the second insert is a no-op.
    /// </summary>
    public Hash128 AddType(string canonicalTypeName)
    {
        var id = Hash128.OfCanonical($"substrate/type/{canonicalTypeName}/v1");
        _inner.AddEntity(id, /*tier*/ 0, TypeMetaTypeId, _sourceId);
        return id;
    }

    /// <summary>Register an attestation-kind entity. Same content-addressing
    /// convergence as types — duplicate calls from different decomposers are
    /// no-ops via ON CONFLICT.</summary>
    public Hash128 AddKind(string canonicalKindName)
    {
        var id = Hash128.OfCanonical($"substrate/kind/{canonicalKindName}/v1");
        _inner.AddEntity(id, /*tier*/ 0, KindMetaTypeId, _sourceId);
        return id;
    }

    /// <summary>Register an attestation-kind entity. The (tier, trust) pair is
    /// kept ONLY for source compatibility and is now a no-op on the kind itself:
    /// kind significance + source trust are NOT tiers/classes stored on the kind
    /// (that was "tiers as entities" — truth #5). They enter each attestation of
    /// this kind as the numeric witness weight folded into the Glicko opponent φ
    /// (see <see cref="AttestationFactory"/>). No tier entity, no HAS_VALUE_TIER
    /// meta-attestation is minted.</summary>
    public Hash128 AddKind(string canonicalKindName, KindValueTier tier, TrustClass trust)
        => AddKind(canonicalKindName);

    /// <summary>Attest that <see cref="_sourceId"/> belongs to
    /// <see cref="_trustClassId"/> — source provenance (a categorical confirm).
    /// Called once at the end of <see cref="Build"/>.</summary>
    private void AddTrustClassAttestation()
    {
        _inner.AddAttestation(AttestationFactory.CreateCategorical(
            subject:    _sourceId,
            kindId:     HasTrustClassKindId,
            obj:        _trustClassId,
            sourceId:   _sourceId,        // self-attested
            contextId:  null,
            confirm:    true,
            witnessWeight: 1.0));          // a source declaring its own trust class
    }

    /// <summary>Finalize the bootstrap intent.</summary>
    public SubstrateChange Build()
    {
        AddTrustClassAttestation();
        return _inner.Build();
    }
}
