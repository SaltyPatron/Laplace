using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// The canonical KIND registry — the single source of truth that turns each
/// relation kind into an ARENA. A kind is an arena: relations of that kind
/// compete for Glicko-2 μ against the neutral baseline, so the consensus μ over
/// a kind is a μ-ranked relational embedding over the shared content nodes. N
/// kinds ⇒ N co-equal embeddings of one identity set (ARCHITECTURE.md — NN is
/// plural; never crown one).
///
/// <para>The registry enforces the ONE canonicalization rule so witnesses
/// co-assert on the LITERAL same consensus pk instead of forking into parallel
/// near-duplicate arenas:</para>
/// <list type="number">
///   <item>Two genuinely different relations → two arenas (PRESERVE):
///   <c>nsubj≠obj</c>, <c>synonym≠translation≠antonym</c>, <c>is_a≠part_of</c>.</item>
///   <item>One relation under two NAMES → one arena (NORMALIZE):
///   <c>HAS_UPOS, HAS_LEX_CATEGORY → HAS_POS</c>.</item>
///   <item>One relation in two DIRECTIONS → one arena, flip endpoints (lossless;
///   the inverse is the reverse query): <c>HAS_HYPONYM, IS_HYPERNYM_OF → IS_A</c>;
///   <c>IS_PART_OF → HAS_PART</c>.</item>
///   <item>Fine vs coarse of one relation → distinct arenas linked by
///   <c>is_a</c>-on-kinds, rolled up: <c>nsubj is_a DEPENDS_ON</c>;
///   <c>HAS_XPOS is_a HAS_POS</c>; <c>ATTENDS / OV_RELATES is_a RELATED_TO</c>.</item>
/// </list>
///
/// <para>A model is case (4), not a separate category: its circuit arenas are
/// real (cross-model co-assertion is direct) and roll up to the seed arenas;
/// the model is type-blind, so the type of a seed edge it corroborates stays the
/// seed's. SYMMETRIC kinds canonicalize endpoint order so <c>(a,b)</c> and
/// <c>(b,a)</c> hit one row. ANTONYM is a confirm in its own Oppositional arena,
/// not a refute (the refute/repel pole is anti-correlation magnitude or the
/// Gödel engine's active refutation).</para>
///
/// <para>Kind ids are content-addressed by canonical name
/// (<c>substrate/kind/&lt;NAME&gt;/v1</c>), matching <see cref="BootstrapIntentBuilder.AddKind(string)"/>,
/// so a kind the registry names and a kind a decomposer bootstrapped collide on
/// the same id with no second source of truth.</para>
/// </summary>
public static class KindRegistry
{
    /// <summary>Whether a relation reads the same in both directions. Symmetric
    /// kinds get endpoint-order canonicalized so (a,b)≡(b,a) on one consensus pk.</summary>
    public enum Symmetry { Asymmetric, Symmetric }

    /// <summary>Resolution of a (possibly source-named) kind to its canonical
    /// arena: the content-addressed kind id, its significance rank (→ witness
    /// weight → opponent φ), its symmetry, whether the source name's endpoints
    /// must be flipped to reach canonical direction, and its roll-up parent.</summary>
    public readonly record struct KindResolution(
        Hash128 Id, double Rank, Symmetry Symmetry, bool Flip, Hash128? ParentId, string Canonical);

    private sealed record KindDef(double Rank, Symmetry Symmetry, string? Parent);

    /// <summary>Content-addressed kind id from a canonical name — the convention
    /// shared with <see cref="BootstrapIntentBuilder"/>.</summary>
    public static Hash128 KindId(string canonicalName) =>
        Hash128.OfCanonical($"substrate/kind/{canonicalName}/v1");

