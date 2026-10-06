# Laplace architecture

This document describes how the repository implements the machine in [`INVENTION.md`](INVENTION.md) and [`INVENTIONS.md`](INVENTIONS.md). Generated counts live in `docs/INVENTORY.md`.

When this document and the code disagree, fix the document. When two code paths disagree with each other, they implement one law. The invention is that law.

---

## 1. Persistent substrate

The PostgreSQL extension persists four primary substrate families under `extension/laplace_substrate/sql/schema/tables/`:

| Table | Primary role |
|---|---|
| `entities` | canonical executable content identities plus tier/type/source metadata |
| `physicalities` | typed physical realization: coordinate, Hilbert address, optional packed trajectory and constituent metadata |
| `attestations` | source-attributed typed testimony/observation |
| `consensus` | folded proposition standing: rating, RD, volatility, witness count and related state |

Everything else (journals, working-set and index bookkeeping) is operational state around these four tables, not knowledge. `docs/INVENTORY.md` is the generated catalog.

Every entity has a physicality; entities are both building blocks and content. Physicality admission records source/unit/time provenance through ordinary attestations only — an attestation recording that an entity has a physicality adds nothing and is not admitted.

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

### Referential integrity

The large partitioned write paths carry no per-row foreign keys. Content addressing lets every writer know a target id before the target row exists, so each writer lands leaves before the compositions, attestations and consensus cells that reference them (leaf → trunk). A reference to an absent id is a writer defect.

---

## 2. Executable content identity and recursive composition

Native composition is `engine/core/src/hash_composer.c`; the Merkle hash law is `engine/core/src/hash128.c`.

For a composed node:

```text
n == 0 -> zero/empty result
n == 1 -> child identity is preserved
n > 1  -> hash128_merkle(tier, ordered child ids, n)
```

The function signature carries `tier`, and `hash128_merkle` discards it. The law is that a composite id is BLAKE3 over the ordered 16-byte child ids, repeats included, truncated to 16 bytes, with nothing else in the input: no domain byte, tier, type, recipe, version, source, ordinal or container identity. A codepoint id is BLAKE3 of its UTF-8 bytes, from the Tier-0 ROM; a leaf hashes one to four bytes and a node at least thirty-two, so a node can never collide with a leaf. The current `hash128_merkle` prepends a `0x01` domain byte to the child-id sequence. That violates the law, disagrees with Laplace-Native and the Engine for every composition, and must be removed; do not implement from it.

So the identity law is:

```text
same ordered child-id sequence
-> same composite content id
```

The recipe decides how content decomposes into a tree; it never salts the hash.

regardless of the tier at which that same content is observed/used. Tier remains altitude/floor/storage/occurrence metadata rather than canonical content identity. A single-child composition is the child id.

Source identity is likewise not part of the canonical content hash, so the same canonical composition admitted from multiple sources converges.

The executable hash is a BLAKE3-derived 128-bit value. That is a finite address window, not a mathematical proof of global injectivity over an unbounded family of finite structures. Normal same-content convergence is **content-address convergence**, not a “hash collision”; a true same-id/different-preimage collision is a separate integrity event.

The recursive representation itself is larger than the id: exact ordered constituents are retained in the trajectory/composition structure.

---

## 3. Physicality: coordinate, carrier and realized curve

`physicalities` stores a 4D `coord`, a Hilbert address and, where the physicality is compositional, an exact packed constituent trajectory plus `n_constituents` and related metadata.

These are different things.

### 3.1 `coord` is the real geometric placement

The native composer in `engine/core/src/hash_composer.c` calls `math4d_centroid` over child coordinates and then derives the Hilbert address from that parent coordinate.

For child points inside/on the unit 4-ball, the Euclidean centroid remains inside/on that same ball (`INVENTION.md` §2).

Tier-0 atom placement is generated once into the versioned T0 perfcache. DUCET/UCA rank supplies the deterministic atom order, and `super_fibonacci_point_open(rank)` maps that rank onto S³ with a base-2 radical-inverse radial parameter. This decouples collation order from Hopf latitude: every rank prefix spreads across the whole shell. The mmap'd `laplace_t0_perfcache.bin` record (`id + uca_order + coord + hilbert + segmentation flags`) is what native and C# consumers read; nothing regenerates Tier-0 geometry independently. Tier-0 codepoints are never re-recorded as ingest novelty.

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

