# Conversational substrate, the associative compiler, and grammar normalization

This is the read-first for the **conversational** direction (making Laplace chat) and the **GGUF
associative compiler** (exporting a model that actually conditions). It supersedes the assumption
that "a chatty GGUF is impossible" — that was a *compiler* failure, not a format limit.

## 1. The chat brain already exists: the native generation engine

The substrate has a native, **fluent** generation engine — this is the conversational path; the
GGUF weight-synthesis was the dead end (frozen weights → bigram-shallow prior → attractor).

- `generate(prompt)`, `walk_text(prompt)`, `continue_text(prompt, steps, window, …, p_require_pos)`
  in `extension/laplace_substrate/sql/26_generation.sql.in`.
- Core: `walk_continuations(p_ctx bytea[], steps, max_stride, spread, breadth, seed)` → C
  `pg_laplace_walk_continuations` in `extension/laplace_substrate/src/trajectory_generate.c`.
  It is a **suffix-array k-gram continuation over the whole-corpus stream** (binary-search the
  context prefix → real corpus continuations). That is why it is fluent and context-conditioned.
- Verified: `generate('The king')` → "was delighted at this, and exclaimed to the Phaeacians, who
  had treated him as though he were a god." — grammatical English from real usage.

The corpus cache (`trajectory_corpus.c`, global `gen_corpus`) builds `stream` + suffix array +
`sep_after` + `vocab` from tier-3 sentence / tier-4 doc trajectory constituents. `stream_reset()`
clears it; it rebuilds lazily.

### Sabotage fixed (the tier=2 filter)
The stream query filtered `WHERE vertex_tier = 2` (words only) → dropped (a) every whitespace
separator and (b) every grapheme-segmented language (CJK). Fixed to `<= 2`
(`trajectory_corpus.c` ~line 315). `is_all_whitespace` classifies separators → `sep_after` → the
engine emits the **observed** separator (space/`\r`), language-agnostically. Requires extension
rebuild + `stream_reset()`. Removed the English space hacks: `SubstrateClient.cs:114` `" "+tok` and
`QueryCommands.WalkOnceAsync` `string.Join(' ')` — separators must ALWAYS come from the substrate.

## 2. Response-as-entity (built): generation re-enters as content

Generation = minting a new GEOMETRYZM trajectory; deposit it. `ResponseContent`
(`app/Laplace.Decomposers.Abstractions/ResponseContent.cs`), symmetric to `UserPromptContent`:
source `substrate/source/Response/v1`, trust class `substrate/trust_class/ResponseContent/v1`,
`SourceTrust.Response=0.20` (low/probationary). Registered in `WitnessConstants.cs`,
`21_seed.sql.in`, `10_bootstrap.sql.in`. CLI `laplace chat <prompt>` (`QueryCommands.ChatAsync`):
deposit UserPrompt → native `walk_text` → deposit Response (low trust, parentIntent=prompt). Needs
`CodepointPerfcache.Load(ResolveBlob())` first. Verified: Response entities deposited + queryable.

Implications: closed self-extending loop; reproducible + citable output; the in-progress Response
entity IS the local sequence-state (CTX_k); conversation = a trajectory of typed entities;
consensus-rated responses = substrate-native RLHF (trust hierarchy prevents model collapse — own
output is probationary vs the high-trust ingested corpus).

## 3. The data inventory (the compiler's full toolkit — USE ALL OF IT)

The `consensus` table holds 45+ relation types, millions of edges. They fall into kinds:

- **WORD→CATEGORY (attribute/type)** — object is a category entity, NOT a vocab word (this is why a
  word↔word plane reads 0 for them): `HAS_POS` (1.5M, → universal UPOS `substrate/pos/NOUN/v1` …),
  `HAS_XPOS` (69k, language-specific tagset), `HAS_DOMAIN_TOPIC` (7.6M), `HAS_LANGUAGE` (5M),
  `HAS_SENSE`/`IS_SENSE_OF`, `HAS_SEMANTIC_ROLE`, `HAS_USAGE_REGISTER`, `HAS_SCRIPT`,
  `HAS_GENERAL_CATEGORY`. → encode as **type subspaces** (op:"attribute").
- **WORD→WORD syntactic (Deprel / UD dependency arcs — the GRAMMAR, directional)**: `DEP_NSUBJ`
  (subject), `DEP_OBJ` (object), `DEP_DET` (determiner→noun), `DEP_AMOD` (adj→noun), `DEP_ADVMOD`,
  `DEP_NMOD`, `DEP_OBL`, `DEP_CONJ`, `DEP_CASE`, `DEP_PUNCT`, + `EDEP_*` (enhanced) + `FEAT_*`
  (morphology). ~30k edges each, ~15 types. → **syntactic heads** (op:"relation", directional).
