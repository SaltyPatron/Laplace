# Benchmark suite expansion map

The executable source/core profiles live in `scripts/benchmark-profiles.json`. This file names the next profile boundaries so implementation extends one benchmark system instead of creating disconnected one-off tests.

| Profile | Boundary | Primary evidence |
|---|---|---|
| `core-single` | built native source | single-worker codepoints / token-equivalent / tier-tree nodes |
| `core-scale` | built native source | measured physical-core/SMT scaling curve |
| `moby-roundtrip` | built native + managed source | exact ingest/export timings + bit-perfect bytes |
| `moby-db-roundtrip` | installed/live database | record/persist/read/reconstruct, rows/pages/WAL/storage + bit parity |
| `query-plan-actual` | installed/live query | preflight `EXPLAIN` + actual execution/resource receipt |
| `sparse-address` | installed/native providers | `N`, selected `K`, address work, candidate/filter work, `O(log N)+selected-work` proof |
| `accelerator-sparse` | identical program, alternate physical provider | CPU vs GPU/provider wall/resources, H2D/D2H bytes, VRAM, K, parity |
| `reuse-cold-warm` | same canonical content/program | first observation vs canonical/index/perfcache reuse |
| `storage-census` | installed world epoch | data/index/perfcache/TOAST/dead/free bytes + write/read economics |
| `ud-full` | corpus-scale admission | complete UD Treebanks units/bytes/E-P-A/wall/CPU/memory/I/O |
| `cognition-accepted-work` | complete public route | quality-passing requests + plan/work/resource receipts |
| `model-export` | foundry/target compile | selected scope -> reproducible artifact, bytes/work/resources/parity |
| `competitor-equivalent` | derived comparison | accepted equivalent work under versioned external pricing |

## Complexity receipt

A `sparse-address` result may report the intended `O(log N)+O(K)` form only when the receipt names the exact address provider and the actual selected-work algorithm. `K` is not allowed to mean an undocumented post-hoc sample. If selected work is `K log K`, `K^2`, or iterative, record that instead.

## Accelerator receipt

`accelerator-sparse` changes the physical provider, not the semantic program. It must preserve the same selected IDs/operators and semantic result while recording data transfer and active device state. GPU presence alone is not GPU use; GPU use alone is not GPU-resident world state.

## State-boundary rule

Source/core profiles cannot quietly query or mutate the installed substrate. Installed/live profiles bind package/extension/world/evidence/resource epochs separately from repository source. Corpus profiles bind exact input roots/digests. Derived competitor reports cite the exact underlying Laplace receipts they price.
