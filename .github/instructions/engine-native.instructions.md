---
name: 'Native engine rules'
description: 'Layer boundaries and build law for engine/ and extension/ native C/C++'
applyTo: 'engine/**,extension/**'
---
# Native engine rules

## Layer boundaries (structural — do not blur)
- `engine/dynamics` (DLL `laplace_dynamics`): INGEST-side decomposition kernels. All
  dense contractions (GEMM, threshold-pair emission, activation) live here,
  MKL-parallelized. C-ABI, bound via `NativeInterop.cs`.
- `engine/synthesis` (DLL `laplace_synthesis`): OUTPUT side — inference/generation/GGUF
  export (recipe, arch template, qk_pairs, tensor_decompose, format_writer). NOT for ingest.
- `engine/core` (DLL `laplace_core`): hashing, glicko2, hilbert, intent_stage
  (COPY-binary staging), perfcache, grammar tags.
- C#/SQL are ORCHESTRATION ONLY. No GPU code anywhere in `engine/` or `extension/` —
  structural decision, not an omission.

## Build law
- New source file ⇒ add to BOTH the HEADERS and SOURCES lists in that target's
  `CMakeLists.txt`. Ninja auto-reconfigures when a CMakeLists.txt changed.
- Rebuild one target: `cmd /c "call scripts\win\env.cmd && cd build-win && cmake --build . --target laplace_dynamics"`.
- After ANY engine rebuild, run `scripts\win\build-extensions.cmd` — extension DLLs
  statically import `laplace_core_static.lib`, so extension freshness ≠ engine freshness
  (lesson L4). Confirm with `SELECT * FROM substrate_health();` and
  `SELECT * FROM api('<substring>');` — do not treat any single lexical helper
  as a rebuild or invention gate.
- MSB3027 copy failure ⇒ output tree is poisoned ⇒ clean-rebuild (lesson L3).

## Conventions
- `rc == 0` is SUCCESS in this codebase's native lookups (lesson L1).
- perfcache blobs (`laplace_t0_perfcache.bin`, `laplace_highway_perfcache.bin`) are
  deterministic and CI-gated; PG side is gated on the `laplace_substrate.perfcache_path` GUC.
- `engine/manifest/relation_types.toml` generates the 256-bit highway mask (153 bits,
  13 salience bands). It is fixed + live-verified (doc 05 Rule #5) — never backfill old
  DB generations, regenerate them.
