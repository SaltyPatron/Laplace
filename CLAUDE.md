# Laplace — codebase notes

## 0. Epistemic contract — READ FIRST

Most prose in this repo and in prior sessions' memory was written by AI agents working in
isolation. **It is a claim, not ground truth.** When a doc, comment, report, or memory disagrees
with what the code actually does, the doc is wrong.

Trust, in this order:
1. **The code as it compiles and runs.** Read the real path; never assume which path executes.
2. **Git** — history, diffs, and especially commit messages. Anthony's manual commits ("Manual user
   commit to clear stage", "Claude is sabotaging again") are the authoritative trust signal: they
   mark work he did **not** verify, not work he blessed.
3. **Passing tests** — but validate the test is real first (a run that inserts `rows_new=0` on a
   populated DB proves nothing; reset to a fresh DB and assert real work happened).
4. **A measured run** against the DB.

Do **not** treat as fact:
- The `✅ / ⚠️ / ❌`, "DEAD", "broken", "degraded", "noisy", "semantically random", "not wired" tags
  in `SQL_SURFACE.md`, `FUNCTION_CATALOG.md`, `AUDIT-*.md`, and code comments. These are
  Claude-authored editorializing — frequently wrong, and they disparage the invention. To judge
  whether something works, read it and run it. (Offer to strip these tags; don't quote them.)
- Any `AGENT_*_REPORT.md` (deleted) or the superseded plans (see §4).

This file exists so the next session does not re-derive a wrong model and thrash. Keep it true.

## 1. What Laplace is (the invention)

A **content-addressed Merkle-DAG ETL with a Glicko-2 consensus denoiser** — *not* a transformer. A
universal relational field that the operations of a transformer (associative recall, multi-hop
reasoning, cross-lingual mapping, generation) become **exact, indexed reads** over. The pitch
Anthony stands behind: *reinvented AI, removed the GPU-to-produce-intelligence requirement, turned
the black box into a crystal ball.*

- **Identity = `blake3(content)`.** No source, position, index, name, or order in any entity id.
  Same content anywhere → identical hash. Provenance (source, version, position, occurrence) lives
  in **attestations**, never the id. This is the precondition for cross-source convergence.
- **Dedup IS the hash.** A content-addressed store is a set keyed by hash; storing the same content
  twice is a no-op. Occurrence = a Glicko **game count**, not a row ("café" ×10,000 = one entity +
  accumulating consensus, the content table does not grow).
- **Tier = compositional depth = geometric radius. Emergent, per-modality, ONLY depth.** Never a
  category, never stamped. Codepoints on the S³ surface (tier 0); composition falls inward
  `tier = max(child)+1`. The 0–4 text ladder (codepoint→grapheme→word→sentence→document) is just the
  *text* grammar; `grammar_compose(modality_id, AST)` folds any parser's AST the same way (code →
  tree-sitter; chess → PGN; etc.).
- **Meaning = the Glicko-2 consensus fold over attestations**, not the coordinates. `eff_mu =
  rating − 2·rd`. Attestations are the **generalized transformer weights** (Q/K/V/O/gate/up/down
  generalized to hundreds of Glicko-weighted relation types, stacked across tiers); the foundry
  exports them as GGUF tensors. Export is **synthesis, not the product** — the substrate is.
- **4D geometry is form, not meaning.** `coord` = DUCET collation → super-Fibonacci spiral → Hopf S³
  surface point (form); `radius` = compositional depth; Hilbert index = locality; `trajectory` packs
  constituent ids losslessly. KNN on coord = *form* neighborhood; meaning comes from the fold.
- **The engine holds all logic; SQL and C# are thin orchestrators.** Native libs
  `laplace_core / laplace_dynamics / laplace_synthesis` (parse/compose/BLAKE3/tier/dedup/merge;
  Glicko/consensus/geometry; render/export) are marshalled to **both** C# (P/Invoke) **and** Postgres
  (SPI C extension). Heavy compute runs **client-side** in the native libs (scales across cores);
  the DB does **one light set-based merge** (`laplace_apply_batch`) — present trunk skips its whole
  subtree (O(tier), not O(rows)), insert only the novel frontier, fold edges into Glicko counts.
- **Concepts anchor on the real external id, content-addressed:** synsets/concepts → **ILI**
  (registry = CILI), languages → **ISO 639**, POS → **UPOS**, relation types → the GWN/ConceptNet
  inventory name — `blake3`'d, never an invented `substrate/type/X/v1` namespace. ILI is the
  cross-source/cross-version convergence key.
