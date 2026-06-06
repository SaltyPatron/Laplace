using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// The canonical KIND registry — the single source of truth for the substrate's
/// COMPLETE kind vocabulary across every modality: linguistic (POS, dependency,
/// feature, sense, lexical relations), structural-standards (Unicode / ISO),
/// TENSOR-ROLE (the model modality — see below), and geometric. A kind is an
/// ARENA: relations of that kind compete for Glicko-2 μ against the neutral
/// baseline, so the consensus μ over a kind is a μ-ranked relational embedding
/// over the shared content nodes. N kinds ⇒ N co-equal embeddings of one
/// identity set (ARCHITECTURE.md — NN is plural; never crown one).
///
/// <para>The registry enforces the ONE canonicalization rule so witnesses
/// co-assert on the LITERAL same consensus pk instead of forking into parallel
/// near-duplicate arenas:</para>
/// <list type="number">
///   <item>Two genuinely different relations → two arenas (PRESERVE):
///   <c>nsubj≠obj</c>, <c>synonym≠translation≠antonym</c>, <c>is_a≠part_of</c>.</item>
///   <item>One relation under two NAMES → one arena (NORMALIZE):
///   <c>HAS_UPOS → HAS_POS</c>; <c>DEFINED_AS, DEFINES → HAS_DEFINITION</c>.</item>
///   <item>One relation in two DIRECTIONS → one arena, flip endpoints (lossless;
///   the inverse is the reverse query): <c>HAS_HYPONYM, IS_HYPERNYM_OF → IS_A</c>;
///   <c>IS_PART_OF → HAS_PART</c>.</item>
///   <item>Fine vs coarse of one relation → distinct arenas linked by
///   <c>is_a</c>-on-kinds, rolled up: <c>nsubj is_a DEPENDS_ON</c>;
///   <c>HAS_XPOS is_a HAS_POS</c>; <c>ATTENDS / OV_RELATES is_a RELATED_TO</c>.</item>
/// </list>
///
/// <para><b>Naming convention (2026-06-05 ruling — professional, unambiguous):</b>
/// (1) a kind name reads left-to-right as the assertion: "subject KIND object"
/// must parse as a sentence; (2) directional transforms end in <c>_TO</c>,
/// attribute-bearing kinds read <c>HAS_X</c>, identity/correspondence kinds read
/// <c>IS_X_OF</c>; (3) no name may be readable as two different assertions —
/// the NORMALIZES failure class: one id carried "codepoint normalizes-to form"
/// AND "model norm scales channel"; both renamed (<c>NORMALIZES_TO</c> /
/// <c>NORM_SCALES</c>); (4) source-native vocabularies keep their family prefix
/// (<c>DEP_*</c>, <c>FEAT_*</c>, ATOMIC's <c>X_*</c>/<c>O_*</c>, the tensor
/// roles). Rename table of record:
///   NORMALIZES (unicode-reserved) → NORMALIZES_TO;
///   NORMALIZES (model)            → NORM_SCALES;
///   DEFINES / DEFINED_AS          → HAS_DEFINITION (one arena — same assertion,
///                                   and "synset DEFINES gloss" read backwards);
/// kept after review: HAS_SCRIPT (codepoint property) vs USES_SCRIPT (language
/// practice) — two real assertions; HAS_A (ConceptNet-native possession/part
/// arena, fine-vs-coarse under HAS_PART); TRANSCRIBES_AS; HAS_EXAMPLE.</para>
///
/// <para><b>The TENSOR-ROLE family is first-class</b> (2026-06-05 ruling): the
/// ten weight-table arenas (EMBEDS, Q/K/V/O_PROJECTS, GATES, UP/DOWN_PROJECTS,
/// NORM_SCALES, OUTPUT_PROJECTS) plus TOKEN_MAPS_TO and MERGES_WITH are the
/// substrate's own MODEL modality — what a model witnesses at ingest AND the
/// mold-filling map at export (ConsensusReExport reads exactly these arenas
/// back into weight tensors). They are placement/structure arenas (no roll-up
/// parent); the model's RELATEDNESS reads (ATTENDS / OV_RELATES / COMPLETES_TO)
/// are the query-time vocabulary and roll up to the seed arenas. A model is
/// case (4), not a separate category: cross-model co-assertion is direct, and
/// the model is type-blind — the type of a seed edge it corroborates stays the
/// seed's.</para>
///
/// <para>SYMMETRIC kinds canonicalize endpoint order so <c>(a,b)</c> and
/// <c>(b,a)</c> hit one row. ANTONYM is a confirm in its own Oppositional arena,
/// not a refute (the refute/repel pole is anti-correlation magnitude or the
/// Gödel engine's active refutation).</para>
///
/// <para>Kind ids are content-addressed by canonical name
/// (<c>substrate/kind/&lt;NAME&gt;/v1</c>), matching <see cref="BootstrapIntentBuilder.AddRelationType(string)"/>,
/// so a kind the registry names and a kind a decomposer bootstrapped collide on
/// the same id with no second source of truth.</para>
/// </summary>
public static class RelationTypeRegistry
{
    /// <summary>Whether a relation reads the same in both directions. Symmetric
    /// kinds get endpoint-order canonicalized so (a,b)≡(b,a) on one consensus pk.</summary>
    public enum Symmetry { Asymmetric, Symmetric }

