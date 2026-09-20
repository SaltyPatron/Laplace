# Laplace

[![Build, deploy, test](https://github.com/SaltyPatron/Laplace/actions/workflows/laplace.yml/badge.svg)](https://github.com/SaltyPatron/Laplace/actions/workflows/laplace.yml)
[![Benchmark evidence](https://github.com/SaltyPatron/Laplace/actions/workflows/benchmark-evidence.yml/badge.svg)](https://github.com/SaltyPatron/Laplace/actions/workflows/benchmark-evidence.yml)

**A deterministic, content-addressed AI substrate that stores exact structure once, learns by witnessing, and reasons by pulling an indexed web instead of rebuilding relevance from a dense parameter tensor on every request.**

Laplace starts from a simple observation: a countably open Tier-0 address law and any finite observed prefix can support an unbounded family of finite recursive compositions. Give every admitted atom a deterministic identity and placement, compose larger objects recursively, preserve their exact ordered constituent trajectories, and the whole construction can remain inside one fixed bounded geometric domain. Unicode is one finite standards-defined materialization of that open Tier-0 law, not its capacity.

That makes text, code, games, media, model checkpoints, conversations and other finite symbolic structures instances of the same machine rather than separate pipelines.

An arbitrarily long history of finite observations remains countable, so adding knowledge over time does not require expanding the geometric extent. The same bounded world can become denser and more richly interconnected while identity, trajectory, AST/DAG structure, relations, evidence and provenance preserve distinctions that coordinates alone do not.

```text
source / observation
        |
        v
exact recursive composition ----> physicality / trajectory
        |                                  |
        +---- attributed testimony --------+
                         |
                         v
                  Glicko-2 standing
                         |
                         v
prompt/root --> query-relative coupling --> indexed star expansion --> converge/rank
                    ^                         hops + fanout                |
                    |                                                    v
                    +---------------- witness <----- realize <------------+
```

Laplace is not a transformer implementation. It reconstructs many of the jobs for which transformers use tokenization, embeddings, attention, learned weights, layers, KV state and autoregressive decoding with explicit identities, trajectories, indexes, witnessed relations, uncertainty-bearing standing and sparse traversal.

## What the architecture enables

The mechanisms below are not isolated research curiosities. Together they imply product capabilities that must remain visible in design and implementation:

- **Structural software construction:** code is grammar-derived recursive structure. Generation should reuse, compose or minimally mutate known AST structure before inventing new code; compile/test/runtime outcomes witness the next repair.
- **Duplicate-code convergence:** exact canonical AST duplicates converge automatically; normalized structure, control/data flow, algebraic form and behavioral evidence can expose deeper duplicate implementations for consolidation.
- **Whole-application mutation and fast realization:** a repository is one recursively composed application root. A small edit creates new identities only along the changed ancestry; unchanged subtrees are structurally shared. A checkout is an export of the resulting complete root.
- **Repair knowledge across repositories:** failed and successful development attempts are witnessed trajectories. Their AST/dependency/diagnostic/Fréchet shapes can surface similar problems in other authorized repositories.
- **Deterministic machine-cost analysis:** source, bytecode, executable/container and machine instructions can lower into control/data/dependency structure and then into target-ISA/microarchitecture cycle expressions instead of being reduced to benchmark averages.
- **One knowledge world with explicit authority and compute:** knowledge packages grant governed scope/capabilities over the shared world; billing meters how deeply and broadly the same intelligence searches it through hops, fanout and physical work.
- **Governed abstention and security:** knowledge remains knowledge. Authority/firmware decides what may be coupled, derived, realized, exported or executed, with explicit WHY/WHY_NOT receipts.

See [`docs/CAPABILITIES.md`](docs/CAPABILITIES.md) for the binding synthesis. Tests, gates, issues and benchmarks prove these capabilities; they are not substitutes for implementing them.

## What is different

- **Exact recursive identity.** Same canonical content under the same declared recipe means the same executable entity. Repetition does not mint another copy of `king`, the same chess line, the same AST subtree or the same document fragment; new occurrences add provenance and evidence around reusable structure.
- **Open Tier-0, bounded geometric realization.** Tier-0 has no architectural terminal rank; each selected executable generation materializes a finite set of ranks. The current Unicode generation places its finite standards window deterministically on the unit 3-sphere, while native composition remains on or inside the bounded 4D ball. Adding ranks densifies the same bounded world rather than increasing its geometric extent.
- **Lossless trajectories.** A content trajectory stores the exact ordered constituent identities. Packed trajectory vertices are reversible manifests, not fake spatial positions; realized curves resolve those identities back to child coordinates in ordinal order.
- **Numbers are trajectories too.** Ordinary finite digital values do not consume new Tier-0 atoms. `0.34567` composes from `0 . 3 4 5 6 7` into one reusable scalar root; repeated samples/channels reference that root as new occurrences. A finite prefix of π is the same wide-composition mechanism.
- **Perfcaches compose too.** Deterministic reusable tiers can be mmap ROMs: numbers feed pixels/samples, pixels feed patches/regions/images, audio samples feed windows/tracks, and video reuses the same image/audio cache generations. A complete parent hit short-circuits descendant recomposition; partial misses descend only into novel branches.
- **A navigable web, not a flat graph.** Canonical entities simultaneously participate in recursive compositions, containing trajectories, occurrences, typed relations, contexts, sources and geometric neighborhoods. Concepts can be traversed through Hilbert/locality, angular/curve/set geometry, containment, order, usage and evidence planes inside the same bounded world; pull one strand and many independently indexed structures can answer.
- **Interpretation is a response of the web.** The whole admitted observation, its constituent occurrences, prior discourse bindings and open obligations perturb every eligible indexed plane. Mutually compatible responses determine orientation, bindings and provider/program admissibility; Laplace does not need to choose one meaning first and then search around that decision.
- **Sparse forward execution.** Hops and fanout bound indexed star expansions over the responding part of the world. A*, Dijkstra, strongest-walk, trajectory continuation, containment, geometry and relation-specific reads are operators inside that program—not the definition of cognition by themselves.
- **Evidence instead of opaque authority.** Observations become attributed attestations. Consensus carries Glicko-2 rating, RD, volatility and witness count, so support, uncertainty, disagreement and provenance remain queryable.
- **Learning is online.** Admitting new evidence changes standing without an offline gradient-training cycle. Previously known structure is reused instead of relearned into another copy of a parameter tensor.
- **One knowledge world, variable compute.** Product/resource tiers can govern hop depth, fanout, providers, search work and other explicit execution budgets without pointing lower-cost requests at a deliberately knowledge-reduced model.
- **Modality is grammar, not architecture.** UAX/NFC is the text floor; code uses structural grammars; chess uses moves/positions/lines/games; images, audio, documents and model artifacts decompose through their own typed ladders into the same identity/physicality/evidence substrate.
- **Receipts are part of the product.** Selection can carry the identities, relation types, standing, uncertainty, source scope, trajectory state and work budget that produced it.

## PostgreSQL stores the world; native code executes it

Laplace does not put cognition—or any other repeated high-volume algorithm—inside a tower of per-row orchestration calls.

PostgreSQL owns durable state, MVCC, B-tree/GiST/GIN/HASH indexes, set selection and server-side integration. Native C/C++ owns the hot recursive and repeated work: decomposition, composition, trajectory operations, frontier expansion, graph/search mechanics, reductions, deterministic math, reconstruction, encoding/materialization and other inner loops.

SPI is the bridge, not the cognition engine. Hot paths are shaped around prepared, set-sized fetches and bounded native execution rather than repeatedly crossing SQL/function boundaries for every candidate.

```text
indexed/set-sized fetch
        |
        v
native arrays + state
        |
        +--> loops / recursion / fanout / reductions / search
        |
        v
bounded result + receipt
```

That execution grain is deliberate across **decomposition, ingestion, reads, cognition, analysis/domain engines, reconstruction, synthesis and export**. RBAR loops, recursive SQL used as an inner engine, repeated scalar calls, uncontrolled `LATERAL` fanout, per-element P/Invoke/SPI, per-call temp-table machinery, per-item transaction/COPY and duplicated scalar/batch bodies are architecture defects when they move repeated algorithmic work out of the coarse native/set core.

The speedup is therefore not merely “C++ is faster than SQL.” Laplace tries to do **less work** by addressing only the responding frontier, and to pay **less overhead per selected unit** by keeping the inner loop native.

## Why four dimensions?

The bounded-composition theorem does not require 4D. The current machine representation has a more concrete reason to like it.

Each binary64 component provides 53 reversible carrier bits when Laplace fixes the exponent and uses the sign plus mantissa. One `GeometryZM` trajectory vertex therefore carries exactly:

```text
4 × 53 = 212 bits

128  complete constituent entity id
 16  packed ordinal
 16  run length
 52  flags / typed metadata
---
212
```

`mantissa_pack()` and `mantissa_unpack()` round-trip that payload exactly. A 3D binary64 carrier still has room for the current 128-bit identity but less metadata; a 2D carrier does not fit that identity in one vertex. That is an implementation trade, not an information-theoretic limit on the invention.

See [`engine/core/src/mantissa.c`](engine/core/src/mantissa.c), [`engine/core/src/trajectory.c`](engine/core/src/trajectory.c) and [`docs/INVENTION.md`](docs/INVENTION.md).

## Conventional-model roles, different machinery

| Conventional role | Laplace-native source |
|---|---|
| tokenizer / vocabulary | deterministic atom + modality decomposition |
| embedding / address | canonical identity, physicality, Hilbert/locality, typed strata |
| position | trajectory ordinal + realized curve |
| Q | active admitted observation + discourse bindings + open obligations |
| K | indexed typed addresses able to respond |
| QK / attention | query-relative coupling: which typed paths respond to the current state |
| attention heads | independent relation/operator/provider/tier/context planes |
| V | responding physicalities, facts, evidence and calculations |
| O | receipted fold into updated bindings, frontier, orientation and obligations |
| layer stack | repeated couple / expand / fold / update rounds |
| KV/runtime memory | substrate + session/frontier + rebuildable perfcaches |
| training update | witnessing + uncertainty-bearing consensus fold |
| decoding | dynamic trajectory continuation and realization |

The important substitution is physical: a transformer repeatedly synthesizes relevance from learned latent state; Laplace records and indexes witnessed/composed structure so the forward program can calculate attention as a sparse response of explicit state.

The response is not collapsed immediately into one universal relevance scalar. Structure, relation identity, ordinal/gap state, evidence, contradiction, standing, source scope, geometry and provenance remain typed until the selected program decides which are gates, costs, ranking dimensions or evidence.

## Measured floor

The repository ships a revision-bound benchmark workflow that builds the exact selected source revision, hashes/binds the produced native artifacts, records host provenance and uploads machine-readable evidence.

A completed evidence run on the project's 6-core/12-thread Intel i7-6850K host measured the native composition path, **single-threaded, with no PostgreSQL and no GPU selected**, at approximately:

```text
1.86 million Unicode codepoints / second
465 thousand 4-char BPE-equivalent input units / second
4.56 million exact tier-tree nodes / second
```

A later run of the same benchmark family has also reproduced the same order of magnitude on that host. The BPE-equivalent number is only a familiar normalization: the underlying work is exact Unicode handling, segmentation, recursive tier-tree construction, content/Merkle identity and geometric composition—not one flat token operation.

Whole-machine, database-backed, query/cognition and accepted-answer benchmarks remain separate profiles so unlike workloads are not silently promoted into one number. The point of the current result is the floor: millions of exact structural operations per second on decade-old consumer CPU hardware before modern ISA width, accelerator offload or mature-substrate reuse is credited.

See [`docs/benchmarks/MANUAL_BENCHMARK_EVIDENCE.md`](docs/benchmarks/MANUAL_BENCHMARK_EVIDENCE.md) and [the benchmark workflow](.github/workflows/benchmark-evidence.yml).

## What is already here

The repository contains the native core and PostgreSQL substrate, multimodal decomposers, evidence/consensus machinery, spatial and trajectory indexes, graph/trajectory search operators, model/checkpoint ingestion and synthesis tooling, chess as an executable proving domain, a React product surface, and multiple protocol fronts:

- OpenAI-compatible HTTP API
- MCP server
- CLI / SQL operation surfaces
- UCI chess engine and Lichess integration
- entity/profile/evidence/trajectory exploration
- model/checkpoint readers and GGUF materialization
- deterministic benchmark and CI evidence lanes

The system deliberately keeps conventional checkpoints, corpora, tools, users and generated outputs as participants/witnesses in one world rather than making any one of them the hidden authority.

## Repository map

```text
engine/       native core, graph/math/dynamics, synthesis and format machinery
extension/    PostgreSQL substrate: schema, indexes, native operators and SQL surfaces
app/          .NET ingestion, services, APIs, MCP, chess, migrations and tests
web/          Vite/React product surface
scripts/      build, seed, benchmark, verification and CI entry points
docs/         invention, architecture, specs, evidence, plans and generated inventory
```

Start with:

- [`docs/README.md`](docs/README.md) — documentation authority map
- [`docs/INVENTION.md`](docs/INVENTION.md) — the intended invention, head to tail
- [`docs/INVENTIONS.md`](docs/INVENTIONS.md) — mechanism/capability catalog
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — architecture as currently built
- [`AGENTS.md`](AGENTS.md) — execution contract for implementation agents
- [`docs/benchmarks/MANUAL_BENCHMARK_EVIDENCE.md`](docs/benchmarks/MANUAL_BENCHMARK_EVIDENCE.md) — reproducible performance evidence

## Build and run

Linux uses the repository pipeline that CI executes:

```bash
sudo bash scripts/setup-host.sh   # host bootstrap / reconciliation
bash scripts/pipeline.sh build
bash scripts/test-parallel.sh --engine
```

The main delivery workflow handles build, install, database lifecycle, application publication and product proof on the managed host. Windows entry points live under `scripts/win/`.

For substrate introspection after installation:

```sql
SELECT * FROM ops.api('walk');
```

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) and the guides under [`docs/guides/`](docs/guides/) for the full operational surface.

## License

See [LICENSE](LICENSE). Seeded sources retain their own licenses and attribution/provenance in the substrate.