`extension/laplace_substrate/sql/functions/structural/entity_curve.sql.in` realizes curves. Fréchet/Hausdorff/shape operations belong on these realized coordinates, not on mantissa carriers.

The packed 16-bit ordinal/run fields are not global composition-size ceilings. `engine/core/src/trajectory.c` carries logical sequence position and run splitting beyond those local field widths.

Contains, precedes and co-occurrence are read from trajectories: `laplace_trajectory_constituent_ids(traj)` is the distinct-id set behind the GIN containment index (`@>`, `&&`); `laplace_trajectory_constituents(traj)` is the full ordinal-ordered sequence. Ordered reads fetch one WKB blob per trajectory and decode vertices natively with `mantissa_unpack`; vertex order in the blob is ordinal order, so no SQL sort or per-vertex unnest is needed.

### 3.4 One coordinate law

A composite's coordinate is the Euclidean centroid of its children's coordinates (`math4d_centroid`), and its Hilbert address is derived from that coordinate. Every lane that places a composite — text, chess, code, model, image, audio — uses that same native law.

---

## 4. Evidence and consensus fold

`AttestationRow` in `app/Laplace.Substrate/Crud/SubstrateChange.cs` carries the typed proposition/witness state used by the write path. The outcome domain distinguishes refute/draw/confirm rather than treating omission as falsehood.

`engine/core/src/glicko2.c` implements the Glicko-2 fold in fixed-point arithmetic. A claim is a game series (games plus a score in [0,1]; a draw is 0.5). A claim is an n-ary composition of content ids of any arity and tier, and consensus is keyed on that claim composition. The current consensus table is partitioned by `HASH(subject_id)` and keyed by `laplace.consensus_id`, `BLAKE3(subject ‖ type ‖ object)` with a zero-filled missing object. That violates the law, which keys consensus on the claim composition; do not implement from it. Scoring runs once over a source's landed evidence: each touched cell is one rating period over all of its durable evidence. Under the law, a witness's repeats of one claim are run-length games that the client folds into one rating period per cell per witness, so the database receives one update per cell per witness, and different witnesses play first in, first out.

Evidence moves through distinct stages:

```text
source observation / occurrence
-> attributed attestation
-> proposition-addressed fold
-> consensus standing (rating, RD, volatility, witnesses, ...)
```

A read may use conservative standing such as `rating - 2*rd`, but that scalar does not erase the underlying provenance, contradiction or typed relation state.

---

## 5. Ingestion and decomposition

Ingest is one generic flow for every source — curated corpora, models, chess, documents, user uploads. A source contributes a recipe; it does not contribute a writer.

```text
source trunk entity
  -> file trunk entities (one per artifact)
     -> record content hangs under its file through the file's physicality trajectory
extraction: every artifact of the source is decomposed (in parallel) into staged
            compositions (ids + ordered children), claims, and file/source trunks;
            colliding records converge in staging
stage 1:    prove novelty trunk-first against the substrate (a present trunk covers
            its subtree), realize physicalities for novel entities only, land
            entities / physicalities / attestations leaf -> trunk in bulk
stage 2:    Glicko-2 scoring of the landed evidence into consensus, once
```

Content shared with another source gains a second parent trajectory; that multi-parent DAG is the provenance. A record's fields are content like any other; its position in the file, its line number and its byte offset are occurrence under the file trunk, never identity, and file layout is packaging. Source text is recorded as it arrives: WordNet's `%p` stays `[%,p]` and WN-LMF's `n` stays `[n]`. That `%p` means the part relation, or that `n` means what UPOS `NOUN` means, is attested by the mapping source or the governed alias, and equivalent codes set the same registry mask bit, so sources converge without rewriting what they said. Unmapped syntax is explicitly unresolved, never dropped. A source's identifiers (sense keys, synset offsets, ILIs, rolesets, class ids) are content; structured ones decompose into their parts as ordered compositions, never joined strings, and what they identify is attested.

