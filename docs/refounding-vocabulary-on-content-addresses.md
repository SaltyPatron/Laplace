# Re-founding the vocabulary on content addresses

Status: design closed, executing. Author contract — every decision below is **made**, not open.

## The sin being removed

Type/relation/POS/language/source "vocabulary" is currently minted as a separate entity whose id is
`blake3("substrate/type/{NAME}/v1")` — a hash of a synthetic **path string**, not content. That:

- breaks the first law (ids are *pure content addresses*);
- creates a redundant second identity next to the real content node (each anchor already emits a
  `HAS_NAME_ALIAS` to the codepoint-walk content entity for its own name);
- has no compositional depth, which is the only reason a prior change fabricated
  `EntityTier.Vocabulary = 5` — a radial shell *deeper than a document*, to home entities that
  shouldn't exist.

## The law (geometry), restated

`tier == compositional depth == radius`. tier 0 = codepoints **on the S³ surface** (the T0 perfcache:
DUCET/super-Fib/Hopf form coord). Composition moves **inward**: `tier = max(child tier) + 1`; n-grams
fall within the codepoint skin (grapheme/word/sentence/document = shells 1–4). Promotion requires
composition. **No entity gets a tier it did not compose. No category is ever a tier.**

## The target model

Vocabulary **is content**. Each type/relation/source/POS/language term is the ordinary content
entity for its own name's bytes:

- **id** = the natural-unit content-root of the name's codepoint walk (NOT `blake3(name)` — the merkle
  of its codepoint/grapheme/word composition, identical to what ingesting that literal text yields).
- **tier** = the derived compositional depth of that walk (a one-word name → 2). Never stamped.
- **role** ("this node is a relation type / POS / language / source") = an `IS_TYPED_AS` attestation
  to the corresponding meta, resolved by consensus — typing-as-attestation, like every other fact.

There is exactly one identity per concept. The anchor class is gone.

## Decision 1 — the single id primitive (`canonical_content_id`)

All five minting families must compute the **same** id for the same name, exactly as they all compute
`blake3("substrate/type/{NAME}/v1")` today. We replace that with one shared native primitive:

```
laplace_canonical_content_id(const char* name, hash128_t* out)   // engine/core
```

It runs the real text decomposer + hash_composer and returns the natural-unit root id (the same id the
ingest path assigns to that literal text). Exposed as:

- SQL: `laplace.canonical_content_id(text) RETURNS bytea` (extension C wrapper).
- C#: `NativeInterop.CanonicalContentId(string, out Hash128)` (already have `ContentWitnessBatch.RootId`
  — route both to the same native code).
- Codegen: the generated SQL seed frag emits `canonical_content_id('IS_A')`; the generated
  `relation_law.c` resolves relation ids via the same primitive (cached at first use, as today).

By construction all five agree, because there is now one function, not five copies of a string rule.

