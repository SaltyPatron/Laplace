## Bucket: X2_substrate_sql

SQL substrate surface = `extension/laplace_substrate/sql/*.sql.in`. Per invariant 5 SQL is a THIN
orchestrator; heavy compute must live in the native C engine, not PL/pgSQL.

### Files read (coverage)
- [x] 01_schema.sql.in — clean (single C version fn)
- [x] 02_entities.sql.in — clean schema
- [x] 03_physicalities.sql.in — clean (content-addressed PK, source-free geometry)
- [x] 04_attestations.sql.in — clean schema
- [x] 05_indexes.sql.in — clean
- [x] 06_glicko2.sql.in — C aggregate bindings; clean
- [x] 07_cascade.sql.in — C binding; clean
- [x] 08_sp_trajectory_ops.sql.in — thin C wrappers; clean
- [x] 09_brin_tier_ops.sql.in — clean
- [x] 10_bootstrap.sql.in — **tier-as-kind (VOCAB_TIER=5); invented namespace**
- [x] 11_entities_exist_bitmap.sql.in — C bindings; clean (the correct top-down dedup probe)
- [x] 12_consensus_schema.sql.in — clean (eff_mu index correct)
- [x] 13_mu_law.sql.in — eff_mu correct; **duplicate eff_mu impls (SQL + C)**
- [x] 14_period_fold.sql.in — **catch-up-drain fold + multiple forked fold lanes + heavy Glicko orchestration in SQL**
- [x] 15_readback.sql.in — consensus-traversing reads; clean
- [x] 16_inspect.sql.in — consensus-traversing reads; clean
- [x] 17_consensus_reads.sql.in — consensus-traversing reads; clean
- [x] 18_ops_surface.sql.in — **invented namespace in relation_type_id/source_id; mislabeled vocab metric**
- [x] 19_relation_law.sql.in — C bindings; clean
- [x] 20_converse.sql.in — reads OK; **label() regex-unwraps invented namespace**
- [x] 21_seed.sql.in — **invented `substrate/...` namespace for ALL anchors (POS/relations/trust/iso639)**
- [x] 22_cascade_surface.sql.in — clean
- [x] 23_structural_surface.sql.in — clean
- [x] 24_identity_health.sql.in — **codifies tier 5 = Vocabulary (tier-as-kind blessed)**
- [x] 25_intent_preflight.sql.in — C binding; clean
- [x] 26_generation.sql.in — **trajectory_pairs backfill anti-pattern; bigram generators; continue_text stub**
- [x] 27_apply_batch.sql.in — CLEAN (pure C binding, no PL/pgSQL, no ON CONFLICT)
- [x] generated/21_seed_pos.sql.in — invented `substrate/pos/.../v1` (not UPOS canonical)
- [x] generated/21_seed_relation_types.sql.in — invented `substrate/type/.../v1`
- [x] inference/attention.sql.in — consensus traversal; clean
- [x] inference/language_coverage.sql.in — clean
- [x] inference/synonyms.sql.in — emergent synset traversal; clean
- [x] inference/synset_members.sql.in — emergent synset traversal; clean
- [x] inference/translate_to.sql.in — emergent traversal; clean
- [x] inference/translations.sql.in — emergent traversal; clean
- [x] laplace_substrate.sql.in — include driver; clean
- [x] laplace_substrate_upgrade.sql.in — include driver; clean
- [x] sqldefines.h.in — macros; clean
- [x] uninstall_laplace_substrate.sql.in — **empty file (one blank line) — no-op uninstaller**

---

### Findings

#### F1 — Tier used as KIND: `VOCAB_TIER = 5` (invariant 3)
FILE: 10_bootstrap.sql.in:50, and lines 58-151 (all bootstrap INSERTs use VOCAB_TIER)
SEVERITY: HIGH — CATEGORY: invention-violation
CLAIM: Every meta-vocabulary entity (Type, RelationType, PhysicalityType, Source, the trust classes,
the text-ladder types Codepoint/Grapheme/Word/Sentence/Document) is stamped `tier = 5` via the
constant `VOCAB_TIER CONSTANT smallint := 5`. Tier is supposed to be compositional depth ONLY
(emergent, `tier = max(child)+1`); kind belongs in `type_id`/physicality/trust. Stamping a fixed
tier=5 to mean "this is vocabulary" is exactly the `EntityTier.Vocabulary = 5` violation called out
in CLAUDE.md §2 and the `vocabulary-is-content-not-anchors` memory.
VERIFIED: read the constant decl and every `INSERT INTO entities ... VOCAB_TIER ...` block.
CONFIDENCE: high.

