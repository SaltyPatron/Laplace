# Laplace — Completion Plan: current state → finish line

Written 2026-08-02 from a measured, two-agent, code-level audit. This is the
single document the owner asked for: how the system should work and why, what
is settled, what is broken or missing with the code that proves it, and the
dependency-ordered plan to done. It contains **no frozen record counts** —
counts rot and then get misused as gates. Where evidence is needed, the
*command* is given; run it, don't quote it.

Maintenance law for this file: update it when a phase item completes; never
add a status number to it; if a claim here disagrees with the running system,
the system wins and this file is the thing to fix.

---

## 0. Standard of evidence

1. **The running system outranks prose, including this file and every code
   comment.** Comments are prior sessions' output; two were proven stale this
   session alone (`chat.sql.in:429-436` claims fixes are unmerged that landed
   in `main` on 2026-07-28; the alphabetical-bits law in the docs was repealed
   by ADR 0001).
2. **Identity is the existence test.** Type, tier, and source filters narrow
   reads; they never prove absence. "Which sources are seeded?" is not
   "is this content here?" — hash the content and probe it
   (`containers_of`, `entities` by id). This session's two worst errors were
   both this mistake.
3. **Held-out verification is mandatory.** A fix verified only on the prompt
   it was developed against is a defect (`converse_tiered` shipped that way
   and hung on every common topic). Fresh probes, misses reported first.
4. **Distributions stay distributions.** A→[…] until the final collapse.
   Argmaxing a bias distribution to one sense zeroed `infer()`'s signal the
   night it shipped.
5. **Every recurring read graduates to an installed surface.** Ad-hoc SQL is
   an operator diagnostic, never a deliverable, never a client path.

Standing verification commands (run, don't trust):
- live surface: `SELECT * FROM laplace.api('<substring>')`
- what has ever entered this instance: `SELECT * FROM ingest_run_journal`
- health: `SELECT * FROM substrate_health()` / MCP `health`
- generated rosters/counts: `docs/INVENTORY.md` (CI-gated; note its blind
  spot, Phase 4 item 9)

---

## 1. The finish line

The system is done when, over any seeded modality, an end user can hold a
multi-turn conversation through typed surfaces only, where every reply is:

- **generated** (composed from witnessed trajectories and rated edges, not a
  filled template),
- **steered** (each emission step scored against the elected concept
  frontier — attention at every token, not just at seeding),
- **grounded** (every claim traceable to witnesses; refuted edges avoided;
  unknown things abstained from rather than fabricated),
- **discourse-aware** (orientation reads the witnessed turn history, not one
  carried topic id),
- **measured** (a scored harness gates regressions on answer quality,
  continuation quality, and latency),

and where a new corpus enters through a finished lane (source identity,
names, typed edges, license) and a new *kind* of question is answered by
attesting vocabulary — not by an operator writing a new function per
question.

---

## 2. The architecture frame — how it should work and why

The owner's frame is the correct one and this plan adopts it: **an embedded
system.**

- **Hardware — the substrate. Done and correct.** Content-addressed AST
  merkle DAG with structural dedup (identical content is one id, by
  collision, no resolution step); tier ladder as the abstraction hierarchy
  (codepoint → word → sentence → document; position → game); geometry on S³
  with Hilbert ordering as the identity/order/serialization plane;
  trajectories as the lossless sequence lane (order lives in geometry, not
  in materialized adjacency edges — a deliberate Pillar-5 ruling);
  attestations as provenanced testimony with three-valued outcomes
  (refutation is first-class evidence); Glicko-2 in fixed point as the fold —
  every edge a rated player with its own uncertainty, trust an input to the
  math, the rating period the batch, ingest completion = fold completion.
  Spec 37 §9 rules the whole layer out of scope for change, and this
  session's audit (structural claims verified at code level, fresh-data
  ingest exercised end to end) supports that ruling.

- **ROM/firmware — the perfcache blobs and the native operation set.** Hot
  invariants get compiled into mmap'd blobs (spec 33); operations over the
  substrate are a small fixed instruction set (spec 37): RESOLVE, SENSE,
  ELECT, WEIGHT, SCAN, SELECT, TRAVERSE, SEQUENCE, REALIZE, WITNESS — one
  implementation each, variants as modes, executing in one canonical order
  (S0–S10), hash space until the single REALIZE point. **This layer is
  drafted, partially implemented, and is where the system actually is on the
  road to done.**

