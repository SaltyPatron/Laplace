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

## Phase E — Native highway fast-path  [needs perfcache generation + mask backfill]
On hart-server the highway perfcache blob is NOT loaded (GUC lists only the t0 codepoint blob)
and `entities.highway_mask` is NULL for 100% of entities. Build: regenerate
`laplace_highway_perfcache.bin`, set the GUC, backfill `entities.highway_mask` (per-entity
relation-participation bits). Then intent→`laplace_highway_band_mask` AND `entity.highway_mask`
is a native bitmask pre-gate (zero SQL) per doc 15 §3C-b, and the read walk consumes the
highway bits it currently ignores (§1.5).

## Phase F — Free-form FLUENCY lift (§3C)  [SQL + C]
Beyond trigram/gloss: consume `rd` as sampling temperature, S³ angular + trajectory-ordinal
continuity + hilbert locality as beam terms, witness mass as evidence; and a denser sequential
corpus (example sentences now; the dense PRECEDES layer once the sentence corpora seed). This
turns recombined-glosses into genuine sentence generation.

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

## KNOWN DEBT: converse_walk is plpgsql RBAR (violates "all math in C/C++/SPI")
converse_walk builds the corpus stream via plpgsql array appends and runs the trigram->bigram walk
with `generate_subscripts` FULL SCANS per step (O(n)/step). This is RBAR that native
`trajectory_generate.c::pg_laplace_walk_continuations` already does in C via a suffix array
(O(log n)/step). CORRECT FIX (needs the extension build, not psql): extend walk_continuations (or a
new C fn) to accept (a) a topic-RESTRICTED corpus = the containers_of(word) tier-3 sentence set, and
(b) per-token topic weights for steering, so the walk is native; converse_walk then becomes a thin SQL
wrapper like walk_text. Until then converse_walk is a PROTOTYPE demonstrating the tier-fan-out+steering
approach; chat's describe/what_is could default to converse() (pure SQL, no RBAR, always coherent)
instead of the RBAR walk. witness_precedes_chain's per-bigram loop should also be a single batched
native deposit. NOTE the whole free-form walk is starved on hart-server anyway: the literary documents
(Odyssey/Ulysses/Moby Dick/Sherlock) that would feed rich tier-3 prose are NOT seeded here (Ishmael has
0 containing sentences); the corpus is CILI/WordNet/FrameNet glosses, so the walk recombines definitions.