    // ── Canonical arenas (relation kinds). Scalar config (HAS_*_SIZE), geometry
    //    placements (*_PROJECTS / EMBEDS / GATES), and external-id are NOT arenas
    //    and are intentionally absent — they route to metadata / the geometry axis.
    private static readonly Dictionary<string, KindDef> Canon = new(StringComparer.Ordinal)
    {
        // Standards-structural (Unicode / ISO) — high-trust skeleton.
        ["USES_SCRIPT"]              = new(KindRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_SCRIPT"]              = new(KindRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_GENERAL_CATEGORY"]    = new(KindRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_COMBINING_CLASS"]     = new(KindRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_BLOCK"]               = new(KindRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_UPPERCASE_MAPPING"]   = new(KindRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_LOWERCASE_MAPPING"]   = new(KindRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["CANONICAL_DECOMPOSES_TO"] = new(KindRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["NORMALIZES"]              = new(KindRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["TRANSCRIBES_AS"]          = new(KindRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["IS_LANGUAGE_CODE"]        = new(KindRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_ISO639_1_CODE"]       = new(KindRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["MEMBER_OF_MACROLANGUAGE"] = new(KindRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_LANGUAGE"]            = new(KindRank.StandardsStructural, Symmetry.Asymmetric, null),

        // Taxonomic.
        ["IS_A"]           = new(KindRank.Taxonomic, Symmetry.Asymmetric, null),
        ["IS_INSTANCE_OF"] = new(KindRank.Taxonomic, Symmetry.Asymmetric, "IS_A"),
        ["MANNER_OF"]      = new(KindRank.Taxonomic, Symmetry.Asymmetric, "IS_A"),
        ["IS_SENSE_OF"]    = new(KindRank.Taxonomic, Symmetry.Asymmetric, null),

        // Partitive / attributive. Member/substance meronymy are DISTINCT arenas
        // (preserve distinctions) rolled up to HAS_PART.
        ["HAS_PART"]      = new(KindRank.Partitive, Symmetry.Asymmetric, null),
        ["HAS_MEMBER"]    = new(KindRank.Partitive, Symmetry.Asymmetric, "HAS_PART"),
        ["HAS_SUBSTANCE"] = new(KindRank.Partitive, Symmetry.Asymmetric, "HAS_PART"),
        ["HAS_ATTRIBUTE"] = new(KindRank.Partitive, Symmetry.Asymmetric, null),
        ["HAS_PROPERTY"]  = new(KindRank.Partitive, Symmetry.Asymmetric, "HAS_ATTRIBUTE"),
        ["HAS_A"]         = new(KindRank.Partitive, Symmetry.Asymmetric, "HAS_PART"),
        ["HAS_POS"]       = new(KindRank.Partitive, Symmetry.Asymmetric, null),
        ["HAS_XPOS"]      = new(KindRank.Partitive, Symmetry.Asymmetric, "HAS_POS"),
        ["HAS_FEATURE"]   = new(KindRank.Partitive, Symmetry.Asymmetric, null),
        ["HAS_SENSE"]     = new(KindRank.Partitive, Symmetry.Asymmetric, null),
        ["IS_PIXEL_OF"]   = new(KindRank.Partitive, Symmetry.Asymmetric, null),
        ["IS_AT_SAMPLE"]  = new(KindRank.Partitive, Symmetry.Asymmetric, null),

        // Causal / implicational.
        ["ENTAILS"]            = new(KindRank.Causal, Symmetry.Asymmetric, null),
        ["CAUSES"]             = new(KindRank.Causal, Symmetry.Asymmetric, null),
        ["CAUSES_DESIRE"]      = new(KindRank.Causal, Symmetry.Asymmetric, null),
        ["HAS_SUBEVENT"]       = new(KindRank.Causal, Symmetry.Asymmetric, null),
        ["HAS_FIRST_SUBEVENT"] = new(KindRank.Causal, Symmetry.Asymmetric, "HAS_SUBEVENT"),
        ["HAS_LAST_SUBEVENT"]  = new(KindRank.Causal, Symmetry.Asymmetric, "HAS_SUBEVENT"),
        ["HAS_PREREQUISITE"]   = new(KindRank.Causal, Symmetry.Asymmetric, null),
        ["MOTIVATED_BY_GOAL"]  = new(KindRank.Causal, Symmetry.Asymmetric, null),
        ["OBSTRUCTED_BY"]      = new(KindRank.Causal, Symmetry.Asymmetric, null),
        ["CREATED_BY"]         = new(KindRank.Causal, Symmetry.Asymmetric, null),

        // Syntactic dependency (parent of the dynamic DEP_* family).
        ["DEPENDS_ON"]  = new(KindRank.Partitive, Symmetry.Asymmetric, null),

        // Equivalence (kept DISTINCT — same-language synonymy ≠ cross-language translation).
        ["IS_SYNONYM_OF"]     = new(KindRank.Equivalence, Symmetry.Symmetric, "RELATED_TO"),
        ["IS_TRANSLATION_OF"] = new(KindRank.Equivalence, Symmetry.Symmetric, "RELATED_TO"),
        ["HAS_VARIANT_OF"]    = new(KindRank.Equivalence, Symmetry.Symmetric, "RELATED_TO"),
        ["IS_SIMILAR_TO"]     = new(KindRank.Equivalence, Symmetry.Symmetric, "RELATED_TO"),
        ["IS_LEMMA_OF"]       = new(KindRank.Equivalence, Symmetry.Asymmetric, null),
        ["IS_PARTICIPLE_OF"]  = new(KindRank.Equivalence, Symmetry.Asymmetric, null),
        ["FORM_OF"]           = new(KindRank.Equivalence, Symmetry.Asymmetric, "RELATED_TO"),
        ["DEFINED_AS"]        = new(KindRank.Equivalence, Symmetry.Asymmetric, "RELATED_TO"),

        // Oppositional — antonym / negative assertions are CONFIRMS in their own
        // arenas (not refutes; the refute pole is active refutation).
        ["IS_ANTONYM_OF"]    = new(KindRank.Oppositional, Symmetry.Symmetric, null),
        ["DISTINCT_FROM"]    = new(KindRank.Oppositional, Symmetry.Symmetric, null),
        ["NOT_DESIRES"]      = new(KindRank.Oppositional, Symmetry.Asymmetric, null),
        ["NOT_USED_FOR"]     = new(KindRank.Oppositional, Symmetry.Asymmetric, null),
        ["NOT_CAPABLE_OF"]   = new(KindRank.Oppositional, Symmetry.Asymmetric, null),
        ["NOT_HAS_PROPERTY"] = new(KindRank.Oppositional, Symmetry.Asymmetric, null),

        // Associative — RELATED_TO is the roll-up parent for the relatedness family.
        ["RELATED_TO"]             = new(KindRank.Associative, Symmetry.Symmetric, null),
        ["DERIVATIONALLY_RELATED"] = new(KindRank.Associative, Symmetry.Symmetric, "RELATED_TO"),
        ["DEFINES"]                = new(KindRank.Associative, Symmetry.Asymmetric, null),
        ["HAS_EXAMPLE"]            = new(KindRank.Associative, Symmetry.Asymmetric, null),
        ["HAS_ETYMOLOGY"]          = new(KindRank.Associative, Symmetry.Asymmetric, null),
        ["DEPICTS"]                = new(KindRank.Associative, Symmetry.Asymmetric, null),
        ["CAPTIONS"]               = new(KindRank.Associative, Symmetry.Asymmetric, null),
        ["ADJACENT_TO_PIXEL"]      = new(KindRank.Associative, Symmetry.Symmetric, null),
        ["PERTAINS_TO"]            = new(KindRank.Associative, Symmetry.Asymmetric, "RELATED_TO"),
        ["ALSO_SEE"]               = new(KindRank.Associative, Symmetry.Asymmetric, "RELATED_TO"),
        ["IN_VERB_GROUP_WITH"]     = new(KindRank.Associative, Symmetry.Symmetric, "RELATED_TO"),
        ["HAS_DOMAIN_TOPIC"]       = new(KindRank.Associative, Symmetry.Asymmetric, null),
        ["HAS_DOMAIN_REGION"]      = new(KindRank.Associative, Symmetry.Asymmetric, null),
        ["HAS_DOMAIN_USAGE"]       = new(KindRank.Associative, Symmetry.Asymmetric, null),
        ["USED_FOR"]               = new(KindRank.Associative, Symmetry.Asymmetric, null),
        ["CAPABLE_OF"]             = new(KindRank.Associative, Symmetry.Asymmetric, null),
        ["AT_LOCATION"]            = new(KindRank.Associative, Symmetry.Asymmetric, null),
        ["LOCATED_NEAR"]           = new(KindRank.Associative, Symmetry.Symmetric, null),
        ["HAS_CONTEXT"]            = new(KindRank.Associative, Symmetry.Asymmetric, null),
        ["DESIRES"]                = new(KindRank.Associative, Symmetry.Asymmetric, null),
        ["RECEIVES_ACTION"]        = new(KindRank.Associative, Symmetry.Asymmetric, null),
        ["SYMBOL_OF"]              = new(KindRank.Associative, Symmetry.Asymmetric, null),
        ["DERIVED_FROM"]           = new(KindRank.Associative, Symmetry.Asymmetric, "DERIVATIONALLY_RELATED"),
        ["ETYMOLOGICALLY_RELATED_TO"]   = new(KindRank.Associative, Symmetry.Symmetric, "HAS_ETYMOLOGY"),
        ["ETYMOLOGICALLY_DERIVED_FROM"] = new(KindRank.Associative, Symmetry.Asymmetric, "HAS_ETYMOLOGY"),

        // Model circuit arenas — witnesses, roll up to relatedness. COMPLETES_TO
        // is shared directly with corpora (n-gram continuation), so it has no parent.
        ["ATTENDS"]      = new(KindRank.TensorCalculation, Symmetry.Asymmetric, "RELATED_TO"),
        ["OV_RELATES"]   = new(KindRank.TensorCalculation, Symmetry.Asymmetric, "RELATED_TO"),
        ["COMPLETES_TO"] = new(KindRank.TensorCalculation, Symmetry.Asymmetric, null),

        // External cross-reference (recoverable join key, low significance).
        ["HAS_EXTERNAL_ID"] = new(KindRank.ScalarValued, Symmetry.Asymmetric, null),
    };

    // ── Aliases: a source kind name that is the SAME assertion as a canonical
    //    kind, optionally with endpoints FLIPPED to reach canonical direction.
    private static readonly Dictionary<string, (string Canon, bool Flip)> Alias = new(StringComparer.Ordinal)
    {
        // POS family — one assertion, several names.
        ["HAS_UPOS"]         = ("HAS_POS", false),
        ["HAS_LEX_CATEGORY"] = ("HAS_POS", false),

        // Taxonomy — canonical IS_A is "subject (specific) is_a object (general)".
        ["HAS_HYPERNYM"]   = ("IS_A", false),   // x's hypernym is y  ⇒ x is_a y
        ["IS_HYPERNYM_OF"] = ("IS_A", true),    // x is hypernym of y ⇒ y is_a x
        ["HAS_HYPONYM"]    = ("IS_A", true),    // x's hyponym is y   ⇒ y is_a x
        ["IS_HYPONYM_OF"]  = ("IS_A", false),
        ["HAS_INSTANCE"]   = ("IS_INSTANCE_OF", true),   // x has instance y ⇒ y is_instance_of x

        // Meronymy — canonical HAS_PART/HAS_MEMBER/HAS_SUBSTANCE is "whole has_* part".
        ["IS_PART_OF"]      = ("HAS_PART", true),
        ["IS_MEMBER_OF"]    = ("HAS_MEMBER", true),
        ["IS_SUBSTANCE_OF"] = ("HAS_SUBSTANCE", true),

        // WordNet domain pointers — canonical is "member HAS_DOMAIN_* domain".
        ["IS_DOMAIN_TOPIC_MEMBER"]  = ("HAS_DOMAIN_TOPIC", true),
        ["IS_DOMAIN_REGION_MEMBER"] = ("HAS_DOMAIN_REGION", true),
        ["IS_DOMAIN_USAGE_MEMBER"]  = ("HAS_DOMAIN_USAGE", true),

        // Sense — canonical IS_SENSE_OF is "sense is_sense_of word".
        ["HAS_SENSE_OF"] = ("IS_SENSE_OF", false),

        // ConceptNet names that are the SAME assertion as a canonical arena.
        ["SIMILAR_TO"] = ("IS_SIMILAR_TO", false),
        ["MADE_OF"]    = ("HAS_SUBSTANCE", false),   // whole MadeOf material ⇒ whole has_substance material
    };

    /// <summary>Resolve a kind name (canonical OR a source alias) to its arena.
    /// Unknown names fall back to a Probationary self-named arena (never throws,
    /// so an unenumerated kind degrades gracefully rather than breaking ingest).</summary>
    public static KindResolution Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        bool flip = false;
        if (Alias.TryGetValue(name, out var a)) { flip = a.Flip; name = a.Canon; }

        if (Canon.TryGetValue(name, out var def))
            return new KindResolution(
                KindId(name), def.Rank, def.Symmetry, flip,
                def.Parent is null ? null : KindId(def.Parent), name);

        // Unregistered kind: keep it usable, mark it probationary, no parent.
        return new KindResolution(KindId(name), KindRank.Probationary, Symmetry.Asymmetric, flip, null, name);
    }

    /// <summary>Resolve a UD dependency relation to its own arena under the
    /// DEPENDS_ON taxonomy: <c>nsubj → DEP_NSUBJ is_a DEPENDS_ON</c>;
    /// <c>nsubj:pass → DEP_NSUBJ_PASS is_a DEP_NSUBJ</c>. The deprel is identity
    /// (its own embedding), NOT erased into context_id.</summary>
    public static KindResolution ResolveDeprel(string deprel)
    {
        ArgumentException.ThrowIfNullOrEmpty(deprel);
        string norm = deprel.Trim().ToLowerInvariant();
        string canon = "DEP_" + norm.Replace(':', '_').ToUpperInvariant();
        int colon = norm.IndexOf(':');
        string parent = colon > 0 ? "DEP_" + norm[..colon].ToUpperInvariant() : "DEPENDS_ON";
        return new KindResolution(KindId(canon), KindRank.Partitive, Symmetry.Asymmetric, false, KindId(parent), canon);
    }

    // ── Endpoint orientation: flip to canonical direction, then for symmetric
    //    kinds canonicalize order so (a,b) and (b,a) land on one consensus pk.
    private static (Hash128 Subject, Hash128? Object) Orient(in KindResolution r, Hash128 subject, Hash128? obj)
    {
        if (obj is { } o)
        {
            if (r.Flip) (subject, o) = (o, subject);
            if (r.Symmetry == Symmetry.Symmetric && subject.CompareToBytewise(o) > 0)
                (subject, o) = (o, subject);
            return (subject, o);
        }
        return (subject, null);   // unary/categorical attestation
    }

    /// <summary>Build a categorical attestation routed through the registry:
    /// canonical kind id, registry rank, endpoint flip + symmetry-order applied.
    /// The source-trust stays the decomposer's (a per-source property).</summary>
    public static AttestationRow Attest(
        Hash128 subject, string kindName, Hash128? obj, Hash128 sourceId, double sourceTrust,
        Hash128? contextId = null, bool confirm = true, long observationCount = 1)
    {
        var r = Resolve(kindName);
        var (s, o) = Orient(r, subject, obj);
        return AttestationFactory.CreateCategorical(
            s, r.Id, o, sourceId, contextId, confirm, r.Rank * sourceTrust, observationCount);
    }

    /// <summary>Build a magnitude-weighted attestation routed through the registry
    /// (model circuits, PMI, …): signed magnitude scored via tanh(m/M) where M is
    /// the measured per-arena scale (<paramref name="arenaScale"/> — a scale, never
    /// a value-dropping floor), weight = registry rank × source trust, endpoints
    /// oriented as in <see cref="Attest"/>.</summary>
    public static AttestationRow AttestWeighted(
        Hash128 subject, string kindName, Hash128? obj, Hash128 sourceId, double sourceTrust,
        double magnitude, double arenaScale, Hash128? contextId = null, long observationCount = 1)
    {
        var r = Resolve(kindName);
        var (s, o) = Orient(r, subject, obj);
        return AttestationFactory.CreateWeighted(
            s, r.Id, o, sourceId, contextId, r.Rank, sourceTrust, magnitude, arenaScale, observationCount);
    }

    /// <summary>Resolve a dependency relation and build its attestation in one
    /// step (the deprel becomes the kind/arena; head is the object).</summary>
    public static AttestationRow AttestDeprel(
        Hash128 dependent, string deprel, Hash128 head, Hash128 sourceId, double sourceTrust,
        long observationCount = 1)
    {
        var r = ResolveDeprel(deprel);
        return AttestationFactory.CreateCategorical(
            dependent, r.Id, head, sourceId, /*context*/ null, confirm: true,
            witnessWeight: r.Rank * sourceTrust, observationCount: observationCount);
    }

    /// <summary>Split a CoNLL-U feature <c>"Name=Value"</c> into its parts (and
    /// keep multi-valued <c>"Name=A,B"</c> intact as the value).</summary>
    public static bool ParseFeature(string feature, out string name, out string value)
    {
        name = ""; value = "";
        if (string.IsNullOrEmpty(feature)) return false;
        int eq = feature.IndexOf('=');
        if (eq <= 0 || eq >= feature.Length - 1) return false;
        name = feature[..eq].Trim();
        value = feature[(eq + 1)..].Trim();
        return name.Length > 0 && value.Length > 0;
    }

    /// <summary>Resolve a morphological feature TYPE to its own arena under
    /// HAS_FEATURE: <c>Number → FEAT_NUMBER is_a HAS_FEATURE</c>. The feature
    /// VALUE (Sing) is the object, never bundled into the kind. Like deprels, the
    /// family is dynamic (hundreds of type×value combinations, never enumerated).</summary>
    public static KindResolution ResolveFeature(string featureName)
    {
        ArgumentException.ThrowIfNullOrEmpty(featureName);
        string canon = "FEAT_" + featureName.Trim().ToUpperInvariant();
        return new KindResolution(KindId(canon), KindRank.Partitive, Symmetry.Asymmetric, false,
                                  KindId("HAS_FEATURE"), canon);
    }

    /// <summary>Resolve a <c>"Name=Value"</c> feature and build its attestation:
    /// the feature type is the kind/arena, the value is the object entity.</summary>
    public static AttestationRow AttestFeature(
        Hash128 subject, string featureName, Hash128 valueEntity, Hash128 sourceId, double sourceTrust,
        long observationCount = 1)
    {
        var r = ResolveFeature(featureName);
        return AttestationFactory.CreateCategorical(
            subject, r.Id, valueEntity, sourceId, /*context*/ null, confirm: true,
            witnessWeight: r.Rank * sourceTrust, observationCount: observationCount);
    }

    /// <summary>All canonical kinds with their resolved arena metadata — for the
    /// taxonomy seed (kind entities + is_a-on-kinds edges) and for tests.</summary>
    public static IEnumerable<KindResolution> AllCanonical()
    {
        foreach (var name in Canon.Keys)
            yield return Resolve(name);
    }

    /// <summary>Seed the static canonical-kind taxonomy into a bootstrap change:
    /// one Kind-typed entity per canonical kind, plus the <c>is_a</c>-on-kinds
    /// roll-up edges (ATTENDS is_a RELATED_TO, HAS_XPOS is_a HAS_POS, …) as
    /// SubstrateMandate refutable attestations. Idempotent (content-addressed +
    /// ON CONFLICT) so every decomposer's <see cref="BootstrapIntentBuilder"/>
    /// can call it and only the first run lands rows — this is the FK floor that
    /// lets registry-routed attestations reference canonical kinds regardless of
    /// decomposer layer order. Dynamic families (DEP_*, FEAT_*) are NOT seeded
    /// here — their members are emitted on first sight at ingest, with their own
    /// is_a edge to a static parent declared here (DEPENDS_ON, HAS_FEATURE).</summary>
    public static void SeedCanonical(SubstrateChangeBuilder builder, Hash128 sourceId)
    {
        var all = new List<KindResolution>(AllCanonical());
        foreach (var k in all)
            builder.AddEntity(new EntityRow(k.Id, (byte)MetaTier.Kind, BootstrapIntentBuilder.KindMetaTypeId, sourceId));
        foreach (var k in all)
            if (k.ParentId is { } parent)
                builder.AddAttestation(Attest(k.Id, "IS_A", parent, sourceId, SourceTrust.SubstrateMandate));
    }

    /// <summary>Emit a dynamic-family kind (DEP_*, FEAT_*) and its is_a edge to
    /// the family parent at ingest, so its FK and roll-up exist. TWO gates with
    /// DIFFERENT scopes, deliberately:
    /// <list type="bullet">
    ///   <item><paramref name="seenEntitiesThisBatch"/> — PER BATCH. The kind
    ///   ENTITY row rides EVERY batch that references the kind, so each batch
    ///   is referentially SELF-CONTAINED and batches commit in any order
    ///   (ParallelWorkers &gt; 1, parallel producers). A run-scoped gate here
    ///   was the ordering bug: only the first-sight batch carried the entity,
    ///   and a later batch applied concurrently could reference it before it
    ///   committed. Re-presented entity rows dedup at the writer (and its
    ///   run-scoped proven-id cache makes the re-presentation free).</item>
    ///   <item><paramref name="seenAttestationsThisRun"/> — PER RUN (concurrent
    ///   set under parallel producers). The IS_A edge is TESTIMONY: one run =
    ///   ONE witness statement on the taxonomy edge, regardless of how many
    ///   batches mention the kind — the accumulating writer consumes scores
    ///   per presented row, so re-emitting per batch would multiply games.</item>
    /// </list></summary>
    public static void SeedDynamic(SubstrateChangeBuilder builder, in KindResolution k, Hash128 sourceId,
                                   ISet<Hash128> seenEntitiesThisBatch,
                                   ConcurrentIdSet seenAttestationsThisRun)
    {
        if (seenEntitiesThisBatch.Add(k.Id))
            builder.AddEntity(new EntityRow(k.Id, (byte)MetaTier.Kind, BootstrapIntentBuilder.KindMetaTypeId, sourceId));
        if (k.ParentId is { } parent && seenAttestationsThisRun.Add(k.Id))
            builder.AddAttestation(Attest(k.Id, "IS_A", parent, sourceId, SourceTrust.AcademicCurated));
    }

    /// <summary>Seed a dependency relation's full kind chain (subtype → base →
    /// DEPENDS_ON) so every level's entity + is_a edge exists before the
    /// dependency attestation references it. <c>nsubj:pass</c> seeds DEP_NSUBJ
    /// (is_a DEPENDS_ON) then DEP_NSUBJ_PASS (is_a DEP_NSUBJ).</summary>
    public static void SeedDeprel(SubstrateChangeBuilder builder, string deprel, Hash128 sourceId,
                                  ISet<Hash128> seenEntitiesThisBatch,
                                  ConcurrentIdSet seenAttestationsThisRun)
    {
        int colon = deprel.IndexOf(':');
        if (colon > 0) SeedDynamic(builder, ResolveDeprel(deprel[..colon]), sourceId, seenEntitiesThisBatch, seenAttestationsThisRun);
        SeedDynamic(builder, ResolveDeprel(deprel), sourceId, seenEntitiesThisBatch, seenAttestationsThisRun);
    }
}
