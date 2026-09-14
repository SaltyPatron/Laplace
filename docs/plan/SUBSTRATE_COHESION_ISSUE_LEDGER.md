# Substrate cohesion issue ledger

Status: **historical campaign index; detailed 2026-08-20 matrix is preserved in Git history.**

This file no longer assigns global scheduling priority or current completion percentages. The original ledger was valuable evidence from the SQL/substrate campaign around #1132, but its row-by-row status was tied to the 2026-08-20 source/database state and is unsafe as a current execution plan.

Current authority is:

1. current inventor instruction;
2. `docs/INVENTION.md` / `docs/INVENTIONS.md`;
3. binding specs and `AGENTS.md`;
4. current decisions/plans;
5. owning GitHub issues/PRs;
6. current code/tests/live evidence.

The issues below remain useful ownership/history references. Their present state and acceptance must be read from GitHub, not inferred from this dated ledger.

## Cohesion issue families retained from the campaign

### SQL / operation-surface / execution grain

- #1135 — repository SQL audit/remediation/non-regression;
- #1047 / #1181 — canonical scalar/batch/set semantics and parity;
- #588 — prepared native/SPI/managed plan reuse;
- #951 / #811 — SQL/C#/native operation ownership and thin adapters;
- #989 — health/discovery versus full census work.

Current governing correction: execution grain is a **repository-wide law**, not only a SQL-read optimization. Decomposition, ingestion, cognition, analysis/domain engines, reconstruction, synthesis and export all owe coarse native/set execution for repeated algorithmic work.

A nominal batch that loops scalar DB/PInvoke/SPI operations is still RBAR. PostgreSQL owns durable indexed state/set access, SPI is the prepared set-sized bridge, and native C/C++ owns hot loops/recursion/frontier/reduction/format kernels where applicable.

### Identity / recursive composition / physicality

- #1045 / #1048 — recursive content composition and trajectory integrity;
- #1052 / #1008 — entity/storage uniqueness and tier/identity interaction;
- #1132 — substrate cohesion/invariant campaign;
- #959 — loud same-id/different-preimage collision handling;
- #1443 / #1451 — semantic identity independent of physical parallel scheduling.

Current governing correction: normal same-content convergence is not called a cryptographic collision. Multi-child native identity currently includes the declared tier in the Merkle recipe; single-child composition collapses to the child id. Exact ordered structure lives in trajectories/DAGs, not in one coordinate or hash alone.

Current geometry documentation must also preserve the distinction between real `coord`, mantissa-packed trajectory carrier and realized child-coordinate curve. Native centroid and managed Karcher parent-coordinate laws are presently divergent and must not be described as one rule.

### Modality / media / occurrence

- #1133 — modality registry/masks;
- #1134 — exact media reconstruction/physical recipes;
- #1180 — source occurrence/provenance versus canonical content;
- #1177 — decomposer normalization and source fidelity.

Current governing correction: modality changes the typed decomposition grammar, not the underlying identity/evidence/cognition machine. Source/language/path/batch/worker facts do not silently salt canonical content unless the declared content recipe actually includes them.

### Perfcache / derived accelerators

- #1043 — T0 codepoint perfcache law;
- #469 / #529 — highway/perfcache/mask algebra;
- #838 — chess deterministic cache companion work.

Current governing correction: perfcaches/indexes are derived accelerators. A miss cannot change canonical identity, evidence or authoritative answer semantics.

### Fold / evidence / standing

- #964 and related fold-throughput work;
- #1303 / #1321 and later standing/evidence corrections where still open/relevant.

Current governing correction: identity, attributed testimony, deterministic calculation, provenance/dependence and folded standing are distinct state classes. Glicko standing is queryable evidence/uncertainty state, not truth or universal relevance.

### Query / cognition / serving

The 2026-08-20 ledger predated the current canonical forward-program and the later coupling-field correction. Current authority is `docs/specs/36_Laplace_Forward_Pass.md` and `docs/specs/37_Substrate_Operation_ISA.md`:

```text
RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE
        → PROPOSE → STEER → SELECT → REALIZE → WITNESS
```

Whole-observation coupling precedes unconstrained policy/provider selection. Hops/fanout are explicit sparse-compute axes over one knowledge world. A*, walk, continuation, containment and geometry are operators inside that program.

## Current proof/status rule

Do not reuse the old ledger's labels as current delivery claims.

A current status claim needs the evidence appropriate to the claim:

```text
mathematical invariant -> proof
implementation invariant -> executable/property/conformance test
live substrate invariant -> live readback/counterexample scan
performance -> exact-revision/artifact/host/provider receipt
product delivery -> installed/deployed operator-visible proof where required
```

A merged PR proves landing, not deployment or product acceptance. An issue state proves administration, not runtime behavior.

## Why the detailed matrix was retired from current view

The original matrix embedded then-current row counts, implementation percentages, campaign ordering and “not done/partial/done” snapshots. Those values became stale while remaining visually authoritative. Keeping them in a live scheduling document risked three failures:

1. implementing August priorities instead of the current accepted task;
2. interpreting old partial mechanisms as the invention limit;
3. using old implementation gaps to overwrite stronger later architecture/proof law.

The detailed 2026-08-20 matrix remains recoverable from Git history for archaeology and regression investigation.