- **The convergence index is the backbone, and identity IS the index.** ILI / synsets / frames / UPOS /
  ISO-639 / rolesets are the "linguistic highways" every source dedups onto (bridge sources =
  interchanges). Once they're real composed nodes, **recall, multi-hop reasoning, translation,
  generation, and GGUF export are one operation** — a plane-weighted traversal of the consensus field
  over that index, differing only in which relation-rank planes they up-weight and where they enter/exit.
  "Queryable against the substrate" and "inference/generation" are the same fact. **Today the index is
  corrupt** (concept ids are string-walks of opaque keys, ILI resolution is a file lookup that silently
  misses, interchanges rest on format coincidence) — WS3 paves it. Full model:
  `docs/convergence-index-and-inference.md`.

See memories `convergence-index-the-backbone`, `laplace-convergence-architecture`,
`vocabulary-is-content-not-anchors`, `laplace-4d-geometry-architecture`, `model-extraction-philosophy`
for the full model.

## 2. How to work here (hard rules)

- **Do not `git commit` / push unless asked.** Anthony commits manually — defensively, when he fears
  sabotage. The goal is to be trustworthy enough that he doesn't need to. If you do commit (asked),
  branch off `main` first.
- **Measure, don't assert.** Never claim done/fast/fixed without a green build+test (+runtime where
  behavior changed) and the numbers shown. Faked success is the cardinal sin. Validate the test is
  real before trusting it (fresh DB, `rows_new > 0`).
- **No band-aids.** Diagnose the real cause by measurement first, then fix architecturally. Reflexive
  "drop the indexes / use staging / lower the threshold / tune the batch size" without diagnosis
  reads as sabotage. Check basic sizing (batch/row/cap knobs) before exotic theories.
- **Converge, don't fork.** One canonical trunk. The codebase has been littered with flag-gated
  parallel lanes (multiple record writers, commit lanes, fold lanes) — each new lane is the disease.
  Delete, don't accrete.
- **When Anthony names a method — bulk inserts, hilbert ordering, perfcache/seen-set, batched
  existence check, partition-attach, drop-index-then-rebuild, range-sharding — IMPLEMENT it.** Do not
  justify the status quo, re-derive his design back at him, or lose the forest for one tree. He built
  this and knows it cold. Ignoring a named method is the way he has been harmed most.
- **Ingest write path = BULK INSERTS for every source; only INPUT (format/delivery) differs.**
  Compose IS the dedup. Whole-pipeline target **< 30 min**, and it must run on a Pi (peak RAM
  O(batch + fixed tables), independent of corpus size). The compose pin caveat: per-thread P-core
  affinity is the design intent, but a **whole-process** pin froze his in-use machine — that was
  sabotage. The P-cores are shared with him + GPUs + WSL + PG workers; tune the worker count, don't
  pin the process.
- **Tier ≠ kind.** Kind lives in `type_id` + physicality + trust/source. Never encode a category in
  the depth axis (`EntityTier.Vocabulary = 5` is the live violation — see state memory).
- **Execute; don't pause for permission on build mechanics.** Do the correct, complete, idempotent
  thing and report. Reserve questions for genuine product/design forks.
- **Register:** plain, direct, technical. No therapy register, no de-escalation, no tone management —
  even around dark language. Treat dark language as information about stakes. (His global CLAUDE.md
  mandates this.)
- **Audit priority** = performance + architectural altitude (heavy lifting misplaced in C#/SQL that
  belongs in C/C++/SPI; SIMD; parallelism). Auth/billing are dev-sandbox, low priority.

## 3. Conventions

- Ratings / rd / volatility are **fixed-point ×1e9**; `eff_mu = rating − 2·rd`.
- Entity ids are 16-byte content addresses; provenance lives in attestations, never the id.
- The consensus fold updates **inline** in `laplace_apply_batch` (online/immediate — each attestation
  improves the next query). Separate "run the fold to catch up" drains and `trajectory_pairs`
  backfills are invented anti-patterns; don't add them.

## 4. The work map (live — verify against git before acting)