    /// <summary>Resolution of a (possibly source-named) kind to its canonical
    /// arena: the content-addressed kind id, its significance rank (→ witness
    /// weight → opponent φ), its symmetry, whether the source name's endpoints
    /// must be flipped to reach canonical direction, and its roll-up parent.</summary>
    public readonly record struct RelationTypeResolution(
        Hash128 Id, double Rank, Symmetry Symmetry, bool Flip, Hash128? ParentId, string Canonical);

    private sealed record KindDef(double Rank, Symmetry Symmetry, string? Parent);

    /// <summary>Content-addressed kind id from a canonical name — the convention
    /// shared with <see cref="BootstrapIntentBuilder"/>.</summary>
    public static Hash128 RelationTypeId(string canonicalName) =>
        Hash128.OfCanonical($"substrate/kind/{canonicalName}/v1");

    // ── Canonical arenas (relation kinds). Scalar config (HAS_*_SIZE), geometry
    //    placements (*_PROJECTS / EMBEDS / GATES), and external-id are NOT arenas
    //    and are intentionally absent — they route to metadata / the geometry axis.
    private static readonly Dictionary<string, KindDef> Canon = new(StringComparer.Ordinal)
    {
        // Standards-structural (Unicode / ISO) — high-trust skeleton.
        ["USES_SCRIPT"]              = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_SCRIPT"]              = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_GENERAL_CATEGORY"]    = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_COMBINING_CLASS"]     = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_BLOCK"]               = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_UPPERCASE_MAPPING"]   = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_LOWERCASE_MAPPING"]   = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["CANONICAL_DECOMPOSES_TO"] = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        // Renamed from NORMALIZES (the one-name-two-assertions failure class —
        // see header rename table): codepoint → normalized form. Reserved for
        // the Unicode normalization emitter; the model's per-channel norm is
        // NORM_SCALES in the tensor-role family below.
        ["NORMALIZES_TO"]           = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["TRANSCRIBES_AS"]          = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        // BYTE TIER (2026-06-05): which character a byte means is a property
        // of the ENCODING, never of the byte — DECODES_TO carries the encoding
        // as context (Latin-1 / CP1252 seeded; byte 0x80 → U+0080 vs € vs a
        // UTF-8 fragment are three RELATIONS, one atom). HAS_UTF8_ROLE = the
        // standard's own byte classification (continuation / lead-N / invalid).
        ["DECODES_TO"]    = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_UTF8_ROLE"] = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),

        // Unicode completeness (2026-06-05): the UCD properties the seed left
        // unextracted. Compatibility decomposition is DISTINCT from canonical
        // (a <tag>-form weak equivalence, never folded into the canonical arena).
        ["HAS_TITLECASE_MAPPING"]      = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["COMPATIBILITY_DECOMPOSES_TO"] = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_BIDI_CLASS"]             = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_MIRROR"]                 = new(RelationTypeRank.StandardsStructural, Symmetry.Symmetric, null),
        ["HAS_AGE"]                    = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_NAME_ALIAS"]             = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["CONFUSABLE_WITH"]            = new(RelationTypeRank.StandardsStructural, Symmetry.Symmetric, null),
        ["HAS_EMOJI_PROPERTY"]         = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_NUMERIC_VALUE"]          = new(RelationTypeRank.ScalarValued, Symmetry.Asymmetric, null),
        // ISO 639 completeness: code aliases + classification.
        ["HAS_ISO639_2_CODE"]          = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_LANGUAGE_SCOPE"]         = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_LANGUAGE_TYPE"]          = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["IS_LANGUAGE_CODE"]        = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_ISO639_1_CODE"]       = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["MEMBER_OF_MACROLANGUAGE"] = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_LANGUAGE"]            = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),

        // Taxonomic.
        ["IS_A"]           = new(RelationTypeRank.Taxonomic, Symmetry.Asymmetric, null),
        ["IS_INSTANCE_OF"] = new(RelationTypeRank.Taxonomic, Symmetry.Asymmetric, "IS_A"),
        ["MANNER_OF"]      = new(RelationTypeRank.Taxonomic, Symmetry.Asymmetric, "IS_A"),
        ["IS_SENSE_OF"]    = new(RelationTypeRank.Taxonomic, Symmetry.Asymmetric, null),

        // Partitive / attributive. Member/substance meronymy are DISTINCT arenas
        // (preserve distinctions) rolled up to HAS_PART.
        ["HAS_PART"]      = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),
        ["HAS_MEMBER"]    = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, "HAS_PART"),
        ["HAS_SUBSTANCE"] = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, "HAS_PART"),
        ["HAS_ATTRIBUTE"] = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),
        ["HAS_PROPERTY"]  = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, "HAS_ATTRIBUTE"),
        ["HAS_A"]         = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, "HAS_PART"),
        ["HAS_POS"]       = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),
        ["HAS_XPOS"]      = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, "HAS_POS"),
        ["HAS_FEATURE"]   = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),
        ["HAS_SENSE"]     = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),
        ["IS_PIXEL_OF"]   = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),
        ["IS_AT_SAMPLE"]  = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),

        // Causal / implicational.
        ["ENTAILS"]            = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["CAUSES"]             = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["CAUSES_DESIRE"]      = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["HAS_SUBEVENT"]       = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["HAS_FIRST_SUBEVENT"] = new(RelationTypeRank.Causal, Symmetry.Asymmetric, "HAS_SUBEVENT"),
        ["HAS_LAST_SUBEVENT"]  = new(RelationTypeRank.Causal, Symmetry.Asymmetric, "HAS_SUBEVENT"),
        ["HAS_PREREQUISITE"]   = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["MOTIVATED_BY_GOAL"]  = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["OBSTRUCTED_BY"]      = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["CREATED_BY"]         = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),

        // Predicate semantics (FrameNet / VerbNet / PropBank / SemLink,
        // 2026-06-05). EVOKES_FRAME = the frame-semantic binding (LU → frame);
        // frame elements / thematic roles / numbered args are the schema's
        // PARTS; CORRESPONDS_TO = SemLink's cross-resource alignments
        // (VN class ↔ FN frame, VN role ↔ FN FE, PB arg ↔ VN role) — one
        // symmetric equivalence arena, NEVER welded into IS_A.
        ["EVOKES_FRAME"]      = new(RelationTypeRank.Taxonomic,  Symmetry.Asymmetric, null),
        ["HAS_FRAME_ELEMENT"] = new(RelationTypeRank.Partitive,  Symmetry.Asymmetric, null),
        ["HAS_THEMATIC_ROLE"] = new(RelationTypeRank.Partitive,  Symmetry.Asymmetric, null),
        ["HAS_SEMANTIC_ROLE"] = new(RelationTypeRank.Partitive,  Symmetry.Asymmetric, null),
        ["FRAME_USES"]        = new(RelationTypeRank.Associative, Symmetry.Asymmetric, "RELATED_TO"),
        ["PERSPECTIVE_ON"]    = new(RelationTypeRank.Associative, Symmetry.Asymmetric, "RELATED_TO"),
        ["CAUSATIVE_OF"]      = new(RelationTypeRank.Causal,     Symmetry.Asymmetric, null),
        ["INCHOATIVE_OF"]     = new(RelationTypeRank.Causal,     Symmetry.Asymmetric, null),
        ["CORRESPONDS_TO"]    = new(RelationTypeRank.Equivalence, Symmetry.Symmetric, null),

        // ATOMIC2020 commonsense — Causal family, source-native X_*/O_* prefixes
        // (naming convention rule 4). Names preserved where they predate the
        // registry move (ids stable); IS_FILLED_BY → X_FILLED_BY per the
        // convention sweep (family prefix; greenfield rebuild covers the id).
        ["X_INTENT"]    = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["X_NEED"]      = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["X_WANT"]      = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["X_EFFECT"]    = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["X_REACT"]     = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["X_ATTR"]      = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["X_REASON"]    = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["X_FILLED_BY"] = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["O_EFFECT"]    = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["O_REACT"]     = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["O_WANT"]      = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["IS_AFTER"]    = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["IS_BEFORE"]   = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["OBJECT_USE"]  = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["MADE_UP_OF"]  = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, "HAS_PART"),

        // Syntactic dependency (parent of the dynamic DEP_* family). Enhanced
        // dependencies (CoNLL-U DEPS col) are a DIFFERENT annotation graph —
        // their EDEP_* family rolls up to its own parent, never merged.
        ["DEPENDS_ON"]           = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),
        ["ENHANCED_DEPENDS_ON"]  = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),

        // Equivalence (kept DISTINCT — same-language synonymy ≠ cross-language translation).
        ["IS_SYNONYM_OF"]     = new(RelationTypeRank.Equivalence, Symmetry.Symmetric, "RELATED_TO"),
        ["IS_TRANSLATION_OF"] = new(RelationTypeRank.Equivalence, Symmetry.Symmetric, "RELATED_TO"),
        ["HAS_VARIANT_OF"]    = new(RelationTypeRank.Equivalence, Symmetry.Symmetric, "RELATED_TO"),
        ["IS_SIMILAR_TO"]     = new(RelationTypeRank.Equivalence, Symmetry.Symmetric, "RELATED_TO"),
        ["IS_LEMMA_OF"]       = new(RelationTypeRank.Equivalence, Symmetry.Asymmetric, null),
        ["IS_PARTICIPLE_OF"]  = new(RelationTypeRank.Equivalence, Symmetry.Asymmetric, null),
        ["FORM_OF"]           = new(RelationTypeRank.Equivalence, Symmetry.Asymmetric, "RELATED_TO"),

        // Oppositional — antonym / negative assertions are CONFIRMS in their own
        // arenas (not refutes; the refute pole is active refutation).
        ["IS_ANTONYM_OF"]    = new(RelationTypeRank.Oppositional, Symmetry.Symmetric, null),
        ["DISTINCT_FROM"]    = new(RelationTypeRank.Oppositional, Symmetry.Symmetric, null),
        ["NOT_DESIRES"]      = new(RelationTypeRank.Oppositional, Symmetry.Asymmetric, null),
        ["NOT_USED_FOR"]     = new(RelationTypeRank.Oppositional, Symmetry.Asymmetric, null),
        ["NOT_CAPABLE_OF"]   = new(RelationTypeRank.Oppositional, Symmetry.Asymmetric, null),
        ["NOT_HAS_PROPERTY"] = new(RelationTypeRank.Oppositional, Symmetry.Asymmetric, null),

        // Associative — RELATED_TO is the roll-up parent for the relatedness family.
        ["RELATED_TO"]             = new(RelationTypeRank.Associative, Symmetry.Symmetric, null),
        // Corpus adjacency: "A PRECEDES B" — per-occurrence games via the
        // aggregated factory path (TextEntityBuilder). FOLLOWS is the same
        // assertion read backwards (rule 3: one arena, flip; the inverse is
        // the reverse query) — it was emitted as a SECOND arena with doubled
        // testimony until 2026-06-05.
        ["PRECEDES"]               = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["DERIVATIONALLY_RELATED"] = new(RelationTypeRank.Associative, Symmetry.Symmetric, "RELATED_TO"),
        // Renamed from DEFINES ("synset DEFINES gloss" read backwards — the
        // gloss defines the word; HAS_X is the attribute-bearing convention).
        // DEFINED_AS (ConceptNet) folds in via alias — same assertion.
        ["HAS_DEFINITION"]         = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["HAS_EXAMPLE"]            = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["HAS_ETYMOLOGY"]          = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["DEPICTS"]                = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["CAPTIONS"]               = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["ADJACENT_TO_PIXEL"]      = new(RelationTypeRank.Associative, Symmetry.Symmetric, null),
        ["PERTAINS_TO"]            = new(RelationTypeRank.Associative, Symmetry.Asymmetric, "RELATED_TO"),
        ["ALSO_SEE"]               = new(RelationTypeRank.Associative, Symmetry.Asymmetric, "RELATED_TO"),
        ["IN_VERB_GROUP_WITH"]     = new(RelationTypeRank.Associative, Symmetry.Symmetric, "RELATED_TO"),
        ["HAS_DOMAIN_TOPIC"]       = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        // Wiktionary completeness (2026-06-05): co-hyponyms are a distinct
        // relation (not similarity); registers (slang/archaic/technical) are
        // usage classification with the register WORDFORM as the value.
        ["IS_COORDINATE_TERM_WITH"] = new(RelationTypeRank.Associative, Symmetry.Symmetric, "RELATED_TO"),
        ["HAS_USAGE_REGISTER"]      = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        // WordNet verb frames: synset → frame template content (frames.vrb).
        ["HAS_VERB_FRAME"]          = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        // ConceptNet /r/dbpedia/* — parent of the dynamic DBPEDIA_* family
        // (the DEP_*/FEAT_* precedent: ~30 source-defined arenas, preserved).
        ["HAS_DBPEDIA_RELATION"]    = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["HAS_DOMAIN_REGION"]      = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["HAS_DOMAIN_USAGE"]       = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["USED_FOR"]               = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["CAPABLE_OF"]             = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["AT_LOCATION"]            = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["LOCATED_NEAR"]           = new(RelationTypeRank.Associative, Symmetry.Symmetric, null),
        ["HAS_CONTEXT"]            = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["DESIRES"]                = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["RECEIVES_ACTION"]        = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["SYMBOL_OF"]              = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["DERIVED_FROM"]           = new(RelationTypeRank.Associative, Symmetry.Asymmetric, "DERIVATIONALLY_RELATED"),
        ["ETYMOLOGICALLY_RELATED_TO"]   = new(RelationTypeRank.Associative, Symmetry.Symmetric, "HAS_ETYMOLOGY"),
        ["ETYMOLOGICALLY_DERIVED_FROM"] = new(RelationTypeRank.Associative, Symmetry.Asymmetric, "HAS_ETYMOLOGY"),

        // ── TENSOR-ROLE family — the substrate's MODEL modality (header doc).
        // The ten weight-table arenas the cell ETL loads at ingest AND the
        // mold-filling map ConsensusReExport reads at export. Placement /
        // structure arenas: no roll-up parent (they are not relatedness —
        // ATTENDS/OV_RELATES/COMPLETES_TO below are the query-time reads).
        ["EMBEDS"]          = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["Q_PROJECTS"]      = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["K_PROJECTS"]      = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["V_PROJECTS"]      = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["O_PROJECTS"]      = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["GATES"]           = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["UP_PROJECTS"]     = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["DOWN_PROJECTS"]   = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        // Renamed from NORMALIZES (model side of the split — header rename table).
        ["NORM_SCALES"]     = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["OUTPUT_PROJECTS"] = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        // Tokenizer structure: slot → wordform mapping (scored by the morph's
        // geometric residual where anchored) and the BPE merge lattice.
        ["TOKEN_MAPS_TO"]   = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["MERGES_WITH"]     = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),

        // Model circuit arenas — witnesses, roll up to relatedness. COMPLETES_TO
        // is shared directly with corpora (n-gram continuation), so it has no parent.
        ["ATTENDS"]      = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, "RELATED_TO"),
        ["OV_RELATES"]   = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, "RELATED_TO"),
        ["COMPLETES_TO"] = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),

        // External cross-reference (recoverable join key, low significance).
        ["HAS_EXTERNAL_ID"] = new(RelationTypeRank.ScalarValued, Symmetry.Asymmetric, null),
    };

    // ── Aliases: a source kind name that is the SAME assertion as a canonical
    //    kind, optionally with endpoints FLIPPED to reach canonical direction.
    private static readonly Dictionary<string, (string Canon, bool Flip)> Alias = new(StringComparer.Ordinal)
    {
        // POS family — one assertion, several names. (HAS_LEX_CATEGORY was
        // wrongly aliased here — a lexname is POS×domain, split at ingest by
        // the WordNet decomposer since 2026-06-05; alias removed.)
        ["HAS_UPOS"]         = ("HAS_POS", false),

        // Definition family — one assertion, several names (header rename table).
        ["DEFINES"]    = ("HAS_DEFINITION", false),
        ["DEFINED_AS"] = ("HAS_DEFINITION", false),

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

        // Adjacency read backwards — one arena, flipped (rule 3).
        ["FOLLOWS"] = ("PRECEDES", true),

        // ConceptNet names that are the SAME assertion as a canonical arena.
        ["SIMILAR_TO"] = ("IS_SIMILAR_TO", false),
        ["MADE_OF"]    = ("HAS_SUBSTANCE", false),   // whole MadeOf material ⇒ whole has_substance material

        // FrameNet relation names that are the SAME assertion as a canonical
        // arena: frame inheritance IS the taxonomic assertion; a subframe is a
        // component scene (the HAS_SUBEVENT assertion, stated child-first).
        ["INHERITS_FROM"] = ("IS_A", false),
        ["SUBFRAME_OF"]   = ("HAS_SUBEVENT", true),
        ["IS_INHERITED_BY"] = ("IS_A", true),

        // ATOMIC names that are the SAME assertion as a canonical arena.
        ["HINDERED_BY"]  = ("OBSTRUCTED_BY", false),   // co-asserts with ConceptNet ObstructedBy
        ["IS_FILLED_BY"] = ("X_FILLED_BY", false),     // convention sweep: family prefix
    };

    /// <summary>Resolve a kind name (canonical OR a source alias) to its arena.
    /// Unknown names fall back to a Probationary self-named arena (never throws,
    /// so an unenumerated kind degrades gracefully rather than breaking ingest).</summary>
    public static RelationTypeResolution Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        bool flip = false;
        if (Alias.TryGetValue(name, out var a)) { flip = a.Flip; name = a.Canon; }

        if (Canon.TryGetValue(name, out var def))
            return new RelationTypeResolution(
                RelationTypeId(name), def.Rank, def.Symmetry, flip,
                def.Parent is null ? null : RelationTypeId(def.Parent), name);

        // Unregistered kind: keep it usable, mark it probationary, no parent.
        return new RelationTypeResolution(RelationTypeId(name), RelationTypeRank.Probationary, Symmetry.Asymmetric, flip, null, name);
    }

    /// <summary>Resolve a UD dependency relation to its own arena under the
    /// DEPENDS_ON taxonomy: <c>nsubj → DEP_NSUBJ is_a DEPENDS_ON</c>;
    /// <c>nsubj:pass → DEP_NSUBJ_PASS is_a DEP_NSUBJ</c>. The deprel is identity
    /// (its own embedding), NOT erased into context_id.</summary>
    public static RelationTypeResolution ResolveDeprel(string deprel)
    {
        ArgumentException.ThrowIfNullOrEmpty(deprel);
        string norm = deprel.Trim().ToLowerInvariant();
        string canon = "DEP_" + norm.Replace(':', '_').ToUpperInvariant();
        int colon = norm.IndexOf(':');
        string parent = colon > 0 ? "DEP_" + norm[..colon].ToUpperInvariant() : "DEPENDS_ON";
        return new RelationTypeResolution(RelationTypeId(canon), RelationTypeRank.Partitive, Symmetry.Asymmetric, false, RelationTypeId(parent), canon);
    }

    /// <summary>Attest one ENHANCED dependency edge (CoNLL-U DEPS col):
    /// dependent —EDEP_*→ head, kind resolved via
    /// <see cref="ResolveEnhancedDeprel"/>. Same shape as
    /// <see cref="AttestDeprel"/> — a different annotation graph, own family.</summary>
    public static AttestationRow AttestEnhancedDeprel(
        Hash128 dependent, string deprel, Hash128 head, Hash128 sourceId, double sourceTrust,
        long observationCount = 1)
    {
        var r = ResolveEnhancedDeprel(deprel);
        return AttestationFactory.CreateCategorical(
            dependent, r.Id, head, sourceId, /*context*/ null, confirm: true,
            witnessWeight: r.Rank * sourceTrust, observationCount: observationCount);
    }

    /// <summary>Resolve an ENHANCED dependency relation (CoNLL-U DEPS col) to
    /// its own arena under ENHANCED_DEPENDS_ON: <c>nsubj → EDEP_NSUBJ</c>;
    /// <c>nsubj:xsubj → EDEP_NSUBJ_XSUBJ is_a EDEP_NSUBJ</c>. A different
    /// annotation graph from the basic DEP_* family — never merged.</summary>
    public static RelationTypeResolution ResolveEnhancedDeprel(string deprel)
    {
        ArgumentException.ThrowIfNullOrEmpty(deprel);
        string norm = deprel.Trim().ToLowerInvariant();
        string canon = "EDEP_" + norm.Replace(':', '_').ToUpperInvariant();
        int colon = norm.IndexOf(':');
        string parent = colon > 0 ? "EDEP_" + norm[..colon].ToUpperInvariant() : "ENHANCED_DEPENDS_ON";
        return new RelationTypeResolution(RelationTypeId(canon), RelationTypeRank.Partitive, Symmetry.Asymmetric, false, RelationTypeId(parent), canon);
    }

    /// <summary>Resolve a ConceptNet <c>/r/dbpedia/*</c> relation to its own
    /// arena under HAS_DBPEDIA_RELATION (dynamic family — the DEP_*/FEAT_*
    /// precedent): <c>dbpedia/genre → DBPEDIA_GENRE is_a HAS_DBPEDIA_RELATION</c>.</summary>
    public static RelationTypeResolution ResolveDbpedia(string rel)
    {
        ArgumentException.ThrowIfNullOrEmpty(rel);
        string norm = rel.Trim();
        if (norm.StartsWith("dbpedia/", StringComparison.OrdinalIgnoreCase)) norm = norm[8..];
        string canon = "DBPEDIA_" + norm.Replace('/', '_').ToUpperInvariant();
        return new RelationTypeResolution(RelationTypeId(canon), RelationTypeRank.Associative, Symmetry.Asymmetric, false,
                                  RelationTypeId("HAS_DBPEDIA_RELATION"), canon);
    }

    // ── Endpoint orientation: flip to canonical direction, then for symmetric
    //    kinds canonicalize order so (a,b) and (b,a) land on one consensus pk.
    private static (Hash128 Subject, Hash128? Object) Orient(in RelationTypeResolution r, Hash128 subject, Hash128? obj)
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
        Hash128 subject, string typeName, Hash128? obj, Hash128 sourceId, double sourceTrust,
        Hash128? contextId = null, bool confirm = true, long observationCount = 1)
    {
        var r = Resolve(typeName);
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
        Hash128 subject, string typeName, Hash128? obj, Hash128 sourceId, double sourceTrust,
        double magnitude, double arenaScale, Hash128? contextId = null, long observationCount = 1)
    {
        var r = Resolve(typeName);
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
    public static RelationTypeResolution ResolveFeature(string featureName)
    {
        ArgumentException.ThrowIfNullOrEmpty(featureName);
        string canon = "FEAT_" + featureName.Trim().ToUpperInvariant();
        return new RelationTypeResolution(RelationTypeId(canon), RelationTypeRank.Partitive, Symmetry.Asymmetric, false,
                                  RelationTypeId("HAS_FEATURE"), canon);
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
    public static IEnumerable<RelationTypeResolution> AllCanonical()
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
        var all = new List<RelationTypeResolution>(AllCanonical());
        foreach (var k in all)
            builder.AddEntity(new EntityRow(k.Id, (byte)MetaTier.RelationType, BootstrapIntentBuilder.RelationTypeMetaTypeId, sourceId));
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
    public static void SeedDynamic(SubstrateChangeBuilder builder, in RelationTypeResolution k, Hash128 sourceId,
                                   ISet<Hash128> seenEntitiesThisBatch,
                                   ConcurrentIdSet seenAttestationsThisRun)
    {
        if (seenEntitiesThisBatch.Add(k.Id))
            builder.AddEntity(new EntityRow(k.Id, (byte)MetaTier.RelationType, BootstrapIntentBuilder.RelationTypeMetaTypeId, sourceId));
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

    /// <summary>Seed an ENHANCED deprel kind + its roll-up chain — the
    /// EDEP_* mirror of <see cref="SeedDeprel"/>. A subtyped rel
    /// (advcl:cond) attests is_a EDEP_ADVCL, so the PARENT kind entity must
    /// stage too (the 2026-06-05 ar_padt referential-proof lesson: a file may
    /// contain only subtyped forms, never the bare rel).</summary>
    public static void SeedEnhancedDeprel(SubstrateChangeBuilder builder, string deprel, Hash128 sourceId,
                                          ISet<Hash128> seenEntitiesThisBatch,
                                          ConcurrentIdSet seenAttestationsThisRun)
    {
        int colon = deprel.IndexOf(':');
        if (colon > 0) SeedDynamic(builder, ResolveEnhancedDeprel(deprel[..colon]), sourceId, seenEntitiesThisBatch, seenAttestationsThisRun);
        SeedDynamic(builder, ResolveEnhancedDeprel(deprel), sourceId, seenEntitiesThisBatch, seenAttestationsThisRun);
    }
}
