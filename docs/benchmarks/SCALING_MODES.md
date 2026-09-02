# Core composition scaling modes

Tracking: #1441. First discriminating receipt: Actions run `33608791817`, job `100178833249`, source `262731f6b80584ed61c6d5b5ca3423c8e4e00005`.

Laplace has two different legitimate whole-machine composition questions. They must not share one ambiguous headline number.

## 1. Unique-corpus makespan

Profile: `core-scale`  
Suite: `makespan`

This mode asks:

> How quickly can this machine finish one finite heterogeneous corpus when every source document is an indivisible semantic work item and each document is composed exactly once?

The scheduler may assign different documents to different workers, but it may not cut one document into arbitrary byte chunks because doing so changes the UAX29/composition boundary being measured.

The first run exposed why this distinction matters. Its corpus contained 1,054 documents / 69.9 MB / 69,867,473 codepoints. The machine-readable shard receipt showed:

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

Measured throughput rose from about 1.785M codepoints/s at one worker to about 2.99M codepoints/s at two workers, then stayed near that level through twelve workers. That plateau is **not evidence by itself of native-core serialization**. The 41.6 MB document is an indivisible straggler and establishes the batch critical path; additional workers finish their much smaller shards and wait.

This is useful evidence. It measures real finite-batch scheduling and says that intra-document parallelism or a different semantic scheduling boundary would be required to reduce that particular corpus makespan further.

It is not the right receipt for maximum independent-request service capacity.

## 2. Replicated independent-stream throughput

Profile: `core-scale-streams`  
Suites: `throughput`, `scale`

This mode asks:

> How much aggregate native composition work can the host execute when several independent documents/requests/streams are available concurrently?

For every scaling point, each pinned worker executes one complete copy of the same real corpus. Therefore the amount of work actually executed grows with worker count:

```text
1 worker  -> 1 corpus stream
2 workers -> 2 corpus streams
6 workers -> 6 corpus streams
12 workers -> 12 corpus streams
```

No document is split. Every reported codepoint and tier-tree node is actually passed through `content_witness_tree_build`; the aggregate result is not `single-thread rate × workers` arithmetic.

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
- parallel efficiency;
- largest-document fraction for context.

Physical cores are populated before SMT siblings.

## Why both remain in the suite

These profiles answer different questions:

| Profile | Work held fixed? | Primary question |
| --- | --- | --- |
| `core-scale` | yes: one unique corpus | finite batch makespan / scheduler stragglers |
| `core-scale-streams` | no: one full stream per worker | maximum aggregate independent work throughput |

A large document dominating `core-scale` is a product fact, not a benchmark failure. Treating that makespan plateau as the machine's aggregate throughput ceiling would be the failure.

Conversely, `core-scale-streams` must not be cited as proof that one giant document becomes N-way parallel internally. It proves concurrency across independent semantic work streams.

## Historical ~3.4M equivalent/s

The remembered historical multi-million 4-character-equivalent result is not reconstructed by multiplying the committed ~465k single-thread floor. `core-scale-streams` exists to recover, reject, or supersede that result with an executable receipt on current code.

## Comparison law

When publishing either curve, name the mode, corpus identity, worker topology and work denominator. Never reduce both modes to one unlabeled `tokens/sec` scalar.
