# Ingest boundary and recipe law

Status: **current architectural correction / P0 admission authority**

Owners: #1045, #1177, #1132  
Executable invariance gate: #1443  
Clean-product counterpart: `SaltyPatron/Laplace-Refactor#115`, `#171`

## Why this exists

Laplace has repeatedly fixed source ingestion one corpus at a time even though many failures have the same cause: source-specific code was allowed to own machinery that belongs to the universal substrate.

The recurring failure is not that WordNet, PGN, UD, Tree-sitter, PNG, Wiktionary, FrameNet, models, etc. require different parsers or academic interpretation. They do. The failure is allowing those providers/adapters to become independent content-composition, identity, deduplication, scheduling, persistence, or semantic engines.

`ContentTierSpine` already states the intended ownership for text/content: decomposers yield observations while the common spine owns composition, existence and staging. This document generalizes that rule across world admission.

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

No boundary in that list becomes another merely because they happen to have the same cardinality in one implementation.

### Artifact boundary

A real selected file/member/object in the source estate. It owns artifact identity, provenance, journal/resume accounting, and complete-coverage disposition.

An artifact can contain zero, one, or many source-format objects. A corpus can contain many artifacts.

### Transport boundary

A physical read/feed unit chosen for I/O, memory, parser APIs, or resource scheduling.

It is never canonical content identity. A UTF-8 scalar, quoted record, grammar token, AST construct, image sample, compressed block, or other semantic/source-format unit may cross a transport boundary. The reader/provider must carry enough state to recover the same source structure.

### Parser/codec source-object boundary

A record, CST/AST node, field, row, game, sentence, frame, model tensor descriptor, codec element, etc. recovered according to the source/provider contract.

This is observation/source structure. It is **not automatically a canonical content composition**. The recipe decides which recovered values/structures become content, occurrence, reference, provenance, testimony, calculation, or packaging.

### Canonical semantic boundary

The universal substrate classes:

```text
content                         -> canonical composition / Merkle identity
ordered occurrence             -> physicality / trajectory
unordered multi-value state    -> collection composition
opaque external identity       -> typed reference
source claim                    -> attributed testimony
provenance                      -> context / occurrence metadata
deterministic consequence      -> calculation / perfcache
packaging/provider syntax      -> reconstruction/provider state unless declared otherwise
unresolved field meaning       -> explicit unresolved obligation
```

Canonical identity follows these laws, not physical execution grain.

### Persistence boundary

A probe batch, intent/staging batch, COPY page/buffer, transaction, merge set, partition task, fold batch, etc. selected by the physical plan.

It may change performance, WAL, memory, CPU occupancy and scheduling. It may not change canonical state.

## Source-provider ownership

A source/provider may legitimately own:

- enumeration of the source's physical artifact graph;
- exact decode/container unpacking;
- grammar/codec/standards parsing;
- source-specific field/span/ordinal extraction;
- source-specific academic interpretation and mapping declarations;
- provider identity/version/error/recovery evidence;
- exact inverse/reconstruction support or explicit loss declaration.

A source/provider must **not** own:

- a private canonical content hash/identity rule;
- a private Unicode/content ladder when the universal content spine applies;
- a private Merkle/dedup law;
- a private scheduler/backpressure/resource policy;
- a private persistence/COPY/apply protocol;
- batch cardinality as semantic identity;
- a source-specific cognition/search engine;
- silent fallback from unknown field meaning to `content`.

Source-specific semantic knowledge should be declarative recipe/profile data plus only the irreducible parser/codec/academic kernel required to recover that source.

## Whole working set versus streaming

Laplace does not require a doctrinal `stream everything in tiny chunks` implementation.

If an admitted physical plan has enough memory for a complete 70 MB document, its complete Merkle/tier tree, dedup working set, parser state and required scratch, using that complete working set is valid. On a host with ~128 GB RAM, a 70 MB text artifact is not intrinsically a memory problem.

Streaming is appropriate when the source/provider or resource plan calls for it. When streaming is used, the stream partitions are physical only. They must disappear from the canonical result.

