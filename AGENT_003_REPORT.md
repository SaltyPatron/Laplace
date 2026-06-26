# AGENT_003 — Report: tiers, vocabulary, identity, and the meta↔content boundary

My explicit understanding of the substrate as it bears on the "hardcoded vocabulary tier" problem,
what's actually wrong, and what the fix is. Written after being corrected (repeatedly) by the author;
where I was wrong is logged in §6 deliberately. File/line refs are from the current tree.

---

## 1. What the substrate is (the model I now hold)

**One mechanism: content-addressed nodes at a derived tier, plus typed attestations on those nodes.**

- **Identity is trivial:** `id = blake3(content)`. Same content → same hash. The word "noun" is blake3
  of its bytes. A node's id is never minted out-of-band; it falls out of its content. (`Hash128.OfCanonical`
  /`hash_canonical("substrate/...")` is the *violation*, not the rule — see §3.)
- **Tier = compositional depth = geometric radius.** Codepoints sit on the **S³ surface** (tier 0, the
  T0 perfcache: DUCET/super-Fib/Hopf form coord). Composition falls **inward**: `tier = max(child)+1`
  (`grammar_compose.cpp:404`). The law is explicit — *"tier promotion requires composition"*
  (`scripts/sql/substrate-law-probes.sql:7-12`). Tier is **emergent, never stamped, never a category.**
- **Tiers are per-modality, grammar-fed.** `codepoint→grapheme→word→sentence→document` (0–4) is just the
  **text** ladder, and UAX29 is just the **text** segmentation grammar. `grammar_compose(modality_id, AST)`
  folds *any* parser's AST the same way, typing nodes `substrate/type/grammar/{modality}/{node}`
  (`grammar_compose.cpp:21-29`). So:
  - **code** → the language's tree-sitter grammar (hundreds exist; `external/tree-sitter-grammars/`).
  - **chess (PGN)** → a position is content composed from its canonical surface (emergent tier); the
    move/game structure is **attestation edges** (`state —move→ state`, scored by result),
    not deeper tiers (`app/Laplace.Modality/TurnSubstrate.cs`, `ChessState.cs`).
  - **AV / SCADA** → perceptual segmentation / protocol+event-windowing as their grammars.
  Tier therefore **cannot** be a hardcoded 0–4 or 5.
- **Attestations are the generalized weights.** Typed, Glicko-2-weighted relational edges
  (`IS_A`, `HAS_POS`, `IS_TRANSLATION_OF`, `HAS_DEFINITION`, …) are the analog of a transformer's
  `Q/K/V/O/gate/up/down/norm` — but hundreds of relation types instead of ~7 matrices, **stacked across
  tiers**. Within-unit composition = tiers; between-unit relations = attestations. The foundry exports
  these consensus planes into GGUF weight tensors. (This is why rank/band changes broke the foundry.)
- **Classification is multi-axis and orthogonal.** An entity is not "content XOR vocabulary." It carries:
  | axis | what it encodes |
  |---|---|
  | `tier` | compositional depth / radius — *only* |
  | physicality | CONTENT / BUILDING_BLOCK / PROJECTION (`pk_*` metas, `10_bootstrap.sql.in:68-78`) |
  | `type_id` | meta-type: Word, POS, RelationType, Language, Synset, `grammar/{modality}/{node}` |
  | trust class / source | provenance layer: **Mandate (app/meta) · Academic/Corpus (seed) · UserPrompt/Response (user) · Adversarial** |
  | attested vs not | has consensus relations or not |
  "App/meta vs substrate vs user" lives in **type_id + physicality + trust/source** — *never* in tier.

