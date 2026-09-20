# Laplace current task index

This file is a navigation/status index. It is **not** invention authority and it does not impose a fixed global execution order.

Authority is defined by `AGENTS.md`, `docs/README.md`, `docs/INVENTION.md`, `docs/CAPABILITIES.md`, `docs/INVENTIONS.md`, the binding specs, and the current inventor request. GitHub issues own bounded implementation/acceptance work; code/runtime/CI prove implementation state.

Historical recovery diaries, database snapshots, old branch state, and cross-repository coordination plans must not be treated as current truth merely because they once appeared in this file.

## Whole-machine invariants (not a scheduling order)

The numbered items below preserve cross-cutting machine constraints discovered during active work. They are **not** a global backlog, finish-line sequence, or permission to ignore the current accepted user scope. Current inventor instruction and explicit ordering select the work; these invariants constrain how that work is implemented. Do not pick an earlier number merely because it is easier to turn into a test/gate.

1. **Hold the whole machine.** ISA / OODA / Gödel; personality firmware ≠ knowledge; kernel governance; seeded vs user vs snapshot export; Mold-A-Model realizes Q/K/V/O/gate/up/down/norm/embed from substrate; Laplace-builds-Laplace is the closed loop — not a side quest.
2. **Live host is the evidence.** Web/API/MCP/OpenAI, Postgres, `pg_stat_statements`, ingest journals, `/opt/laplace`, `/vault`. Status prose and GitHub titles are not truth.
3. **One deployed revision.** Application DLLs, prefix native libs, PostgreSQL `laplace_execution_*` MODULE, T0 LPRF v4, extension catalog (including `physicality_observations`) are one build. `check-deployed-revision.sh` exits 0. CI *installs*; skip-success and qualify-cancel are not delivery. Do not race a live prefix hack against an in-flight deliver.
4. **Honest surfaces.** Health/capabilities/docs/issues match as-built. No scaffold stream. Ready means this process loaded T0.
5. **Perfcache is used compositionally.** T0 `records[cp]`; no Unicode re-record; highway/numbers/chess actually called; missing Factor/GenCorpus/separator stay explicit. The 0..255 number ROM accelerates canonical number roots; it is not the numeric universe. `0.34567` still composes as `0 . 3 4 5 6 7`, and a finite π prefix is the same wide reusable composition. Repeated equal scalar values add occurrences, not new scalar content. Higher deterministic tiers may also be mmap ROMs: pixels → patches → regions → images; samples → windows → tracks. Video reuses image/audio caches. #1711 owns the common cache registry/lattice.

    Unicode sequence (generate then execute, like EF/SSIS): (1) decomposer/tool reads UCD source, (2) emit native tables + persist T0 perfcache blob, (3) install those artifacts when UCD actually changed — isolated `ninja laplace_t0_perfcache`, not every product SHA, (4) execute native admission so T0 entities/physicalities are **in Postgres** as the FK anchor. The blob is ROM for `records[cp]`. It is not the populate path. T0 belongs in the database because everything else references it.

    Highway is both the relation-law ROM (`laplace_mask256_t`, bit, band, default rank) **and** a 32-byte bit-bang on the entity (`entities.highway_mask`) for search/filter/query with indexes — not a second graph. Noun vs verb, synonym/antonym/meronym, definitional vs oppositional are bits/bands whose **importance is query-relative** (STEER), same class of head/plane as Fréchet, angular, Karcher, trajectory. Warehouse unbanded μ (ISO `HAS_VARIANT_OF` on top; gloss-labeled `HAS_DEFINITION`) is ranking with the highway off. Live `relation_band_live_counts` empty is the catalog not following the ROM. Do not collapse this to one relevance scalar.