#### F2 — Tier-as-kind is codified/blessed in the health check
FILE: 24_identity_health.sql.in:18-21
SEVERITY: HIGH — CATEGORY: invention-violation
CLAIM: `identity_law_violations()` declares tier legal only when `tier IN (0,1,2,3,4,5)` with the
inline comment `-- 5 = Vocabulary (system/bootstrap entities; see EntityTier.cs)`. The integrity
checker therefore *enforces* the tier-5-as-kind convention rather than flagging it. So the violation
in F1 is structurally locked in: the law-violation report would never catch it. (Cross-checks the C#
`EntityTier.cs` reference — same enum lives in the app layer.)
VERIFIED: read function body + comment.
CONFIDENCE: high.

#### F3 — Invented `substrate/type/X/v1` namespace for every convergence anchor (invariant 6)
FILE: 18_ops_surface.sql.in:1-4 (`relation_type_id`), 7-10 (`source_id`); 21_seed.sql.in:1-303;
generated/21_seed_pos.sql.in:4-20; generated/21_seed_relation_types.sql.in:4-177;
10_bootstrap.sql.in:53-133; 24_identity_health.sql.in:31-35
SEVERITY: HIGH — CATEGORY: invention-violation
CLAIM: Invariant 6 / CLAUDE.md §1 say concepts must anchor on REAL external ids (synsets→ILI,
languages→ISO 639, POS→UPOS, relation types→GWN/ConceptNet inventory name) — "never an invented
`substrate/type/X/v1` namespace." The entire identity layer does the forbidden thing:
`relation_type_id(p_name)` builds `'substrate/type/' || p_name || '/v1'` then BLAKE3s it; POS are
seeded as `substrate/pos/ADJ/v1` (not the UPOS string `ADJ`); ISO-639 scopes/types as
`substrate/iso639/scope/I/v1`; trust classes, sources, and 170+ relation types all as
`substrate/...` strings. This is the "convergence index is corrupt" condition described in the
`convergence-index-the-backbone` memory — identity keyed on an invented namespace instead of the
real interchange id, so cross-source/cross-version convergence cannot key on the highway id.
VERIFIED: read the string-builder fns and the full seed VALUES lists.
CONFIDENCE: high (this is the design as written; whether it's "intended legacy" is a product call,
but it contradicts the stated invariant).

#### F4 — Consensus is built by a separate "catch-up drain" fold, not inline (invariant 4)
FILE: 14_period_fold.sql.in (whole file: `materialize_period_consensus` 828-851,
`finish_consensus_fold` 604-720, `finish_consensus_fold_steps` 727-826, `consensus_fold_swap`
474-528, `walk_fold_prepare/finalize` 540-602)
SEVERITY: HIGH — CATEGORY: invention-violation / altitude
CLAIM: The Glicko-2 consensus ratings are produced by a batch "terminal/period fold" that drains
`consensus_period_staging_*` (or walk-staging) tables, re-reads the existing `consensus` rows as
seeds, recomputes ratings, writes a `consensus_next` table and ALTER-TABLE-renames it over
`consensus` (a full rebuild + swap). The `apply_batch` write path (27) only folds attestation
`observation_count`; it does NOT update consensus ratings. This is precisely the "separate run-the-
fold-to-catch-up drain" anti-pattern that invariant 4 and the `consensus-folds-inline-not-drain`
memory forbid ("Glicko-2 updates inline in the one-call apply merge"). The fold is offline/batch,
not online/immediate.
VERIFIED: read 27 (returns only attestations_*; no consensus write), and the full 14 drain/swap
machinery. CAVEAT: confirming the inline path is truly absent requires reading `apply_batch.c`
(C bucket) — if apply_batch.c folds consensus inline, then 14 is redundant dead drain; either way
the SQL surface ships a drain.
CONFIDENCE: med-high.

