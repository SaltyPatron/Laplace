# Laplace — architecture as built

This document describes the implementation that exists in this repository and names material divergences where two current paths do not obey one law.

It is **not** the authority for narrowing the invention. The intended machine, mathematical construction and preservation laws are stated in [`INVENTION.md`](INVENTION.md) and [`INVENTIONS.md`](INVENTIONS.md). Active implementation/acceptance work belongs in GitHub issues. Generated inventories belong in `docs/INVENTORY.md`.

When code and this document disagree, fix this document. When two current code paths disagree with each other or with the invention, record the divergence as an implementation obligation rather than declaring whichever path was inspected first to be the architecture.

---

## 1. Persistent substrate

The PostgreSQL extension persists four primary substrate families under `extension/laplace_substrate/sql/schema/tables/`:

| Table | Primary role |
|---|---|
| `entities` | canonical executable content identities plus tier/type/source metadata |
| `physicalities` | typed physical realization: coordinate, Hilbert address, optional packed trajectory and constituent metadata |
| `attestations` | source-attributed typed testimony/observation |
| `consensus` | folded proposition standing: rating, RD, volatility, witness count and related state |

Supporting tables/journals include canonical names, repair/dirty state and ingest/index progress. Exact generated counts and partition inventory are intentionally not duplicated here; `docs/INVENTORY.md` is regenerated and CI-gated.

The high-level separation is deliberate:

```text
identity/content
    != physical realization
    != occurrence/provenance
    != testimony
    != folded standing
    != deterministic calculation
```

A content identity does not become true merely because it exists, and testimony does not become content identity merely because many sources repeat it.

### Referential integrity is an implementation obligation

The substrate intentionally avoids conventional per-row foreign keys across its large partitioned/hot write paths. Content addressing makes integrity cheap to check and reason about, but it does **not** make dangling references logically impossible: a buggy/partial writer can still emit an id whose target row is absent.

Therefore absence of FKs trades write-time FK overhead for stronger writer, test, audit and live-proof obligations. Documentation must not claim that a dangling reference is “not expressible.”

---

## 2. Executable content identity and recursive composition

The current native composition authority is `engine/core/src/hash_composer.c`, with the Merkle hash law in `engine/core/src/hash128.c`.

For a composed node:

```text
n == 0 -> zero/empty result
n == 1 -> child identity is preserved
n > 1  -> hash128_merkle(tier, ordered child ids, n)
```

The function signature carries `tier`, but **the current `hash128_merkle` implementation explicitly discards it with `(void)tier`**. The executable id is a BLAKE3-derived hash over the Merkle domain byte plus the ordered child-id sequence. Tier, source, ordinal and container identity are not mixed into that hash.

So the current native identity law is:

```text
same ordered canonical child-id sequence
-> same composite content id
```

regardless of the tier at which that same content is observed/used. Tier remains altitude/floor/storage/occurrence metadata rather than canonical content identity. Single-child promotion collapses to the child id under the current native rule.

This behavior is pinned by `app/Laplace.Core.Tests/Core/ContentAddressingLawTests.cs`, which deliberately composes the same two-child sequence at several tiers so singleton collapse cannot hide a tier-salted hash regression.

Source identity is likewise not part of the canonical content hash, so the same canonical composition admitted from multiple sources converges.

The current executable hash is a BLAKE3-derived 128-bit value. That is a finite implementation address/window, not a mathematical proof of global injectivity over an unbounded family of finite structures. Normal same-content convergence is **content-address convergence**, not a “hash collision”; a true same-id/different-preimage collision is a separate integrity event.

The recursive representation itself is larger than the id: exact ordered constituents are retained in the trajectory/composition structure.

---

## 3. Physicality: coordinate, carrier and realized curve

`physicalities` stores a 4D `coord`, a Hilbert address and, where the physicality is compositional, an exact packed constituent trajectory plus `n_constituents` and related metadata.

These are different things.

### 3.1 `coord` is the real geometric placement

The native composer in `engine/core/src/hash_composer.c` calls `math4d_centroid` over child coordinates and then derives the Hilbert address from that parent coordinate.