The governing invariant is not `always stream` or `always load all`. It is:

> **Choose a physical plan from actual topology/resources and source structure, then prove that every legal equivalent plan produces the same canonical world.**

## P0 physical-plan invariance

For the same exact source artifact + recipe/profile/provider generation, vary legal physical execution settings such as:

```text
read buffer size
parser feed chunk size
record batch size
probe batch size
worker count / CPU affinity
scheduling order where source order is preserved
COPY/apply/merge batch size
cache/perfcache warm/cold state where identity is unaffected
```

The durable semantic fingerprint must remain identical for logically order-independent state:

- canonical ids;
- Merkle/universal-AST composition;
- physicality trajectories, ordinals, gaps and multiplicity;
- occurrences;
- typed references;
- testimony ids and observation cardinality;
- provenance/source coordinates;
- reconstruction output or declared loss.

Only physical receipts may differ: time, CPU, RSS, I/O, WAL, cache behavior, temporary staging and worker scheduling.

#1443 owns the executable cross-source matrix.

## Measured counterexample: OpenSubtitles

#1180 records a concrete violation: arbitrary 512-pair batching participates in durable content-object construction. Changing the physical batch therefore changes which content identities exist.

That is not merely an OpenSubtitles bug. It is a failing instance of this generic law and must ultimately pass #1443's common rebatching gate rather than remain a bespoke source-local assertion.

## Benchmark lesson: document grain is not universal work grain

Benchmark run `33608791817` processed a 69.9 MB real corpus. One 41.6 MB document was 59.526% of the corpus, so the unique-corpus document-grain scheduler had a theoretical maximum makespan speedup of about 1.6799x. The measured best was about 1.6765x.

That result is valid evidence for finite unique-corpus makespan. It is **not** evidence that native composition only scales to two cores. It demonstrates why a convenient high-level object can become an accidental scheduling bottleneck when treated as indivisible physical work.

The follow-up aggregate-stream benchmark keeps semantic documents intact while measuring actual independent concurrent streams. Future intra-document parallel work must use lawful source/content structure and prove identical root/topology/reconstruction; it may not manufacture arbitrary semantic chunks to improve a graph.

## Current P0 priority

1. #1443 — generic physical-plan/boundary invariance.
2. #1008 + #1052 — one canonical entity per id; storage uniqueness/FKs agree with identity.
3. #1041 — complete field disposition: content/reference/occurrence/provenance/claim/calculation/packaging/unknown.
4. #1180 — remove batching/provenance from OpenSubtitles content identity and prove rebatching parity.
5. #1042 — make whitespace classification selected-UCD authority; multi-codepoint whitespace-word emission is already fixed.
6. #1134 and related media identity/reconstruction defects.

Historical text-lane blockers #1039 and #1040 are now closed after current-main implementation/test verification.

After those correctness gates:

- #1045 recipe/source-profile consolidation;
- #1153 native source fidelity;
- #1178 normalized readers;
- #967 safe generic apply concurrency;
- #1175/#1080 performance/amplification receipts;
- final bounded/full reseed under #1132.

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

## Non-success

The following do not satisfy this architecture:

- replacing sixteen decomposers with sixteen classes behind one interface while each still owns its semantics and execution;
- using a file, line, row, AST node, parser callback, 512-row block or I/O buffer as content identity by default;
- adding more per-source caches/batchers/thread pools to mask common-spine defects;
- accepting a faster ingest when worker/batch settings change durable state;
- declaring an opaque identifier to be content because it is UTF-8;
- using parser success as truth/admission authority;
- preserving source-specific compatibility readers forever rather than moving reads to normalized state;
- letting benchmark design redefine product semantics.

## Acceptance summary

A new source is admitted by a selected artifact/profile plus a qualified provider and recipe. The generic machine performs canonical composition, presence/dedup, deposition, persistence and receipts. Equal canonical content converges across sources and physical plans. Source-specific structure and claims remain reconstructable and attributable. Physical execution can be optimized aggressively because its semantic equivalence is continuously proven.
