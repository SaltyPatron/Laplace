# Laplace documentation map

This directory contains invention law, implementation architecture, binding design contracts, current evidence, product/domain guides, and historical material. Those categories are not interchangeable.

## Read order

1. [`INVENTION.md`](INVENTION.md) — canonical intended invention, theorem, recursive representation, coupling model, evidence model, execution model, and proof obligations.
2. [`CAPABILITIES.md`](CAPABILITIES.md) — binding synthesis of what those primitive laws enable together. Read this before narrowing work to a local test, gate or subsystem.
3. [`INVENTIONS.md`](INVENTIONS.md) — mechanism/capability catalog. It summarizes the invention; it does not override `INVENTION.md`.
4. [`../AGENTS.md`](../AGENTS.md) — execution/authority rules for applying the invention during repository work.
5. [`ARCHITECTURE.md`](ARCHITECTURE.md) — current source/as-built architecture. A mismatch with the invention is an implementation gap unless higher authority changes the invention.
6. [`specs/README.md`](specs/README.md) — binding component contracts interpreted under the invention and capability synthesis.
7. GitHub issues — bounded implementation/acceptance ownership, interpreted under the authority above.
8. Current code, tests, CI, live receipts and measurements — evidence of what is actually implemented.
9. Dated plans, campaign ledgers, audits, recovery notes and archived documents — historical evidence only unless explicitly reactivated.

The latest prompt does not erase higher-level project scope. A status file, issue body, old plan or another repository cannot silently narrow the invention or replace an explicit current inventor correction.

## Core invention / architecture

- [`INVENTION.md`](INVENTION.md) — head-to-tail invention definition and proof model.
- [`CAPABILITIES.md`](CAPABILITIES.md) — binding synthesis of the compound product consequences: knowledge authority, measured cognition, structural software construction, duplicate-code convergence, repair trajectories, machine-cost derivation and root-transition deployment.
- [`INVENTIONS.md`](INVENTIONS.md) — concise mechanism catalog.
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — current implementation architecture and known divergences.
- [`audits/INVENTION_DEVIATION_LEDGER_2026-09-19.md`](audits/INVENTION_DEVIATION_LEDGER_2026-09-19.md) — current-vs-historical deviation ledger tied to concrete code/issues.
- [`INVENTORY.md`](INVENTORY.md) — generated repository inventory/counts.
- [`specs/33_Perfcache_Blob_Law.md`](specs/33_Perfcache_Blob_Law.md) — derived ROM law and the current blob roster (T0, highway, numbers, chess, factor/generation gaps).

Important current distinctions include:

```text
canonical content / recursive composition
        !=
coord              real geometric placement
        !=
packed trajectory  exact constituent manifest
        !=
realized curve     ordered child placements
        !=
occurrence / source / testimony / deterministic calculation
```

The bounded-domain theorem is dimension/radix independent. Current 4D/binary64 is an executable carrier choice; the four components provide 212 reversible payload bits per packed trajectory vertex.

## Cognition / forward execution

The governing contracts are:

- [`specs/36_Laplace_Forward_Pass.md`](specs/36_Laplace_Forward_Pass.md)
- [`specs/37_Substrate_Operation_ISA.md`](specs/37_Substrate_Operation_ISA.md)
- GitHub issue #1401 for current implementation acceptance.

The canonical program is:

```text
RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE
        → PROPOSE → STEER → SELECT → REALIZE → WITNESS
```

`COUPLE` is the query-relative response field: the complete admitted observation tugs every eligible indexed plane before unconstrained interpretation/routing is frozen. A*, Dijkstra, walks, trajectory continuation, containment, geometry, chess search and other domain operators are tools inside the program rather than separate cognition definitions.

Functional Q/K/V/O correspondence is documented as a comparison of jobs, not an instruction to rebuild a transformer internally.

## Execution-grain law

The physical execution unit is not automatically the semantic object.

Across ingest, decomposition, composition, cognition, domain search, analysis, reconstruction and export, repeated work should cross SQL/SPI/managed/native boundaries at the coarsest lawful set/batch frontier.

Per-row/per-candidate/per-node calls, recursive SQL used as a hot inner engine, or scalar loops disguised as batches remain performance/architecture defects even when their semantic outputs are correct.

See [`plan/INGEST_BOUNDARY_AND_RECIPE_LAW.md`](plan/INGEST_BOUNDARY_AND_RECIPE_LAW.md) and the forward-pass/ISA specs.

## Benchmarks / capacity evidence

- [`benchmarks/MANUAL_BENCHMARK_EVIDENCE.md`](benchmarks/MANUAL_BENCHMARK_EVIDENCE.md) — benchmark suite and evidence law.
- [`benchmarks/SCALING_MODES.md`](benchmarks/SCALING_MODES.md) — file-grain makespan vs independent-stream scaling vs single-DAG frontier scaling.

Managed-host capacity evidence distinguishes **serviceable throughput** from **explicit saturation**. The workflow now derives serviceable worker points with reserved CPU headroom; full logical-CPU saturation requires opt-in and is a different experiment.

## Product / domain guides

Guides under [`guides/`](guides/) consume the common machine. They do not define private intelligence stacks.

Examples:

- [Structural software construction](guides/software-construction.md) — grammar/AST construction, canonical reuse, duplicate convergence, witnessed repairs, cross-repository maintenance and root-transition deployment.
- [Knowledge authority, governance and security](guides/knowledge-authority.md) — knowledge packages, capabilities, honest abstention, Red Spear / Blue Shield / White Judge and effective-mind composition.
- [Machine-cost analysis and measured cognition](guides/machine-cost-analysis.md) — artifact→ISA/microarchitecture cycle derivation and compute-depth billing.
- [Compositional perfcache lattice](guides/compositional-perfcache.md) — dense/sparse mmap ROMs for reusable scalar/pixel/patch/region/image/audio/chess/code structures and cross-modality cache reuse.
- Knowledge Arena — pinned-world games exposing paths, evidence, hops/fanout and receipts.
- Name Game — human-vs-Laplace resolution/identity/event latency proof.
- Chess Forward Pass — chess as a cross-modal proving domain using the same coupling/ISA program.

Product examples do not limit the general invention.

## Plans / status

Files under [`plan/`](plan/) are decomposition, acceptance or historical campaign aids. No fixed order inside a plan outranks the current inventor request or `AGENTS.md` authority rules.

Current-facing compatibility files such as `COMPLETION_PLAN.md`, `SESSION-AUDIT.md` and older dated remediation records have been reduced/demoted where necessary so they cannot masquerade as current global status.

## Historical / archive

[`archive/`](archive/) contains superseded specifications, reports, plans, status snapshots and old agent material. It is evidence/chronology only.

If historical material conflicts with current invention/specs, the historical material does not win. If it exposes an unresolved implementation defect, the defect may still be real and should be re-verified against current source/runtime rather than dismissed.

## Cross-repository references

Other repositories, including `Laplace-Refactor`, may contain related implementations, issues and experiments. Those are coordination/comparison links unless the current user explicitly scopes work there. They do not own or narrow this repository's invention by default.
