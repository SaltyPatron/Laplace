# Chat, geometry, and operation audit — 2026-08-09

**Status:** measured implementation report; not current authority

**Branch measured:** `agent/codex-chat-operation-audit` from `origin/main` at `954abd29`

**Authority:** running behavior and current code outrank this report. The durable
contract is `docs/plan/REAL_CONVERSATION_AND_MODEL_CONSENSUS_FINISH_LINE.md`.

## Outcome

Ordinary text composition obeys the physicality law: a composed entity's PointZM is
derived from the real placements of its children. Its mantissa-packed trajectory
contains their identities and ordering; that packed geometry is not the centroid
input.

Two model-ingest paths violated this law. They placed projections from packed
factor/testimony vertices. They now use real token placements and derive Hilbert from
the resulting centroid. The Constellation API already returned real PointZM, but the
browser discarded M, normalized XYZ, and reapplied the 4D radius. That collapsed
distinct points onto common display rays. It now rotates the actual X–M plane before
orthographic projection and exposes the rotation angle.

The chat adapter also crossed several dishonest boundaries: a trailing assistant
message could become the prompt, controls were accepted without execution, MCP-only
converse shapes were hidden from HTTP, and a code model was advertised without a
compile/attest/revise loop. Those behaviors are corrected in this branch. The
canonical dynamic-frontier conversation and Gödel code loop remain unfinished and
are not relabeled as complete.

## Geometry: the two coordinate-looking payloads

1. `physicalities.coord` is the real `geometry(PointZM)` placement. X/Y/Z/M drive
   `radius_origin`, the GiST geometry index, and `hilbert_index`.
2. `physicalities.trajectory` is a `geometry(GeometryZM)` ordered manifest.
   `Trajectory.Build` mantissa-packs constituent `Hash128` values, ordinal flags, and
   specialized testimony into valid doubles. Those vertices are not the child
   entities' PointZM placements.

`NgramTrajectory.Compose` collects each child's real X/Y/Z/M, computes
`Math4d.Centroid(coords)`, encodes Hilbert from that centroid, and separately calls
`Trajectory.Build(childIds)`. `TextEntityBuilder` likewise writes the native
composition tree's placement to PointZM and the packed child manifest to trajectory.

### Live installed-operation proof for `cat`

This proof used the deployed MCP's catalogued `converse.resolve`, `constituents`, and
`entity_physicality_coords` operations. No raw SQL was issued.

| Entity | X | Y | Z | M |
|---|---:|---:|---:|---:|
| `c` child | -0.08515472746995996 | 0.06205109997665473 | 0.9712065517771341 | 0.2136730379958429 |
| `a` child | 0.028552609843525943 | 0.10041694554690862 | -0.7485571252615492 | 0.6548002869106869 |
| `t` child | 0.10969790228008013 | -0.023450683249382282 | -0.8434230074008723 | -0.5254084756428615 |
| child centroid | 0.017698594884548703 | 0.046339120758060355 | -0.20692452696176244 | 0.1143549497545561 |
| stored `cat` PointZM | 0.017698594884548703 | 0.046339120758060355 | -0.20692452696176244 | 0.1143549497545561 |

The stored parent equals the child centroid at floating precision. The trajectory
operation independently returned the ordered child ids `c`, `a`, `t`. This directly
rejects the hypothesis that normal `cat` placement is an average of packed hash
vertices.

### Model placement defects corrected

`ModelTokenEdgeETL` contained the suspected category error:

- factor deposits copied `dep.Xyzm[0..3]`, the first packed trajectory vertex, into
  PointZM and left Hilbert at its default;
- structure-circuit deposits built a packed signature trajectory and averaged its
  vertices with `Math4d.Centroid`.

Both now select salient token entities, look up their deposited content PointZM
placements, compute the real centroid, and encode Hilbert from it. Factor and ranked
testimony remain exclusively in `TrajectoryXyzm`. The ETL test independently asserts
the exact PointZM/Hilbert placement and trajectory constituent order. If no ranked
token is placeable, ingest retains the testimony and emits a warning but omits the
projection physicality; it never fabricates a point from packed hashes or zero.

### Why Constellation looked linear/clouded