For child points inside/on the unit 4-ball, the Euclidean centroid remains inside/on that same ball. This is the current native bounded-composition proof used in `INVENTION.md`.

Tier-0 atom placement is implemented by `engine/core/src/super_fibonacci.c`; the finite Unicode generation is exhaustively exercised by `engine/core/tests/test_super_fibonacci.cpp` within its declared floating-point tolerance.

### 3.2 packed `trajectory` is an exact manifest, not a spatial path

`engine/core/src/mantissa.c` and `engine/core/src/trajectory.c` encode constituent identity/order metadata into GeometryZM-compatible binary64 carrier values.

Each binary64 component contributes 53 reversible payload bits when the exponent is held fixed and the sign plus mantissa are used as carrier state. Four components therefore provide 212 bits:

```text
128  complete constituent entity id
 16  packed ordinal
 16  run length
 52  flags / typed metadata
---
212
```

The packed X/Y/Z/M numbers must **not** be treated as the child's real geometric coordinate.

### 3.3 realized curve resolves live child coordinates

The geometric path is reconstructed by:

```text
packed vertex
-> unpack complete child entity id + metadata
-> resolve child physicality
-> use child coord
-> order by logical ordinal
-> ST_MakeLine / native equivalent
```

`extension/laplace_substrate/sql/functions/structural/entity_curve.sql.in` is one concrete realization surface. Fréchet/Hausdorff/shape operations belong on these realized coordinates, not on mantissa carriers.

The packed 16-bit ordinal/run fields are not global composition-size ceilings. `engine/core/src/trajectory.c` carries logical sequence position and run splitting beyond those local field widths; the native test `LaplaceCoreTrajectory.WiderThanTheOrdinalFieldRoundTrips` exercises a 70,000-constituent sequence.

### 3.4 current centroid/Karcher divergence

The repository currently contains two different parent-coordinate laws:

- native `hash_composer_compose_node` uses `math4d_centroid`, placing composites generally inside the 4-ball;
- managed `app/Laplace.Substrate/Abstractions/NgramTrajectory.cs` uses `Math4d.KarcherMean`, explicitly intending to keep composed coordinates on S³. `app/Laplace.Chess/Service/ChessGraph.cs` and at least one model/tokenizer path also use Karcher mean.

Both can obey the weaker bounded-domain invariant when inputs/outputs are valid, but they do **not** encode the same radial meaning and can produce different Hilbert addresses. This is a real as-built architecture divergence, not a documentation preference. Any claim that all composites are “on S³” or that all composites use centroid is currently too strong until the implementation is reconciled/reseeded under one declared rule.

---

## 4. Evidence and consensus fold

`AttestationRow` in `app/Laplace.Substrate/Crud/SubstrateChange.cs` carries the typed proposition/witness state used by the write path. The outcome domain distinguishes refute/draw/confirm rather than treating omission as falsehood.

`engine/core/src/glicko2.c` implements the Glicko-2 fold in fixed-point arithmetic. The write path in `app/Laplace.Substrate/Crud/Npgsql/ConsensusAccumulatingWriter.cs` coalesces batch deltas and dispatches relation/type-scoped fold work so a cell remains on one ordered lane while independent relation/type partitions can progress concurrently.

The current architecture therefore has three distinct evidence stages:

```text
source observation / occurrence
-> attributed attestation
-> proposition-addressed fold
-> consensus standing (rating, RD, volatility, witnesses, ...)
```

A read may use conservative standing such as `rating - 2*rd`, but that scalar does not erase the underlying provenance, contradiction or typed relation state.

---

## 5. Ingestion and decomposition

Decomposers live primarily under `app/Laplace.Substrate/Abstractions/` and source/domain projects. The shared abstraction emits `SubstrateChange` state rather than giving each source its own SQL write semantics.

The intended/common ingest shape is:

```text
physical artifact enumeration
-> one-pass source stream
-> typed decomposition
-> working-set dedup / canonical reuse
-> bulk existence / tier work
-> set-sized persistence/COPY
-> evidence fold
-> receipt/journal completion
```

`app/Laplace.Substrate/Abstractions/IngestPipeline.cs` provides the generic streaming/worker contract, trunk short-circuiting and deferred/working-set behavior.

