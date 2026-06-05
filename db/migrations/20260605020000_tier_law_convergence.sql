-- 20260605020000_tier_law_convergence.sql
--
-- TIER LAW: tiers 0..N are CONTENT composition depth only — tier 0 is the
-- Unicode codepoint anchor (exactly the 1,114,112 codepoints), 1 observed
-- units, 2 n-grams, upward as content composes. Canonical-named meta
-- vocabulary lives in the meta band pinned by 10_bootstrap.sql.in:78-80
-- (META_TIER=250, KIND_TIER=248, TRUST_TIER=247).
--
-- Measured violation (laplace-dev before drop, named via the readback
-- surface): 11,038 non-codepoint entities squatting in tier 0 —
-- Model_Axis 9,984; UcdClassifier 803; Text 131; Kind 95; Type 11;
-- Scalar 5; Source 4; OrdinalContext 2; Model_Tokenizer/Architecture/
-- Model_Recipe 1 each — written by C# decomposers hardcoding /*tier*/ 0
-- while the SQL bootstrap pinned the same vocabulary at 247/248/250: two
-- writers, two tier laws, same types at tiers 0+248 and 0+247+250.
-- Reference entities (Language/ISO639Code/Tatoeba_Sentence/WordNet_*) were
-- likewise stamped into content tiers 2/3.
--
-- The C# writers are fixed in the same commit (MetaTier.cs mirrors the
-- bootstrap constants; every meta writer now uses the band). This migration
-- converges EXISTING databases. Content types (Codepoint/Grapheme/Word/
-- Sentence/Document/Ngram) are never touched.

-- Kinds → KIND_TIER (248)
UPDATE laplace.entities e
SET    tier = 248
WHERE  e.type_id = laplace.canonical_id('substrate/type/Kind/v1')
  AND  e.tier <> 248;

-- Meta vocabulary → META_TIER (250).
-- Type-typed rows at TRUST_TIER (247) are the bootstrap trust classes and stay.
UPDATE laplace.entities e
SET    tier = 250
WHERE  e.type_id IN (
        laplace.canonical_id('substrate/type/Type/v1'),
        laplace.canonical_id('substrate/type/Source/v1'),
        laplace.canonical_id('substrate/type/PhysicalityKind/v1'),
        laplace.canonical_id('substrate/type/UcdClassifier/v1'),
        laplace.canonical_id('substrate/type/OrdinalContext/v1'),
        laplace.canonical_id('substrate/type/Model_Axis/v1'),
        laplace.canonical_id('substrate/type/Model_Tokenizer/v1'),
        laplace.canonical_id('substrate/type/Model_Recipe/v1'),
        laplace.canonical_id('substrate/type/Architecture/v1'),
        laplace.canonical_id('substrate/type/Scalar/v1'),
        laplace.canonical_id('substrate/type/Text/v1'),
        laplace.canonical_id('substrate/type/UD_UPOS/v1'),
        laplace.canonical_id('substrate/type/UD_XPOS/v1'),
        laplace.canonical_id('substrate/type/UD_Feature/v1'),
        laplace.canonical_id('substrate/type/Language/v1'),
        laplace.canonical_id('substrate/type/ISO639Code/v1'),
        laplace.canonical_id('substrate/type/Tatoeba_Sentence/v1'),
        laplace.canonical_id('substrate/type/WordNet_Synset/v1'),
        laplace.canonical_id('substrate/type/WordNet_Sense/v1'),
        laplace.canonical_id('substrate/type/WordNet_POS/v1'),
        laplace.canonical_id('substrate/type/WordNet_LexCategory/v1'),
        laplace.canonical_id('substrate/type/Wiktionary_POS/v1'),
        laplace.canonical_id('substrate/type/Atomic_Marker/v1'),
        laplace.canonical_id('substrate/type/Atomic_Split/v1'))
  AND  e.tier NOT IN (247, 250);

-- Anchor heal: any entity whose id IS a codepoint id (one-way derivable:
-- id = BLAKE3-128(UTF-8(cp)), the codepoint_render map) must sit at tier 0
-- with type Codepoint — heals e.g. single-codepoint tokenizer pieces that a
-- writer stamped with a foreign type.
UPDATE laplace.entities e
SET    tier = 0,
       type_id = laplace.canonical_id('substrate/type/Codepoint/v1')
FROM   laplace.codepoint_render r
WHERE  e.id = r.id
  AND  (e.tier <> 0 OR e.type_id <> laplace.canonical_id('substrate/type/Codepoint/v1'));
