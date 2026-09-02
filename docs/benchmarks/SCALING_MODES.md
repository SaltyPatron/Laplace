# Core composition scaling modes

Tracking: #1441 and #1451. First discriminating receipt: Actions run `33608791817`, job `100178833249`, source `262731f6b80584ed61c6d5b5ca3423c8e4e00005`.

The first real scaling run exposed **three** different performance questions. They must never share one ambiguous headline number.

## 1. Unique-corpus, file-grain makespan — diagnostic lower bound

Profile: `core-scale`

This mode asks:

> How quickly can this harness finish one finite heterogeneous corpus when the harness assigns each complete input file to exactly one worker and processes each file once?

That is a **benchmark scheduling constraint**, not a Laplace semantic law and not the intended final parallel architecture. A document is a high-level composition/DAG; it is not inherently one CPU task.

The first run contained 1,054 documents / 69.9 MB / 69,867,473 codepoints. The machine-readable shard receipt showed:

```text
2 workers
  41,601,961 bytes   1 document
  28,286,542 bytes   1,053 documents

3 workers
  41,601,961 bytes   1 document
  19,606,932 bytes   1 document
   8,679,610 bytes   1,052 documents

6 workers
  41,601,961 bytes   1 document
  19,606,932 bytes   1 document
  ~2.17 MB each      remaining workers
```

The largest input is 59.526% of all corpus bytes. With the harness refusing to distribute work *inside* that document, the theoretical makespan speedup ceiling is approximately:

```text
69,888,503 / 41,601,961 = 1.67993x
```

The measured best was about 1.676x. The run therefore nearly saturated the **coarse file-grain scheduler it was given**.

It does **not** establish:

- a native two-core ceiling;
- a whole-machine Laplace throughput ceiling;
- that files/documents are correct worker atoms;
- that a 41.6 MB document cannot use multiple cores;
- that production ingest should schedule one file per worker.

It is retained because it is useful negative evidence: file-grain scheduling can impose a floor on top of the already single-thread primitive floor.

## 2. Replicated independent-stream throughput — machine saturation

Profile: `core-scale-streams`
Suites: `throughput`, `scale`

This mode asks:

> How much aggregate native composition work can the host execute when multiple independent semantic streams are available concurrently?

For every scaling point, each pinned worker executes one complete copy of the same real corpus. The amount of work actually executed grows with worker count:

```text
1 worker   -> 1 corpus stream
2 workers  -> 2 corpus streams
6 workers  -> 6 corpus streams
12 workers -> 12 corpus streams
```

Every reported codepoint and tier-tree node is actually passed through `content_witness_tree_build`; this is not `single-thread rate × workers` arithmetic.

This profile is useful for measuring CPU/cache/memory-system saturation and service capacity across independent requests. It is **not** proof that one large semantic object is internally parallel. Replicating the whole corpus per worker deliberately avoids the file-grain straggler so the host can be saturated; it does not solve the architectural scheduling defect exposed by mode 1.

The receipt records:

- exact physical/logical CPU topology and affinity;
- complete corpus bytes/codepoints/documents per worker;
- actual total bytes/codepoints/documents executed at each point;
- nodes per worker and aggregate nodes;
- every repeat and measured wall interval;
- codepoints/s;
- 4-character BPE-equivalent units/s;
- tier-tree nodes/s;
- measured speedup against the 1-worker stream point;
- parallel efficiency.

Physical cores are populated before SMT siblings.

## 3. Single-semantic-DAG frontier scaling — required architecture proof

Tracking: #1451.

This is the measurement the first benchmark suite still lacks.

It asks:

> Can the same single large canonical content object use multiple workers internally while producing the exact same semantic result as scalar execution?

The intended physical shape is:

```text
complete canonical input
  -> exact UAX / structural scaffold
  -> dependency frontiers
       leaves / independent low-tier nodes
       -> grapheme frontier
       -> word frontier
       -> sentence frontier
       -> document/root
  -> same exact canonical root and structural fingerprint
```

The worker count, scheduling order, thread/task identity and transport partitioning are physical-plan state only. They may change timing and resource receipts; they may not change canonical ids, geometry, trajectories, reconstruction or root identity.

A valid benchmark for this mode must run the **same single giant document** at worker grants 1, 2, 3, 4, physical-core count and selected SMT points. It must verify scalar/parallel semantic parity before reporting speedup.

Until #1451 is implemented, no result from modes 1 or 2 may be cited as proof of intra-document or intra-DAG parallelism.

## Why all three meanings matter

| Profile / target | Work held fixed? | Physical grain | Primary question |
| --- | --- | --- | --- |
| `core-scale` | yes: one unique corpus | whole input files | how badly does coarse file scheduling constrain this finite batch? |
| `core-scale-streams` | no: one full corpus per worker | independent streams | how much aggregate core work can the host sustain? |
| #1451 single-DAG scale | yes: one exact semantic object | dependency frontier / DAG nodes | can one semantic object use the machine without changing meaning? |

The first run showed that `core-scale` is a **lower-bound diagnostic**, not an architecture target. The 41.6 MB straggler is evidence that the harness's physical grain is wrong for measuring machine capability; it is not a product requirement that one document remain bound to one worker.

## The measured floor hierarchy

The current legacy evidence should be stated explicitly:

```text
~465k BPE-equivalent/s
  = real single-thread primitive composition floor

~748k BPE-equivalent/s in the first `core-scale` run
  = real finite-corpus throughput under a file-grain scheduler
  = nearly the mathematical ceiling of that coarse scheduler
  = NOT a whole-machine ceiling

aggregate independent-stream capacity
  = pending #1442 measurement

single-semantic-object parallel capacity
  = pending #1451 implementation and measurement
```

That makes the first multi-worker result a **floor imposed on a floor**, not a failed proof of a low machine ceiling.

## Historical ~3.4M equivalent/s

The remembered historical multi-million 4-character-equivalent result is not reconstructed by multiplying the committed ~465k single-thread floor. `core-scale-streams` exists to recover, reject, or supersede aggregate host throughput with an executable receipt. #1451 separately owns the stronger claim that one exact semantic DAG can exploit the host internally.

## Comparison law

When publishing a curve, always name:

- benchmark mode;
- exact semantic workload;
- physical scheduling grain;
- worker topology/resource grant;
- whether total work is fixed or replicated;
- semantic parity gate;
- measured physical work.

Never reduce the three modes to one unlabeled `tokens/sec` scalar and never call file-grain or replicated-stream parallelism proof of internal semantic-DAG parallelism.