#### F5 — Multiple forked fold lanes (invariant 8 "converge, don't fork")
FILE: 14_period_fold.sql.in — `materialize_period_partition` (91-166) vs
`materialize_period_partition_fresh` (175-233); `consensus_fold_one_partition` (372-467) with
`lane = COALESCE(current_setting('laplace.fold_lane'),'engine')` selecting between the C
`consensus_fold_partition` (319) and an in-SQL `consensus_fold` aggregate (263-278); the walk lane
`consensus_fold_walks` (362); `finish_consensus_fold` (604) vs `finish_consensus_fold_steps` (727,
resumable) vs `walk_fold_prepare`/`walk_fold_finalize` (540/581).
SEVERITY: HIGH — CATEGORY: fork
CLAIM: At least five parallel implementations of "fold the staged journal into consensus" coexist,
flag-gated by `laplace.fold_lane` and by walk-vs-flat-vs-period table shapes. This is the
flag-gated-parallel-lanes disease invariant 8 names explicitly (it even lists "fold lanes").
VERIFIED: read each function and the lane switch.
CONFIDENCE: high.

#### F6 — Heavy Glicko/consensus compute orchestrated as dynamic SQL (invariant 5 altitude)
FILE: 14_period_fold.sql.in:110-158 (materialize_period_partition), 189-225
(materialize_period_partition_fresh), 427-463 (consensus_fold_one_partition builds a UNION-ALL
seed+staged query and runs the `consensus_fold` ordered-set aggregate over it via `EXECUTE`)
SEVERITY: MEDIUM — CATEGORY: altitude
CLAIM: The consensus rebuild — group/merge, seed re-read, ordered Glicko fold, partition routing —
is assembled as PL/pgSQL `format()`/`EXECUTE` query strings. The per-matchup arithmetic is in C
(the sfunc), but the fold's orchestration, the seeded-rebuild join, and the partition fan-out are
SQL. Invariant 5 puts "Glicko/consensus" compute in `laplace_dynamics`; the `lane='engine'`
C path (`consensus_fold_partition`) is the correct home — the coexisting SQL lane is the misplaced
altitude. (Injection-safe: `format` args are `%I` pg_class names and `%s` integers only.)
CONFIDENCE: med.

#### F7 — `trajectory_pairs` materialized backfill (invariant 4 anti-pattern, named)
FILE: 26_generation.sql.in:113-166 (`trajectory_pairs` table + `trajectory_pairs_ensure` TRUNCATE-
and-rebuild from `trajectory_cooccurrence_by_stride`), used by `trajectory_pairs_plane` (171)
SEVERITY: HIGH — CATEGORY: invention-violation
CLAIM: Invariant 4 / `consensus-folds-inline-not-drain` memory call out "trajectory_pairs backfills"
as an invented anti-pattern by name. The code materializes a `trajectory_pairs` table by scanning
all content trajectories (`cooccurrence_scan`) and TRUNCATE+re-INSERTing whenever the physicality
count/clock changes (`trajectory_pairs_ensure`) — an offline backfill of a derived co-occurrence
table, exactly what the invariant prohibits.
VERIFIED: read the table DDL, the ensure fn (probe-based cache invalidation + TRUNCATE/INSERT), and
the plane consumer.
CONFIDENCE: high.

