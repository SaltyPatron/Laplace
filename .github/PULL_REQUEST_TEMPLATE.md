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

## Architectural impact

<!-- Touched any of these? Note here. Otherwise "none". -->

- [ ] Schema (entities / physicalities / attestations)
- [ ] Engine C ABI (laplace.h)
- [ ] PG extension surface (laplace--*.sql)
- [ ] C# app boundary (P/Invoke signatures)
- [ ] Plugin interfaces (ISource / IDecomposer / etc.)
- [ ] Build system (CMakeLists.txt / Makefile / Justfile)
- [ ] Dependencies (STANDARDS.md dep table)

## Decisions to record

<!-- If this PR makes an architectural decision, note it here so it can be added to .agent/status/decisions.md when merged. -->

## Notes for reviewer

<!-- Anything that needs context, caveats, or special attention. -->