Locators: `app/Laplace.Substrate/Abstractions/IngestPipeline.cs` (streaming/worker contract), `NativeRecipeCompiler.cs` and `SemanticSourceRecipe.cs` (recipes), `recipes/` (per-source recipe data).

`OperationalDecomposer` admits the invention and binding specification artifacts as ordinary content through the same pipeline under `SubstrateMandate` trust. The build copies their exact bytes beneath `seeds/operational/`; see `seeds/operational/README.md`.

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

C# and SQL are orchestration/contract/transport boundaries, not inner-loop runtimes.

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

The performance gain has two independent factors:

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

`engine/manifest/relation_types.toml` governs the relation registry: aliases, bands/ranks and append-only highway bits; `scripts/codegen-attestation-law.py` generates the native tables. A relation is a content-derived entity whose meaning is attested and realizable in any language; the English names in the manifest are developer handles, and a highway bit is a perfcache slot over that entity, never its identity. A relation bit is one primitive meaning. Direction is an alias flip, never a second bit. Negation is a refute outcome, never a `NOT_` relation. A closed sub-category (part/member/substance) is a qualifier on the attestation; an open one (a property name) goes in the object as the composition `[property, value]`. Governed vocabularies (relations, UPOS, deprels, features, languages, modalities) are perfcache ROMs keyed by content id with append-only bit slots, never the identity of a meaning; entities carry OR-masks (`highway_mask` and its sister masks) deposited from consensus. A mask is a prefilter; a mask miss is never absence.

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

`generation.forward_program(...)` (`extension/laplace_substrate/sql/functions/generation/walk_continuations.sql.in`) is one C entry point, `pg_laplace_forward_trace`, over the whole program:

```text
RESOLVE -> COUPLE -> ORIENT -> ROUTE -> SCAN -> COMPOSE
        -> PROPOSE -> STEER -> SELECT -> REALIZE -> WITNESS
```