This common spine is important, but “uses the shared pipeline somewhere” is not sufficient proof that every source obeys the execution-grain law. Source-specific caller loops, per-record probes, private commit loops or per-element managed/native/database crossings remain architecture defects where they exist.

`OperationalDecomposer` admits the selected original invention and binding specification artifacts through the shared full-source native grammar/file pipeline under `SubstrateMandate` trust. The build copies their exact source bytes and preserves their repository paths beneath `seeds/operational/`; grammar, lexical composition and file provenance remain inspectable. This source does not replace the contracts with authored summaries or manufacture lexical instruction aliases. Source admission and execution of an ISA program are separate implementation requirements. See `seeds/operational/README.md` for the selected artifacts and ingestion command.

---

## 6. Universal execution grain

The physical split applies across **decomposition, ingestion, read/cognition, analysis/domain engines, reconstruction, synthesis and export**:

```text
PostgreSQL
  persistence / MVCC / transactions / B-tree, GiST, GIN, HASH / selective set access
        |
        | prepared, bounded/set-sized SPI
        v
native C/C++
  loops / recursion / parsing kernels / composition / trajectories /
  graph-search/frontier work / reductions / deterministic math /
  encoding / reconstruction / materialization
        |
        v
bounded/set result + receipt
```

C# and SQL remain orchestration/contract/transport boundaries. They should not become alternate inner-loop runtimes.

The architecture is therefore not “C is faster than SQL.” It changes the **grain** of execution. Patterns such as the following are defects when they sit in a hot/repeated loop:

```text
caller loop -> scalar SQL/native call
per-row SPI_prepare/SPI_execute
one P/Invoke per atom/node/candidate
recursive CTE as the graph/cognition inner engine
uncontrolled LATERAL fanout
per-call temp table/materialization
per-item transaction/COPY
batch API implemented as scalar calls in a loop
per-value high-level export/materialization
```

The intended performance gain has two independent factors:

```text
less work selected
  via canonical identity, indexes, containment, perfcache, reuse, hops/fanout

x

less overhead per selected unit
  via coarse native/set execution and fewer boundary crossings
```

Wider SIMD, AVX-512 and GPU providers are additional headroom, not the premise of the architecture.

---

## 7. Relation registry and indexed web

`engine/manifest/relation_types.toml` governs canonical relation identities, aliases, bands/ranks and append-only highway bits. Generated native data is produced by `scripts/codegen-attestation-law.py`.

The substrate is not merely a flat relation table. A canonical entity can simultaneously participate in:

- recursive child/parent composition;
- ordered trajectories and containment;
- typed relations/consensus;
- occurrences and sources/contexts;
- physical/Hilbert neighborhoods;
- deterministic calculations and domain-specific state.

Those independent indexed structures overlap on canonical identities. This is the implementation basis of the “spider-colony web” model: a query can pull several planes around the same exact entity/trajectory and preserve which routes responded.

---

## 8. Query-relative forward execution

The current repository no longer has only disconnected walk helpers. It contains a canonical forward-program surface.

`extension/laplace_substrate/sql/functions/generation/walk_continuations.sql.in` defines `generation.forward_program(...)` as one C entry point (`pg_laplace_forward_trace`) whose declared contract includes:

```text
exact prompt admission
query-relative routing
candidate adjudication
obligation closure
selection
semantic-act fingerprinting
working-state extension
terminal execution receipt
```

Its returned trace/receipt contains fields including root id, candidate/context/channel counts, occurrence coverage, relation families, opposition, support anchor/relation/rating/RD/witness/source/context state, routing round, program id, required/satisfied/remaining obligations, completion/disposition, output fingerprint and semantic-act id.

`extension/laplace_substrate/sql/functions/generation/walk_text.sql.in` makes `generation.forward_text(...)` invoke that canonical program once and only realize output after a completed semantic act exists. `converse.forward_turn(...)` supplies prior session turn/content identities as prior frontier state rather than rendering a transcript and reparsing it. `converse.chat(...)` currently projects the canonical forward-turn surface for normal chat.

Naming, sense and frame connections in the active query evidence resolve candidate identities. They do not establish an instruction or assign request/operand roles. The initial query is constrained only by an explicit caller relation mask.

