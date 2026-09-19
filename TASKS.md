# Laplace current task index

This file is a navigation/status index. It is **not** invention authority and it does not impose a fixed global execution order.

Authority is defined by `AGENTS.md`, `docs/README.md`, `docs/INVENTION.md`, `docs/INVENTIONS.md`, the binding specs, and the current inventor request. GitHub issues own bounded implementation/acceptance work; code/runtime/CI prove implementation state.

Historical recovery diaries, database snapshots, old branch state, and cross-repository coordination plans must not be treated as current truth merely because they once appeared in this file.

## Total order of operations (binding)

A later prompt does **not** replace earlier ones. Do not jump to a newly mentioned subsystem because it is locally easier. Execution is this order. Invention notes below are constraints on *how* each step is done, not permission to skip ahead.

1. **Hold the whole machine.** ISA / OODA / Gödel; personality firmware ≠ knowledge; kernel governance; seeded vs user vs snapshot export; Mold-A-Model realizes Q/K/V/O/gate/up/down/norm/embed from substrate; Laplace-builds-Laplace is the closed loop — not a side quest.
2. **Live host is the evidence.** Web/API/MCP/OpenAI, Postgres, `pg_stat_statements`, ingest journals, `/opt/laplace`, `/vault`. Status prose and GitHub titles are not truth.
3. **One deployed revision.** Application DLLs, prefix native libs, PostgreSQL `laplace_execution_*` MODULE, T0 LPRF v4, extension catalog (including `physicality_observations`) are one build. `check-deployed-revision.sh` exits 0. CI *installs*; skip-success and qualify-cancel are not delivery. Do not race a live prefix hack against an in-flight deliver.
4. **Honest surfaces.** Health/capabilities/docs/issues match as-built. No scaffold stream. Ready means this process loaded T0.
5. **Perfcache is used.** T0 `records[cp]`; no Unicode re-record; highway/numbers/chess actually called; missing Factor/GenCorpus/separator stay explicit.

    Highway is both the relation-law ROM (`laplace_mask256_t`, bit, band, default rank) **and** a 32-byte bit-bang on the entity (`entities.highway_mask`) for search/filter/query with indexes — not a second graph. Noun vs verb, synonym/antonym/meronym, definitional vs oppositional are bits/bands whose **importance is query-relative** (STEER), same class of head/plane as Fréchet, angular, Karcher, trajectory. Warehouse unbanded μ (ISO `HAS_VARIANT_OF` on top; gloss-labeled `HAS_DEFINITION`) is ranking with the highway off. Live `relation_band_live_counts` empty is the catalog not following the ROM. Do not collapse this to one relevance scalar.
6. **Ingest grain.** Generic recipe multi-file ETL: native compose → bulk identity → COPY → **set-sized fold**. Cancel closes files. Stop SQL `upsert_evidence_type` / scalar `FOR UPDATE` as the fold.
7. **Identifier stack and GeometryZM.** Unicode/ISO/CILI/synset/frame; kill regex/Latin fake ORIENT. Packed trajectory ≠ realized curve. Contains/precedes/co-occur from the LINESTRING. O(tier) probes.

    ISO, CILI, WordNet/VerbNet/FrameNet, SemLink, Predicate Matrix exist so surface forms **bubble up** to stable numeric identities (Hash128 entity, synset, ILI, frame, roleset). Coupling’s “dot product” and SELECT’s “softmax” run on those ids and typed planes (highway bit, angular, Fréchet, Karcher, standing) — not on UTF-8 and not on a GPU tensor. `substrate/iso639/variant/grclass/v1` as a warehouse label is the ladder failing to surface the bubbled id.
8. **Forward pass fires.** `RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE → PROPOSE → STEER → SELECT → REALIZE → WITNESS`. Empty consensus stays empty (no fake prose). Highway/live bands exist so COUPLE has a field. Firmware steers; prompts do not attest by default.
9. **Mold-A-Model / realize.** Fill transformer slots from substrate operators; export is a recipe snapshot. Not Build-A-Bear fluff; not `cp` ingested GGUF.
10. **Gödel close.** Chat and code as observations; outputs become inputs; Laplace updates Laplace under kernel governance.

