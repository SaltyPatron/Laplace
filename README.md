# Laplace

[![Build, deploy, test](https://github.com/SaltyPatron/Laplace/actions/workflows/laplace.yml/badge.svg)](https://github.com/SaltyPatron/Laplace/actions/workflows/laplace.yml)

**A deterministic, content-addressed AI substrate that stores exact structure once, learns by witnessing, and reasons by pulling an indexed web instead of rebuilding relevance from a dense parameter tensor on every request.**

Laplace starts from a simple observation: a countably open Tier-0 address law and any finite observed prefix can support an unbounded family of finite recursive compositions. Give every admitted atom a deterministic identity and placement, compose larger objects recursively, preserve their exact ordered constituent trajectories, and the whole construction can remain inside one fixed bounded geometric domain. Unicode is one finite standards-defined materialization of that open Tier-0 law, not its capacity.

That makes text, code, games, media, model checkpoints, conversations and other finite symbolic structures instances of the same machine rather than separate pipelines.

An arbitrarily long history of finite observations remains countable, so adding content over time does not require expanding the geometric extent. The same bounded world can become denser and more richly interconnected while identity, trajectory/composition structure, derived AST/DAG projections, relations, evidence and provenance preserve distinctions that coordinates alone do not.

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

These mechanisms compose into the product:

- **Structural software construction:** code is grammar-derived recursive structure. Generation should reuse, compose or minimally mutate known canonical tier/trajectory structures before inventing new code; AST/CST is realized only when a consumer or toolchain requires it; compile/test/runtime outcomes witness the next repair.
- **Duplicate-code convergence:** exact canonical tier/trajectory compositions converge automatically; derived AST shape, normalized structure, control/data flow, algebraic form and behavioral evidence can expose deeper duplicate implementations for consolidation.
- **Whole-application mutation and fast realization:** a repository is one recursively composed application root. A small edit creates new identities only along the changed ancestry; unchanged subtrees are structurally shared. A checkout is an export of the resulting complete root.
- **Repair content across repositories:** failed and successful development attempts are witnessed trajectories. Their tier/trajectory structure plus derived AST/dependency/diagnostic/Fréchet shapes can surface similar problems in other authorized repositories.
- **Deterministic machine-cost analysis:** source, bytecode, executable/container and machine instructions can lower into control/data/dependency structure and then into target-ISA/microarchitecture cycle expressions instead of being reduced to benchmark averages.
- **One content world with explicit authority and compute:** content packages grant governed scope/capabilities over the shared world; billing meters how deeply and broadly the same intelligence searches it through hops, fanout and physical work.
- **Governed abstention and security:** content remains content. Authority/firmware decides what may be coupled, derived, realized, exported or executed, with explicit WHY/WHY_NOT receipts.

See [`docs/CAPABILITIES.md`](docs/CAPABILITIES.md) for the binding synthesis.

## What is different

- **Exact recursive identity.** Same content means the same executable entity; a recipe decides how content decomposes into a tree, never what its hash is. Repetition does not mint another copy of `king`, the same chess line, the same code composition or the same document fragment; new occurrences add provenance and evidence around reusable structure.
- **One composition is one entity everywhere.** [k,i,n,g] is the same king entity whether it is used as a word, name, title, label or constituent. Likewise, an exact 2x2 pixel composition inside an 8x8 region is that same global 2x2 entity wherever it occurs; parent/tier/role live in trajectories and occurrences, not the hash.
- **Open Tier-0, bounded geometric realization.** Tier-0 has no architectural terminal rank; each selected executable generation materializes a finite set of ranks. The selected Unicode generation places its finite standards window deterministically on the unit 3-sphere, while native composition remains on or inside the bounded 4D ball. Adding ranks densifies the same bounded world rather than increasing its geometric extent.
- **Lossless trajectories.** A content trajectory stores the exact ordered constituent identities. Packed trajectory vertices are reversible manifests, not fake spatial positions; realized curves resolve those identities back to child coordinates in ordinal order.
- **Numbers are trajectories too.** Ordinary finite digital values do not consume new Tier-0 atoms. `0.34567` composes from `0 . 3 4 5 6 7` into one reusable scalar root; repeated samples/channels reference that root as new occurrences. A finite prefix of π is the same wide-composition mechanism.
- **Perfcaches compose too.** Deterministic reusable tiers can be mmap ROMs: numbers feed pixels/samples, pixels feed patches/regions/images, audio samples feed windows/tracks, and video reuses the same image/audio cache generations. A complete parent hit short-circuits descendant recomposition; partial misses descend only into novel branches.
- **Cache profiles are modular.** A deployment can map only ASCII, a chosen color palette, a declared speech-frequency/filter-bank band, hot patches/images, or any dependency closure it actually needs. Subsets retain the same global canonical identities; the profile changes residency and cost, not meaning.
- **Images are multiscale structural lattices.** An 8×8 region contains 204 contiguous square occurrences across scales 1×1 through 8×8. Those are references into globally canonical reusable subpatches, so repeated texture/shape/background structure converges across images and video rather than being rediscovered.
- **A navigable web, not a flat graph.** Canonical entities simultaneously participate in recursive compositions, containing trajectories, occurrences, typed relations, contexts, sources and geometric neighborhoods. Concepts can be traversed through Hilbert/locality, angular/curve/set geometry, containment, order, usage and evidence planes inside the same bounded world; pull one strand and many independently indexed structures can answer.
- **Interpretation is a response of the web.** The whole admitted observation, its constituent occurrences, prior discourse bindings and open obligations perturb every eligible indexed plane. Mutually compatible responses determine orientation, bindings and provider/program admissibility; Laplace does not need to choose one meaning first and then search around that decision.
- **Sparse forward execution.** Hops and fanout bound indexed star expansions over the responding part of the world. A*, Dijkstra, strongest-walk, trajectory continuation, containment, geometry and relation-specific reads are operators inside that program—not the definition of cognition by themselves.
- **Evidence instead of opaque authority.** Observations become attributed attestations. Consensus carries Glicko-2 rating, RD, volatility and witness count, so support, uncertainty, disagreement and provenance remain queryable.
- **Learning is online.** Admitting new evidence changes standing without an offline gradient-training cycle. Previously known structure is reused instead of relearned into another copy of a parameter tensor.
- **One content world, variable compute.** Product/resource tiers can govern hop depth, fanout, providers, search work and other explicit execution budgets without pointing lower-cost requests at a deliberately content-reduced model.
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

The bounded-composition theorem does not require 4D. The carrier format is why 4D is convenient.

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

`mantissa_pack()` and `mantissa_unpack()` round-trip that payload exactly. A 3D binary64 carrier still has room for a 128-bit identity but less metadata; a 2D carrier does not fit that identity in one vertex. That is a format trade, not a limit on the invention.

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

## Repository map

```text
engine/       native core, graph/math/dynamics, synthesis and format machinery
extension/    PostgreSQL substrate: schema, indexes, native operators and SQL surfaces
app/          .NET ingestion, services, APIs, MCP, chess, migrations and tests
web/          Vite/React product surface
scripts/      build, seed, install and CI entry points
docs/         invention, architecture, specs, guides and generated inventory
```

Start with:

- [`docs/INVENTION.md`](docs/INVENTION.md) — the machine
- [`docs/INVENTIONS.md`](docs/INVENTIONS.md) — mechanism catalog
- [`docs/CAPABILITIES.md`](docs/CAPABILITIES.md) — what the laws compose into
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — how this repository implements it
- [`AGENTS.md`](AGENTS.md) — implementation rules for this repository
- [`docs/README.md`](docs/README.md) — documentation map

## Build, Run, and Operate

Laplace provides unified entry points for building from source, running the local development server, managing the substrate and application databases, and operating installed releases across Linux and Windows.

### 1. Build and Test from Source

**Linux (`Justfile` / `scripts/pipeline.sh`):**
```bash
sudo bash scripts/setup-host.sh          # One-time host bootstrap / toolchain setup
bash scripts/pipeline.sh build           # Build native engine, PG extension, C# app, and web UI
bash scripts/pipeline.sh install         # Install extension and runtime artifacts
bash scripts/test-parallel.sh --engine   # Run engine and substrate verification suite
```

**Windows (`laplace-ops` / `scripts/win/`):**
```powershell
laplace-ops prepare                      # Fetch pinned grammars and Unicode reference inputs
laplace-ops build                        # Build native C++, managed .NET solution, CLI, and web UI
laplace-ops test                         # Run native interop and decomposition test suites
```

### 2. Run the Application

```powershell
# Run in foreground (http://localhost:5187)
laplace-ops run

# Or manage as a background development instance
laplace-ops start
laplace-ops status
laplace-ops logs
laplace-ops stop
laplace-ops verify
```

To build, publish, migrate databases, and verify a local IIS installation in one step:
```powershell
laplace-ops deploy -Target desktop
```

Windows web builds reuse `web/dist` when the source inputs, generated API contract,
toolchain and output hashes match. Dependency restore has its own lock receipt in
the configured build root. `scripts/win/build-web.cmd --force` explicitly restores
and rebuilds; `--skip-install` uses existing dependencies without certifying a new
installation. `deploy/windows/Build-IisPackage.ps1 -ForceWeb` requests the same forced
web rebuild while packaging IIS.

Desktop development and IIS delivery use the same component builder. API, CLI,
UCI, migrations and web each have an independent artifact receipt, with managed
dependencies taken from the project-reference graph. `laplace-ops deploy desktop
-Components web` updates the frontend; `-Components api,cli` selects API/web and CLI;
`-Components modular` selects Engine/MCP delivery independently. `-Plan` shows the
selected stages and `-Force` explicitly rebuilds the selected closure. An unchanged
full build reuses its component artifacts and complete verified IIS package.

Extension publication preserves existing PostgreSQL sessions, including ingests
between working windows. IIS activation reconnects its own pools; other clients
finish with the runtime they loaded. `scripts/win/install-extensions.cmd
--force-recycle` is an explicit maintenance operation that terminates clients.
The legacy `--recycle` flag preserves them during ordinary publication.

`laplace-mono ingest chain` validates all source names before starting its first
source. An invalid later name fails immediately without admitting earlier sources.

### 3. Database Lifecycle & Maintenance

Laplace separates content/substrate state (`LAPLACE_DB`) from accounts, sessions, API keys, and billing (`LAPLACE_APP_DB`). You can inspect, migrate, repair (recreate after an external drop), or reset either database without rebuilding the application:

**Via `laplace-ops` (Windows / cross-target operator CLI):**
```powershell
# Check migration status (substrate, application, or both)
laplace-ops db -Database both -DatabaseOperation status

# Apply pending migrations or recreate a missing (dropped) database
laplace-ops db -Database substrate -DatabaseOperation repair
laplace-ops db -Database application -DatabaseOperation repair

# Reset (drop and recreate from migrations, preserving role grants)
laplace-ops db -Database substrate -DatabaseOperation recreate -Yes
laplace-ops db -Database application -DatabaseOperation recreate -Yes

# Recycle the local IIS worker cleanly after database maintenance
laplace-ops restart-iis
```

**Via `scripts/db-migrations.sh` or `Justfile` (Linux / direct migrator):**
```bash
scripts/db-migrations.sh status          # Combined or substrate migration status
scripts/db-migrations.sh up              # Create missing DB(s) and apply pending migrations
scripts/db-migrations.sh nuke --yes      # Recreate substrate DB from migrations
scripts/db-migrations.sh app-up          # Apply application DB migrations only
scripts/db-migrations.sh app-nuke --yes  # Recreate application DB from migrations
just db-fresh                            # Nuke, migrate, sync extensions, tune PG, and check health
```

See [docs/guides/database-maintenance.md](docs/guides/database-maintenance.md) and [deploy/windows/DATABASE-MAINTENANCE.md](deploy/windows/DATABASE-MAINTENANCE.md) for full details.

### 4. Ingestion & CLI Usage

Once installed, `laplace-mono` runs from any working directory without requiring a build shell:

```bash
laplace-mono --where
laplace-mono status
laplace-mono ingest unicode
laplace-mono ingest document /path/to/documents --verify
laplace-mono chat "Explain this relationship"
```

For substrate introspection directly in PostgreSQL:
```sql
SELECT * FROM ops.api('walk');
```

See [docs/guides/installed-mono-client.md](docs/guides/installed-mono-client.md), [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), and the guides under [docs/guides/](docs/guides/) for the complete operational surface.

## Native read contracts

Exact entity and physicality sets use `laplace_entities` and
`laplace_physicalities` from Laplace-postgres. Engine and MCP share
`Laplace-Engine/src/read_ids.c`; monorepo inspection and batch geometry use the
same primary-key owner through `ops.entity_physicalities`. Request ordinals
retain duplicates, missing IDs stay absent, and reads retain the caller's
snapshot and installed namespaces.

CLI inspection and UI provenance share `ops.evidence_series_receipt` over
`ops.claim_witness_series`. Native containment supplies each registered
witness's record count (`games`), occurrences (`tokens`), total outcome score,
trust, dependence root and position. The score divided by games is a mean
outcome, not a categorical confirmation. `ops.evidence_receipt` aggregates
those same series into distinct witness counts for descriptions and summary
receipts. Source IDs and claim IDs remain available beside their labels.

## License

See [LICENSE](LICENSE). Seeded sources retain their own licenses and attribution/provenance in the substrate.
