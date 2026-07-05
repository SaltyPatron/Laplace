# Laplace

Content-addressable geometric-attestation substrate, and a construction (not training)
path from it to runnable transformer models. This file is the entry point: what the
invention is, where the truth lives, and how to operate the repo. It points; the
`.scratchpad/` docs hold the substance. Last full overhaul: 2026-07-05.

## The invention, in one arc

Every fact from every source — WordNet, ConceptNet, a chess game, a user prompt, a probed
neural network's own weights — reduces to one **attestation** 5-tuple
`(subject, relation_type, object, source, outcome)`. Three layers with three keys carry
everything (this is the load-bearing mental model, from `.scratchpad/11` §0):

- **CONTENT** (`entities`/`physicalities`), keyed by BLAKE3 content hash. Deduped:
  identical content = identical id, at every tier, from every source — cross-source
  merging is a hash collision, not an entity-resolution pass. "Pawn E2→E4" is ONE entity
  no matter who played it.
- **EVIDENCE** (`attestations`), one row per occurrence, with source/context/outcome.
  Nothing is mashed: Magnus's E2E4 and yours are distinct provenanced rows.
  `outcome ∈ {Loss=0, Draw=1, Win=2}` is bit-identical to chess's `PlyOutcome` on purpose.
- **CONSENSUS** (`consensus`), keyed by `consensus_id(subject,type,object)`, folding
  evidence into literal Glicko-2 (`rating`, `rd`, `volatility`, `witness_count`). The
  same math that rates chess players rates epistemic confidence; `eff_mu = rating − 2·rd`
  is the conservative estimate everything ranks by. Chess is the proving domain because
  its ground truth is objectively checkable.

Retrieval/reasoning is graph search over that consensus-weighted graph (`recall()`,
`generate_walk()`, native A* frontier) — not vector similarity. There is no GPU code in
`engine/` or `extension/` and that is structural.

**The trap every fresh reader falls into** (it happened twice in doc 01; do not repeat it):
`physicalities.coord`/`trajectory` geometry is **NOT a semantic embedding**. It is
(a) a lossless, deterministic, content-addressed identity/serialization system — tier-0
codepoints get fixed S³ positions seeded by Unicode's own UCA collation order; composed
entities store an exactly-invertible ordered sequence of their children's coordinates
(`ContentRoundtrip.cs` rebuilds original bytes from a document id alone) — and (b) a raw
bit-packed payload channel (`mantissa.c`) smuggling hash ids/scores/counts through the
same PointZM columns, disambiguated only by a flag bit. Distance on S³ ≠ meaning. The
SEMANTICS live in the Glicko-weighted attestation graph: "a colony of spider webs — pull
one strand and Glicko-2 tells you what tugs back and how hard." That web IS a weighted
graph Laplacian; its eigenmap (the colony at rest) is the semantic embedding, CONSTRUCTED
from the consensus spectrum, never trained (`.scratchpad/09`).