11. **Test purpose vs qualification ocean.** ~2700 managed Fact/Theory (Chess 854, Substrate 966, Decomposers 307, OpenAI 264, …). Many are real *unit* proofs of parsers/gates (e.g. vacuous-fact gloss). Qualification treated them as ABI proof for any `extension/` edit and walked them drop-by-drop under 15m. Goldens that freeze `/health` stream names and empty chat as success are ceremony. Native-dev/pg_regress is the prove path for SQL/C. Audit and keep tests that can be false; stop using the slnx ocean as the gate.

12. **CI daisy-chain is not impact.** A `pipeline.sh` or SQL change must not rebuild/test/publish chess lab, API, MCP, UCI, Lichess, or UI. `FULL_PUBLISH_PROJECTS` + `live` on every native install is the 18-minute conveyor. Plan only the surfaces whose bytes change. systemd restart of Postgres is activation of *native prefix*, not a reason to republish CuteChess.

13. **Live product identifiers (API 2026-09-19).** `/v1/explore/catalog` top_relations is not the invention: `substrate/iso639/variant/grclass/v1` as a label; HAS_VARIANT_OF winning unbanded μ; HAS_DEFINITION subject rendered as the gloss so both columns match (`00021f56…` “a shout or song of praise to God” HAS_DEFINITION the same gloss). `ops.top_relations_readable` labels the word (`hallelujah`). MCP `define king` returns senses. `Unrealized entity` is a fallback in SQL/C#/walk UI when display_label has no surface. Warehouse and leaders/home disagree. This is identifier-stack + realize + ranking (steps 7–8), not a warehouse MVP. Use API/MCP as evidence; do not invent a smaller product.

**Where this session actually is:** step 3 incomplete (CI `ab71b189` still qualifying; live libs/MODULE rebound by hand; `physicality_observations` created by hand; app DLLs still Sep 17 so `/health` still `F-scaffold`; no application receipt). Chat WITNESS works; REALIZE returned 0 rows. That is a step-8 symptom. Do not start step 8/9 until step 3 is a real install.

## Inventor-pointed constraints (session 2026-09-19)

Cumulative. These constrain the steps above; they are not a second backlog to wander through.

### 1. One deployed revision (hart-server)

Live prefix is split. Application, `/opt/laplace/app` native libs, `/opt/laplace/lib` native libs, PostgreSQL `laplace_execution_*` MODULE bindings, and T0 LPRF v4 must identify one build. `scripts/check-deployed-revision.sh` must pass. `/health/ready` must load T0 in the API process, not only via Postgres `word_id`. Chat/MCP/OpenAI must stop dying on missing MODULE / `witness_unavailable`.

Do not treat `SaltyPatron/Laplace-Refactor` as this host's product. Refactor is a separate repository.

### 2. CI/CD that actually installs

`Product — main delivery` is one 420-minute self-hosted script pretending to be stages. Qualification `cancel-in-progress` drops in-flight delivers on the next push. “Success” often means skip. `managed-dev` has a 15-minute packed `dotnet test` deadline. cmake can write prefix files then fail `ALTER EXTENSION`, leaving the host split. `/build` disk must stay usable. A green Actions run with skipped install is not delivery.

### 3. Honest operator surfaces and authority

No `F-scaffold`. Health/capabilities describe as-built state. Spec 33 keeps the perfcache roster in the law. Issues/docs/AGENTS/TASKS must not advertise live native forward when WITNESS cannot run. GitHub issues track execution; they do not outrank the invention.

### 4. Perfcache as derived ROM

T0 (`records[cp]` over Unicode), highway bit plane, modality numbers 0..255, chess position/transition. Image/audio/video compose above T0; they do not mint a private alphabet. Glicko is not a blob. Factor/GenCorpus/separator ROMs are still missing. Number ROM has no GUC. After Unicode is seeded, do not re-record codepoints as ingest novelty. SQL/C functions must actually hit the mmap (today the ROM can be mapped while `laplace_execution_*` is unbound).

### 5. Ingest grain: generic recipe ETL, not fold/drain theater

