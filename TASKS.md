# Laplace current task index

This file is a navigation/status index. It is **not** invention authority and it does not impose a fixed global execution order.

Authority is defined by `AGENTS.md`, `docs/README.md`, `docs/INVENTION.md`, `docs/INVENTIONS.md`, the binding specs, and the current inventor request. GitHub issues own bounded implementation/acceptance work; code/runtime/CI prove implementation state.

Historical recovery diaries, database snapshots, old branch state, and cross-repository coordination plans must not be treated as current truth merely because they once appeared in this file.

## Inventor-pointed work stack (session 2026-09-19)

A later prompt does **not** replace earlier ones. This stack is cumulative. Implementation continues from the top of the still-open items without discarding the rest.

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
