# Core composition scaling modes

Tracking: #1441, #1451, #1436. The scaling suite answers several different questions. They must not share one ambiguous headline number.

## 1. Unique-corpus, file-grain makespan

Profile: `core-scale`.

This asks how quickly the current harness can finish one finite heterogeneous corpus when each complete input file is assigned to one worker and processed once.

That is a benchmark scheduling constraint, not a Laplace semantic law and not the intended final parallel architecture. A document is a high-level composition/DAG; it is not inherently one CPU task.

The first discriminating run (`33608791817`) contained one 41,601,961-byte document that was 59.526% of the measured corpus. Because that harness would not distribute work inside the document, its theoretical makespan speedup ceiling was about 1.68x, and the measured result approached that ceiling.

That run therefore proved the coarse file-grain scheduler was the bottleneck. It did **not** prove a native two-core ceiling, a whole-machine throughput ceiling, or that files are lawful production worker atoms.

`core-scale` remains useful as negative evidence about physical work grain.

## 2. Replicated independent-stream scaling

Profile: `core-scale-streams`. Suites: `throughput`, `scale`, `all`.

Every worker executes one complete copy of the same real corpus. The measured work therefore grows with worker count:

```text
1 worker -> 1 corpus stream
2 workers -> 2 corpus streams
N workers -> N corpus streams
```

Every reported codepoint and tier-tree node is actually processed by the native composition path. This is not `single-thread result × N` arithmetic.

This profile measures aggregate concurrent composition capacity. It is **not** proof that one semantic object is internally parallel.

## Serviceable capacity and saturation are different experiments

A managed host also runs PostgreSQL, the Actions runner, monitoring/control processes, and product services. Therefore the normal benchmark workflow does **not** silently consume every allowed logical CPU.

The workflow resolves a versioned scale plan before measurement:

```text
managed-host default
  -> reserve declared logical-CPU headroom
  -> derive serviceable scaling points
  -> pass those exact points to the scaling harness
  -> record scale-plan.json

explicit saturation
  -> operator sets allow_saturation=true
  -> full logical-CPU boundary may be admitted
  -> receipt labels the run saturation-allowed
```

On the known 6-core/12-thread i7-6850K runner with the default reserve of two logical CPUs, the serviceable default resolves to:

```text
1, 2, 3, 4, 6, 8, 10
```

The 12-worker point is no longer a default managed-host measurement. It requires explicit saturation opt-in.

This distinction is not cosmetic. Actions run `34823625126` failed before a sealed benchmark artifact was produced during the previous scaling design that admitted the full logical-CPU boundary. The available evidence does not prove which final process failed, so the run is recorded as an incomplete/failing receipt rather than assigned a fabricated terminal cause. It is nevertheless a valid counterexample to treating `all allowed CPUs` as the default service-capacity experiment on a live managed host.

A larger throughput number from a point that starves the required service/control plane is a saturation result, not a normal customer/service capacity result.

## 3. Single-semantic-DAG frontier scaling

Tracking: #1451.

This is the stronger architecture proof:

> Can one exact large semantic object use multiple workers internally while producing the same canonical result as scalar execution?

The intended physical shape is:

```text
complete canonical input
  -> exact structural/dependency scaffold
  -> independent dependency frontiers execute concurrently
  -> barriers only where parent dependencies require them
  -> identical ids / coordinates / Hilbert / trajectories / root
```

Worker count, task identity, scheduling order and transport partitioning are physical-plan state only. They may change timing and receipts; they may not change canonical semantics.

A valid single-DAG benchmark runs the same large object under multiple admitted worker grants and verifies semantic parity before reporting speedup.

Until that implementation lands, neither file-grain makespan nor replicated independent streams may be cited as proof of intra-object parallelism.

## Why all three meanings matter

| Profile / target | Work held fixed? | Physical grain | Primary question |
| --- | --- | --- | --- |
| `core-scale` | yes | whole input files | how badly does coarse file scheduling constrain one finite batch? |
| `core-scale-streams` | no; one full stream per worker | independent streams | how much aggregate work can the admitted host resource envelope sustain? |
| #1451 single-DAG scale | yes; one exact object | dependency frontier / DAG nodes | can one semantic object use several workers without changing meaning? |

For managed-host scaling, every published curve additionally names whether it is **serviceable** or **saturation** evidence.

## Receipt law

A scaling receipt names at least:

- benchmark mode;
- exact source revision and built artifact identities;
- exact semantic workload;
- physical scheduling grain;
- physical/logical CPU topology;
- resolved worker points and CPU affinity;
- reserved headroom and whether saturation was explicitly allowed;
- whether total semantic work is fixed or replicated;
- semantic parity gate where applicable;
- all measured repeats;
- codepoints/s, BPE-equivalent comparison units/s and tier-tree nodes/s;
- speedup and parallel efficiency;
- host/product/database health evidence needed for any serviceable-capacity claim.

Never collapse these modes into one unlabeled `tokens/sec` scalar. Never call replicated-stream or file-grain parallelism proof of single-object parallelism. Never call a point that knocks required host services offline normal service capacity.
