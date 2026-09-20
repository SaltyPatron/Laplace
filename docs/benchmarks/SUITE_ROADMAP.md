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
| `repo-mutation-roundtrip` | canonical admitted repository + bounded structural edit | reuse/new-node counts + new root derivation + complete checkout realization + exact tree/file/content parity + CPU/memory/I/O/time receipt |

## Complexity receipt

A `sparse-address` result may report the intended `O(log N)+O(K)` form only when the receipt names the exact address provider and the actual selected-work algorithm. `K` is not allowed to mean an undocumented post-hoc sample. If selected work is `K log K`, `K^2`, or iterative, record that instead.

## Accelerator receipt

`accelerator-sparse` changes the physical provider, not the semantic program. It must preserve the same selected IDs/operators and semantic result while recording data transfer and active device state. GPU presence alone is not GPU use; GPU use alone is not GPU-resident world state.

## State-boundary rule

Source/core profiles cannot quietly query or mutate the installed substrate. Installed/live profiles bind package/extension/world/evidence/resource epochs separately from repository source. Corpus profiles bind exact input roots/digests. Derived competitor reports cite the exact underlying Laplace receipts they price.

## Full-repository mutation/realization target

The repository benchmark is the scaled structural analogue of the Moby exact-realization proof.

```text
admitted application root R0
-> apply one or more bounded semantic AST mutations
-> derive R1 by creating only changed structure + ancestry
-> prove unchanged canonical subtree reuse
-> realize the complete R1 checkout
-> verify directories/files/bytes/content identities against expected R1
```

Receipts separate structural construction from O(output-bytes) realization:

- changed leaves / new AST nodes / new ancestor compositions;
- reused canonical subtrees and reuse ratio;
- root-derivation time/resources;
- files/directories/output bytes;
- realization/filesystem/verification time and physical work;
- complete result fingerprint/parity.

A roughly four-second end-to-end realization for a repository on the scale of Laplace is an **engineering goal to measure/falsify**, not a current performance claim. GitHub issue #1710 owns the executable benchmark. The benchmark must not obtain that number by copying an existing checkout or bypassing canonical reconstruction.