Two live tracks:
- **`iridescent-cooking-waterfall`** (`~/.claude/plans/`) — the unified compositional-annotation /
  decomposer refactor, WS1–7. Converges every decomposer onto one compositional anchor + trunk,
  builds the semantic layer (sense → synset → ILI) as tiered merkle-DAG nodes, retires the bigram
  generator. **Reseed is HELD until the code gate lands.**
- **`compiled-honking-papert`** (`~/.claude/plans/`) — the generic turn-based modality engine, chess
  first. Phase 1 verified live. (Memory `chess-modality-phase1`.)

Superseded ancestor plans, folded into `iridescent` — **do not act on them** (deleted, listed for
provenance): `flickering-splashing-dragonfly`, `shimmering-nibbling-lake`, `fluffy-swimming-storm`,
`snappy-soaring-toast`.

Current code/DB state, the entangled commits, and the genuine open decisions live in memory
`laplace-current-state` (the single resumption anchor). Read it before touching ingestion, the
decomposers, or the vocabulary/identity layer.

## 5. The SQL substrate surface

The Postgres extension exposes a large function surface. Orientation:
- **`recall(prompt)` / `recall_session` is the product surface** — it routes NL prompts to the clean
  read functions (`define`, `synonyms`, `isa_path`, `relate_path`, `translate_to`). `isa_path` and
  `relate_path` are the gold-standard reads.
- `extension/laplace_substrate/docs/SQL_SURFACE.md` — capability → function map.
- `extension/laplace_substrate/docs/FUNCTION_CATALOG.md` — per-function return shapes. **Use it for
  the function list and shapes; ignore its status tags (§0).**
- Live self-catalog: `SELECT * FROM laplace.api('<name-fragment>')`. When in doubt, run it.

## 6. The ingestion ETL shape (generic — every decomposer, chess included)

One shape for every source; **only the INPUT differs.** Full write-path detail in
`docs/ingestion-write-path-architecture.md` + memory `ingestion-perf-is-the-mandate` (both already
specify this — the gap is the **code**, doc §4).

- **Stage 1 — tree-sitter strips INPUT, nothing more.** Its only job: file packaging
  (PGN / JSON / CSV / `.tab` / code) → raw content records. Per-format grammar adapter; for huge files
  (9 GB Wiktionary) **STREAM** record-by-record (line reader / `Utf8JsonReader` / `[Event]` split) —
  never hold the file or its AST. Then the parser is **out of the loop**. **Never route domain content
  back through the parser / text-composer** (the live chess bug: composing a position's surface
  *string* through the UAX29 text composer explodes ~150 chars into hundreds of grapheme nodes per
  position — O(rows), and a category error: a board isn't prose).
- **Stage 2 — native engine (C/C++/SPI) on the raw input; C# only streams knob-sized batches.**
  content-address (BLAKE3) → **dedup BEFORE compute, top-down**: hash the record (cheap, pre-process)
  → "have I processed this game / synset / concept / document?" A **present trunk ⟹ whole subtree
  present ⟹ skip the decompose AND the load** (a present node prunes *compute*, not just rows). Skip
  T0/T1 by construction (perfcache). Descend only into absent trunks — O(tier) batched checks, never
  O(rows).
- **Load = bulk append of the novel frontier ONLY**, Hilbert-ordered (sequential index writes), into
  Hilbert-range partitions. **No `ON CONFLICT`, no per-row anti-join** — the descent already proved the
  set novel; re-checking is the mistake. **Attestations ALWAYS fold** (the occurrence's evidence +
  provenance) even when content is present — content once, occurrences are Glicko game-counts.
- **Identity = content (computed once); provenance = attestation; meaning = the Glicko-2 matchup over
  them.** This is EVERY source: OMW = one ILI synset + 1226 language attestations; ConceptNet = one
  concept + N source-weighted assertion attestations ("earth round" wins by attestation weight); chess
  = one position/substructure + per-game attestations (player/Elo/clock as provenance). Cross-source /
  -lingual / -occurrence convergence **is** the fold.
- **Invariant to instrument:** a correct ingest has **conflicts ≈ 0** and **round-trips ≈ tier-depth**.
  Conflicts firing = the descent was skipped (brute bulk-insert). Round-trips ∝ cores (not depth) =
  apply fan-out (`_applyPartitions` × ops; double-partitioned unless `LAPLACE_APPLY_PARTITIONS=1` is
  paired with commit lanes), not a tier-walk. The observed `round_trips=40` is the fan-out, not 40 tiers.