The explicit bound relation-read reader in `prompt_intent.h` requires source-attributed `CALLS` and `HAS_INPUT` statements about the exact current request root, sharing one source and the invocation context explicitly supplied by the caller. The eleven-argument `generation.forward_program` overload takes that required context and delegates to the same native body as the existing ten-argument entry point. Here `CALLS` declares application of a predicate independently to each member of an input set; its object is a relation ID, not an ISA opcode. This contract does not encode ordered argument slots or repeated instructions. Declared input IDs can be semantic concepts reached through witnessed naming/sense bindings rather than literal surface constituents. The result is computed through the exact declared relation against those semantic IDs. Contract witnesses, source/context, predicate and input bindings participate in the program fingerprint. Competing contracts retain ambiguity, and completion requires the complete declared input identity set to be satisfied, including when distinct inputs share a surface occurrence.

Ordinary chat does not obtain an invocation context from a matching content string, alias or historical lesson. Deriving an executable interpretation for a novel natural-language request still requires a native consumer of witnessed structural, semantic-role and discourse constraints. Unicode surfaces and rendered languages retain their own content identities while converging through typed evidence on language-independent concept, predicate and program identities. Preserving the original ISA documents makes that source available; it does not itself implement the missing interpretation machinery.

### What is not yet proved by the existence of this entry point

The invention requires the whole admitted observation to produce a typed **query-relative coupling/response field** before interpretation/provider policy is prematurely frozen. The current native program claims query-relative routing/adjudication and exposes many trace dimensions; that does not by itself prove that every eligible structural, occurrence, relation, evidence, geometry, discourse and obligation plane participates with the intended semantics.

Therefore architecture documentation must distinguish:

```text
as built:
  canonical native forward program with prompt admission, routed evidence,
  adjudication, obligation closure, selection and receipts

invention/acceptance:
  complete typed coupling field over all eligible responding planes,
  joint interpretation/ambiguity handling before unconstrained policy,
  sparse execution compiled from that interpretation
```

Tests such as `scripts/test-forward-prompt-analysis.py`, extension regression coverage and OpenAI-compatible live-forward tests prove specific contracts. They do not magically prove every future coupling channel is complete.

---

## 9. Sparse star execution: hops and fanout

After admission/orientation, the current program exposes explicit hop/fanout parameters. Native/read operators include graph walk, A*/Dijkstra, containment, geometry, continuation and realization surfaces under `extension/laplace_substrate/src/` and the generated SQL catalog.

The useful compute shape is a sequence of indexed star expansions:

```text
active root/frontier
-> indexed responders
-> bounded admitted fanout
-> selected responders become next centers
-> convergent routes retained/ranked
-> repeat to hop/resource boundary
```

A* or Dijkstra is therefore one operator for one cost/goal contract, not the definition of cognition. The same is true of strongest-walk, n-gram continuation or geometric proximity.

Hops, fanout, candidate/frontier budgets and eligible operator/provider families are also the natural execution/billing dimensions because they bound explicit work over one shared knowledge world rather than selecting a deliberately smaller-knowledge model.

---

## 10. Read/operator surfaces

The extension keeps purpose schemas such as `ops`, `consensus`, `converse`, `lexical`, `taxonomy`, `generation`, `structural`, `chess` and `realize` rather than exposing one giant untyped namespace.

Representative native sources include:

| Source/family | Role |
|---|---|
| `recall.c` | indexed recall/definition/shape reads |
| `generate_walk.c` | graph/frontier expansion and strongest-walk operators |
| `astar_path.c` | Dijkstra / opt-in admissible geometric A* |
| `prompt_coherence.c` | prompt-relative coherence/election support |
| `trajectory_generate.c`, forward-program native code | continuation/routing/forward execution |
| `fold_route.c`, `consensus_fold_step.c` | consensus fold |
| `highway_mask.c`, `perfcache.c` | derived accelerator/highway operations |
| graph/model/containers/realize/geometry native families | typed domain and realization operators |

`SELECT * FROM ops.api('<substring>')` introspects the installed operation surface.