6. **Ingest grain.** Generic recipe multi-file ETL: native compose → bulk identity → COPY → **set-sized fold**. Cancel closes files. Stop SQL `upsert_evidence_type` / scalar `FOR UPDATE` as the fold.
7. **Identifier stack and GeometryZM.** Unicode/ISO/CILI/synset/frame; kill regex/Latin fake ORIENT. Packed trajectory ≠ realized curve. Contains/precedes/co-occur from the LINESTRING. O(tier) probes.

    Spec 05/08, not a graph edge: identity ≠ testimony ≠ consensus ≠ occurrence ≠ calculation. An attestation is subject, relation type, optional object, source, optional context, outcome, score, uncertainty inputs, and observation count — and that witnessing occurred. Confirmation, draw, and refutation are distinct; absence is unknown. Recorded (source-observable) and calculated (analyzer/version/recipe) are different witness classes. Consensus is the folded standing cell for one typed triple, not the attestation. Laplace is the parasite that admits, couples, steers, realizes, and witnesses that world. It is not `node → edge → node`.

    ISO, CILI, WordNet/VerbNet/FrameNet, SemLink, Predicate Matrix exist so surface forms **bubble up** to stable numeric identities (Hash128 entity, synset, ILI, frame, roleset). Coupling’s “dot product” and SELECT’s “softmax” run on those ids and typed planes (highway bit, angular, Fréchet, Karcher, standing) — not on UTF-8 and not on a GPU tensor. `substrate/iso639/variant/grclass/v1` as a warehouse label is the ladder failing to surface the bubbled id.
8. **Forward pass fires.** `RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE → PROPOSE → STEER → SELECT → REALIZE → WITNESS`. Empty consensus stays empty (no fake prose). Highway/live bands exist so COUPLE has a field. Firmware steers; prompts do not attest by default.
9. **Mold-A-Model / realize.** Fill transformer slots from substrate operators; export is a recipe snapshot. Not Build-A-Bear fluff; not `cp` ingested GGUF.
10. **Gödel close.** Chat and code as observations; outputs become inputs; Laplace updates Laplace under kernel governance.

11. **Test purpose vs qualification ocean.** ~2700 managed Fact/Theory (Chess 854, Substrate 966, Decomposers 307, OpenAI 264, …). Many are real *unit* proofs of parsers/gates (e.g. vacuous-fact gloss). Qualification treated them as ABI proof for any `extension/` edit and walked them drop-by-drop under 15m. Goldens that freeze `/health` stream names and empty chat as success are ceremony. Native-dev/pg_regress is the prove path for SQL/C. Audit and keep tests that can be false; stop using the slnx ocean as the gate.

12. **CI daisy-chain is not impact.** A `pipeline.sh` or SQL change must not rebuild/test/publish chess lab, API, MCP, UCI, Lichess, or UI. `FULL_PUBLISH_PROJECTS` + `live` on every native install is the 18-minute conveyor. Plan only the surfaces whose bytes change. systemd restart of Postgres is activation of *native prefix*, not a reason to republish CuteChess.

13. **Dated runtime observations are evidence, never scheduling authority.** Live API/DB/host measurements must be re-read when they matter to the accepted task. Historical SHA, install, row-count, latency, CI, or readiness observations in this file do not select work and must not block a later explicit inventor scope. Preserve the durable defect class; remeasure the current instance.

## Durable inventor constraints distilled from the 2026-09-19 session

These are architectural constraints, not current host status and not a scheduling order. Any dated runtime examples that remain below are historical evidence only and must be reverified before use.

### 1. One deployed revision

Application, native prefix, PostgreSQL execution module, perfcache generation and installed extension catalog must identify one coherent build when deployment is being claimed. `check-deployed-revision.sh` and process-local readiness are evidence tools, not a global prerequisite for unrelated source work. Re-read the actual host before making a deployment claim.

Do not treat `SaltyPatron/Laplace-Refactor` as this repository's product unless the inventor explicitly scopes work there.

### 2. CI/CD that actually installs

Delivery workflows must be dependency-aware, must not report skipped installation as delivered behavior, and must leave source/build/install/runtime identities receipted. CI structure serves delivery; it must not become an all-repository test conveyor or a reason to delay unrelated implementation.

### 3. Honest operator surfaces and authority