The backend gets graph-node geometry through `EntityPrimaryFormsBatchAsync`, which
reads `laplace.entity_physicalities(id)`: real PointZM. The display transform was:

```text
old: normalize(X,Y,Z) * hypot(X,Y,Z,M)
new: rotate the actual (X,M) plane, then display (X',Y,Z)
```

The old transform erased M and mapped every point sharing an XYZ direction onto one
ray. Large uniform spheres obscured the remaining separation. The new transform
retains M with an adjustable X–M rotation and does not normalize away interior
position.

One sampling defect remains. `/v1/visualizations/substrate` selects endpoints of
`consensus.top_relations(limit)`. That is a salience-biased relation sample, not a
representative PointZM/Hilbert-space sample. Projection is corrected, but the
Constellation still needs an installed typed spatial-sampling operation before it can
claim to survey the substrate's spatial distribution.

## Chat surface audit

| Behavior | Before | Branch state | Still missing |
|---|---|---|---|
| Prompt role | Last non-empty message of any role | Newest non-empty `user` message | Ordered roles/content parts in the canonical conversation trajectory |
| Session history | Client history ignored; topic carry in substrate | Unchanged and explicit | Versioned turn/message/tool trajectory |
| Converse controls | MCP exposed `shape`, `bands`, `elaborate`; HTTP did not | HTTP validates and passes all three | Shared capability schema generated from installed operations |
| Unsupported controls | Several accepted and ignored | Rejected for the selected lane | Implement stop/top-p only when supported |
| Walk controls | `window` and `top_k` ignored | Passed as `maxOrder` and `topK` | Canonical transition shared with converse |
| Natural `define whale` | `define` won topic election | Pinned `definition → define` relation-namer alias demotes the operator | Extension install/restart and live proof |
| Code model | Advertised, then routed through generic walk | Not advertised and rejected | Authorized edit → compile/test → attest → revise/abort loop |
| Web search | Accepted fields without execution | Explicit unsupported error | Governed tool/receipt contract |
| Empty consensus | Truthful empty result | Preserved | Capability/seed explanation in UI |
| Timing | No comparable receipt | Substrate, first-result, total, output size, and rate | Persistent traces and percentiles |

Natural `define whale` failed for a precise native reason. Live measurement showed
`prompt_coherence('define whale')` rank `define` ahead of `whale`; semantic tie-break
fields were zero. The namer table recognized manifest surface `definition` but did not
map imperative `define`. The new alias marks `define` as the relation operator, so the
entity token can win. Explicit `query(shape=define, topic=whale)` already returned the
installed definition.

Removing `laplace-code-001` is an honesty fix, not code-generation delivery. Code is
complete only when repository state, compiler/test outcomes, diagnostics, revisions,
and accept/abort decisions become one witnessed trajectory under the Gödel/OODA loop.

## Performance receipt semantics

OpenAI chat now reports:

- `substrate_ms`: time awaiting substrate work; streaming excludes SSE write/backpressure;
- `first_result_ms`: request start to first substrate result;
- `elapsed_ms`: request start to response construction;
- UTF-8 bytes, Unicode code points, and whitespace-delimited words;
- generated trajectory tokens and trajectory tokens per substrate second on walk.

The MCP wrapper adds elapsed time, canonicalized inner-result bytes/code points, row
count, and rows per second to successful JSON-object tool results. The size fields
explicitly exclude the attached performance envelope; measuring that envelope would
be self-referential. This makes typed operations comparable without client-authored
SQL.

`walk_text` tokens are substrate trajectory selections, not necessarily checkpoint or
GGUF tokenizer tokens. They are not directly comparable to llama.cpp tokens/second.
An equivalence experiment must hold realized text and tokenization unit constant or
report both units.

## SQL, C#, and native orchestration audit

### Installed SQL operations

The function tree contains 33 files with PL/pgSQL bodies, 257 with SQL bodies, and 57
with C bindings. Recursive CTEs occur in only three installed operations:
`readback/constituents_closure`, `analysis/laplace_ancestry`, and
`consensus/relate_path`.

Dynamic `EXECUTE` occurs in lifecycle/maintenance work (`drop_retired_content_lane`,
`geometry_audit`, `consensus_fold_result`, `evict_source`, and
`attestation_merge`), not the current chat hot path.

