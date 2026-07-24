<!-- CORRECTION 2026-07-23: the KNOWN DEBT section's "witness_precedes_chain is
     still a per-bigram plpgsql loop" is STALE — it was rewritten to one batched
     consensus_upsert deposit through the ingest spine (GH #428, CLOSED). -->
<!-- DRAINED 2026-07-20 — this doc is no longer a tracker.
     Phases A-F map 1:1 onto GitHub issues: A done+verified; B #358; C #359;
     D #360; E blocked on B (#358) with its highway half re-verified live
     (laplace_highway_ready()=t, 2.11M masked entities); F #361.
     Also drained: converse_walk RBAR (superseded by native steered_walk.c),
     walker duplication #354, witness_precedes_chain #428 (closed),
     starved free-form walk #361 + reseed #413.
     Open work lives in GitHub issues. Keep this file for the design rationale only. -->

# 22 — Conversational Engine: remaining architecture plan

Goal: the substrate converses like a human — deterministic, grounded, language-agnostic —
using the invention's own machinery (bands/highway, ILI, frames, trajectories, ingest),
not English regex/stoplists or side-tables.

Functions live: `converse` (structured narrative), `converse_walk` (free-form fused walk),
`chat` (intent-routed + relational reasoning + multi-turn). All on hart-server; persisted
in `functions/converse/*.sql.in`, registered in both manifests.

## Phase A — Band gating  [DONE, verified]
All three functions gate relations by `relation_highway_band(type_id)=band`
(def=1, tax=2, part=4, caus=5, assoc=7). No `relation_type_id('IS_A')` names remain.

## Phase B — Language-agnostic INTENT via frames  [SQL, now]
Replace chat's English intent regex with frame evocation:
prompt word → evoked frame (word→frame is a taxonomic-band edge; verified `cause`→`Causation`,
VerbNet 27.x) → band via a governed FRAME→BAND map (Causation→causal, Part_whole→partitive,
Type/Instance→taxonomic, Similarity→equivalence, Antonymy→oppositional). The frame layer IS
the language-agnostic semantic layer (frames unify across languages); frame→band is governance
like relation→band. Deliverable: `intent_band(prompt)` returning the salience band asked for.
Gate: coverage of the core intents must beat the regex, else keep regex for uncovered intents.

## Phase C — Language-agnostic TOPIC resolution  [SQL, now]
Replace the English function-word stoplist. A topic is a NOUN/ILI concept in argument
position. Two signals, both in-substrate:
 1. POS-in-context via `pos_class_transitions`/`pos_transition_plane` (a POS bigram model from
    trajectories) — tag the prompt; "are" as copula tags verb, excluded; homographs resolved
    by context, not a word list. (`vocab_dominant_pos` alone fails: fear=verb, part=noun.)
 2. content-band mass: topic = concept with most def/tax/part subject-edges; intent words
    (part/cause) are consumed by Phase B's frame lookup, not treated as topics.

## Phase D — Session as a TRAJECTORY entity (chess-game parity)  [needs ingest/writer spine]
A turn (prompt→response) = a move = a content-addressed entity; a session = the game = a
trajectory whose ordered constituents are the turns; ingested via the same spine as documents
and games (SubstrateChangeBuilder → NpgsqlSubstrateWriter). The `session_topics` unlogged table
is the current stopgap. Memory becomes the session trajectory IN the substrate — walkable,
content-addressed — and prompts/responses become first-class witnessed content (closes the
Gödel loop: evaluation IS ingestion). This is C#/ingest, not SQL; the SQL side already unpacks
trajectories (converse_walk uses `trajectory_unpacked_points`), so read-side is ready.

## Phase E — Native highway fast-path  [PARTIALLY DONE 2026-07-18, PR #349]
The native bitmask pre-gate itself is now BUILT and used: `walk_branches` (generate_walk.c)
takes `p_intent_mask bytea`, ANDs it natively against `entities.highway_mask` in the beam
candidate loop (`highway_table_mask_and`/`_any`, no perfcache dependency for the gate --
`entities.highway_mask` is a plain DB column, populated at consensus-fold write time per
`project_ucd_perfcache_law` memory). What's NOT done: (a) nothing computes a real
`intent_band(prompt)` yet to feed that mask -- Phase B below is still open, so no live caller
actually supplies a non-null `p_intent_mask` today; (b) this session did not re-verify
hart-server's specific highway-perfcache-loaded / highway_mask-backfilled state described
above -- re-check live (`SELECT laplace_highway_ready()`, `SELECT count(*) FILTER (WHERE
highway_mask IS NOT NULL) FROM entities`) before assuming either claim still holds on that
host specifically (hart-desktop was separately backfilled per memory, hart-server was not,
as of the date that memory was written).

## Phase F — Free-form FLUENCY lift (§3C)  [SQL + C, STILL OPEN]
Beyond trigram/gloss: consume `rd` as sampling temperature, S³ angular + trajectory-ordinal
continuity + hilbert locality as beam terms, witness mass as evidence; and a denser sequential
corpus (example sentences now; the dense PRECEDES layer once the sentence corpora seed). This
turns recombined-glosses into genuine sentence generation.
NOTE 2026-07-18: doc 15 Phase 3C (PR #349) built angular-distance and hilbert-band beam terms,
but for `generate_walk.c`'s graph-fact walker (`walk_branches`), NOT for `converse_walk`'s
free-form token walk (`steered_walk.c`) or `walk_text`'s n-gram walk (`trajectory_generate.c`)
-- those are the engines Phase F is actually about. rd-as-sampling-temperature is also still
unbuilt for either of those. Phase F remains fully open; PR #349 is prior art / a template for
the geometry-term plumbing, not an implementation of this phase.

## Order
A done. B, C now (SQL, verifiable). D, E, F need the ingest spine / perfcache build tooling /
deeper C — scoped with concrete entry points, done where SQL-reachable, not faked.

## Persistence + cleanup state (2026-07-12)
- 4 functions persisted as functions/converse/{converse,converse_walk,chat,witness_precedes_chain}.sql.in
  and registered in manifest.install (179-182) + manifest.upgrade (178-181). They rebuild via
  build-extensions; the live hart-server copies were deployed by direct CREATE OR REPLACE and match
  the repo files. Nothing else on hart-server was modified (parse_ask/resolve/recall_route/recall_*
  are USED, not changed).
- Test residue cleaned: 31 test session_topics rows deleted (unlogged table). Loop-close testing left
  a handful of PRECEDES witnesses under UserPrompt/Response sources (e.g. such->as +2, river->flows) --
  small, outranked by design, part of the self-witnessing lane; not force-removed (no clean per-attestation
  delete without the refute path; removing risks the fold).

## KNOWN DEBT (STALE -- see resolution below): converse_walk plpgsql RBAR
[SUPERSEDED 2026-07-14, commit 0f326432 "perf: converse_walk WALK goes native (steered_walk.c);
GATHER set-based" -- verified live this session (2026-07-17 research pass), two days AFTER this
doc's "Persistence + cleanup state (2026-07-12)" section was written. The plpgsql body that
remains in converse_walk.sql.in is ONLY ORIENT (topic-synset resolution) and GATHER (a set-based
SQL CTE chain) -- no loops, no `generate_subscripts`. The WALK itself now calls a native C kernel,
`steered_walk_raw` -> `pg_laplace_steered_walk` (steered_walk.c) -- NOT `walk_continuations` as
this doc's "CORRECT FIX" originally proposed. Instead of extending walk_continuations, a SEPARATE,
purpose-built native engine was written (trigram/bigram over a caller-built topic-weighted stream,
LCG sampling, no suffix array). That architectural duplication is real and un-fixed: `steered_walk.c`
and `trajectory_generate.c`'s suffix-array engine are two independent native n-gram walkers with no
shared code -- worth its own GH issue if consolidation is ever wanted, but the RBAR/performance
complaint this section originally raised is resolved.]
Still open, unaffected by the above: witness_precedes_chain's per-bigram loop is still a plain
plpgsql FOR loop (`extension/laplace_substrate/sql/functions/converse/witness_precedes_chain.sql.in`,
unchanged since the original 2026-07-12 commit, one `laplace_witness()` SPI call per bigram) --
should be a single batched native deposit. NOTE the whole free-form walk may still be starved on
whichever host this runs against: the literary documents (Odyssey/Ulysses/Moby Dick/Sherlock) that
would feed rich tier-3 prose were NOT seeded as of 2026-07-12 (Ishmael had 0 containing sentences);
re-verify corpus coverage live before assuming this is fixed.