Perfcaches are derived accelerators. They may make selected direct addresses effectively constant-time within their admitted window, but a miss cannot redefine identity or truth.

---

## 11. Model/checkpoint lane

`engine/synthesis/` contains checkpoint/tokenizer/tensor readers and model-related native machinery. `engine/dynamics/` contains graph/spectral/algebra operators such as eigenmaps, Procrustes, Gram-Schmidt and related math. Foundry/export code materializes consumer formats such as GGUF under explicit recipes.

A conventional checkpoint is treated as a source/witness/calculation boundary, not as the hidden authority of Laplace cognition. Consumer structures such as layers, heads, Q/K/V/O and FFN roles can be recorded, compared or used as export targets without making them the native ontology of the substrate.

Export/reconstruction remains subject to the same universal execution-grain law: format correctness does not excuse one-tensor-cell-at-a-time or one-constituent-at-a-time boundary crossings in a hot path.

---

## 12. Build, install and runtime boundaries

Linux delivery is driven by `.github/workflows/laplace.yml` and `scripts/pipeline.sh`; host reconciliation/bootstrap lives in `scripts/setup-host.sh`. Windows entry points live under `scripts/win/`.

The PostgreSQL extension links engine code into the server-side extension build, so engine changes that affect extension behavior require the extension to be rebuilt/reinstalled before installed-regression/live proof is meaningful. `pg_regress` exercises the installed extension, not merely edited `.sql.in` source.

Protocol/product surfaces include the CLI, OpenAI-compatible endpoint, MCP endpoint, chess/UCI surfaces and the web product. These are adapters over the same substrate/operation laws, not separate intelligence implementations.

---

## 13. Proof and benchmark boundaries

Architecture claims are not all proved the same way.

- Bounded centroid closure is a mathematical invariant of the current native composition rule.
- Exact mantissa/trajectory round-trip is an executable serialization property.
- The selected finite Tier-0/Unicode placement window can be exhaustively exercised.
- Retained-database reconstruction tests prove exact reconstruction for their admitted fixtures/corpora.
- A populated live substrate is implementation evidence at scale, not a replacement for the theorem.
- Performance claims require exact revision/artifact/host/provider/workload receipts.

The manual benchmark contract is documented in `docs/benchmarks/MANUAL_BENCHMARK_EVIDENCE.md`.

On a live managed host it now distinguishes **serviceable capacity** from **absolute saturation**. Consuming every schedulable logical CPU and making PostgreSQL/product/runner/control-plane operation unavailable is not a valid serviceable-capacity result. Full saturation belongs to an explicit isolated/saturation profile.

---

## 14. Current architecture obligations exposed by this document

This section exists so “architecture as built” does not hide contradictions behind polished prose.

1. **Coordinate-law divergence.** Native composition uses Euclidean centroid; current managed `NgramTrajectory`/some domain paths use Karcher mean. One declared rule/meaning and reseed/migration proof is required if a universal coordinate law is claimed.
2. **Live recursive closure/integrity proof gate.** Existing unit/reconstruction tests prove important pieces, but the exhaustive live-database gate that resolves every inspected packed constituent, checks bounds/reference closure/RLE counts and emits counterexamples/counts is not yet landed. #1562 owns that executable proof obligation.
3. **Complete coupling-field acceptance.** A real canonical native forward program exists, but complete “tug every eligible strand and preserve typed response” coverage must be proved channel by channel rather than inferred from the function name.
4. **Universal execution-grain enforcement.** Native hot operators exist, but legacy/decomposer/export/analysis/domain paths can still violate the coarse native/set law. Such violations are implementation debts, not evidence that the architecture requires RBAR.
5. **Managed-host serviceable-capacity evidence.** The benchmark suite now enforces reserved default CPU headroom and explicit saturation opt-in; a fresh managed-host serviceable run still owes the empirical receipt proving that the configured reserve keeps required runner/database/product/control-plane health available.
6. **Query/cognition work receipts.** #1561 owns the database-backed benchmark obligation to measure hops/fanout/responders/typed work and preflight-vs-actual cost instead of reducing rich indexed operations to output tokens alone.

These are implementation obligations. None narrows the invention stated in `INVENTION.md`.