- **Programs — shapes and conversation.** Read shapes are *data* (a shape
  table compiling to opcode programs), not strcmp ladders. A conversation is
  a loop of programs: ELECT → plan → SCAN/TRAVERSE/SEQUENCE → steer → sample
  → REALIZE → WITNESS → next turn conditions on everything so far. Questions
  route themselves because prompt tokens *name* relations through attested
  vocabulary (the `rel_mass` mechanism), not because English is parsed.

- **Learning — reinforcement from every angle.** The fold is the learning
  rule. A witnessed outcome credits every participating entity at every tier
  simultaneously, each with its own rating and uncertainty — per-parameter
  credit assignment without backprop. The transformer mapping is exact where
  it matters: consensus is the FFN key-value memory made explicit and
  editable; steer-mass against a frontier is QK^T from rated evidence;
  Gumbel sampling over steered weights is softmax-with-temperature; share
  normalization (÷ own total mass) is the norm layer. The two honest holes
  against a trained transformer: **query formation and mixing weights are
  hand-set constants, and nothing yet folds the *retrieval operators
  themselves* as rated players.** The design-consistent completion (Phase 5)
  is the owner's own insight: the feedback lane already turns confirm/refute
  into rated games — extend the arena so the operator configuration that
  produced a confirmed answer is itself a player that wins.

- **The one failure mode that produced the last 14 months of pain**, named
  by spec 37 §0 and confirmed twice by this session's research: *a canonical
  implementation gets written; the orchestrator that should call it is never
  rewired; both survive; they drift.* The complete steered generation loop
  exists and chat does not call it. The sentence-tier composer was
  perf-fixed in `main` and stays unwired behind a stale comment. The topic
  bias parameter has zero callers. The eval prompt set has no runner. The
  document format router has no production caller. **This is not sabotage;
  it is an ungated failure mode — and G4 (dead-canonical gate) is its
  mechanical kill.** Gates precede features in this plan for exactly that
  reason.

---

## 3. What is settled (verify, then build on it)

- Identity, fold, partitions, geometry, ingest spine, decomposer purity, the
  chess read surface, the model/foundry lane's structure — audited against
  the code and exercised live on fresh data this session (a full PGN archive
  ingested to folded, queryable consensus in minutes; book games and modern
  blitz rated in one arena; think-time-vs-quality structure emerging from
  the fold unqueried).
- The trajectory lane holds the sequence corpus (verify:
  `SELECT count(*) FROM physicalities WHERE trajectory IS NOT NULL`) and
  `trajectory_continuations` predicts witnessed continuations (verify:
  `SELECT realize(object_id), weight FROM trajectory_continuations(ARRAY[word_id('slowly'), word_id('moving')], 6)`).
- The forward pass exists as an installed, inspectable surface:
  `laplace.infer(prompt, limit)` — election as attention, uncollapsed
  candidate distribution, all-senses bias reweighting, one realize.
  Single-step; its limits are stated in its own header.
- Topic election for silent prompts is fixed and held-out-verified
  (ord-recency as the last resort where the fold is silent), with the two
  refuted premises recorded in the commit history.
- The steered generation loop — propose (`trajectory_continuations`),
  steer (`steer_candidates`), sample (Gumbel) — is **implemented and
  reachable** via `walk_text`/`generate`/`walk_continuations`
  (`trajectory_generate.c`). It is not wired into chat (Phase 2).
- The document lane ingests at scale, content-only **by design** (Pillar 3a)
  with a CI gate enforcing that design. Finishing it is specified work
  (Phase 4), not archaeology.

---

## 4. Gap register

Each entry: what/why/where. No counts. Citations are to code that proves the
gap, gathered 2026-08-02.