Health/capability/product surfaces describe as-built state. No scaffold response may masquerade as product behavior. Issues/docs/AGENTS/TASKS do not advertise runtime behavior that current installed evidence cannot support.

### 4. Perfcache as derived ROM

T0, highway, common number roots, chess floors and future ROMs are deterministic rebuildable accelerators. They do not populate or redefine semantic authority. After Unicode atoms are seeded, ordinary content reuses them. Numeric media values compose above that floor: `255 → 2,5,5`; `0.34567 → 0,.,3,4,5,6,7`; long finite constants such as π prefixes are wider instances of the same content trajectory. A cache miss falls back to canonical composition, not a new identity law.

### 5. Ingest grain: generic recipe ETL, not fold/drain theater

Law: artifact → provider/parser → native compose → bulk identity/perfcache probe → COPY → set-sized fold → receipt. Multi-file sources execute under one generic recipe/resource machine. Per-file source structure/provenance is retained, while hot persistence/fold work remains coarse and set-sized rather than scalar SQL/SPI/PInvoke loops.

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

### Compound capability preservation

The invention's compound consequences are binding; see `docs/CAPABILITIES.md`. Do not reduce them to isolated tests, gates or helper APIs.

For software work this means: a repository is a recursive application root; generation searches/reuses canonical grammar-derived AST structure before constructing novel nodes; exact and deeper structural/algebraic duplicates are consolidation candidates; compile/test/runtime failures are witnessed repair trajectories; successful repairs can surface similarly shaped problems in other authorized repositories; only the changed subtree/ancestry should be reminted while unchanged structure is reused; full checkout is realization/export of the resulting complete root.

For product/security/billing this means: knowledge remains one shared world; explicit knowledge grants/capabilities govern what a principal may discover/couple/traverse/derive/realize/export/execute; firmware/governance does not erase facts; hops/fanout and physical work govern compute depth/breadth; machine-cost derivation remains exact/symbolic where possible and measurements are witnesses/calibration.

Tests and gates prove those behaviors. They are not permission to stop before the behaviors exist.


Current bounded implementation owners created from this synthesis:

- #1708 — knowledge packages/capabilities/effective-mind authority plus Red Spear / Blue Shield / White Judge enforcement;
- #1709 — control/data-flow-aware program-to-microarchitecture cycle derivation;
- #1710 — complete application-root mutation and exact full-repository realization benchmark;
- #1711 — compositional perfcache lattice for dense/sparse higher-tier ROMs and cross-modality reuse.

### 10. Forward-pass implementation owner

#1401 owns complete canonical forward participation. `generation.forward_program` is the program boundary; COUPLE must tug every eligible **authorized** plane before a default mask freezes interpretation. Operators (A*, walk, chess, containment, geometry) stay inside the program. Any live failure/success claim must be re-read from current runtime evidence.

## Bounded implementation owners — verify current state before acting

Issue ownership below is navigation, not a global order. Current user scope selects the work; issue/code/runtime state must be refreshed when relevant.

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

## Completion evidence

Do not summarize the repository with a recurring global delivered/not-delivered label. Track the exact requested outcome and the evidence boundary that applies to it.

Plans/issues, branch or PR state, local tests, benchmark startup and source inspection are evidence about particular steps. For an operator-visible runtime change, the relevant completion evidence is the authoritative branch plus the applicable CI/build, installed/deployed artifact, readback/receipt and observed behavior. For a documentation-only request, the committed document itself may be the requested artifact.

When a boundary is missing, name that boundary precisely and continue the implementation rather than replacing the work with a generic statement that Laplace is unfinished or "not the full invention."

## Cross-repository references

`Laplace-Refactor` may contain related implementations/issues. Those links are coordination and comparative evidence unless the current user explicitly scopes work there. They do not delegate this repository's invention, acceptance, or implementation obligations away.

## Historical material

Dated session audits, recovery notes, completion plans and campaign ledgers are preserved as evidence in Git history and/or `docs/archive/`. They must be re-verified before being cited as current status.