Law: artifact → parser → native compose → bulk identity/perfcache probe → COPY → **set-sized fold** → receipt (`INGEST_BOUNDARY_AND_RECIPE_LAW.md`, `IngestPipeline`). Last night: COPY was coarse; fold was `consensus.upsert_evidence_type` thousands of times and scalar `FOR UPDATE` at minutes; Unicode/WordNet spent 93–96% of wall in fold; PropBank cancel left 331 files open (closed on the live journal; `ingest_run_close` now closes files). Multi-file throughput must crush artifacts in parallel under one recipe, not per-source drain pageantry.

### 6. GeometryZM dual carrier and O(tier)

`coord` = real S³/ball placement. Packed trajectory = 212-bit constituent manifest (id + ordinal + RLE + flags). Realized curve = unpack ids → child coords in ordinal order. Never run Fréchet/Hausdorff on packed hashes. Contains / co-occurrence / precedes are trajectory facts, not GPU kernels. Knowledge can occupy the same region and remain distinct by typed metrics (angular, Fréchet, Hausdorff, Karcher). Existence probes are per-tier bitmaps, not per-atom SQL.

### 7. Seeded world vs user content vs export

Seeded corpora/checkpoints: admit, compose, witness, fold. File is provenance. Bit-perfect GGUF round-trip is not a goal. User content Laplace was asked to keep may require exact realization. Export is a filtered snapshot of current standing/geometry/recipe (“I know kung fu”), not `cp model.gguf`. No GPU required for native relations.

### 8. Cross-lingual identifier stack, not regex/Latin fakes

Unicode + ISO 639 + CILI + synsets + frames are the identifiers. They wire through attestations and relations. Serving must not fake ORIENT with topic regex, Latin-hardcoded reads, or pick-first English `define()`.

### 9. The whole watch: ISA, Gödel, OODA, firmware, governance

One program (spec 37 / 36):

```text
RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE
        → PROPOSE → STEER → SELECT → REALIZE → WITNESS
```

Seeded knowledge is attested at ingest. An unattested user prompt is an observation that gets this loop; WITNESS is optional and after the act. Personality is **firmware** (STEER/SELECT policy: curious vs pick-first, strict thresholds, favor/exclude masks), content-addressed, kernel-enforced, detached from knowledge. Prompt text cannot install firmware or escalate effects. Governance does not rewrite identities. Gödel: outputs become inputs; evaluation is ingest. Leaf springs (T0, COPY, chess, SIMD) are not the movement.

Knowledge that guns, racism, cruelty, or other ugly facts exist is not character and must remain addressable. Character is the firmware/governance over observations, witnesses, proofs, confirmations, trust, and sources: what you favor, exclude, require evidence for, or refuse to *do*. Deleting or refusing to admit a fact because it is unpleasant is governance rewriting knowledge. Standing and permission are not identity.

Product identity (inventor framing, keep with the stack): Matrix construct (“I know kung fu” = realize a skill from current substrate/recipe, not copy weights); Bicentennial Man (identity persists; character accumulates as witnessed observations/habits/firmware versions, not weight drift); I, Robot (laws are kernel firmware/permission, not a system prompt); Eagle Eye’s *scope* (the whole web is addressable) without Eagle Eye’s *failure* (opaque policy, no receipts, silent mutation). No GPU. No context window — the substrate is the memory. Mechanistic interpretability is native: every SELECT/STEER names routes, standing, sources, firmware instruction, and receipt. Replay under the same knowledge + firmware + authority reproduces.

### 10. Forward-pass actually firing on this host

#1401. `generation.forward_program` is named as the program. Live chat dies before WITNESS. COUPLE must tug every eligible plane before a default mask. Operators (A*, walk, chess, containment, geometry) stay inside the program.

## Current high-value obligations

### Live host revision coherence (hart-server)

Observed 2026-09-19. `origin/main` is `ab71b189`. `/opt/laplace/current` still names a 2026-09-17 release. `/opt/laplace/lib` received a 2026-09-18 23:45 cmake install (T0 v4, `laplace_execution_526daf316a640d4b.so`) that aborted during `ALTER EXTENSION`. PostgreSQL functions still bind `laplace_execution_e080f3990ea9221a` (file absent). `/opt/laplace/app` still ships 2026-09-17 `liblaplace_core`, so the API rejects the v4 T0 blob and turn-witness stays offline.