**R0 — The database cannot see its own call graph. This is the root of R8
and of the drift failure mode in §2, and it is why every gate below has to
be a grep.**
Measured 2026-08-02: of the functions in the `laplace` schema — 247
`LANGUAGE sql`, 32 plpgsql, 77 C — **zero** have parsed bodies
(`prosqlbody IS NULL` for all of them), because every SQL body is a quoted
string PostgreSQL treats as opaque text. The entire schema carries 9
recorded function dependency edges, all from its 9 views. Consequences,
each observed in this audit rather than hypothesized: a canonical can hold
zero callers indefinitely and nothing objects (five live instances found,
every one by grep, because grep was the only instrument available); a
signature change succeeds precisely because nothing *can* block it, so a
missed caller fails on a user's prompt instead of at install; spec 37 §6's
same-signature install-order arbitration is unenforceable by construction.
**The destination is a substrate read, not a catalog query and never a
grep.** A grep gate string-matches source, which breaks L1 — rendering and
text comparison inside the very check meant to enforce the laws — and it
cannot see dynamic dispatch. A `pg_depend` query is better but still
partial: it sees only *installed* objects, never the `.sql.in` templates
that are the schema of record, nor C, nor C#. The substrate's own answer is
the correct one: **ingest the source and read the graph.** `CALLS`
(`relation_types.toml:124`) and `REFERENCES` (`:1134`) are already governed
and `tree_sitter_sql` is already registered
(`grammar_registry.c:74,132`); a dead canonical is then a function id with
zero incoming `CALLS` edges — one indexed read in id space, perfcacheable
per spec 33, rendering only the final list of dead names. It also makes a
false positive *refutable*, folding like any other testimony.

That path is measured blocked and is GH #765: `ingest code` over the
converse family accepted **zero** files, because `Path.GetExtension`
returns `.in` for `chat.sql.in` and `in` is not in the grammar map
(`CodeDecomposer.cs:60-64`) — the substrate cannot read its own schema of
record; and the same files renamed `*.sql` produced content DAG plus
trajectories with **zero** attestations, so no `CALLS`/`DEFINES`/
`REFERENCES` exist to query even once the file is accepted.