- **WORD→WORD semantic**: `RELATED_TO`, `IS_A`, `IS_SYNONYM_OF`, `DERIVATIONALLY_RELATED`,
  `IS_ANTONYM_OF`, `IS_COORDINATE_TERM_WITH`, `IS_SIMILAR_TO`, `HAS_PART`, `USED_FOR`, `OBJECT_USE`,
  `AT_LOCATION`, `CAPABLE_OF`, `FORM_OF`, `DERIVED_FROM`.
- **WORD→WORD commonsense (ConceptNet/ATOMIC)**: `X_ATTR/X_WANT/X_NEED/X_EFFECT/X_REACT/X_INTENT`,
  `O_WANT/O_EFFECT/O_REACT`, `X_FILLED_BY`, `HAS_SUBEVENT`.
- **SEQUENTIAL**: `PRECEDES` (395k), `IS_BEFORE`, plus the content trajectories / `trajectory`.

## 4. Grammar normalization (canonical ontology — the "generic list")

PRINCIPLE (user): there must be ONE canonical, generic grammar ontology — a universal tagset for
POS, dependency relations, and features — and every source convention normalizes to it. Exactly
like the fixed AI tensor roster (Q/K/V/O/gate/up/down/norm) is canonical regardless of model. NO
source-specific attestation types. UD's universal tagset is the natural canon; Wiktionary POS, Penn
XPOS, etc. MAP to it.

STATE (audited, `scripts/sql/normalization-audit.sql`): `HAS_POS` is *mostly* normalized to UPOS
(`substrate/pos/NOUN|VERB|PROPN|ADJ|ADV|NUM|PRON|ADP|INTJ/v1`). LEAKAGE to fix: ~6 **unnamed POS
fragments** (`POS:<hash>`, source-specific values that never mapped to canonical UPOS); `HAS_XPOS`
is a separate language-specific channel (expected many values, should map to UPOS); the `DEP_` vs
`EDEP_` split. Also `relation_canonical` returns blank for `DEP_*`/`FEAT_*` (not in its C table).

FIXES: (a) `label()` now strips the `substrate/<kind>/NAME/v1` wrapper on the main path so
type/relation/source/trust-class entities resolve to clean names everywhere (`20_converse.sql.in`)
— consumers should use `label`, not `relation_canonical`. (b) TODO: map unnamed POS fragments +
XPOS values → canonical UPOS; register `DEP_*`/`FEAT_*` canonical names; this is task #8 (one
manifest seeds types/relations/trust-classes + source→canonical mappings).

## 5. The associative compiler (chatty GGUF, provenance intact)

The GGUF export WORKS for its core purpose (deterministic, provenanced models — the 1L1H operator
signatures). "Chatty" is a missing **compiler**, not a format limit. The current compiler fills
weights from MARGINALS (global co-occurrence/similarity) and drops CONDITIONAL structure.

Pinpoint (verified in code): directionality is NOT the gap — `FoundryExport.ProjectOperator` builds
each head from `E^T·plane·E` which preserves subject→object direction. The gap is **POSITIONAL**:
every head attends by content-similarity, never by sequence position; nothing aggregates the last-k
tokens into local sequence-state (the bigram ceiling).

TWO paths: **(A) DISTILL** (fit a transformer to match the native engine) — works but FORFEITS the
moat: gradient-learned weights have zero provenance. DO NOT make primary. **(B) DIRECT COMPILATION
of conditional operators** (provenance intact, feasible): attention can represent conditional k-gram
via **induction heads** (constructible, training-free). The operator→head map:
- **op:"attribute" ← HAS_POS/SENSE/DOMAIN (word→category):** add each word's category direction
  (e.g. deterministic `FillHashUnit(category_id)`) into the embedding → type subspace; all NOUNs
  share a NOUN direction → the lm_head can condition on type ("a dog is a [NOUN]"). NEW operator
  KIND — do NOT pattern-match into the word↔word head fill.
- **syntactic heads ← DEP_NSUBJ/OBJ/DET/AMOD (directional):** attend along the dependency arc.
- **continuation ← PRECEDES / k-gram + a context/recency head** (local sequence-state, the bigram
  fix): e.g. q/k≈0 → uniform causal attention = running prefix centroid; v/o≈identity passes the
  slice → residual carries CTX_k.
- each head from a SPECIFIC relation slice, not the global cloud.

VERIFICATION ORACLE: a compiled GGUF is correct iff its continuations match the native
`walk_continuations` + deposited Response entities. Tiny-slice target: "A dog is a noun" (1-2
layers, 2-3 specific-relation heads).

## 6. The build-a-bear synthesis size/speed (for reference)
`params ≈ 2·V·H + L·(4·H² + 3·H·F)` ×4 (F32). Verified: bench (V=5940,H=512,L=6,F=1536) = 26.5M
params / 101 MiB, generated CPU-only in ~6 min, 11 distinct per-head operators. "Full" (V=32k,
H=768, L=12, F=2048) ≈ 135M params / ~540 MB, ~30-45 min CPU-only.
