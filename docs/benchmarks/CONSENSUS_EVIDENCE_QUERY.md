# Consensus evidence query: repeated-scan diagnosis and window-filter comparison

The measured bottleneck in these retained-cell queries was the join from per-cell replayability flags back to materialized evidence. It rescanned all evidence for each cell. A whole-cell window filter removed that repeated work while returning exactly the same eight fields in all four read-only comparisons.

The candidate changes only the `folded` input in `extension/laplace_substrate/src/fold_route.c`: compute `bool_and(fold_replayable) OVER (PARTITION BY ord)`, then retain rows whose whole cell is replayable. The existing flags/count response, evidence selection, ordered native aggregate, quantization, and maximum timestamp remain unchanged.

| Source item | Exact identity |
|---|---|
| Installed source used for the observations | `eeb103178b593670a5f204b7c0daca4ef8eab32b` |
| Installed source tree | `42c617bb6cded8c87ea83b2c55afc9af850dbc5d` |
| Baseline `fold_route.c` blob | `f542379731b1f8f6f859650bec026e44ec3fee50` |
| Window-filter candidate blob | `ed749aa072baf8c8fc058286c4ed934c325e3f09` |
| Read-only qualification helper | `9067f1a1e515b3c2a7cac936671b5c46565244f7` |
| Durable qualification manifest blob | `dcd6bbf3aeca074ae9fd6c40f5fe5094bd19a151` |

The selectors used all 80 games and 4,167 plies from the authenticated first chunk of the completed follow-up recording. They selected complete distinct MOVE and OUTCOME cells using the actual relation, producer source and PlayingId context. Each query then folded all retained evidence for those cells, as the production owner does. These arrays are not a reconstruction of the historical 40 upsert calls.

The baseline plans showed the following repeated work. The counts were unchanged by forcing custom versus generic planning.

| Relation | Selected cells | Retained evidence rows | Evidence scan loops in flags join | Rows rejected by join filter |
|---|---:|---:|---:|---:|
| MOVE | 3,351 | 7,712 | 3,351 | 25,835,200 |
| OUTCOME | 525 | 220,652 | 525 | 115,621,648 |

For OUTCOME, the initial lookup used the existing composite `(subject_id, type_id, object_id)` indexes on all eight leaves. The expensive step was the later materialized-CTE equality join, not an object-only lookup. The candidate's `WindowAgg` processed 7,712 MOVE rows or 220,652 OUTCOME rows once, with one loop. No temporary I/O occurred in these observed plans.

[Actual comparison run 35248243244, job 105293862375](https://github.com/SaltyPatron/Laplace/actions/runs/35248243244/job/105293862375) passed. The [authenticated artifact reader, run 35248517630, job 105294728638](https://github.com/SaltyPatron/Laplace/actions/runs/35248517630/job/105294728638), independently rechecked the retained result pairs.

| Relation | Transaction-local plan mode | Baseline direct query, ms | Window direct query, ms | Baseline EXPLAIN ANALYZE total, ms | Window EXPLAIN ANALYZE total, ms |
|---|---|---:|---:|---:|---:|
| MOVE | Forced custom | 1,515.851 | 100.663 | 2,866.511 | 112.224 |
| MOVE | Forced generic | 1,452.866 | 51.990 | 2,580.364 | 50.751 |
| OUTCOME | Forced custom | 6,654.230 | 327.167 | 12,078.672 | 449.092 |
| OUTCOME | Forced generic | 6,696.168 | 337.024 | 11,831.749 | 446.982 |

Both variants used the same ordered JSON aggregation wrapper for exact comparison; direct-query timings include that wrapper.

Every same-snapshot pair matched `ord`, `replayable`, `n`, `rating`, `rd`, `volatility`, `witness_count` and `ts` exactly. The reader canonicalized the complete ordered JSON arrays with sorted object keys and compact separators before hashing:

| Relation | Result rows | Canonical JSON bytes | SHA-256, identical for baseline/window in both modes |
|---|---:|---:|---|
| MOVE | 3,351 | 528,027 | `c59c1121d64f22abca326374f32a2813e7edfb64d83c02c2930acc5c4618d9ff` |
| OUTCOME | 525 | 83,935 | `842f5f5967d6fb66a20d8e3b3ee05744c20b848a1bf15895ee383f29e3f2601b` |

The original observation artifact is [10507949513](https://github.com/SaltyPatron/Laplace/actions/runs/35248243244/artifacts/10507949513), 1,804,180 bytes, SHA-256 `091b7234f428cd6975f92d61101f904f709683668ff38ffbecff26642c0eacee`. The reader retained the original archive, all raw queries/results/plans and projections in [artifact 10508890496](https://github.com/SaltyPatron/Laplace/actions/runs/35248517630/artifacts/10508890496), 3,619,753 bytes, SHA-256 `70dfb7dac594eb12df6417a657585fa605d938096ef0cf9df2fa275e5ad7f58b`.

Earlier sort-only work remains separate evidence. [Run 35246518981](https://github.com/SaltyPatron/Laplace/actions/runs/35246518981) completed all eight analyzed plans and exact MOVE comparisons, but both OUTCOME cases reached the cumulative 30-second transaction deadline before the final direct result. Its [authenticated reader 35246934372](https://github.com/SaltyPatron/Laplace/actions/runs/35246934372) passed without relabelling the original run successful. Removing the sort left the repeated join intact. An earlier one-cell diagnostic, [run 35244394640](https://github.com/SaltyPatron/Laplace/actions/runs/35244394640), completed its SQL comparisons and rollback but failed on a bare `true` observer footer; that observer error was corrected separately.

These results qualify a finite read-only query comparison. All sampled cells were replayable; controlled mixed/transient, NULL-object, missing-evidence and duplicate-request tests are a separate qualification boundary. Each case used one repeatable-read, read-only snapshot, a 20-second statement limit and a 90-second transaction limit. Plans preceded direct queries, baseline preceded candidate, and repeated execution warmed caches. Concurrent host work was not excluded. Forced planning modes do not establish the planner history of the original ingest backend.

No candidate library was compiled or installed by this comparison, and no upsert, repair, statistics maintenance, global setting or source deployment was performed. The timings are not a complete recording benchmark, a recorded-games-per-second result or an end-to-end speedup claim.