**The primitive already exists** in every layer — C# `ContentWitnessBatch.RootId` / `TextDecomposer.
ContentRootId`, SQL `laplace.word_id(text)`, native `ComposeRootId`. This is a repoint, not new
infrastructure.

**The perfcache trap, and why lazy-at-first-use solves it.** Content-root ids need the codepoint table
(perfcache) loaded; today's type ids are pure `blake3` resolved at C# static-init and at `CREATE
EXTENSION`, with no perfcache. Resolution — mirror the structure that already exists:

- **Native:** `relation_law.c` already resolves ids *lazily* on first use (`relation_ids_ensure` →
  `type_id_from_canonical`, cached). Change only the id function to the content-root (the decomposer is
  in-lib). First relation resolve happens during ingest, perfcache present. No codegen precompute.
- **C#:** the `static readonly OfCanonical(...)` type-id fields become **lazy** (`Lazy<Hash128>` /
  computed property), so the content-root is resolved at first use — after a decomposer has loaded the
  perfcache, which they all do before work.
- **SQL:** nothing computes a content-root at install; all of it runs in `seed_foundation()` after the
  perfcache GUC is set and codepoints exist.

(Codegen-precomputed constants remain a fallback only if some perfcache-free context must reference a
vocabulary id; none is known today.)

**Canonical content form (fixed, uniform):** the id is the content-root of the *literal* canonical name
string as-is — `"IS_A"`, `"NOUN"`, `"Codepoint"` — with NO underscore→space or case transform. All five
families decompose the identical bytes. (The human render name / `HAS_NAME_ALIAS` may still present a
prettier form; that is display only and never the id.)

## Decision 2 — `type_id` column: KEEP, repoint, and back with an attestation

The `entities.type_id` column **stays** (removing it is a separate, far larger denormalization project —
explicitly out of scope here; see "Boundaries"). Its value moves from the path-hash to
`canonical_content_id(typeName)`. Authoritative typing is **additionally** emitted as an `IS_TYPED_AS`
attestation; the column is a denormalized pointer to the same content-type entity. So typing is genuinely
attestation, and the column is a cache — not a second source of truth.

For content entities, `tier_type_id(tier)` returns `canonical_content_id("Codepoint"|"Grapheme"|"Word"|
"Sentence"|"Document")`. (This stays redundant with `tier` exactly as today — deliberately not touched.)

## Decision 3 — break the circular dependency (seed ordering + deferred FK)

`content_root("Codepoint")` needs the codepoint table loaded, and codepoints are typed by it. Resolution:
the type FK is no longer satisfiable at install time, so **move all vocabulary seeding out of the
install-time DO block into a perfcache-aware seed function, and defer FK validation.**

- `10_bootstrap.sql.in` (install): create schema only. Add `entities_type_fk` as **`NOT VALID`** (declared,
  unenforced). No `blake3('substrate/...')` minting remains anywhere.
- `laplace.seed_foundation()` (new, runs after `laplace_substrate.perfcache_path` is set + codepoints
  exist), in order:
  1. Seed the meta roots — `canonical_content_id("Type"|"Codepoint"|"Grapheme"|"Word"|"Sentence"|
     "Document"|"Source"|"RelationType"|"PhysicalityType"|trust classes…)` — each row typed to
     `content_root("Type")` (Type self-typed). Their name-constituent codepoints are not required as a
     precondition (the type FK targets only the type *row*; there is no constituent FK on `entities`).
  2. Seed codepoints (tier 0), typed `content_root("Codepoint")`.
  3. Seed the 16 canonical + all manifest relation types and POS via `canonical_content_id`, each with an
     `IS_TYPED_AS → content_root("RelationType")` / `content_root("Pos")` edge.
  4. `ALTER TABLE entities VALIDATE CONSTRAINT entities_type_fk`.

The deploy/seed sequence becomes: install schema → set perfcache GUC → seed codepoints → `seed_foundation()`
→ corpora. This is also philosophically correct: codepoints are the surface/foundation; *all* vocabulary
is content built on them.

## Decision 4 — replace the path-string scaffolding filters with `is_vocabulary`

Every site that does `canonical_names.name LIKE 'substrate/type/%'` to exclude scaffolding
(`consensus_reads.c:207,217`, `graph_taxonomy.c:141`, `generate_walk.c:32`, `variant_synth.c:340`,
`type_label` `20_converse.sql.in:191`, `render` `15_readback.sql.in`) switches to a typed predicate:

```
laplace.is_vocabulary(id bytea) -> bool   -- EXISTS an IS_TYPED_AS edge to a meta (Type/RelationType/Pos/…)
```

backed by the edges seeded in Decision 3 (cached). `type_label(t)` becomes `render(t)` directly (the name
*is* the content name now; no path to strip). This finally fixes §0 "scaffolding dominates consensus" at
the root instead of by string-matching a synthetic path.

## Decision 5 — flip the two enforcement laws

- `TypeIdLawTests` / `CanonicalPathLawTests`: invert from "type ids MUST be `OfCanonical("substrate/type/
  {name}/v1")`" to "type ids MUST equal `canonical_content_id(name)` and MUST NOT be a `substrate/...`
  path-hash." This is what keeps the five families from drifting back.
- `TypeColumnLawTests`: `relation_type_id('IS_A') == canonical_content_id('IS_A')`.

## File-by-file change set

Native (engine/core): `content_witness_batch.c` (`tier_type_id` → primitive; add
`laplace_canonical_content_id`), `grammar_compose.cpp` (grammar type ids), generated `relation_law.c` via
`scripts/codegen-attestation-law.py` (`type_id_from_canonical` → primitive), display filters
`consensus_reads.c` / `graph_taxonomy.c` / `generate_walk.c` / `variant_synth.c`.

Extension SQL: `10_bootstrap.sql.in` (schema only, FK NOT VALID), new `seed_foundation()`,
`18_ops_surface.sql.in` (`relation_type_id`/`source_id` → primitive; class breakdowns via `is_vocabulary`),
`15_readback.sql.in` (`render` codepoint branch on `type_id = content_root("Codepoint")`; type render via
content name), `24_identity_health.sql.in` (`compositional_type_ids` via primitive; tier law → {0..4}),
`20_converse.sql.in` (`type_label` → `render`), `21_seed*.sql.in` + generated frags, `26_generation.sql.in`,
`23_structural_surface.sql.in`, `22_cascade_surface.sql.in`.

C#: `EntityTypeRegistry`, `BootstrapIntentBuilder`, `VocabularyNames`, `GrammarEntityBuilder`, `ByteAtoms`,
`LayerCompletion`, `NpgsqlSubstrateReader`, `RelationTypeRegistry` (drop the path-hash; ids via primitive),
delete `EntityTier.Vocabulary`/`VOCAB_TIER` and every `EntityTier.Vocabulary` use (tier now derived).
Laws: `TypeIdLawTests`, `TypeColumnLawTests`, `CanonicalPathLawTests`.

## Reseed runbook

1. Build native (core + extension) and app.
2. `install-extensions` (hot-swap the .dll/.so + perfcache).
3. Fresh DB: install schema → set perfcache GUC → seed codepoints → `SELECT laplace.seed_foundation()` →
   `substrate_health()` green (0 identity violations, 0 fake tier bands, tier law {0..4}).
4. Re-ingest corpora.
5. Verify: no entity has a `substrate/...` path-hash id; `is_vocabulary` set matches the seeded metas;
   `render(relation_type_id('IS_A')) = 'IS_A'`; recall surfaces content over scaffolding.

## Boundaries (explicit, not silent)

- The `type_id` column is **kept** (repointed + attestation-backed), not removed. Full denormalization to a
  pure attestation graph is a separate project.
- `type_id` for content entities stays redundant with `tier` (unchanged behavior), now content-addressed.
- Dynamic vocab (deprel/feature/enhanced-deprel) already routes through the registry; it inherits the
  primitive automatically.