`BEGIN ATOMIC` (GH #764) remains worth doing as the *in-database
complement* — it makes PostgreSQL enforce what PostgreSQL can see, at
install time, for free. Blockers there are known and real: strict install
ordering with no forward references (which turns the §6
`senses → bubble_up → lexical_peers → senses` cycle into a hard failure —
break it first), creation-time name binding, and the `eff_mu` inlining law,
which must be verified with `EXPLAIN` rather than assumed to survive.

**Phase 1.** Sequence: land G4 as a grep with a shrink-only allowlist to
stop the bleeding *now*, explicitly as scaffolding; fix the code lane
(#765); replace the grep with the substrate read; adopt `BEGIN ATOMIC`
(#764) alongside. Do not mistake the scaffolding for the destination —
that mistake is the failure mode this whole document is written against.

**R1 — Chat's generation lane is the unsteered one.**
`chat(shape='walk')` → `converse_walk` → `steered_walk_raw`: seeded-random
trigram→bigram backoff over a topic-restricted stream with constant
provenance weights (50/40/0 at `converse_walk.sql.in:103,107,171`), a walker
with no SPI and no topic id (`steered_walk.c:8`), a visited-set that forces
cross-sentence splicing, `p_seed` NULL → deterministic word salad per prompt
(`chat.sql.in:410`), no `p_lang` threading. The steered lane
(`trajectory_generate.c:242-448`) is complete and disjoint. Two installed
bridges are unwired: `converse_compose` (same walker contract with
frontier-derived weights; gated only on a measurement nobody ran —
`converse_compose.sql.in:37-40`) and `converse_tiered` (all three hangs
fixed by `ceef97d` in `main`; unwired by the stale hotfix comment at
`chat.sql.in:429-436`).

**R2 — The tier-collision seam corrupts sense sets.**
Surfaces whose ids also carry a tier-0 Codepoint row union both lineages in
`senses()` — the letter-A sense inside the article's candidate set is the
deepest root of the glacier-class failures (spec 37 §8 step 2 amendment;
`.scratchpad/38 §12b`). Election-layer keys can only paper over it.

**R3 — Sense salience has no prior.**
On single-witness seeds `denote_mu` is constant, so within-token sense
election degrades to arbitrary keys; frame-resource mass biases toward verb
senses (measured: dog→chase, trumpet→proclaim, car→railway before the mass
tiebreak). Fixes: content-band mass comparator in `prompt_coherence.c`
(blocked on a band accessor — `laplace_relation_def_t` has no band field;
band lives behind the highway table) and attesting WordNet sense order at
ingest (`HAS_SENSE_RANK` — the prior the source ships and the decomposer
drops).

**R4 — The document lane borrows the conversational identity.**
`DocumentDecomposer.cs:9-13` stamps `UserPrompt` as source; content DAG +
trajectories only; per-file attestations are two markers
(`DocumentIngestAdapter.cs:58-82`); no names
(`Decomposer.cs:219` base never overridden), no titles (no `HAS_TITLE` in
the manifest), no license (bypasses `SourceVocabularyBootstrap`), invisible
to the generated inventory (`docs-inventory.py:83` scans the wrong project)
and to modality counts (GH #660). CI *enforces* content-only
(`decomposer-gates.json:45-51` + `decomposer-gate-check.py:91-114`) — the
gate must be amended to a bounded trunk-grain expectation or every fix fails
CI. Intake breadth: `*.txt` only; `.md` no-ops (GH #418); a malformed file
kills the run (GH #596); `DocumentRouter` is written, tested, uncalled.

**R5 — Questions do not route themselves yet.**
`rel_mass` reaches relations only through canonical-name fragments; natural
vocabulary ("opening" → `HAS_ECO`) is one attestation lane away, and
`infer()` receives `rel_type_id` from the election and ignores it. Whole
relation families (geographic/political) are absent from the manifest —
additions are append-only bits, no reseed (ADR 0001).

**R6 — No generation evaluation exists.**
`prompts_smoke.txt` has no runner; `EvalCommands` covers ingest fidelity
only; no regress test touches `converse_walk`/`steered_walk_raw`/
`steer_candidates`; nothing fails if chat emits word salad.
`scripts/verify-model-behavioral.py` is the closest template (hub-collapse,
echo-loop, flatness detectors).

**R7 — Discourse state is one scalar.**
`session_last_resolved` returns one id; the witnessed turn history
(deposited every turn by the frontends) is never read back into orientation;
the `prompt` column's only reader is a turn-depth counter
(`chat.sql.in:282-286`).

**R8 — ISA drift-and-duplication debt.**
Many weight formulas, edge scans, render ladders, shape declarations, turn
closers, mutexes (spec 37 §4 disposition table). The published `walk` shape
contract differs from chat's behavior (`query_shapes.sql.in:22` vs
`chat.sql.in:384,389`). Two same-signature bodies arbitrated by install
order (§6). `steer_candidates.covered` computed and never fetched; `kappa`
never passed; `walk_continuations`' "live frontier" is static
(`trajectory_generate.c:220,232` vs its own header).

**R9 — Ungated invariants.**
*Partial as of 2026-08-02 (see `docs/plan/CHECKPOINT_2026-08-02.md`).* The
five-elector key-order invariant is gated (`ElectorArchitectureGateTests`,
#771). G1/G3/G8 shrink-only ratchets run in the policy job (#772). G7 has
shell/cmd parity plus C# dispatch↔manifest (#775). `source_roster` excludes
relation-type bootstrap subjects in SQL (#773) — live ChessPgn/ChessOpenings
recheck still owed before calling R9 closed. Still ungated: G2, G4
(destination = substrate `CALLS` read after #765; grep may scaffold), G5,
G6 completion, G9, G10.

**R10 — Operational fragility.**
Pooled MCP connections die visibly at every postmaster bounce; CI runs green
against an empty database; the sync-extension symbol gate coin-flip is fixed
on this branch (buffered `nm`), the rest remain.

**R11 — Record corrections owed and partially delivered.**
Fixed on this branch: bits-law inversion (docs + CLAUDE.md), retired
trajectory tables, frozen prose counts replaced with INVENTORY pointers,
fold attribution, walk_branches/consensus_walk_edges, perfcache GUC names.
Still owed: `chat.sql.in:429-436` stale comment; `query_shapes` walk row;
`.scratchpad/38`-era claims superseded by this session's election work;
memory artifacts (a "217 sources" denominator exists nowhere in the repo).

---

## 5. The plan — dependency-ordered phases

Each item: **goal / why now / what to consider / where to look / done-when
(behavioral)**. Phases 1–2 are the leverage; nothing after them is blocked
on them except where marked.

### Phase 0 — Land this branch
Goal: `main`, the live box, and the record agree.
Why: the live extension is ahead of `main`; a `main` push reverts live chat.
Consider: prefer local verify → merge → one `main` CI run (do not burn a
redundant feature-branch `workflow_dispatch` as the gate). A push restarts
the service — check `ingest_run_journal`/`pg_stat_activity` first.
Done when: branch merged, live behavior identical post-deploy, glacier
probe answers glacier.

**Checkpoint 2026-08-02:** completion-axis PRs #771–#776 are on `main` and
CI-green. Live box is **not** agreed — orphan `UnicodeDecomposer`
`status=running` journal row after a cancelled foundation seed, thin
residue counts. Shared-writer COPY identity dedup (#776) is CI-proven, not
yet foundation-ladder-proven. Resume ops sequence:
`docs/plan/CHECKPOINT_2026-08-02.md` §2.

### Phase 1 — Gates and measurement before features
Goal: make the drift failure mode and the quality regressions mechanical
build failures.
Why first: every later phase's "done" is otherwise opinion; G4 alone would
have prevented most of R1/R8.
Items:
0. **R0 — make the call graph real, in the substrate** (GH #765, then
   #764). First unblock the code lane so the system can read its own
   source: compound extensions (`chat.sql.in` → `sql`) and SQL structural
   extraction (`DEFINES`/`CALLS`/`REFERENCES`), declared in
   `InitializeAsync` per the decomposer gate. Then G4 is an indexed read
   on `CALLS` in-degree, perfcached, rendering only its final answer —
   the form the read laws actually require. `BEGIN ATOMIC` (#764) lands
   alongside as the in-database complement for what PostgreSQL can see.
   A grep G4 may ship first as explicitly-labeled scaffolding with a
   shrink-only allowlist; it is not the destination. Ship it
   incrementally: verify `eff_mu` inlining survives
   before converting that family, break the §6 install cycle when reached,
   allowlist unconverted functions shrink-only. G4 can land as a grep in
   parallel and be reimplemented against `pg_depend` as coverage grows —
   do not block the gate on the migration.
   *Progress 2026-08-02:* compound-extension discovery landed (#774).
   Structural extraction still emits zero attestations — the W3 body remains.
1. **G4 dead-canonical gate** (zero-caller opcode entry points and
   supersession claims fail the build) — spec 37 §7. *Not built.*
2. **Elector-invariant gate** (five ORDER BY key lists pinned identical).
   *Done (#771).*
3. **Generation smoke harness**: wire `prompts_smoke.txt` (and a widened
   probe set incl. held-out singles like glacier/trumpet) through a runner
   modeled on `verify-model-behavioral.py` detectors (on-topic rate,
   echo/hub collapse, flatness) + per-surface latency budget; run in CI
   against a *seeded* fixture, not an empty DB. *Not started (#755) — next
   measurement workstream after ops unblock.*
4. **G1/G2/G6 literalism + parity gates** (one WEIGHT, no render-in-select,
   C/SQL weight parity) — cheap greps + one fixed-vector test.
   *Progress:* G1/G3/G8 ratchets (#772); G7 C# (#775). G2 and G6-complete
   remain.
Consider: gates land green by grandfathering current violations into an
explicit allowlist that only shrinks.
Done when: a PR that unwires a canonical, adds a per-row render, or degrades
the smoke score cannot merge.

### Phase 2 — Speak (wire the steered lane into chat)
Goal: chat's walk shape emits steered, seeded, language-pinned prose.
Why: the machinery exists; this is wiring plus the one measurement
`converse_compose` was gated on.
Items, in order:
1. Run the `converse_compose` measurement its header demands; compare
   against `converse_walk` on the smoke set.
2. Wire the winner into `chat(shape='walk')`; thread `p_seed` (session-derived,
   not NULL) and `p_lang`; delete the stale `chat.sql.in:429-436` comment and
   re-evaluate `converse_tiered` (fixed in `main` by `ceef97d`) for the
   describe path under a latency budget.
3. Fix the S7 loop's static frontier (`trajectory_generate.c` grows `ctx`
   but never `n_frontier`); pass `kappa`; fetch or drop `covered`.
4. Publish the walk shape honestly in `query_shapes`.
5. Add generation regress: deterministic-seed output pinned on a fixture;
   smoke score thresholds from Phase 1.
Consider: per spec 36/37, steering weights must arrive pre-baked to the
SPI-less walker — the frontier scoring belongs in the SQL/native gather, not
in `steered_walk.c`.
Done when: the smoke harness scores walk-shape replies on-topic and
multi-sentence on the seeded fixture, and a session's repeated prompt varies
with its seed.

### Phase 3 — True senses (the ground under every election)
Goal: sense sets contain only the senses of the surface, ranked by a real
prior.
Items: resolve the tier-collision seam (spec 37 §8.2 — decide whether the
tier-2 row at a surface's identity is a witness-boundary defect and fix at
ingest); band accessor into the `prompt_coherence.c` comparator
(content-band mass beats raw mass — measured on dog/chase); attest
`HAS_SENSE_RANK` at WordNet ingest (append-only bit).
Done when: dog→canine, trumpet→instrument, car→automobile on the live box
*and* ten held-out ambiguous words, with pawn/glacier/napoleon unregressed,
and re-measured OP3 election passes the correctness gate that failed on
2026-07-27.

### Phase 4 — Documents become citizens
Goal: a book enters with identity, name, license, and typed trunk edges —
and everything already ingested stays valid (content ids are stable; the
new lane adds testimony, it does not re-mint).
Items (from the cited 9-step finish list): `DocumentSource : ISeedSource`
with its own source id (stop borrowing `UserPrompt`);
`SourceVocabularyBootstrap.RegisterManifestAsync` in `InitializeAsync`;
`CanonicalNamesForReadback` override; trunk-grain title/author/edition
attestation in `WalkWitness` (Gutenberg headers are structured; O(1) per
file — does not reopen the Pillar-3a per-node grind); `HAS_TITLE` +
document relations appended to the manifest; **amend the content-only gate**
to per-node = 0 / trunk-grain > 0; wire `DocumentRouter` (GH #418) and fix
the malformed-file abort (GH #596); structural documents count in
`modality_counts` (GH #660); fix the inventory scan blind spot
(`docs-inventory.py:83`).
Done when: `containers_of(word_id('gambit'))`'s tier-4 rows return titles
from `canonical_names`; the seed-documents gate is green under the amended
expectation; a deliberately malformed file skips, not aborts.

### Phase 5 — Questions route themselves; retrieval learns
Goal: the escalator.
Items: attest relation-name vocabulary ("opening" names `HAS_ECO`; add the
missing geographic/political family — append-only bits); wire the election's
`rel_type_id` into `infer()`'s read (typed scan when a relation is named,
full distribution otherwise); port `infer` to C per its header (both
directions, n-hop bias, multi-step loop with WITNESS between steps);
discourse readback (orientation consults `recall_session` turn history, not
one id); **retrieval-as-player**: rate operator configurations through the
existing feedback lane so confirmed answers are wins for the parameters
that produced them — the fold as the tuner of its own retrieval.
Done when: "what openings does Magnus play in bullet?" answers through
`infer`/chat with no bespoke function, and a config's rating visibly moves
under confirm/refute.

### Phase 6 — ISA consolidation
Goal: ten opcodes, one implementation each, shape table as the only
dispatch.
Follow spec 37 §8 order (OP4 → OP9 → vocabularies → OP5/OP7 → OP8 → OP1/2 →
OP10), each step shippable green, dispositions per §4. The chess/model/ops
breadth is legitimate and is not a reduction target (§4 note).
Done when: G5/G7/G8/G10 gates green; the disposition table's "absorbs"
lists are empty or named views.

### Phase 7 — Scale and the standing loop
Goal: corpus mass through finished lanes, with the harness watching.
Seed waves (documents through Phase 4's lane; chess breadth — Lumbra's,
TWIC, syzygy; knowledge sources) with the Phase 1 harness re-scored per
wave and read-path timings re-taken per the degree law ("re-time after
every seed"). Fix pooled-connection reconnects; keep one ingest at a time.
Done when: quality scores rise with corpus mass and no surface breaches its
latency budget across two consecutive waves.

---

## 6. For any agent picking this up

Read §0 and obey it. Then read
[`docs/plan/CHECKPOINT_2026-08-02.md`](plan/CHECKPOINT_2026-08-02.md) for the
dated stage (what landed, live seed block, Phase 1 remainder) so you do not
re-derive status from stale issue titles. The drift failure mode in §2 is
the thing you are most likely to recreate — before writing anything new,
`grep` for the canonical that already exists and check its callers. Prefer
wiring to writing. Report misses before hits. The owner has heard fourteen
months of hits; the misses are where the trust is.
