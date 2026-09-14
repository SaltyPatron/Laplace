# Ingest boundary and recipe law

Status: **current architectural correction for world admission.** It does not assign a permanent global P0 order.

Representative owners: #1045, #1177, #1132. Physical-plan invariance gate: #1443. Related work in `Laplace-Refactor` may implement the same law; cross-repository issue links are coordination, not semantic authority.

## Why this exists

Laplace has repeatedly fixed source ingestion one corpus at a time even though many failures have the same cause: source-specific code was allowed to own machinery that belongs to the universal substrate.

WordNet, PGN, UD, Tree-sitter, PNG, Wiktionary, FrameNet, model formats and other sources legitimately require different parsers/codecs/academic mappings. They do **not** require independent content-composition, identity, deduplication, scheduling, persistence, evidence or cognition engines.

`ContentTierSpine` already expresses this split for text/content: decomposers recover observations/source structure while the common spine owns canonical composition, existence/reuse and staging. This document generalizes the law across world admission.

## Five boundaries that are not interchangeable

```text
physical artifact / file occurrence
        !=
transport read / feed buffer
        !=
parser or codec source-format object / record / CST / AST node
        !=
canonical content / composition / occurrence / testimony
        !=
persistence / probe / COPY / apply batch
```

No boundary becomes another merely because two happen to have the same cardinality in one implementation.

### Artifact boundary

A real selected file/member/object in the source estate. It owns artifact identity, provenance, journal/resume accounting and complete-coverage disposition.

An artifact can contain zero, one or many source-format objects. A corpus can contain many artifacts.

### Transport boundary

A physical read/feed unit chosen for I/O, memory, parser APIs or resource scheduling.

It is never canonical content identity. A UTF-8 scalar, quoted record, grammar token, AST construct, image sample, compressed block or other semantic/source-format unit may cross a transport boundary. The reader/provider must carry enough state to recover the same source structure regardless of legal transport partitioning.

### Parser/codec source-object boundary

A record, CST/AST node, field, row, game, sentence, frame, tensor descriptor, codec element, etc. recovered according to the source/provider contract.

This is observed/source structure. It is **not automatically canonical content**. The declared recipe decides which recovered values/structures become content, occurrence, reference, provenance, testimony, deterministic calculation, packaging or unresolved obligation.

### Canonical semantic boundary

Common substrate state includes:

```text
content                         -> canonical recursive composition / executable identity
ordered occurrence             -> trajectory / occurrence physicality
unordered multi-value state    -> declared collection composition
opaque external identity       -> typed reference
source claim                    -> attributed testimony
provenance                      -> context / occurrence metadata
deterministic consequence      -> calculation / rebuildable accelerator state
packaging/provider syntax      -> reconstruction/provider state unless declared otherwise
unresolved field meaning       -> explicit unresolved obligation
```

Canonical identity follows declared semantic recipes, not transport/batch/worker execution grain.

### Persistence boundary

A probe batch, staging/intent batch, COPY buffer, transaction, merge set, partition task, fold batch or other physical persistence unit.

It may change performance, WAL, memory, CPU occupancy and scheduling. It may not change canonical world state.

## Source-provider ownership

A source/provider may own:

- enumeration of the source's selected physical artifact graph;
- exact decode/container unpacking;
- grammar/codec/standards parsing;
- source-specific field/span/ordinal extraction;
- source-specific academic interpretation and mapping declarations;
- provider identity/version/error/recovery evidence;
- exact inverse/reconstruction support or explicit loss declaration.

A source/provider must not own:

- a private canonical content hash/identity rule;
- a private Unicode/content ladder where the universal ladder applies;
- a private Merkle/dedup law;
- a private scheduler/backpressure/resource policy;
- a private persistence/COPY/apply protocol;
- physical batch cardinality as semantic identity;
- a source-specific cognition/search engine;
- silent fallback from unknown field meaning to `content`.

Source-specific semantic knowledge should be declarative profile/recipe data plus the irreducible parser/codec/academic kernel needed to recover that source faithfully.

## Whole working set versus streaming

Laplace does not impose a doctrinal “stream everything in tiny chunks” rule.

If the admitted physical plan has memory for a complete artifact plus its composition/parser/dedup scratch, a whole working set is valid. Streaming is valid when the source/provider/resource plan requires it. Streaming partitions remain physical only and disappear from canonical results.

The invariant is:

> **Choose a physical plan from actual topology/resources and source structure, then prove every legal equivalent plan produces the same canonical world.**

## Universal execution-grain law applies to ingestion too

Semantic correctness and physical execution grain are separate axes.

A parser may correctly recover one million semantic records and still implement them disastrously if the caller performs one SQL/SPI/PInvoke/persistence operation per record.

The intended hot shape is:

```text
artifact / source stream
-> parser/provider recovers source structure
-> coarse native decomposition/composition over a working set or batch
-> bulk identity/existence/reuse work
-> set-sized/COPY persistence
-> set-sized evidence/fold work
-> receipt
```

PostgreSQL owns durable indexed state, transactions and set access. SPI is a prepared/set-sized bridge. Native C/C++ owns repeated parsing/composition/normalization/trajectory/reduction work where the common machine provides those semantics. C#/SQL orchestrate source/session/contracts/transport.

These are architecture smells in repeated ingest work:

```text
one P/Invoke per scalar/token/AST node/record
one existence SQL query per entity
per-row SPI_prepare/SPI_execute
one transaction/COPY invocation per semantic object
batch API implemented by looping scalar write APIs
source-private thread pools competing with the common resource scheduler
source-private caches compensating for a missing common working set
per-record consensus/fold calls when a set-sized fold is possible
```

The optimization goal is therefore two-dimensional:

```text
avoid work
  canonical reuse / working-set dedup / direct identity / perfcache / indexed set probes

x

avoid boundary overhead
  coarse native decomposition/composition + bulk/set persistence/fold
```

A wider SIMD ISA or GPU provider may add headroom later. It is not a substitute for getting this execution grain right.

## Physical-plan invariance

For the same exact source artifact + recipe/profile/provider generation, vary legal physical settings such as:

```text
read buffer size
parser feed chunk size
record batch size
native decomposition/composition batch size
probe batch size
worker count / CPU affinity
scheduling order where source order is preserved
COPY/apply/merge batch size
cache/perfcache warm/cold state where identity is unaffected
```

The durable semantic fingerprint must remain identical for logically order-independent state:

- canonical ids;
- Merkle/recursive composition;
- physicality trajectories, ordinals, gaps and multiplicity;
- occurrences;
- typed references;
- testimony ids and observation cardinality;
- provenance/source coordinates;
- deterministic calculation identity/results under the same recipe;
- reconstruction output or declared loss.

Only physical receipts may differ: time, CPU, RSS, I/O, WAL, cache behavior, batch sizes, temporary staging and worker scheduling.

#1443 owns the executable cross-source matrix.

## Measured counterexample: OpenSubtitles

#1180 records a concrete violation in which an arbitrary 512-pair batch participated in durable content-object construction. Changing the physical batch therefore changed which identities existed.

That is not merely an OpenSubtitles bug. It is one failing instance of the generic boundary-invariance law and belongs under the common rebatching gate rather than as a permanent source-local exception.

## Benchmark lesson: document grain is not universal work grain

A historical benchmark processed a real corpus dominated by one ~41.6 MB document. Under a whole-file unique-corpus scheduler that giant indivisible document mathematically dominated makespan, so the measured plateau primarily characterized the scheduler/workload grain rather than the native composition engine's aggregate concurrency ceiling.

The later independent-stream profile exists to measure aggregate concurrent headroom, and #1451 tracks true one-semantic-DAG internal frontier parallelism.

The broader law is:

> A file/document/source object can remain one exact semantic object while its internally independent physical work is scheduled at a finer dependency-aware grain.

Transport/worker/chunk identity may not enter canonical semantics merely to make parallelism convenient.

## Resource ownership / serviceability

One generic resource authority should admit reader/parser/native/persistence/fold concurrency rather than allowing every source to consume all visible CPUs/memory independently.

On a managed host, “all logical CPUs exist” does not mean an ingest/benchmark should take all of them. Serviceable work reserves database/product/runner/control-plane headroom; full saturation is an explicitly selected isolated profile.

## Required source-profile fields

Every activated source profile should eventually declare at least:

```text
artifact/release authority
provider/grammar/codec identity
artifact and source-object framing
canonical composition/occurrence grain
field/AST-role disposition
ordering/multiplicity semantics
reference namespaces
provenance/testimony rules
normalization/canonicalization rules
inverse/reconstruction or declared loss
legal physical-plan dimensions
qualification fixtures
physical-plan invariance receipt
coverage/amplification receipt
```

## Current issue families, not a fixed global priority

Issues historically central to this law include:

- #1443 — generic physical-plan/boundary invariance;
- #1008 / #1052 — canonical entity/storage uniqueness and identity alignment;
- #1041 — complete field disposition;
- #1180 — OpenSubtitles physical-batch identity defect;
- #1042 — selected-UCD whitespace authority;
- #1134 — media identity/reconstruction;
- #1045 — recipe/source-profile consolidation;
- #1153 — native source fidelity;
- #1178 — normalized readers;
- #967 — common safe apply concurrency;
- #1175 / #1080 — performance/amplification receipts;
- #1132 — broader substrate/reseed acceptance.

This list is ownership/history, not an instruction to ignore a current user-accepted task until the numerically earlier item is finished.

## Non-success

The following do not satisfy this architecture:

- many decomposer classes behind one interface while each still owns private canonical/persistence/scheduler semantics;
- using file, line, row, AST node, parser callback, fixed record block or I/O buffer as canonical content identity by default;
- adding source-private caches/batchers/thread pools to mask common-spine defects;
- accepting a faster ingest when worker/batch settings change durable semantic state;
- accepting a semantically correct ingest whose physical path still performs avoidable per-element DB/native boundary crossings at scale;
- declaring an opaque identifier to be content merely because it is UTF-8;
- using parser success as truth/admission authority;
- preserving source-specific compatibility readers forever instead of normalized state;
- allowing benchmark design or worker topology to redefine product semantics.

## Acceptance summary

A source is admitted by a selected artifact/profile plus a qualified provider and recipe. The common machine performs canonical recursive composition/reuse, deposition, bulk persistence/fold and receipts. Equal canonical content under the same recipe converges across sources and legal physical plans. Source-specific structure/claims remain reconstructable and attributable. Physical execution can be optimized aggressively because semantic equivalence is continuously proved.

Performance acceptance measures both useful semantic work and physical execution grain: native work, DB/SPI/PInvoke boundary counts, batch/set widths, CPU/memory/I/O/WAL and wall time appropriate to the source. Avoidable orchestration overhead is an implementation defect, not a permanent semantic cost.