#### F8 — Bigram generation surface in SQL (known-incoherent foundry path)
FILE: 26_generation.sql.in — `cooccurrence_scan`/`trajectory_cooccurrence` (65-103),
`grapheme_order` (533-574), `word_order` (584-609), `corpus_word_vocab` (616-639),
`foundry_vocab_crawl` (715-857, order1/order2 PRECEDES bigram walks)
SEVERITY: MEDIUM — CATEGORY: altitude / invention-violation
CLAIM: A large bigram/n-gram generator is implemented as multi-CTE SQL over `PRECEDES` consensus
edges and trajectory adjacency. The `iridescent-cooking-waterfall` plan and the
`foundry-synthesis-findings` memory record that the bigram generator is being RETIRED and that
exactly this `lm_head=single-stride bigram` path produces incoherent models. It is both heavy
compute misplaced in SQL and the disparaged generation lane. (Not asserting it's "dead" — flagging
that it's the bigram path slated for retirement and lives in SQL.)
VERIFIED: read each generator CTE chain.
CONFIDENCE: med.

#### F9 — `continue_text` silently ignores most of its parameters (silent scope-cut)
FILE: 26_generation.sql.in:918-928
SEVERITY: MEDIUM — CATEGORY: correctness / invention-violation (silent stub)
CLAIM: `continue_text(p_prompt, p_steps, p_window, p_spread, p_breadth, p_stop, p_boost,
p_require_pos)` accepts `p_stop`, `p_boost`, `p_require_pos` and just calls
`walk_text(p_prompt, p_steps, GREATEST(p_window,1), p_spread, p_breadth)` — `p_stop`, `p_boost`,
`p_require_pos` are dropped on the floor and the `mu` output column is hardcoded `NULL::numeric`. A
caller passing stop-words / a require-POS gate gets them silently ignored. Either implement them or
drop the parameters; the current shape is a silent MVP stub presenting a richer contract than it
honors.
VERIFIED: read the body — the three params appear only in the signature.
CONFIDENCE: high.

#### F10 — `substrate_counts` "vocabulary" metric filters tier 0, contradicting VOCAB_TIER=5
FILE: 18_ops_surface.sql.in:82-90
SEVERITY: MEDIUM — CATEGORY: correctness (prose/code disagreement)
CLAIM: The metric labeled `'entities/vocabulary (tier 0)'` counts `entities WHERE tier = 0 AND
type_id <> ALL(text-ladder types)`. But the actual vocabulary/meta entities are stamped `tier = 5`
(F1), not 0. So this metric does NOT count the vocabulary; it counts tier-0 entities that aren't
text-ladder types (essentially mislabeled), while the real tier-5 vocab is reported nowhere. The
label and the data model disagree.
VERIFIED: cross-read bootstrap (tier 5) vs this WHERE tier=0.
CONFIDENCE: high.

#### F11 — `label()` reconstructs meaning by regex-stripping the invented namespace
FILE: 20_converse.sql.in:39-58 (8 nested `regexp_replace` peeling `substrate/type|relation|trust_
class|source|entity/.../v1`, `substrate/pos/...`, `language:xxx`, `ud/feature/...`, ILI `i\d+`, etc.)
SEVERITY: LOW-MEDIUM — CATEGORY: altitude / invention-violation (downstream symptom of F3)
CLAIM: Because identity is the invented `substrate/...` string (F3), the read side must regex-unwrap
that namespace at render time to produce a human label. This is brittle display logic in SQL and a
direct symptom of the corrupt convergence index — if anchors were real external ids with proper
HAS_NAME_ALIAS/HAS_DEFINITION links, this regex stack would be unnecessary.
VERIFIED: read the nested regex chain.
CONFIDENCE: med.

#### F12 — Duplicate eff_mu implementations (minor fork)
FILE: 13_mu_law.sql.in:5-8 (`effective_mu` C) and 10-13 (`eff_mu` SQL `rating - 2*rd`)
SEVERITY: LOW — CATEGORY: fork
CLAIM: Two functions compute the conservative mean: `effective_mu` (C) and `eff_mu` (SQL). All reads
use the SQL `eff_mu`. The SQL form matches the charter (`eff_mu = rating - 2*rd`, fixed-point ×1e9
— verified: `glicko2_initial_rd()=350e9`, `volatility=60e6`, `tau=500e6`, all ×1e9-scaled, correct).
Having both is a small redundancy; if `effective_mu` (C) ever diverges from `rating-2*rd` the two
disagree silently. Low risk, worth consolidating.
VERIFIED: read both defs and the fixed-point constants.
CONFIDENCE: high (eff_mu correctness); the divergence risk is low.

#### F13 — ON CONFLICT in the consensus rebuild (invariant 7 nuance)
FILE: 14_period_fold.sql.in:152-158 (materialize_period_partition `ON CONFLICT (id) DO UPDATE`),
224 (`_fresh` `DO NOTHING`)
SEVERITY: LOW — CATEGORY: invention-violation (scoped)
CLAIM: Invariant 7 forbids `ON CONFLICT`/per-row anti-join on the *ingest content frontier*. The
ingest path (27 apply_batch) is correctly clean. These ON CONFLICTs are on the `consensus` rebuild,
which is a different table — but they exist only because the fold is a batch rebuild (F4); an inline
fold would update consensus deterministically by key without conflict races. Noting for completeness;
not the frontier-load violation.
VERIFIED: read both INSERT...ON CONFLICT blocks.
CONFIDENCE: high (presence); med (severity framing).

#### F14 — `uninstall_laplace_substrate.sql.in` is empty
FILE: uninstall_laplace_substrate.sql.in:1 (single blank line)
SEVERITY: LOW — CATEGORY: dead-code / correctness
CLAIM: The uninstaller is a no-op. `DROP EXTENSION` will rely on PG's dependency tracking, but any
objects not registered as extension members (e.g. the dynamically-created `consensus_next`,
`consensus_*_staging_*`, `trajectory_pairs`, `session_topics` UNLOGGED tables) won't be cleaned up.
Either intentional (PG handles members) or an unfinished stub — flagging so it isn't mistaken for a
real teardown.
CONFIDENCE: high (it's empty); med (whether it matters).

#### INFO — clean / good
- 27_apply_batch.sql.in is the canonical thin write path: pure `LANGUAGE C` binding to
  `pg_laplace_apply_batch`, zero PL/pgSQL, no ON CONFLICT, no anti-join, one round-trip. Matches
  invariant 5/7 exactly. (Its doc comment is accurate, not editorializing.)
- 11_entities_exist_bitmap.sql.in `content_descent_bitmap` is the correct top-down O(tier) dedup
  probe (present trunk ⟹ skip subtree) — invariant 7 done right, in C.
- The reads (15/16/17/18/20 and all inference/*) genuinely TRAVERSE the consensus field by
  `eff_mu(rating,rd)` and synset co-membership (synonyms/translations as emergent structure), not
  string matching. `attention.sql` uses Glicko eff_mu as attention weight with S³ geodesic as
  annotation. This half of the surface is sound.
- 26_generation.sql.in:21-47 actively DROPs retired forked lanes (`content_pairs`/`content_index`/
  `rebuild_content_index*`/`cooccurrence_scan` old sig) — correct convergence behavior.
- No SQL-injection found: all dynamic SQL (`format`/`EXECUTE` in 14/19/20/23) uses `%I` for
  pg_class-derived identifiers and `%s` for integers; `api()`/`register_canonical()` are
  parameterized. `consensus_id` is content-addressed on (subject,type,object) with no
  source/position — invariant 1 respected.

---

### Bucket summary
- HIGH: 5 (F1 tier-as-kind, F2 tier-as-kind blessed, F3 invented namespace, F4 drain fold, F5 forked
  fold lanes, F7 trajectory_pairs backfill) — note this is 6 entries; count by severity below.
- Counts: HIGH = 6 (F1,F2,F3,F4,F5,F7); MEDIUM = 4 (F6,F8,F9,F10); LOW/LOW-MED = 4 (F11,F12,F13,F14);
  INFO = clean set.
- Single worst issue: **F4 — the Glicko-2 consensus fold is an offline batch DRAIN + table swap
  (14_period_fold), not the inline online fold the invention requires (invariant 4).** It is
  compounded by F5 (five forked fold lanes) and F6 (the fold orchestrated as dynamic SQL instead of
  in `laplace_dynamics`). Tied in importance is the pair F1/F2 (tier 5 = Vocabulary is both shipped
  in the bootstrap and locked in by the integrity checker — the canonical tier-as-kind violation)
  and F3 (the convergence index keyed on the forbidden invented `substrate/.../v1` namespace).
