# Substrate cohesion issue ledger

Status date: 2026-08-19

This ledger maps every outcome from the SQL/substrate campaign to merged evidence,
an owning GitHub issue, and a falsifiable completion gate. It is the scheduling
companion to [SUBSTRATE_COHESION_STATUS.md](SUBSTRATE_COHESION_STATUS.md).

## Status authority

- Executable code, schema, tests, PostgreSQL plans, database measurements, and CI
  traces decide implementation status.
- A merged PR proves that code landed. It does not prove deployment, bounded
  ingest, full reseed, performance, or product behavior.
- [#1132](https://github.com/SaltyPatron/Laplace/issues/1132) is the campaign epic.
  This ledger links its children and dependencies instead of replacing them.
- [#1177](https://github.com/SaltyPatron/Laplace/issues/1177) owns the companion
  decomposer-normalization campaign. Shared identity/reseed gates must pass once,
  not independently under incompatible rules.

## SQL architecture and performance

| Outcome | Status | Landed evidence | Owning issue | Completion gate |
| --- | --- | --- | --- | --- |
| Whole-repository SQL scanner | Done | #1136; 550 files/26,292 lines/2,754 units on current main | [#1135](https://github.com/SaltyPatron/Laplace/issues/1135) | Scanner regression and high-severity CI remain green on a clean checkout. |
| Stable exact/near-clone inventory | Done as measurement | #1136; current 12 exact and 14 near clusters | [#1135](https://github.com/SaltyPatron/Laplace/issues/1135), [#951](https://github.com/SaltyPatron/Laplace/issues/951) | Every cluster has keep/fold/delete disposition and the production function/file count shrinks without reader drift. |
| Shrink-only SQL finding budgets | Partial | New unbaselined high findings fail; medium/low totals are reported | [#1135](https://github.com/SaltyPatron/Laplace/issues/1135) | Every warning class has an explicit non-increasing budget and linked measured disposition. |
| Canonical scalar/batch implementations | Partial | #1164 repairs `lexical_peers`; several good native batch families exist | [#1047](https://github.com/SaltyPatron/Laplace/issues/1047), [#1181](https://github.com/SaltyPatron/Laplace/issues/1181) | Every published family declares cardinality; scalar/batch/reference parity passes; no caller or batch loops a scalar database operation. |
| `structural.cluster` correctness | Broken on main | Non-empty scalar/batch failures reproduced; local prototype proves result shape only | [#1181](https://github.com/SaltyPatron/Laplace/issues/1181) | Non-empty parity fixtures and bounded warm/cold plan budgets pass. |
| Early result reduction | Audit only | #1136 inventories late limits, joins, SRFs, and fences | [#1135](https://github.com/SaltyPatron/Laplace/issues/1135) | Each hot operation proves candidate reduction precedes expensive rendering/scoring/fanout. |
| Honest top-k and truncation | Audit only | Numeric cap and unordered-limit inventory exists | [#1135](https://github.com/SaltyPatron/Laplace/issues/1135), [#1047](https://github.com/SaltyPatron/Laplace/issues/1047) | Every cap is classified as semantic top-k, work budget, transport cap, or explicit sample with deterministic ordering and underfill/truncation receipt. |
| Partition-prunable large-table reads | Partial | #1141 removed one full-tier apply scan | [#1135](https://github.com/SaltyPatron/Laplace/issues/1135), [#1008](https://github.com/SaltyPatron/Laplace/issues/1008) | Known `relation_canonical`/highway wrappers are removed from partition keys; plans prove bounded leaves and result parity. |
| Cheap default health/discovery | Not done | Exact scan defects measured | [#1135](https://github.com/SaltyPatron/Laplace/issues/1135), [#989](https://github.com/SaltyPatron/Laplace/issues/989) | Default health/catalog calls use estimates or maintained state; exact corpus scans are explicit offline operations with receipts. |
| SRF cardinality/cost contracts | Not done | Current audit finds most SRFs at default `ROWS 1000` | [#1047](https://github.com/SaltyPatron/Laplace/issues/1047), [#811](https://github.com/SaltyPatron/Laplace/issues/811) | Public operations carry measured `ROWS`, `COST`, volatility, and parallel-safety declarations or planner support. |
| Native set-sized prepared SPI | Partial | Native batch/probe code exists; no complete census/gate | [#1047](https://github.com/SaltyPatron/Laplace/issues/1047), [#588](https://github.com/SaltyPatron/Laplace/issues/588) | Hot native sites prove pinned prepared plans, bounded fetch/iteration, no per-row prepare, and plan/result parity. |
| Index/index-only workload audit | Partial | Several ingest probes and partition problems repaired | [#588](https://github.com/SaltyPatron/Laplace/issues/588), [#1135](https://github.com/SaltyPatron/Laplace/issues/1135) | Declared serving/recovery workloads justify every large index; plans report index-only eligibility, heap fetches, bytes, and maintenance cost. |
| SQL/C# thin adapters over shared cores | Partial | Many hot primitives are native; independent SQL/C# semantics remain | [#951](https://github.com/SaltyPatron/Laplace/issues/951), [#811](https://github.com/SaltyPatron/Laplace/issues/811) | ISA families terminate in one relational/native core; adapters contain only binding, transaction, orchestration, or transport logic. |

## Identity, composition, modality, and media

| Outcome | Status | Landed evidence | Owning issue | Completion gate |
| --- | --- | --- | --- | --- |
| Same recovered content -> same hash | Partial | Native BLAKE3/Merkle and media numeric-floor parity tests | [#1132](https://github.com/SaltyPatron/Laplace/issues/1132), [#904](https://github.com/SaltyPatron/Laplace/issues/904) | Cross-language/native/SQL entry-order fixtures prove identical content ids while metadata/provenance changes only occurrence/testimony. |
| Codepoints as universal floor | Core implemented, global gate missing | Text/image/audio use codepoint leaves; dense 0-255 cache matches text roots | [#1132](https://github.com/SaltyPatron/Laplace/issues/1132), [#1043](https://github.com/SaltyPatron/Laplace/issues/1043) | Every admitted content recipe descends to validated version-pinned codepoints; stale/invalid floor caches fail loudly. |
| Recursive content composition | Partial | Tier trees, Merkle composition, and trajectories reuse constituent ids | [#1045](https://github.com/SaltyPatron/Laplace/issues/1045), [#1048](https://github.com/SaltyPatron/Laplace/issues/1048) | Fray census proves no unlawful nonphysical content, missing constituent, off-DAG identity, or content salted by source facts. |
| One logical entity per id | Not done | None; schema still keys `(id, tier)` | [#1052](https://github.com/SaltyPatron/Laplace/issues/1052), [#1008](https://github.com/SaltyPatron/Laplace/issues/1008) | Database uniqueness and all ingest novelty/cache keys agree on one id; multi-tier duplicates are impossible. |
| Tier as recipe-relative altitude, not identity | Not done in storage | Native collapse behavior exists; database partitions/key by tier | [#1008](https://github.com/SaltyPatron/Laplace/issues/1008), [#1132](https://github.com/SaltyPatron/Laplace/issues/1132) | Tier/role observations coexist independently of identity and never select the only surviving entity row. |
| Recipe-specific structural realizations | Not done | Physicalities are globally keyed by opaque id but no governed recipe/realization contract exists | [#1052](https://github.com/SaltyPatron/Laplace/issues/1052), [#1134](https://github.com/SaltyPatron/Laplace/issues/1134) | Multiple lawful realizations coexist; their preimage/version/recipe is queryable and deterministic. |
| Occurrence/interpretation separate from content | Partial | Several decomposers now preserve occurrences and typed references | [#1045](https://github.com/SaltyPatron/Laplace/issues/1045), [#1177](https://github.com/SaltyPatron/Laplace/issues/1177) | Every type/modality/role/tier/source claim has an occurrence/interpretation home without changing content identity. |
| Canonical append-only modality registry | Not started | Incompatible media/model/grammar concepts remain | [#1133](https://github.com/SaltyPatron/Laplace/issues/1133) | Stable bits, aliases, tombstones, recipe families, generation, and capacity are generated identically in C/C#/SQL. |
| Derived modality masks | Not started | Highway mask is a related but different accelerator | [#1133](https://github.com/SaltyPatron/Laplace/issues/1133), [#469](https://github.com/SaltyPatron/Laplace/issues/469), [#529](https://github.com/SaltyPatron/Laplace/issues/529) | Authoritative interpretation evidence rebuilds zero/one/many modality bits; removal/staleness affects speed only. |
| Cross-modal trajectories | Not proven | Generic trajectories are type-agnostic | [#1133](https://github.com/SaltyPatron/Laplace/issues/1133) | Mixed text/image/audio/code constituents round-trip with unambiguous recipe/roles while geometry remains modality-agnostic. |
| Image exact reconstruction | Not done | RGBA, width, and height reach the ingest record; only flattened values are witnessed | [#1134](https://github.com/SaltyPatron/Laplace/issues/1134) | Dimensions, channel order, values, recipe, and provenance round-trip; codec-equivalent recovery converges. |
| Audio exact reconstruction | Not done | PCM and sample rate reach the ingest record; sample rate/channel layout are not witnessed | [#1134](https://github.com/SaltyPatron/Laplace/issues/1134) | PCM order, rate, channels/layout, recipe, and provenance round-trip; codec-equivalent recovery converges. |
| Shared value `255` with independent roles | Partial | Modality-number cache and image tests converge on decimal content root | [#1134](https://github.com/SaltyPatron/Laplace/issues/1134), [#1052](https://github.com/SaltyPatron/Laplace/issues/1052) | Text/image/audio/network observations share one content id while every role/recipe/source claim survives every ingest order. |
| OpenSubtitles content/occurrence split | Broken on main | #1163 removed pairwise semantic fanout but salted durable content with source/batch facts | [#1180](https://github.com/SaltyPatron/Laplace/issues/1180) | Rebatching/provenance changes preserve content ids and change only governed source occurrences; bounded reader/size parity passes. |

## Perfcaches and mask algebra

| Outcome | Status | Landed evidence | Owning issue | Completion gate |
| --- | --- | --- | --- | --- |
| T0 codepoint perfcache | Implemented, enforcement incomplete | Dense Unicode cache is loaded by native and managed paths | [#1043](https://github.com/SaltyPatron/Laplace/issues/1043) | Loader enforces pinned UCD version, shared property mapping, sanity census, checksum, and native/managed parity. |
| Dense modality-number cache | Implemented | 256 records; ids match decimal text content roots | [#1133](https://github.com/SaltyPatron/Laplace/issues/1133) | Cache is registered/versioned in the common bundle and fallback yields identical ids/coordinates. |
| Highway perfcache/mask | Implemented as an accelerator, law incomplete | Native/C#/SQL operations and fallback paths exist | [#469](https://github.com/SaltyPatron/Laplace/issues/469), [#529](https://github.com/SaltyPatron/Laplace/issues/529) | One mask algebra, bit-identical parity, and tests proving no mask miss changes authoritative answers. |
| Unified generated perfcache bundle | Not done | T0, highway, number, and chess caches have separate formats/loaders | [#1132](https://github.com/SaltyPatron/Laplace/issues/1132), [#1133](https://github.com/SaltyPatron/Laplace/issues/1133) | Registry declares generation/checksum/dependencies/capacity; deterministic build, atomic publish, load state, hit/miss/fallback, and parity are observable. |
| Chess deterministic caches | Partial companion work | Position/transition blobs and atomic publication exist | [#838](https://github.com/SaltyPatron/Laplace/issues/838) | Live/PGN parity, history-sensitive state, cross-source identity, projection, and clean-seed proof pass. |

## Extension installation, staging, and reseed

| Outcome | Status | Landed evidence | Owning issue | Completion gate |
| --- | --- | --- | --- | --- |
| Manifested extension modules | Mostly done | Completeness-gated install/upgrade manifests and generated SQL chains | [#1132](https://github.com/SaltyPatron/Laplace/issues/1132), [#1135](https://github.com/SaltyPatron/Laplace/issues/1135) | Fresh install and every supported upgrade produce the same governed catalog and reproducible artifact hashes. |
| Content-derived extension version | Done for SQL artifact | CMake hashes manifest-listed SQL inputs | [#1132](https://github.com/SaltyPatron/Laplace/issues/1132) | CI proves any shipped input changes version and unshipped/orphan files cannot affect or evade it. |
| Seed population outside `CREATE EXTENSION` | Done structurally | Extension DDL and decomposer population are separate | [#1132](https://github.com/SaltyPatron/Laplace/issues/1132) | Install is quick/reproducible and never hides corpus population or long maintenance. |
| Durable per-file resume | Implemented; production acceptance partial | #898 closed after generic journal/canonical fixes | [#1045](https://github.com/SaltyPatron/Laplace/issues/1045), [#1177](https://github.com/SaltyPatron/Laplace/issues/1177) | Real hard-kill campaigns across large sources prove durable-before-complete and no refolding of completed files. |
| Deterministic stage/conform/global merge | Not done | Existing pipeline batches directly into substrate writes/folds | [#1045](https://github.com/SaltyPatron/Laplace/issues/1045), [#1132](https://github.com/SaltyPatron/Laplace/issues/1132) | Source stages are resumable; identity/testimony merge globally; collision disagreements fail loudly; fold/masks/indexes run once by phase. |
| Loud identity collision handling | Not done | Known `ON CONFLICT DO NOTHING` path remains tracked | [#959](https://github.com/SaltyPatron/Laplace/issues/959) | Same id/different preimage or governed claim fails admission with a reproducible receipt. |
| Fold throughput | Partial | Writer/fold improvements landed; CILI bottleneck remains owner | [#964](https://github.com/SaltyPatron/Laplace/issues/964) | Every source meets declared cells/s and amplification budgets with exact fold parity. |
| Universal ingest throughput verdict | Not done | CI/scripts and journal metrics are not one enforcement surface | [#1080](https://github.com/SaltyPatron/Laplace/issues/1080) | CLI/API/MCP/UI/CI runs record and evaluate the same substrate-owned baseline/verdict. |
| Bounded source admission | Not done after merge batch | Unit/integration suites pass; standing DB predates merge batch | [#433](https://github.com/SaltyPatron/Laplace/issues/433), [#1175](https://github.com/SaltyPatron/Laplace/issues/1175), [#1177](https://github.com/SaltyPatron/Laplace/issues/1177) | Foundation and every large lane publish rows, amplification, bytes, RSS, IO, WAL, temp, skew, restart, idempotency, and reader receipts. |
| UD database acceptance | Not done | Representation/sizing merged; isolated canary completed ISO only | [#433](https://github.com/SaltyPatron/Laplace/issues/433), [#1177](https://github.com/SaltyPatron/Laplace/issues/1177) | EWT then full UD complete with bounded RSS/database growth, truthful journals, restart, and idempotency. |
| <=2-hour clean full seed | Not done | Phase envelope documented; no qualifying run | [#1132](https://github.com/SaltyPatron/Laplace/issues/1132) | Parse/stage <=35m, consolidate <=20m, fold <=35m, indexes <=20m, analyze/validate <=10m on declared hardware. |

## Operation publication, diagnostics, and product proof

| Outcome | Status | Landed evidence | Owning issue | Completion gate |
| --- | --- | --- | --- | --- |
| Explicit operation allow-list | Not done | `ops.api()` discovers broad schemas/functions | [#989](https://github.com/SaltyPatron/Laplace/issues/989), [#811](https://github.com/SaltyPatron/Laplace/issues/811) | Only registered operations are reachable; internal, destructive, aggregate, and maintenance shapes require explicit lifecycle/safety policy. |
| Operation metadata contract | Not done | Catalog exposes name/args/result/kind only | [#811](https://github.com/SaltyPatron/Laplace/issues/811), [#1047](https://github.com/SaltyPatron/Laplace/issues/1047) | Registry declares cardinality, modalities, safety, cost, bounds, ordering, truncation, receipt, version, and parity. |
| One typed dispatcher | Not done | MCP has an installed-op invoker; surface parity remains open | [#812](https://github.com/SaltyPatron/Laplace/issues/812) | MCP, OpenAI-compatible HTTP, internal API, CLI, and UI invoke the same operation id/program and emit equivalent receipts. |
| Shared inspect/debug/coverage operations | Partial | SQL and admin diagnostics exist piecemeal | [#811](https://github.com/SaltyPatron/Laplace/issues/811), [#1153](https://github.com/SaltyPatron/Laplace/issues/1153), [#1175](https://github.com/SaltyPatron/Laplace/issues/1175) | Every source/operation exposes the same coverage, plan/work, provenance, amplification, and validation diagnostics through every surface. |
| Normalized readers | Partial companion work | Several lexical/chess readers changed | [#1178](https://github.com/SaltyPatron/Laplace/issues/1178) | Readers traverse senses, occurrences, collections, and trajectories without compatibility testimony; parity fixtures pass. |
| Stateful forward pass | Not done | Architecture and acceptance issues exist | [#921](https://github.com/SaltyPatron/Laplace/issues/921), [#924](https://github.com/SaltyPatron/Laplace/issues/924) | One program performs resolve -> orient -> route -> scan -> compose -> propose -> steer -> select -> realize -> witness and mutates frontier after each emitted constituent. |
| Seeded behavioral product gate | Not done | Harness scaffolding exists; product acceptance remains open | [#755](https://github.com/SaltyPatron/Laplace/issues/755) | MCP and HTTP pass correction, anaphora, topic return, abstention, source ablation, code feedback, model consensus, and trace parity on the certified seed. |

## Session delivery and preservation ledger

| Artifact | State | Evidence/use | Required next action |
| --- | --- | --- | --- |
| #1136 SQL audit | Merged | `docs/sql-cohesion-audit-2026-08-18.md`, scanner, tests, CI | Keep measurements current and execute #1135 rather than treating the audit as completion. |
| #1155 source-fidelity audit | Merged baseline | `docs/semantic-source-fidelity-audit-2026-08-19.md` | #1153/#1177 own implementation; preserve unpublished follow-up edits separately. |
| #1164 lexical peer batch | Merged | Scalar/batch semantics and measured warm-buffer improvement | Use as one reference pattern; do not claim family-wide completion. |
| #1180 OpenSubtitles identity | Open issue | Concrete merged violation of same-content law | Fix before full OpenSubtitles ingest. |
| #1181 structural cluster | Open issue plus local prototype | Correctness defects, parity experiment, unacceptable plan cost | Redesign/rebase, add seeded regression and plan budgets, then publish. |
| UD isolated canary | Incomplete experiment | ISO completed; UD never created a run/file journal | Rerun only after current main is installed in an isolated DB with full measurement. |
| Discontinuous FrameNet local commit | Preserved, unpublished | `a57f620b` on `fix/framenet-occurrence-spans` | Return to #1177 ownership; validate/rebase before publication. |
| Source-remediation sequence draft | Preserved, unpublished | Local `agent/source-fidelity-audit` edits | Reconcile with #1177 ledger and publish only non-duplicative content. |

## Release order

1. Correct #1180/#1181 and audit merged content preimages for the same defect
   class.
2. Complete #1052/#1008/#904/#1048 global identity and materialization law.
3. Complete #1133/#1134 modality, masks, and exact media reconstruction.
4. Complete #811/#812/#989/#1047 operation registry, cardinality, and dispatcher.
5. Execute #1135's proven planner/full-scan queue, then its measured clone and
   scalar/batch queue.
6. Complete #1153/#1177/#1178 source fidelity and normalized readers.
7. Complete #1175/#1080 amplification, convergence, skew, bytes, and throughput
   gates.
8. Implement #1045/#1132 staged global consolidation and fold/index phases.
9. Run bounded sources, including a real UD acceptance run and corrected
   OpenSubtitles benchmark.
10. Run the <=2-hour clean full seed, publish the complete receipt, then run
    #755/#921/#924.