PL/pgSQL loops are mostly bounded maintenance. Two runtime paths deserve attention:

- `structural.cluster_batch` loops over seeds and invokes `structural.cluster` per
  seed: an installed batch facade that remains database-side RBAR;
- `converse.converse_tiered` loops per step and realization item. It remains off the
  product hot path because its content defect is unresolved.

### Native SPI heavy lifters

Counting `SPI_` sites/tokens identifies the native orchestration centers:
`recall.c` 137, `generate_walk.c` 97, `variant_synth.c` 63,
`prompt_coherence.c` 58, `trajectory_generate.c` 48, `lexical_case.c` 43,
`realize_batch.c` 42, `graph_taxonomy.c` 34, `graph_cascade.c` 31, and
`astar_path.c` 29. A high count is not itself a defect; it marks where plans, SPI
lifecycle, batching, and timing deserve first inspection.

The division is broadly correct: SQL/PLpgSQL composes installed operations; native
C/C++ handles election, recall/walk, graph traversal, trajectory generation,
realization, and model math. `converse.chat` is a SQL orchestrator over those typed
operations rather than C#-generated dynamic SQL.

### C# round trips and RBAR

The inspected chat and visualization paths do not call the database once per output
node. Visualization batches physicalities and evidence counts; explain batches
attestations; label resolution uses batch reads. C# loops primarily map results.

Remaining orchestration debt:

- tenant-scoped chat creates `pg_temp.consensus` from
  `consensus.scoped_consensus(sources)` per request. Shadowing is intentional, but the
  read surface performs DDL/materialization and is not strictly read-only;
- resolution and label realization can require separate round trips;
- explorer detail endpoints compose several serial installed reads and need measured
  end-to-end budgets;
- Constellation sampling is coupled to top relations rather than a spatial operation.

## What is good, bad, slow, and absent

### Good and verified

- Normal content placement bubbles up from real child PointZM; packed trajectory
  preserves identity/order separately.
- Explicit query shapes and the foundation lexical seed produce useful structured reads.
- MCP is wired to `/opt/laplace/app/laplace-mcp` and exposes catalogued operations.
- Chat, visualization geometry/evidence, explain attestations, and labels use batched reads.
- Native extension, .NET solution, endpoint contracts, and production web build together.

### Bad or misleading, corrected here

- model circuit/factor PointZM derived from packed trajectory values;
- browser projection discarded M;
- HTTP chat selected assistant/system content as prompt;
- converse controls differed between MCP and HTTP;
- accepted-but-unused controls;
- advertised code model without a feedback loop;
- absent response/tool performance receipts.

### Slow or structurally risky

- tenant-scoped consensus materialization per request;
- serial explorer composites without one latency receipt;
- PL/pgSQL RBAR in `structural.cluster_batch` and `converse_tiered`;
- production chunks: `three` about 736 kB and `force-graph` about 917 kB minified;
- relation-biased Constellation selection renders a dense, semantically narrow subset.

### Absent

- canonical ordered conversation trajectory and dynamic-frontier forward pass;
- tool-call/result and structured-output protocols;
- code execution/attestation/revision loop;
- spatially representative Constellation sampling operation;
- pooled heterogeneous-model source/circuit ablation acceptance;
- persistent performance trace ids and percentile comparison;
- seeded profiles beyond the current foundation roster.

## Verification record

- full .NET solution build: succeeded, zero warnings/errors;
- OpenAI-compatible hermetic tests: 171 passed;
- model placement tests: 6 passed, including exact PointZM/Hilbert assertions;
- elector architecture tests: 4 passed;
- MCP project build: succeeded, zero warnings/errors;
- IntelLLVM engine and PostgreSQL extensions: 230 build steps succeeded;
- OpenAPI generation, TypeScript typecheck, and production web build: succeeded;
- full endpoint run: 172 passed and two DB-tier tests failed because the deployed DB
  lacks `consensus.highway_mask_deposit(bytea[], bytea[])`. This is an explicit
  deployment/schema mismatch, not a hermetic failure.

The branch was built but not installed or deployed. The native alias, MCP performance
envelope, HTTP fields, and UI changes are therefore not claimed as live behavior.