`Product — main delivery` often reports success while skipping install, or cancels qualify when the next push lands. Qualification is one self-hosted job with a 15-minute `managed-dev` test deadline (`LAPLACE_MANAGED_TEST_TIMEOUT`). `/build` was 100% full; orphaned CMake trees were reclaimed (~22G free at last check).

`check-deployed-revision.sh` and process-local T0 readiness landed in `6fe57850`. File-journal close on cancel landed in `ab71b189`; live PropBank leftover files were closed (331 cancelled / 3334 ok). Neither commit is the installed prefix until a deliver actually installs.

Do not treat Laplace-Refactor as the live product.

### Forward-pass completion

Primary current issue: #1401.

The governing forward contract is:

```text
RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE
        → PROPOSE → STEER → SELECT → REALIZE → WITNESS
```

The complete admitted observation must be able to tug every eligible indexed plane before a default sense/provider mask freezes the interpretation. A*, walk, KNN, trajectory continuation, chess search, relation reads and calculators are operators inside this program, not separate cognition engines.

Current code/runtime status must be re-read before changing implementation; dated issue comments and archived forward-path gap reports are not sufficient.

### Benchmark / capacity evidence

Owners include #1432, #1436 and #1451.

The managed-host benchmark workflow now has source changes on `docs/canonical-invention-contract-20260914` that:

- derive an explicit serviceable scale plan before measurement;
- reserve two logical CPUs by default on the managed runner;
- resolve the known 6C/12T host to `1,2,3,4,6,8,10` by default;
- reject scale points above the serviceable ceiling unless `allow_saturation=true` is explicitly selected;
- write `scale-plan.json` and expose the resolved worker/mode in the evidence summary;
- retain full logical-CPU saturation as a separate opt-in experiment.

Actions run `34823625126` remains an incomplete/failing counterexample from the prior all-logical-CPU default. Do not invent a more specific terminal cause without evidence.

The next proof step is CI/source validation plus a new serviceable `scale`/`throughput` dispatch on the fixed revision. #1451 separately owns single-semantic-DAG frontier parallelism; replicated streams are not that proof.

### Bounded-domain executable proof gate

The invention proof is stronger than a prose theorem but the full live recursive CI proof gate is not yet complete.

A correct gate must distinguish:

```text
coord                 real placement
packed trajectory     exact constituent manifest
realized curve        child physicality coordinates ordered by ordinal
```

It must verify bounded parent/child placement, exact unpack/ordinal/RLE count, constituent existence, realized curves, recursive reconstruction where decoders exist, and lawful coordinate collisions. Packed mantissa values must never be tested as if they were spatial positions.

### Execution grain

The same performance law applies across the machine:

```text
semantic structure / operation
        !=
worker / SQL call / SPI call / PInvoke call
```

Repeated hot work belongs at the coarsest lawful native/set boundary with explicit receipts. Per-candidate/per-row/per-node database or managed/native crossings are implementation defects even when semantic results are correct.

### Content identity / physicality consistency

Current source has at least one documented realization divergence: native composition uses Euclidean centroid while managed `NgramTrajectory`/some chess/model paths use Karcher mean. Both preserve the no-outside-boundary invariant, but they are not the same placement recipe. Architecture/tests must not pretend otherwise; implementation should converge on the selected canonical recipe when that decision is applied.

Current Hash128 and binary64 carrier widths are implementation windows, not theorem limits.

## Delivery meaning

A change is not delivered merely because:

- a plan or issue describes it;
- code exists on an unmerged branch;
- a PR is open;
- unit tests pass locally;
- a benchmark starts;
- a source tree looks correct.

Delivery means the authoritative branch, required CI, installed/deployed artifact where applicable, readback/proof, and operator-visible behavior agree with the requested acceptance boundary.

## Cross-repository references

`Laplace-Refactor` may contain related implementations/issues. Those links are coordination and comparative evidence unless the current user explicitly scopes work there. They do not delegate this repository's invention, acceptance, or implementation obligations away.

## Historical material

Dated session audits, recovery notes, completion plans and campaign ledgers are preserved as evidence in Git history and/or `docs/archive/`. They must be re-verified before being cited as current status.
