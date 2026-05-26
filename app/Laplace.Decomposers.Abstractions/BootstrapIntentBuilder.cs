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
///   <item>Trust-class entity id = <c>BLAKE3("substrate/trust/&lt;ClassName&gt;/v1")</c></item>
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

    /// <summary>Register an attestation-kind entity with ADR 0044 kind-
    /// value-tier + source-trust-class meta-attestations on the kind entity
    /// itself. The <see cref="AttestationFactory"/> uses these priors to
    /// initialise Glicko-2 state for each attestation of this kind.</summary>
    public Hash128 AddKind(string canonicalKindName, KindValueTier tier, TrustClass trust)
    {
        var id = AddKind(canonicalKindName);

        // HAS_VALUE_TIER meta-attestation (kind → tier entity).
        // Tier entity IDs must match 10_bootstrap.sql.in Stage 3 seeds exactly.
        var tierEntityId = TierEntityId(tier);
        _inner.AddEntity(tierEntityId, /*tier*/ 0, KindMetaTypeId, _sourceId);
        _inner.AddAttestation(AttestationFactory.Create(
            subject:    id,
            kindId:     HAS_VALUE_TIER_KindId,
            obj:        tierEntityId,
            sourceId:   _sourceId,
            contextId:  null,
            tier:       KindValueTier.T1,   // meta-attestation is substrate mandate
            trust:      TrustClass.SubstrateMandateTier1));

        // HAS_TRUST_CLASS meta-attestation (kind → trust-class entity)
        _inner.AddAttestation(AttestationFactory.Create(
            subject:    id,
            kindId:     HasTrustClassKindId,
            obj:        _trustClassId,
            sourceId:   _sourceId,
            contextId:  null,
            tier:       KindValueTier.T1,
            trust:      TrustClass.SubstrateMandateTier1));

        return id;
    }

    // Must match 10_bootstrap.sql.in Stage 3 canonical name exactly.
    private static readonly Hash128 HAS_VALUE_TIER_KindId =
        Hash128.OfCanonical("substrate/kind/HAS_KIND_VALUE_TIER/v1");

    // Canonical names must match 10_bootstrap.sql.in Stage 3 tier entities exactly.
    private static Hash128 TierEntityId(KindValueTier tier) => tier switch
    {
        KindValueTier.T1  => Hash128.OfCanonical("substrate/kind_tier/T1_Mandate/v1"),
        KindValueTier.T2  => Hash128.OfCanonical("substrate/kind_tier/T2_StandardsStructural/v1"),
        KindValueTier.T3  => Hash128.OfCanonical("substrate/kind_tier/T3_Taxonomic/v1"),
        KindValueTier.T4  => Hash128.OfCanonical("substrate/kind_tier/T4_Partitive/v1"),
        KindValueTier.T5  => Hash128.OfCanonical("substrate/kind_tier/T5_Causal/v1"),
        KindValueTier.T6  => Hash128.OfCanonical("substrate/kind_tier/T6_Equivalence/v1"),
        KindValueTier.T7  => Hash128.OfCanonical("substrate/kind_tier/T7_Oppositional/v1"),
        KindValueTier.T8  => Hash128.OfCanonical("substrate/kind_tier/T8_Associative/v1"),
        KindValueTier.T9  => Hash128.OfCanonical("substrate/kind_tier/T9_TensorCalculation/v1"),
        KindValueTier.T10 => Hash128.OfCanonical("substrate/kind_tier/T10_ScalarValued/v1"),
        KindValueTier.T11 => Hash128.OfCanonical("substrate/kind_tier/T11_Probationary/v1"),
        _                 => Hash128.OfCanonical("substrate/kind_tier/T11_Probationary/v1"),
    };

    /// <summary>Attest that <see cref="_sourceId"/> belongs to
    /// <see cref="_trustClassId"/>. Called once at the end of
    /// <see cref="Build"/>.</summary>
    private void AddTrustClassAttestation()
    {
        // Attestation id = BLAKE3 of (subject ‖ kind ‖ object ‖ source ‖ context)
        Span<byte> buf = stackalloc byte[16 * 5];
        _sourceId.WriteBytes(buf.Slice(0, 16));
        HasTrustClassKindId.WriteBytes(buf.Slice(16, 16));
        _trustClassId.WriteBytes(buf.Slice(32, 16));
        _sourceId.WriteBytes(buf.Slice(48, 16));  // self-attested
        // context = zero (no context)
        for (int i = 64; i < 80; i++) buf[i] = 0;
        var attestationId = Hash128.Blake3(buf);

        _inner.AddAttestation(new AttestationRow(
            Id: attestationId,
            SubjectId: _sourceId,
            KindId: HasTrustClassKindId,
            ObjectId: _trustClassId,
            SourceId: _sourceId,
            ContextId: null,
            RatingFp1e9: 1_500_000_000_000L,   // Glicko-2 default mu=1500
            RdFp1e9:     350_000_000_000L,    // default RD=350
            VolatilityFp1e9: 60_000_000L,     // default vol=0.06
            LastObservedAtUnixUs: 0,
            ObservationCount: 1));
    }

    /// <summary>Finalize the bootstrap intent.</summary>
    public SubstrateChange Build()
    {
        AddTrustClassAttestation();
        return _inner.Build();
    }
}