**Mold-A-Model** (`.scratchpad/12`): every substrate primitive has a transformer slot —
consensus adjacency → weights/topology; relation types + salience bands → attention heads;
S³/angular/fréchet/intersects geometry → geometric heads, positions, sparsity; hilbert
index → positional encoding; voronoi cells → MoE routing; spectral rank of the Laplacian
→ hidden dim; the completion operator → lm_head. Deterministic closed-form GGUF export
(`engine/synthesis`, `FoundryCommands`); every weight decomposes back to its witnesses.
The one open research question: does consensus × geometry × trajectory ROUTE as well as
trained attention at depth (09's "reduced residue"). The FAITHFUL flattening was a bigram
lookup with the layers off — a finished diagnosis, not anti-goal evidence.

Ingestion is RECORDING, not processing (`.scratchpad/08`): transcribe what a source
literally asserts (witnessed layer); defer everything derived to a versioned, evictable
analysis pass (calculated layer). Calc outranks observation only where both claim the
same fact; trust binds to source/method, and their divergence is itself signal.

## Doc map (`.scratchpad/`) — read before deep work in the relevant area

| Doc | Role | Status |
|-----|------|--------|
| 01 Initial review | First reconstruction of the invention + flaws | historical; corrections preserved in-line |
| 02 Identified issues | THE issue tracker (compacted 2026-07-05: status index + open detail + lessons L1-L11) | living |
| 03 / 04 Chess audit + fixes | Chess subsystem inventory/defects | living, chess-scoped |
| 05 Substrate invariants | Axioms: what a collision/bit/tier certifies | living — binding |
| 06 Engineering ruleset | Rules #1-#12: how substrate code must be written; Rule #8 = the ingest sequence | living — binding |
| 07 SQL surface audit | Inline-SQL/duplication/missing-views audit + P0-P3 roadmap | living; §5 re-baselined 2026-07-05 |
| 08 Record vs Calculate | Witnessed/calculated split spec | living spec |
| 09 Substrate-as-LM | Synthesis thesis, FAITHFUL diagnosis, proper-build spec, spider-web=Laplacian | living spec |
| 10 SQL consolidation | Zero-loss numbered-file removal + lockout gates | done; the gate pattern to reuse |
| 11 Chess provenance/consensus | Three-layer model; chess geometry ladder spec | living spec |
| 12 Mold-A-Model map | Substrate primitive → transformer slot bijection | living spec |
| 13 Stabilization audit + plan | Current-state truth table + phased refactor plan (THE active plan) | living — start here for "what next" |
| 14 Foundry root cause | Why heads/layers mash + why no conversation: 5 mechanisms (M1-M5), supply-vs-consumption table, prescriptions P1-P10, live reseed baseline, literature panel | living — the foundry build's working doc |

Current state in one line: substrate + Rule #8 pipeline core are solid; the mess is
seven ingestion lanes (Issue 45), read-side fragmentation (Issue 46), and the foundry
gaps (Issues 04/05/06) — all sequenced in doc 13.

## Build / deploy / seed — READ BEFORE RUNNING ANYTHING

Two toolchains; not interchangeable: `Justfile` + root CMake = Linux/CI. The real Windows
workflow is `scripts/win/*.cmd`: `rebuild-all.cmd` (clean+codegen+build+perfcache),
`db-reset.cmd`, `seed-foundation.cmd` (10 core layers), `seed-step.cmd <source>`
(see `--list`; runs an independent post-step `:verify_step` — never trust the CLI's own
summary line), `seed-everything.cmd` (hours-scale). `scripts/win/env.cmd` is the
toolchain source of truth (cmake/ninja/icx paths, Server-GC vars; `dotnet` stays bare).

**CRITICAL: never invoke `scripts/win/*.cmd` through the PowerShell tool.** This
machine's pwsh has a confirmed .cmd-launch regression
([PowerShell#27634](https://github.com/PowerShell/PowerShell/issues/27634), KB5095093) —
bogus `'ocal' is not recognized` before the script runs. Always use the Bash tool:
`cmd //c "scripts\\win\\seed-step.cmd wordnet"`.

Other operational law, earned the hard way (details: 02 lessons L1-L11):
- Ingest mutex guard matches on process COMMAND LINE (`Laplace.Cli` via Win32_Process) —
  `dotnet run` launches as `dotnet.exe`.
- After ANY engine rebuild, run build-extensions (static-lib import means extension DLL
  freshness ≠ engine freshness); `senses('dog') > 0` is the real health check.
- MSB3027 copy failure ⇒ output tree poisoned ⇒ clean-rebuild.
- Never edit a `.cmd` while it is executing; recycle PG backends only between stages.
- One ingest at a time; parallel agent sessions have killed Postgres mid-write before.

DB access: `psql -h localhost -U postgres -d laplace` (password `postgres`);
`SET search_path = laplace, public;` first. `SELECT * FROM api('<substring>');` is the
schema's self-introspection catalog — check it before assuming a helper doesn't exist.

## Core concepts (fast reference)

- **Tiers** (floor, not identity — 05 Rule #1b): 0 codepoint ("Rosetta stone"),
  1 grapheme, 2 word, 3 sentence, 4 document; per-modality ladders. Same content = same
  id at every tier; tier is NEVER mixed into the hash. Tree-sitter's job is narrow:
  unpack container formats (~37 of ~300 vendored grammars wired), then hand off.
- **Decomposers** (`Laplace.Decomposers`, 26 of them): pure content → `SubstrateChange`
  record streams; ZERO inline SQL (protected property). The pipeline spine
  (`IngestBatchPipeline` working-set mode → `ConsensusAccumulatingWriter` →
  `NpgsqlWorkingSetApply`, bracketed by `IngestRunner`) does batching/dedup/fold/COPY —
  Rule #8 is the sequence spec; doc 13 Phase 1 unifies the seven adapter lanes onto it.
- **highway_mask**: 256-bit relation-TYPE bitmask on entities+attestations, generated
  from `engine/manifest/relation_types.toml` (153 assigned bits, 13 salience bands
  mandate=1.0 … probationary=0.05 — the same number weights read-time confidence and
  export planes). Fixed + live-verified (05 Rule #5); pre-2026-07-01 DB generations are
  regenerated, never backfilled.
- **perfcache**: two mmap'd deterministic blobs required at runtime —
  `laplace_t0_perfcache.bin` (codepoint geometry/hash/segmentation, CI determinism gate)
  and `laplace_highway_perfcache.bin` (relation bit/rank/band); PG side gated on the
  `laplace_substrate.perfcache_path` GUC.
- **Consolidated layout**: 13 projects — libs `Laplace.Core` / `Laplace.Substrate` /
  `Laplace.Decomposers` / `Laplace.Chess`; deployables `Laplace.Cli` /
  `Laplace.Endpoints.OpenAICompat` / `Laplace.Chess.Uci` / `Laplace.Migrations`; 5 test
  projects. Namespaces preserved byte-identically; SourceIds untouched (verified by
  re-ingest hash identity). xunit suites share process-global native state — fixtures
  must never `CodepointPerfcache.Unload()`.

## Non-negotiable working rules

- Verify against live data; never present a narrow patch as the architectural fix
  (Issue 19 is the canonical example of the distinction).
- The spec is the SEQUENCE (Rule #8): the right algorithm at the wrong point in the
  pipeline is a violation.
- Diagnose root cause at the source; no iterative scrambling, no system changes on
  guesses; profile before batching/SIMD (02 L6/L7).
- SQL/C# orchestrate; native C does heavy lifting; KNN comparison points must be bound
  parameters (06 Rules #1/#4); one implementation per fact — duplication needs a
  documented reason (Rule #6).
