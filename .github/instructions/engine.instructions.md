---
description: "Use when editing the C/C++ substrate engine under engine/ — determinism discipline, ISA/toolchain rules, native math/Glicko/geometry kernels, and GoogleTest/CTest. Keywords: engine, liblaplace_core, dynamics, synthesis, glicko2, trajectory, hilbert4d, super_fibonacci, AVX2, AVX512, fast-math, ctest."
applyTo: "engine/**"
---
# Engine (C/C++) — substrate primitives

Read [AGENTS.md](../../AGENTS.md) first for the invariants; this file is the engine-local layer.

- The engine owns deterministic primitives only — hashing, S^3 geometry, Glicko-2, trajectories, dynamics, synthesis kernels. No application logic, no DB orchestration; that lives in `app/`.
- Determinism is binding: never add `-ffast-math` or fp-contract fusion on hot paths. The build pins `-fno-fast-math` and `-ffp-contract=off` at [engine/CMakeLists.txt](../../engine/CMakeLists.txt#L15-L21).
- Honor the ISA target knob (`LAPLACE_TARGET_ISA` = AVX2 dev / AVX512 deploy); do not hardcode `-march`. See [engine/CMakeLists.txt](../../engine/CMakeLists.txt#L23-L30).
- Unicode/UCD versions are pinned for hash stability (17.0.0). Do not bump to a non-stable release — it shifts every substrate hash. See [engine/CMakeLists.txt](../../engine/CMakeLists.txt#L36-L52).
- Use compensated f64 and fixed reduction order on math paths; the Glicko-2 kernel (`glicko2.c`) is paper-pinned and bit-checked by regress — change per-row arguments, not the math.
- Build and run engine tests with `just build` then `just test-engine` (GoogleTest via `gtest_discover_tests`, CTest, with the build-tree `LD_LIBRARY_PATH` set). Do not run raw `ctest` from a stale install. See [Justfile](../../Justfile#L362-L367).