- **RESOLVE / Q.** The prompt is admitted as one exact Merkle DAG (codepoints → UAX#29 graphemes → words → sentences → root), probed trunk-first: the whole root or sentence is looked up first, and descent happens only on a miss.
- **COUPLE / K, QK.** Containment climbs from every node to every containing trajectory with exact ordinals; relation/consensus planes, masks and Hilbert locality respond from the same vertex ids. The response stays typed per plane.
- **ORIENT.** Joint interpretation over surviving bindings; real ambiguity is kept.
- **ROUTE / SCAN / COMPOSE / PROPOSE / STEER / SELECT.** Sparse indexed star expansion under hop/fanout budgets; operators (walks, A*, continuation, geometry) run inside it.
- **V.** The responding trajectories — their other constituents and continuations — plus the consensus cells of those entities.
- **O / REALIZE / WITNESS.** The receipted fold into bindings, frontier and obligations; each emitted constituent updates the trajectory and the next coupling. Exact witnessed continuation distributions ground output. The turn is witnessed as new content.

`generation.forward_text(...)` runs that program once and realizes output from the completed semantic act. `converse.forward_turn(...)` passes prior session turns as ordered discourse identities, not re-rendered text; `converse.chat(...)` is the chat surface over it.

The forward program is source-agnostic: it reads attestations, consensus, occurrence, trajectories and geometry as a whole and never decodes any particular source's layout. Recipes state a source's structure as claims; readers do not know which source made them. Language is context on a binding and a realization choice, never a code path.

---

## 9. Sparse star execution: hops and fanout

After admission/orientation, the program takes explicit hop/fanout parameters. Native/read operators include graph walk, A*/Dijkstra, containment, geometry, continuation and realization surfaces under `extension/laplace_substrate/src/` and the generated SQL catalog.

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

Hops, fanout, candidate/frontier budgets and eligible operator/provider families are the execution and billing dimensions: they bound work over one shared knowledge world.

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

A conventional checkpoint is a source. A model token is its text entity (the model's `king` is the same content id as any other `king`); the model-local token id is only an ordinal in the vocabulary composition. Tensor values are transient operands: they are read to derive the model's knowledge as trajectories — ordered compositions over decoded token content ids — and are never retained as weights or written as token-pair attestations. A circuit is a coordinate `[model witness, plane, layer, head, …]`. Export is a filtered snapshot of current standing, geometry and selected operators under a recipe.

Export and reconstruction follow the same execution-grain law: no one-tensor-cell-at-a-time or one-constituent-at-a-time boundary crossings.

---

## Compound capability architecture

### Multiscale image DAG versus scratch tier tree

`tier_tree_t` is a construction structure with one `parent_idx`: it schedules one decomposition. It is not the durable ontology for overlapping image windows.

A canonical 2×2 subpatch can belong to many 3×3, 4×4 and 8×8 occurrences simultaneously. The persistent model is therefore:

~~~text
canonical subpatch entity P
+ one Content physicality/trajectory describing P's ordered constituents
+ many parent/container occurrences/trajectory references to P
~~~

not one copied P per parent. The canonical subpatch DAG is its own structure; `parent_idx` is never overloaded with multiple parents.

### Compositional perfcache lattice

Cache families (T0/codepoint, highway, vocabulary, modality-number, chess position/transition) are members of one cache registry/dependency lattice (spec 33).

Higher deterministic tiers are valid mmap candidates:

~~~text
number -> pixel -> patch -> region -> image
number/sample -> window -> segment -> track
image + audio -> video composition
~~~

Dense finite domains may use direct addressing. High-cardinality domains may export
their finite admitted/hot canonical estate through a deterministic sparse lookup.

Every loader publishes through one dependency manifest, so video reuses loaded image/audio generations.

Cache/native lookup should resolve request-side keys before SQL/SPI queries so ordinary
database indexes remain eligible.

Selector-scoped modules are part of the registry. A deployment may load an ASCII range, an explicit color palette, a generated finite format domain, a speech/filter-bank calculation band, a hot/admitted set or the dependency closure of selected higher roots. These modules preserve global canonical ids/coords; they change residency/acceleration, not knowledge authority.



Read with `docs/CAPABILITIES.md`: product capabilities arise by composing substrate mechanisms, whether or not a table or function is named after the product verb.

### Software construction and reuse

Code/repository admission uses grammar-derived structure. Construction is the inverse: bind an exact target grammar/toolchain, couple the requirement against known canonical code, reuse/compose existing subtrees where possible, minimally mutate close structures, realize source, run toolchains/tests, witness outcomes, and iterate on the smallest divergent subtree.

Exact canonical tier/trajectory composition duplication is identity reuse. AST/CST is a derived projection when useful to a compiler/editor/toolchain; deeper normalized structural/algebraic/behavioral duplication is calculated evidence for consolidation.

### Application roots and repair trajectories

A repository root is the complete application object. A local code mutation should create new structure only along the changed ancestry; unchanged files/subtrees remain shared canonical state. Full checkout/export is realization of the resulting root.

Compiler/test/runtime failures and fixes are first-class ordered observations. Their AST/dependency/diagnostic/trajectory shapes can be queried against other authorized repositories to surface related defects or already-known repairs.

### Authority and compute

Tenant isolation, knowledge grants/capabilities, active scope, governance and compute envelope are separate inputs to execution. Unauthorized state must not merely be redacted after influencing cognition; COUPLE/ROUTE/REALIZE/EXPORT/EXECUTE eligibility must honor the effective authority boundary.

Hops/fanout/resources determine cognition depth/breadth over the allowed world. Knowledge entitlement does not imply unlimited compute, and compute allowance does not grant forbidden knowledge.

### Machine-cost lane

Source/bytecode/object/executable state lowers into control/data/dependency structure; with explicit execution counts and a target ISA/microarchitecture/memory/clock, it derives exact or symbolic cycles and time. Observed runs are witnesses against that calculation.

## 12. Build, install and runtime boundaries

Linux delivery is driven by `.github/workflows/laplace.yml` and `scripts/pipeline.sh`; host reconciliation/bootstrap lives in `scripts/setup-host.sh`. Windows entry points live under `scripts/win/`.

The PostgreSQL extension links engine code into the server-side extension build, so engine changes that affect extension behavior require the extension to be rebuilt and reinstalled. `pg_regress` exercises the installed extension, not the `.sql.in` sources.

Protocol/product surfaces include the CLI, OpenAI-compatible endpoint, MCP endpoint, chess/UCI surfaces and the web product. These are adapters over the same substrate/operation laws, not separate intelligence implementations.

---