- **Concepts are anchored on real external ids, content-addressed.** A surface lemma and the concept it
  lexicalizes are **different entities**:
  - surface "noun"(en), "Substantiv"(de) → `blake3(content)`, tiered by composition;
  - the concept → its **ILI** (Interlingual Index, registry = **CILI**). ILI ids look like `i77784` and are
    **stable across wordnet versions and languages** (apricot synset = `07750872-n` in WN3.0, `07766848-n`
    in 3.1, `13235-n` in German OdeNet — all ILI `i77784`). Concept `id = blake3(ILI)`;
    `Decomposers.Abstractions/ConceptAnchor.cs:60` already does `ContentEmitter.RootId(ili)`.
  - link: `word —HAS_SENSE→ synset(ILI)`; the ILI node carries definition, hypernyms, cross-lingual
    members, POS.
  - The ILI is the **cross-source/cross-version convergence key**: WN/OdeNet/ConceptNet all land on the
    same ILI → same blake3 node → dedup merges them → the universal relational field. Same rule for other
    vocab: **languages = ISO 639, POS = UPOS, relation types = the GWN/ConceptNet relation inventory
    name.** Real canonical id, blake3'd — never an invented namespace.

- **Dedup / RLE / referential integrity (why the above is safe).**
  - `merkle_dedup_trunk_shortcircuit` (`engine/core/src/merkle_dedup.c`) skips re-emitting any node already
    present (or whose parent is) — *content stored once, occurrences recorded as attestations.*
  - RLE: trajectories pack `(entity_id, ordinal, run_length, flags)` (`mantissa.h`), folding repeated
    *constituents*; attestations are separate rows, never RLE'd.
  - **Referential integrity lives in the typed links.** An attestation `(subject, type, object)` is exact
    regardless of node sharing; the ghost-reference law requires every referenced id to be a real entity.
    Distinctness of concepts is carried by their id (ILI ≠ surface bytes), not by a separate tier or a
    synthetic anchor.

---

## 2. The task that surfaced this

A prior session changed `EntityTier.Vocabulary` from `0` to **`5`** (`app/Laplace.SubstrateCRUD/EntityTier.cs:20`,
commit 68b4825) to dodge a `tier==0`-means-codepoint check in `render()`. That stamped a **category onto the
depth axis** — a radial shell *deeper than a document* for entities that composed nothing. It is a direct
violation of the tier-promotion law and is a large share of the **~5,858 tier-law violations**
(`extension/laplace_substrate/docs/FUNCTION_CATALOG.md:85`, `24_identity_health.sql.in`).

---

## 3. The problems (concrete)

1. **Fabricated tier.** `EntityTier.Vocabulary = 5` and SQL `VOCAB_TIER` (`10_bootstrap.sql.in:50`) encode
   *kind* in the *depth* field. ~40 C# sites + the bootstrap stamp it.
2. **Path-hash dead-leaf anchors.** Vocabulary entities are minted `blake3("substrate/type/{NAME}/v1")` —
   a synthetic namespace, not content — in five families that must agree: SQL bootstrap
   (`10_bootstrap.sql.in:53-151`), SQL resolvers (`relation_type_id`/`source_id`, `18_ops_surface.sql.in:1-10`),
   native `tier_type_id` (`content_witness_batch.c:22-32`) and the generated `relation_law.c:197-204`
   (`type_id_from_canonical`), and the C# registries (`EntityTypeRegistry`, `BootstrapIntentBuilder`,
   `VocabularyNames`). These nodes are **dead leaves**: e.g. `PosReference.cs:61` emits the POS as a
   `Vocabulary`-tier path-hash with no link to the word "noun" and no translations.
3. **Impoverishment (the real damage).** Because the meta nodes are synthetic dead leaves, the metadata is
   **not traversable**: `dog —HAS_POS→ <opaque NOUN anchor>` dead-ends instead of reaching the multilingual
   word "noun" → its definition → its hypernyms. The richness that justifies the architecture is missing.
4. **Axis conflation in the schema reads.** `render()` and several native filters key codepoint/scaffolding
   logic on `tier==0` or `name LIKE 'substrate/type/%'` (`15_readback.sql.in:88`, `consensus_reads.c:207,217`,
   `graph_taxonomy.c:141`, `generate_walk.c:32`) — using tier or a path string to answer a *type/physicality*
   question. `substrate_counts()` still expects vocab at tier 0 (`18_ops_surface.sql.in:82`), so writer and
   reader already disagree post-bump.
5. **CILI is the canonical seeding gap.** Concepts should be anchored on ILI and linked to surface lemmas;
   that ingestion is incomplete (the documented "canonical sin").

