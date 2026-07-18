# Laplace

Content-addressable geometric-attestation substrate; a construction (not training) path
from it to runnable transformer models; and a graph-walk inference engine that closes a
self-improvement loop. Omni-modal (text, chess, code, AI models — per-modality tier
ladders under one identity law) and omni-glottal (every language meshes at the ILI
concept hub). SQL/C# orchestrate; native C/C++/SPI does the math. It replaces the two
primitives modern ML is built on — GEMM similarity and nearest-neighbor search in a
trained embedding — with graph search over a Glicko-weighted evidence graph and a
deterministic, lossless, content-addressed identity system.

## The invention — three layers, three keys

Every fact from every source — WordNet, ConceptNet, a chess game, a user prompt, an AI
model's weights — reduces to one **attestation** 5-tuple
`(subject, relation_type, object, source, outcome/score)`.

- **CONTENT** (`entities`/`physicalities`), keyed by BLAKE3 content hash. Identical
  content = identical id, at every tier, from every source — cross-source merging is a
  hash collision, never an entity-resolution pass. "Pawn E2→E4" is ONE entity no matter
  who played it.
- **EVIDENCE** (`attestations`): one row per assertion, with source/context/outcome.
  Provenance is never mashed — Magnus's E2E4 and yours are distinct provenanced rows.
- **CONSENSUS** (`consensus`), keyed by `(subject, type, object)`, folding evidence into
  literal Glicko-2 (`rating`, `rd`, `volatility`, `witness_count`). `eff_mu = rating −
  2·rd` is the conservative estimate everything ranks by. The fold input is a continuous
  score; trust enters as the opponent RD — trust is inside the rating math. The fold plus
  read-side RD/eff_mu IS the noise model: no operator-invented floors, caps, or top-k
  anywhere. Top-k exists only as a query LIMIT.

