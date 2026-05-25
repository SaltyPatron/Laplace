# ADR 0060: Retire the chunk-sequence roadmap — track by v0.1 milestone + component, reconcile the tracker to code reality

## Status

**Accepted** — 2026-05-25
**Authors:** Anthony Hart

## Context

Issues #1–#8 ("Chunk 1" … "Chunk 8") were authored as a *sequential* roadmap. Development never followed that sequence, and pretending it did created false roadblocking:

- The **foundation** (Chunks 1–3: core math, geometry serde + opclasses, perf-cache + T0 seed) was built and is CI-green.
- The **framework** landed in parallel under a separate track — the "Framework Epic — Decomposer + Ingestion generics (Chunks A–F)" (#232) — not in chunk order.
- The **infrastructure** (custom PG/PostGIS #119, unified CMake #120, MKL/Eigen/Spectra/TBB #121, modularization #118) landed as its own epics.

Worse, the **issue tracker drifted out of sync with the code**: as of 2026-05-25 roughly fifteen stories were fully implemented and covered by green CI yet still marked open — the chunk-2 4D `ST_*_4d` wrappers (#30–#36), the chunk-2 pg_regress suite (#38), and the D-stories for engine modularization + MKL/TBB (#160–#163). Reading the open-issue list gave a picture of "barely started" when the foundation was in fact done. That gap is what made development feel blocked "by stupidity."

## Decision

**1. Retire chunks-as-a-sequence.** The `chunk-N` labels remain only as historical grouping; they carry **no ordering obligation**. Nothing "waits for the previous chunk."

**2. Reconcile the tracker to code reality** (done this session):

- Closed, with per-issue evidence (file + test + CI), the implemented & CI-tested foundation stories: #30–#36, #38 (laplace_geom 4D layer + pg_regress), #160–#163 (engine modularization + MKL/TBB + ISA).
- Closed the **functionally-complete foundation epics** #1 (core math) and #2 (geometry serde + GIST). Residual benchmark/functional-test items remain as their own low-priority open issues (#168, #169, #170, #171) — implementation done, only the benchmark/test left; not blockers.
- Closed #29 as **superseded** (ADR 0029 chose custom S³-aware opclasses over stock `gist_geometry_ops_nd`).
- Kept **Chunk 3** (#3) open for genuine hardening: full byte-for-byte cross-verify (#49, currently sampled) and the cross-machine determinism CI gate (#50, missing).
- Closed #258 by adding a `dotnet-test` job to `integration.yml` (CI previously ran `dotnet build` but no `dotnet test`, so C# regressions passed silently).

**3. Forward work tracks against the `v0.1 — model round-trip` milestone**, not chunk numbers. The decisive v0.1 deliverable per [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md) is **one faithful source-model round-trip** (model → substrate → native package → GGUF proof export → chat). Its critical path is on the milestone: #6 (TransformerModelSource + Procrustes + sparsity), #191 (ModelDecomposer), #7 (synthesis → GGUF), #8 (round-trip + chat), #50 (determinism gate), #181 (ADR 0037 acceptance). The broader decomposer seed-ladder (#184–#194) is **fidelity enrichment, not v0.1-blocking** (CLAUDE.md hard rule 9).

**4. The durable record stays GitHub Issues + ADRs** per [ADR 0017](0017-agent-operating-cadence.md). No `STATE.md` / status cadence file (tried before; degraded into a conversation log).

## Consequences

- The open-issue list now reflects reality: foundation done + CI-gated, forward path visible on one milestone.
- "Where do we stand" is answerable from the milestone + the chunk-3 residuals, not archaeology across 97 issues.
- The CI gate now spans all four layers — engine ctest, pg_regress, the unified build, and (newly) `dotnet test` — so regressions fail the build instead of slipping through.
- Foundation hardening (determinism CI #50/#164/#165, full cross-verify #49, opclass benchmarks #168/#169) is explicit and de-prioritized rather than masquerading as "not started."

## References

- [ADR 0017](0017-agent-operating-cadence.md) — agent operating cadence; issues + ADRs as durable record
- [ADR 0022](0022-adrs-as-decision-format.md) — ADRs as the decision format
- [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md) — the model-round-trip definition of v0.1
- [ADR 0029](0029-custom-indexing-strategy.md) — custom S³-aware opclasses (supersedes #29)
- `v0.1 — model round-trip` milestone (GitHub milestone #2)
- Issue #258 — CI now runs `dotnet test`