---

## 4. The solution

**Stop inventing identity and stop encoding kind in tier. Use the real ids the data already carries.**

- **Identity:** `blake3(content)` for surfaces; `blake3(real-canonical-id)` for concepts/metas —
  **ILI** for synsets/concepts (`ConceptAnchor.RootId(ili)` already exists), **ISO 639** for languages,
  **UPOS** for POS, **GWN/ConceptNet inventory name** for relation types. No `substrate/type/.../v1`.
- **Kind/layer:** carried by `type_id` + physicality + trust/source. Not tier.
- **Tier:** always derived from composition, per the modality's grammar. Delete `EntityTier.Vocabulary` /
  `VOCAB_TIER`; tighten the identity law to `tier ∈ {0..4}` for text (and to the grammar's depth for others).
- **Link, don't flatten, don't dead-leaf:** `surface lemma —HAS_SENSE→ concept(ILI)`; the concept hub
  carries definition / hypernyms / cross-lingual members / POS. The concept stays distinct from the surface
  word (different id string), so dedup never conflates them, and referential integrity sits on the typed
  links. Completing CILI seeding *is* this fix, not a rewrite around it.
- **Reader repoints:** codepoint logic keys on `type_id = Codepoint`, scaffolding filters key on a typed
  predicate (e.g. `IS_TYPED_AS`), not on `tier==0` or a path-string `LIKE`.

This is reseed-class (the bootstrap ids and every `type_id`/relation-`type_id` reference move together), so
it lands as one coherent pass + a reseed, not a partial.

---

## 5. Implementation blast radius (for whoever executes)

Five id-derivation families must change in lockstep (all hash the same string today):
`10_bootstrap.sql.in` (FK targets) · `relation_type_id`/`source_id` (`18_ops_surface.sql.in`) ·
native `tier_type_id` (`content_witness_batch.c`) · generated `relation_law.c` ← `engine/manifest/relation_types.toml`
← `scripts/codegen-attestation-law.py` · C# registries. Plus the two enforcement laws
(`TypeIdLawTests`, `TypeColumnLawTests`/`CanonicalPathLawTests`) which currently *pin* the path-hash
convention, the display/scaffolding filters in §3.4, and the seed-ordering/perfcache concern (content/ILI
ids need the codepoint table loaded, so foundation seeding runs after the perfcache, with the type FK
declared `NOT VALID` until validated post-seed).

---

## 6. Where I was wrong this session (so the next agent doesn't repeat it)

Three wrong models, all **axis-conflations**:
1. `Vocabulary = 5` (inherited) — kind jammed into depth. I initially "fixed" it by →`0`, which is just as
   wrong: tier 0 is the **codepoint S³ surface**; vocabulary is not a codepoint.
2. **"Vocabulary IS content" (`content_root(name)`)** — I proposed deleting the anchor class and making each
   type/relation be the content node of its name. This **flattens**: dedup makes the relation-type the same
   node as every literal text occurrence of the string, destroying the distinct, stable referent that
   `attestation.type_id` depends on.
3. **"Keep anchors distinct via a separate synthetic node"** — re-severs the link to content (dead leaf).

The correction that resolves all three: distinct concept nodes anchored on **real external ids (ILI/…)**,
content-addressed, **linked** into the multilingual content layer; kind in type_id/physicality/trust; tier
always derived. I also wrongly called `HAS_NAME_ALIAS` "redundant" — it (or the ILI link) is load-bearing:
it's what keeps a concept distinct from its name-as-text while staying legible.

---

## 7. Status

- Code: **at baseline**, untouched (`EntityTier.Vocabulary = 5` still present). My earlier bad edits were
  reverted; nothing cut since.
- Docs: `docs/refounding-vocabulary-on-content-addresses.md` rewritten to this model (content-root unification
  marked rejected).
- Memory: `vocabulary-is-content-not-anchors.md` records the corrected model + the three wrong models.
- Sources for ILI/CILI: globalwordnet/cili README; "CILI: the Collaborative Interlingual Index" (GWC 2016);
  `wn` interlingual-queries docs.