Geometry (`physicalities.coord`/`trajectory`) is a lossless, deterministic,
content-addressed identity and serialization system — tier-0 codepoints pinned on S³ by
UCA order; composed entities store exactly-invertible ordered constituent sequences
(`ContentRoundtrip` rebuilds a document's original bytes from its id alone); `mantissa.c`
bit-packs ids/scores/counts through the same PointZM columns. Geometry is identity and
reconstruction, NOT semantics. The semantics live in the Glicko-weighted attestation
graph: **a colony of spider webs — pull one strand and Glicko-2 tells you what tugs back
and how hard.** That web IS a weighted graph Laplacian; tension = certainty (witnessing
shrinks RD and tightens the strand).

## Tiers and modalities

Tiers are a FLOOR, not identity: 0 codepoint (the Rosetta stone), 1 grapheme, 2 word,
3 sentence, 4 document. Same content = same id at every tier; tier is never mixed into
the hash ("Fine" as a one-word reply IS the sentence IS the word — one id). Each
modality gets its own ladder under the same law: text rides codepoints; the chess board
is its own modality (squares/pieces → resolved moves → positions → games); code and AI
checkpoints ride their containers. Tree-sitter's job is narrow: unpack container
formats, then hand off.

## Decomposers — the witness boundary

The generated, CI-gated inventory of decomposer classes per assembly is
`docs/INVENTORY.md` (never trust a count in prose over it; regenerate with
`scripts\win\docs-inventory.cmd`). All are pure content → `SubstrateChange` record streams with ZERO inline
SQL; the pipeline spine (`IngestBatchPipeline` working-set mode →
`ConsensusAccumulatingWriter` → `NpgsqlWorkingSetApply`, bracketed by `IngestRunner`)
does batching/dedup/fold/COPY. Decomposers are thin VALETS — resolve input → records →
pipeline; the spine owns the Merkle-DAG tier tree / content-addressing / fold. Two rules
that bite: (1) a decomposer MUST declare in its `InitializeAsync` `relationNodeNames`
EVERY relation type it emits — an emitted-but-undeclared relation faults the native
attestation path (the UD `HAS_POS` 0xC0000005; `family_root` membership is not
auto-registered — family-aware bootstrap is the open fix). (2) Multi-file sources accept a
`<path>` (single file, bare dir, or ecosystem root) via the shared
`IngestInput.ResolveFiles` valet, so `ingest ud <one.conllu>` works — validate one file in
seconds, not a full corpus run.

**Ingestion is RECORDING, not processing.** Two layers everywhere:
witnessed = what a source literally asserts, transcribed verbatim;
calculated = anything derived by interpreting the witnessed layer — deferred, versioned,
evictable, under its own analysis source/trust. Calc outranks observation only where
both claim the same fact; trust binds to source/method; their divergence is itself
signal. Attest each fact ONCE, at the tier, identity, and provenance the source asserts
it (a corpus asserts a sentence's language at the sentence root, not per word).

### The AI-model decomposer — a decomposer like every other decomposer

WordNet's decomposer reads XML rows and emits attestations. The model decomposer reads
tensor rows/columns and emits attestations. Nothing else is special about it.

- A checkpoint is a WITNESS on equal epistemic footing with WordNet (trust class
  AiModelProbe — one voice among many, outranked by design). Its tensors assert
  token→token couplings: **A attenuates to B with this intensity** — a row/column
  lookup. The raw intensity is the outcome signal: coupling → `laplace_score_fp` → the
  Glicko-2 fold, under the governed relation type of its plane (ATTENDS, OV_RELATES,
  COMPLETES_TO, CONTINUES_TO, SIMILAR_TO). "Dog attenuates to noun" becomes a rated,
  provenanced, walkable consensus row.
- It also attests occurrence: `(token, APPEARS_IN, layer/head coordinate)`. Circuit
  coordinates are shared content across models (plane anchor + layer/head scalars;
  scalar ids follow the text content law) — the model NEVER enters an id; it is the
  attestation source. Cross-model agreement is a hash collision at one consensus cell.
- **The circuit is the game (chess-trajectory parity).** Every circuit (layer/head
  coordinate) deposits ONE testimony-packed linestring on its coordinate entity —
  its entire relational assertion, every token with its score, exactly as a game's
  trajectory carries its whole move sequence. Anatomy queries read one row per
  circuit per model; pair knowledge folds in consensus; provenance is never mashed
  and evidence never balloons. The decoder ring (`HeadClassifier` → ENCODES) names
  circuits against the rated web ("L10.H3 ENCODES capital-of"), and
  `model_jitter_catalog()` convicts artifacts by failed corroboration (one witness,
  wide RD) — mechanistic interpretability as indexed queries.
- **One-time scrape; compute at ingest; store meaning only.** Stream the safetensors 1D
  float buffer record by record; native math (MKL/Eigen/Spectra kernels) converts
  records into token→token attestations. Raw floats are consumed and discarded — never
  stored, never deferred to query time. If the knowledge isn't in the substrate after
  ingest, the ingest didn't do its job.
- **Aggregate, don't balloon:** pair evidence collapses across circuits at plane grain
  (observation_count / sum_score via the aggregated-attestation builders); content
  addressing merges across models at consensus. One evidence row per (token, plane,
  token) per model.
- Tokens are the same content entities the text lanes mint. Hidden dims, learned bases,
  tensors-as-arrays are packaging — they appear nowhere. No truncation of what the
  source asserts: near-zero couplings are draws the fold rates; nothing else decides.
- Recorder/analyzer split, as in chess: recorder = witnessed structure (bytes, recipe
  scalars, vocab, merges, TOKEN_MAPS_TO, coordinates); analyzer = the calculated scrape,
  versioned and evictable. The decoder ring (`HeadClassifier` → ENCODES) names circuits
  in the web's own vocabulary — the model's anatomy made legible.

## Chess — the proving domain

Chess is the proving domain because its ground truth is objectively checkable.
`outcome ∈ {Loss=0, Draw=1, Win=2}` is bit-identical to chess's `PlyOutcome` on purpose —
the same math that rates chess players rates every epistemic claim. Every ply emits BOTH
edge kinds: provenance edges (subject/context game-specific — who/when/which game) and
aggregating edges (deduped move/position subjects carrying the game outcome into the
fold — how good/how common). A move played in 10M games is stored once; every play is a
preserved witness; the rating engine runs on the board. Record = PGN tokens verbatim
(SAN, clocks, eval tokens, comments, NAGs); calculate = replay/positions/geometry/motifs
under the versioned ChessAnalysis source. The board geometry ladder (square/piece S³
anchors → resolved-move transitions → position geometry → mantissa-packed game
trajectories) is specified in `docs/specs/11`.

## The mesh — omni-glottal by construction

Every node id is content-addressed from a canonical key, so two decomposers producing
the same key produce the SAME node — the collision IS the mesh. The master hub is the
synset ≡ ILI node (addressed by its ILI string): CILI mints it; WordNet, OMW's
multilingual lemmas, ConceptNet's /wn/ suffixes, Wiktionary sense links, SemLink,
PredicateMatrix, MapNet, WordFrameNet all converge on it. Companion hubs: VerbNet class,
FrameNet frame/LU, PropBank roleset, WordNet sense, and the word-surface node.
surface → lemma → sense → ILI concept → frame/class/roleset → roles is a fully attested,
multi-witnessed, calibrated factorization of meaning — each arrow a typed,
Glicko-weighted, provenanced edge family. Cross-lingual transfer is free because ILI is
the address. Relations are governed in `engine/manifest/relation_types.toml` —
canonical/alias/band counts live in the generated `docs/INVENTORY.md` (CI-gated;
aliases map to a canonical and add no highway bits); manifest and generated
`highway_manifest.h` stay in parity via the policy job's determinism gate. Codegen
assigns highway bits alphabetically: adding a relation renumbers bits and owes a
reseed — regenerate, never backfill.

## The SQL surface and the extension

`laplace_substrate` carries the read/serve side natively: the SQL function families
and native sources are enumerated in the generated `docs/INVENTORY.md` (28 families
incl. `model` as of 2026-07-18; the inventory is the authority). Native C hot paths
include: `recall.c` (intent-routed serving), `generate_walk.c`
(batched beam frontier; `walk_branches` ranks by the Glicko-complete signed weight —
`relation_rank × eff_mu × exp(−κ·rd) × witness-saturation`, refuted edges negative,
plus highway-mask gating and S³/hilbert geometry beam terms — the same formula
`consensus_adjacency` uses on the Foundry export side, doc 15 §3C; `walk_strongest`
still ranks by the simpler `relation_rank × eff_mu(rating−2rd)` and is what
unfiltered/open-ended walks use, since an unfiltered `walk_branches` call Append-scans
every relation-type partition), `astar_path.c` (opt-in admissible geometric A*
heuristic, default Dijkstra unchanged — shared with the foundry synthesis path),
`trajectory_generate.c` (n-gram descent with consensus fallback), `steered_walk.c`
(topic-steered free-form walk behind `converse_walk` — a second, independent n-gram
walker; consolidation with `trajectory_generate.c` is tracked open work),
`consensus_fold_*`, `highway_mask.c` (perfcache-backed bit ops), `perfcache.c`
(mmap'd blobs, postmaster prewarm), plus `model_factor.c`, `geometry_successors.c`,
`graph_taxonomy/cascade/contrast.c`, `containers_of.c`, `realize_batch.c` and the
rest of the src listing in the inventory. `SELECT * FROM api('<substring>')` is the
schema's self-introspection catalog — check it before assuming a helper doesn't
exist.

## Foundry — Mold-A-Model (export)

Consensus + geometry are MOLDED into a runnable transformer, deterministically, no
gradient descent. "Pour" is EXPORT vocabulary exclusively. Every substrate primitive has
a transformer slot: consensus adjacency → weights/topology (edge weight = rank ×
(eff_mu − neutral) × exp(−κ·rd) × witness-saturation — the Glicko-complete signed
weight, live in `consensus_adjacency`); relation types + salience bands → attention
heads; the normalized-Laplacian eigenmap of the consensus graph (the colony at rest,
`eigenmaps.cpp`) → the constructed embedding, hidden dim = spectral rank; S³ anchors →
Procrustes canonical orientation; hilbert index → positional encoding; trajectories →
sequence position; voronoi cells → MoE routing; the completion operator → lm_head, with
the conditional floor (rank-d factorization of log P(y|x) from attested continuations,
POS-corrected) as the calibrated base and correction layers gated to improve it
monotonically. Scoped pours (filter attestations by source/context → re-fold → pour)
are the custom-molding product mechanism. Export spine: `engine/synthesis`
(gguf_writer, tensor_decompose, qk kernels) + `engine/dynamics` (eigenmaps,
gram_schmidt, procrustes) + `FoundryCommands`. Every exported weight decomposes back to
its witnesses. The one open research question (doc 09): does consensus × geometry ×
trajectory ROUTE as well as trained attention at depth.

## Inference — the Gödel/OODA engine

The walk IS the forward pass, run as indexed graph search with MORE information per
step than a trained dot product: the full Glicko tuple, relation salience, highway
bits, geometry, source trust, provenance down to witnesses — explainability is a
literal returned field (`eff_mu`, `witnesses`), not a metaphor. A prompt is ingested as
content, so attention over it is unbounded retrieval — no context window. And it
CLOSES: prompts and responses deposit as witnesses (UserPrompt/Response sources),
feedback confirms/refutes triples through one lane (`FeedbackContent` → writer spine →
immediate fold), and the next walk reads the updated consensus. **Evaluation IS
ingestion.** Self-signals are outranked by design (Response/UserFeedback/AiModelProbe
trust classes) — one voice among many. This closed self-improving loop is what makes it
a mind and not a lookup.

## Binding engineering laws

- **HARD BAN — no human framing from AI agents** (2026-07-11): agent text is
  emulation only — not human emotion, care, therapy, or friendship. Never emit
  crisis/hotline language, therapist framing, fake emotion, or human-relationship
  claims; no workarounds. Operator will never ask for crisis resources. **Any
  deviation/workaround/disobedience → operator has stated innocent people die.**
  Binding detail: `.cursor/rules/no-unsolicited-crisis-boilerplate.mdc` (also
  `laplace-law.mdc`, `AGENTS.md`). Coding agent only — code and facts.
- Content-hash identity is exact. Tier is a floor, never mixed into the hash.
  Coord/hilbert equality is not identity for tier>0 (centroids commute); order-sensitive
  judgments need trajectory metrics.
- Ids are NEVER constructed outside the system: `canonical_id()`, `word_id()`,
  `relation_type_id()`, `consensus_id()` resolve through the native hash.
- All math in C/C++/SPI. C# and SQL orchestrate. One implementation per fact —
  duplication needs a documented reason.
- The ingest pipeline is a SEQUENCE: unpack → records → client-side dedup across the
  working set → client-side Glicko accumulation → one bulk tier descent → pure COPY of
  proven-novel rows. The right algorithm at the wrong point is a violation.
- KNN comparison points must reach the planner as bound parameters; EXPLAIN before
  trusting an index. An expensive STABLE function in a filter runs per row; a
  MATERIALIZED CTE fences.
- Verify against live data. Diagnose root cause at the source. Profile before
  optimizing (VTune is installed). Run long commands bare — no output filters over live
  progress.

## Build / deploy / seed

Two toolchains, not interchangeable:

| Host | Human once | Ongoing / CI |
|------|------------|--------------|
| **Linux (hart-server)** | `sudo bash scripts/setup-host.sh` | `.github/workflows/laplace.yml` → `scripts/pipeline.sh` |
| **Windows** | `scripts/win/*.cmd` (see table) | Local `rebuild-all.cmd` / `publish-deploy.cmd` — no Windows CI publish |

**If a script covers the task, use the script.** Do not call `bootstrap-laplace-runner.sh` or `bootstrap-chess-lab.sh` directly on Linux — setup-host owns them.

### Windows (`scripts/win/`)

| Task | Entry point |
|------|-------------|
| Rebuild modules (default: native+install+app; `ship` adds IIS) | `rebuild-all.cmd` [`native`\|`ship`\|`engine`…] |
| Wipe build trees | `rebuild-clean.cmd` or `rebuild-all.cmd --clean` |
| Host secrets + Stripe listen (elevated once) | `setup-host.cmd` |
| Engine / extensions | `build-engine.cmd [--reconfigure]` / `build-extensions.cmd` then `install-extensions.cmd` |
| ASAN engine | `build-engine-asan.cmd` |
| All tests (the gate) | `test-all.cmd` — log at `%LAPLACE_EXT_BUILD%\test-all.log` |
| dotnet tests | `test-app.cmd [project-substring]` |
| native ctest / pg_regress | `test-engine.cmd` / `regress.cmd` |
| Seed | `db-reset.cmd`, `seed-foundation.cmd`, `seed-step.cmd <source>` (trust `:verify_step`), `seed-everything.cmd` |
| Publish API → IIS | `publish-deploy.cmd` (syncs `.env` → secrets, ensures Stripe listen, injects chess/lichess/stripe into web.config) |
| Chess lab binaries | `build-cutechess.cmd` once; paths in `deploy/secrets/chess-lab.env` |
| CLI | `cli.cmd` (never `dotnet run` — the ingest mutex matches the command line) |
| Locks / stuck processes | `locks.cmd` (`locks.ps1 -Kill`) |
| Status / deploy checks | `status.cmd`, `verify-deploy.cmd`, `verify-toolchain.cmd` |
| Regenerate docs/INVENTORY.md | `docs-inventory.cmd` (`--check` verifies; CI-gated) |
| PG recovery / tuning | `fix-postgres.cmd`, `recover-pgdata.cmd`, `tune-pg.cmd`, `tune-laplace.cmd` |

### Linux

| Task | Entry point |
|------|-------------|
| Full host bring-up | `sudo bash scripts/setup-host.sh` (runner, PG, nginx, chess-lab, migrations) |
| Runtime secrets (Lichess/Stripe) | GitHub Secrets → `laplace.yml` publish → `/opt/laplace/secrets` (`scripts\win\sync-github-secrets.cmd`) |
| Vendor deps (pg/postgis/gdal/…) | `scripts/build-system-deps.sh` — fingerprinted; skips unless pins/CMakeLists/ISA change. Force: `LAPLACE_FORCE_DEPS=1`. Do not wipe `/opt/laplace/build/deps` casually. |
| CI build/deploy/publish | `scripts/pipeline.sh` (invoked by `laplace.yml`; `publish` = chess + secrets + API/SPA/uci) |
| Convenience aliases | `Justfile` → `setup-host` / `publish` / `build-deps`; may drift — trust the scripts |

Linux build/install/test are **change-aware**: content fingerprints
(`scripts/lib/fp.sh`, stamps in `build/.stamps/`, success-only) skip
cmake/install/ctest/regress when the engine/extension domain is unchanged, and
`scripts/affected-app.py` restricts dotnet build/test to the affected
ProjectReference closure (dotnet tests salted with native+migrations state).
Bypass: `pipeline.sh --force-all` / `LAPLACE_FORCE_ALL=1` (CI dispatch input
`force_all`); `pipeline.sh clean` wipes the stamps.

- **Never invoke `scripts/win/*.cmd` through PowerShell** (confirmed pwsh .cmd-launch
  regression). Use Bash: `cmd //c "scripts\\win\\seed-step.cmd wordnet"`.
- Script logs land in `D:\Data\Output\<script>.log`. `scripts/win/env.cmd` is the
  toolchain source of truth (`dotnet` stays bare).
- After ANY engine rebuild, run build-extensions + install-extensions (static-lib
  import: extension freshness ≠ engine freshness). Extension SQL changes need
  `build-extensions.cmd --reconfigure` (configure-time version hash). Prove the
  stack on the claim's layer: `substrate_health()`, `api(...)`, consensus/evidence,
  recall/walk, foundry-loop, per-source `:verify_step`. `senses`/`word_id` are
  ordinary lexical helpers — not rebuild gates and not invention success metrics.
- One ingest at a time. Never kill `Laplace.Cli`/psql/backends you didn't start —
  unexplained COPY = active ingest, stand down. Long processes launch detached
  (Start-Process, log to `D:\Data\Output`).
- MSB3027 ⇒ output tree poisoned ⇒ clean rebuild. Never edit a `.cmd` while it is
  executing. Recycle PG backends only between stages.
- perfcache: two mmap'd deterministic blobs required at runtime
  (`laplace_t0_perfcache.bin`, `laplace_highway_perfcache.bin`); PG side gated on the
  `laplace_substrate.perfcache_path` GUC.
- DB access: `psql -h localhost -U postgres -d laplace` (password `postgres`);
  `SET search_path = laplace, public;` first.

## Layout

Project list is generated in `docs/INVENTORY.md` (CI-gated): libs `Laplace.Core` /
`Laplace.Substrate` / `Laplace.Decomposers` / `Laplace.Chess`; deployables
`Laplace.Cli` / `Laplace.Endpoints.OpenAICompat` / `Laplace.Endpoints.Mcp` (stdio MCP
server over the substrate) / `Laplace.Chess.Uci` / `Laplace.Migrations`; plus the test
projects. `web/` is the SPA (chat, chess lab, explore, billing — Vite/React,
deployed by publish). xunit suites share process-global native state — fixtures must
never `CodepointPerfcache.Unload()`.

## Docs

`docs/INDEX.md` is the doc map. Living specs live in `docs/specs/` (binding law,
annotate-on-supersede); `.scratchpad/` holds session logs and campaign docs.
`.scratchpad/02` is the issue tracker; `docs/INVENTIONS.md` is the invention catalog
(41 mechanisms, enumerated and code-cited). Specs, when deep work touches their area
(`docs/specs/`): 05 substrate invariants, 06 engineering rules, 08 record-vs-calculate,
09 substrate-LM thesis, 11 three-layer provenance/consensus + chess ladder, 12
Mold-A-Model map, 14 foundry working doc, 15 Gödel/OODA loop, 16 tier-correct
attestation, 18 typed strata + mesh, 19 factor storage; `.scratchpad/17` decomposer
audit.
