# Laplace documentation map

The `docs/` tree contains both current authority and historical evidence. Do not treat every Markdown file as an equal specification.

## Read in this order

1. [`../README.md`](../README.md) — technical introduction and capability map.
2. [`INVENTION.md`](INVENTION.md) — the invention, mathematical construction, proof boundaries and intended machine.
3. [`INVENTIONS.md`](INVENTIONS.md) — mechanism/capability catalog.
4. [`../AGENTS.md`](../AGENTS.md) — implementation-agent authority, execution-grain and delivery contract.
5. [`ARCHITECTURE.md`](ARCHITECTURE.md) — architecture as currently built, including explicit divergences/unfinished proof obligations.
6. Binding current specs, especially:
   - [`specs/36_Laplace_Forward_Pass.md`](specs/36_Laplace_Forward_Pass.md)
   - [`specs/37_Substrate_Operation_ISA.md`](specs/37_Substrate_Operation_ISA.md)
   - the substrate/engineering/record-vs-calculate/perfcache/provenance specs named by `AGENTS.md`.
7. Current decisions/plans/guides for a particular product or implementation area.
8. Current GitHub issues/PRs for acceptance and execution tracking.
9. [`archive/`](archive/) only for historical evidence, rejected designs, old measurements and chronology.

A current inventor correction outranks all derived documents.

## What the current authority says in one page

Laplace is a deterministic, content-addressed, recursively compositional knowledge/cognition substrate.

Exact finite structures are composed from already admitted structures and retain their exact ordered constituent trajectories. The current native centroid realization remains inside a fixed bounded 4D ball; the general recursive/countability/convex-closure argument is not tied to binary notation or exactly four dimensions.

The current four-component binary64 trajectory carrier has 212 reversible payload bits—enough for the complete 128-bit constituent id plus ordinal, run-length and typed flags. Packed carrier numbers are serialization, not child positions; realized geometry resolves those ids to live child coordinates.

Canonical identities participate in many overlapping structures at once: recursive DAGs, containing trajectories, occurrences, typed relations, evidence/consensus, contexts/sources and geometric/index neighborhoods. This is the spider-colony web.

The forward program therefore starts from the complete admitted observation and computes a query-relative typed response/coupling field before unconstrained policy is frozen:

```text
RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE
        → PROPOSE → STEER → SELECT → REALIZE → WITNESS
```

A*, Dijkstra, walk, containment, continuation and geometry are operators inside that program. Hops and fanout are explicit sparse-compute axes over one knowledge world.

The physical execution law applies everywhere:

```text
indexed / set-sized boundary
-> native C/C++ repeated algorithmic work
-> bulk / set result + receipt
```

PostgreSQL owns durable indexed state and set access. SPI is the prepared/set-sized bridge. C#/SQL orchestrate contracts/transport. Decomposition, ingestion, cognition, analysis/domain engines, reconstruction, synthesis and export all obey the same execution-grain law.

## Intended law versus current implementation

Do not repair a mismatch by rewriting the invention around whichever implementation was easiest to find.

`ARCHITECTURE.md` currently records material obligations including:

- native centroid versus some managed/domain Karcher parent-coordinate paths;
- exhaustive live recursive physicality/trajectory/reference closure proof not yet landed;
- complete typed coupling-field coverage still needing channel-by-channel proof;
- remaining legacy RBAR/small-boundary execution across pipelines;
- benchmark service-headroom enforcement still needing executable protection.

Those are implementation/proof obligations.

## Evidence classes

Use the right evidence for the claim:

```text
mathematical claim        -> mathematical proof
serialization/invariant   -> executable/property/conformance test
finite implementation set -> exhaustive finite check where feasible
live substrate claim      -> live query/readback/counterexample scan
performance claim         -> exact revision/artifact/host/provider/workload receipt
product delivery          -> installed/deployed operator-visible proof where required
```

See [`benchmarks/MANUAL_BENCHMARK_EVIDENCE.md`](benchmarks/MANUAL_BENCHMARK_EVIDENCE.md) for benchmark evidence and serviceable-capacity versus saturation rules.

## Status documents are not specifications

Dated status/plan files may contain excellent measurements and still be obsolete as current action ordering. Current-facing dated reports that became misleading have been demoted to historical snapshots; their original detail remains in Git history.

An issue/plan/status comment may describe missing implementation. It may not silently turn an MVP, fallback, lower-compute approximation, one search operator, a smaller model, or an old implementation limitation into the definition of Laplace.

## Cross-repository references

`Laplace-Refactor` and other repositories may contain related implementations, counterexamples or coordination issues. They do not automatically own the meaning of the invention in this repository.

Compare implementations against the current semantic laws above rather than treating one repository's schema/ABI/status as normative for the other.
