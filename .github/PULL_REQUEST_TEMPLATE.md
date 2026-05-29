<!--
  PR template for Laplace. Most work lands as direct commits to main
  (solo work). PRs are used when changes are large enough to warrant
  a checkpoint review, or when CI signal matters before merging.
-->

## Summary

<!-- One paragraph. What changes, and why. -->

## Linked issue

Closes #

## Acceptance criteria (copy from issue)

- [ ] (criterion 1)
- [ ] (criterion 2)
- [ ] (criterion 3)

## Verification

- [ ] `just build` green locally
- [ ] `just test` green locally
- [ ] `just verify` green locally (if applicable for the chunk)
- [ ] No new banned-vocabulary mentions outside allowlist (RULES.md R0)
- [ ] No `-ffast-math` introduced on hot paths (STANDARDS.md / RULES.md R7)
- [ ] No silent failures, no flat thresholds, no MVPs (RULES.md R9)
- [ ] Prompt/cascade work preserves ADR 0035 (prompt ingestion + prompt-local claim scoping + compiled cascade, no RBAR/recursive CTE/cursor/app-loop hot path)
- [ ] Traversal mode/source scope/evidence traces make abstention, hallucination, and drift inspectable where relevant
- [ ] Consensus/source work preserves ADR 0036 (arena semantics + source trust/source lineage, no raw repetition as truth)
- [ ] Model-ingest/synthesis work preserves ADR 0037 (model ingest is a streaming O(params) ETL of weight tables — significant cells emitted as Glicko-2 matchup observations; never GEMM-at-ingest over vocab², never bit-perfect/round-trip preservation, never a flat top-k that discards the model; synthesis pours substrate facts into a chosen recipe mold, sparse-by-construction with exact zeros where no significant attestation exists — per docs/SUBSTRATE-FOUNDATION.md truths 1, 2, 6, 10)
- [ ] Interior `d×d` tensor axis → token-entity resolution is OPEN per docs/SUBSTRATE-FOUNDATION.md — do not assert a resolution; flag and pin with Anthony

## Architectural impact

<!-- Touched any of these? Note here. Otherwise "none". -->

- [ ] Schema (entities / physicalities / attestations)
- [ ] Engine C ABI (laplace.h)
- [ ] PG extension surface (`.sql.in` modules / generated `--*.sql`)
- [ ] C# app boundary (P/Invoke signatures)
- [ ] Plugin interfaces (ISource / IDecomposer / etc.)
- [ ] Prompt ingestion / cascade traversal
- [ ] Arena semantics / source trust policy
- [ ] Build system (CMakeLists.txt / Makefile / Justfile)
- [ ] Dependencies (STANDARDS.md dep table)

## Decisions to record

<!-- If this PR makes an architectural decision, note it here so it can be added to the relevant ADR under docs/adr/ when merged. -->

## Notes for reviewer

<!-- Anything that needs context, caveats, or special attention. -->
