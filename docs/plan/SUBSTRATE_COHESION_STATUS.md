# Substrate cohesion, SQL, modality, and reseed campaign status

Status date: 2026-08-20

This is the integration inventory for the substrate-cohesion campaign established
by [#1132](https://github.com/SaltyPatron/Laplace/issues/1132). It covers the SQL,
identity, modality, media, perfcache, operation-surface, extension-install, and
greenfield-reseed work. It does not replace the decomposer campaign inventory in
[DECOMPOSER_NORMALIZATION_STATUS.md](DECOMPOSER_NORMALIZATION_STATUS.md).

## Executive verdict

The repeatable SQL audit and several important infrastructure corrections are
landed. The generated PostgreSQL extension package is also substantially more
mature than a monolithic SQL-file count suggests. The overall campaign is not
close to production acceptance, however, because its governing storage and
operation contracts remain unresolved:

- `entities` still permits the same id at multiple tiers through `PRIMARY KEY
  (id, tier)`;
- content identity, recipe-specific realization, occurrence/interpretation, and
  typed testimony do not yet have independently enforceable homes;
- image dimensions and audio rate/channel layout are still discarded by the
  witness path;
- there is no canonical modality registry or derived modality mask;
- `ops.api()` is still broad catalog discovery rather than a governed operation
  registry;
- only a small fraction of the measured SQL remediation queue has been executed;
- the merged OpenSubtitles representation currently salts content identity with
  source schema, language, ordinals, and arbitrary batch boundaries;
- the merged implementation has not passed bounded source gates or a clean full
  reseed.

Judgment rather than a measured completion percentage:

| Lane | Estimated completion | Reason |
| --- | ---: | --- |
| SQL audit and non-regression instrumentation | 90% | Scanner, reports, tests, and high-severity CI ratchet are merged. Planner-budget fixtures and automatic linkage from every finding to measured disposition remain. |
| Measured SQL remediation | 15% | One full-tier apply scan and one lexical scalar/batch family are repaired. Most proven pruning/scan defects and clone/cardinality families remain. |
| Extension artifact/install mechanics | 70% | Manifest completeness, content-derived versions, generated install/upgrade chains, and native library installation exist. Lifecycle compatibility and clean-install/deploy receipts remain incomplete. |
| Content/identity/materialization contract | 20% | Native same-content hashing and codepoint-floor composition exist; physical database uniqueness and recipe/occurrence separation do not. |
| Modality, media reconstruction, and derived masks | 20% | Image/audio ladders and the shared numeric floor exist. Registry, masks, exact reconstruction testimony, and entry-order gates do not. |
| Perfcache architecture | 45% | T0, highway, modality-number, and chess caches exist. One generated registry/bundle, lifecycle metadata, parity, and fallback law are not universal. |
| Governed operations and endpoint parity | 10% | A parameterized installed-operation invoker exists, but publication, metadata, safety policy, receipts, and MCP/OpenAI/HTTP parity remain unresolved. |
| Greenfield population and <=2-hour reseed proof | 10% | Resume, index recovery, and ingest telemetry improved in the decomposer campaign. The staged global consolidation program and final timed seed do not exist. |
| Full campaign through certified production reseed | 25-35% | The audit foundation is credible; the governing schema, operation, modality, and release proofs remain the majority of the work. |

These estimates exclude the separately cataloged decomposer-normalization campaign.

## Governing laws

1. The same canonical recovered content has the same hash. Source, label,
   language, modality, tier, recipe, interpretation, and observation do not salt
   content identity.
2. Codepoints are the common floor. Every higher content node is an acyclic
   canonical composition of existing content ids and may itself be a constituent.
3. Ordered structure is a trajectory. Unordered multi-value state is a collection
   composition. A source claim is testimony, not a replacement for either.
4. Content identity, structural realization, occurrence/interpretation, and typed
   testimony are separate objects with separate keys.
5. Tier is a position/floor within an interpretation recipe, not a global identity
   category.
6. Modality and highway masks are derived routing accelerators. They never enter
   content hashes and a miss is never authoritative absence.
7. SQL and C# are planner, transaction, orchestration, and transport adapters.
   Shared relational or native cores own semantics; callers do not loop hidden
   scalar database operations.
8. Greenfield idempotency is deterministic staging and consolidation with loud
   collision failure, not `DO NOTHING` that conceals disagreement.

## Evidence baseline

### Repository audit

The audit rerun on merged `main` at `d640b1ad` reported:

```text
550 SQL files / 26,292 lines
2,754 auditable units
609 findings
12 exact + 14 near-clone clusters
high=0 medium=446 low=83 info=106
```

The original #1136 baseline was 549 files, 25,861 lines, 2,712 units, and 602
findings. The high-severity ratchet remains green, but total findings and SQL
surface area have not begun a sustained reduction.

### Live database

The current `ops.source_status(NULL)` roster contains only CILI, Unicode, ISO639,
WordNet, VerbNet, PropBank, FrameNet, MapNet, WordFrameNet, SemLink, and
PredicateMatrix. Its successful source runs occurred around 02:32-02:41 UTC; the
large normalization merge batch landed around 18:35-18:43 UTC. The standing
database therefore proves the earlier representation, not the merged one. It has
no completed UD, OMW, Wiktionary, ConceptNet, ATOMIC, Tatoeba, OpenSubtitles,
model, or chess source run.

### Installed schema and surfaces

- `extension/laplace_substrate/sql/schema/tables/entities.sql.in` still declares
  `PRIMARY KEY (id, tier)` and partitions by tier.
- `physicalities` has a global id key, but the id preimage and row shape do not
  expose a governed recipe/realization identity contract.
- `ops.api()` enumerates broad schemas from `pg_proc` and returns only name,
  arguments, result, and routine kind.
- `ImageIngestRecord` carries width/height and `AudioIngestRecord` carries sample
  rate, but their `WalkWitness` methods emit only completion/file metadata.
- media ladders use codepoint leaves; the dense 0-255 modality-number perfcache
  resolves ids through the same text content-root operation.
- native `laplace_modality_t`, managed `MediaLadderKind`, model `Modality`, and
  grammar/recipe identifiers remain distinct incompatible concepts called
  modality.

## Outcome catalog

| Expected outcome | Status | What is actually true |
| --- | --- | --- |
| Repeatable whole-repository SQL audit | Done | #1136 merged the dependency-free scanner, stable findings, clone detection, report, tests, and CI ratchet. |
| Exact and near-clone remediation | Started | The audit finds 12 exact and 14 near-clone clusters. #1164 normalized one lexical family; the repository has not entered a shrink-only consolidation cycle. |
| One semantic scalar/batch core per operation | Partial | `lexical_peers`, `structural.cluster`, `attested_language`, and `bubble_up` delegate to canonical set cores with nonempty parity fixtures. Other independent scalar/batch and SQL/native families remain. |
| Early result reduction and honest top-k | Partial | `cluster`, `bubble_up`, `surface_sample`, angular KNN/locale, global top relations, band facts, evidence/salience reads, vocabulary heads, and consensus-web traversal no longer hide guessed candidate multipliers. Rendering is total where identifiers are valid output; vocabulary lexical filters consume complete ranked populations through set-based pages. Read/foundry APIs preserve declared capacities including zero, fixed 32/64/512 read truncation is gone, and graph allocation is derived from the requested traversal shape or an explicit caller capacity. Remaining fixed limits and transport/operator caps still require classification. |
| Index-friendly predicates and partition pruning | Partial | #1141 removed one 194M-row apply scan. Function-wrapped consensus keys still defeat type partition pruning in known read operations. |
| No default full-corpus health/discovery scans | Not done | `relation_bands()` and default substrate health retain exact whole-corpus work. Exact maintenance/deep-audit scans are not yet cleanly separated from cheap defaults. |
| Planner contracts for public functions | Not done | Most SRFs retain default `ROWS 1000`; cost, volatility, parallel safety, bounds, and work receipts are not governed from one registry. |
| Native prepared set-sized SPI for hot paths | Implemented for the confirmed census | `graph_contrast` binds both endpoints and every resolved synset in one ordinal-preserving array query; `geometry_successors` exposes one native array core and a scalar SQL adapter. The corrected census has zero confirmed native per-element SPI sites. A mechanical regression gate is still needed. |
| Native work capacity | Partial | `explore_web` and `walk_branches` no longer clamp requests to fixed node ceilings. They allocate from the exact requested frontier shape and reject only integer/result-coordinate or PostgreSQL `MaxAllocSize` violations. `walk_branches` continuity admission follows the complete belief-score tie at the beam boundary rather than a guessed beam multiplier. Other native constants remain under classification. |
| Converse corpus capacity | Partial | `generation.walk_batch` no longer takes an arbitrary 400-container subset and then cuts it again at 160; its canonical overload accepts caller capacity, while the compatibility adapter requests the complete set. `converse.compose` no longer cuts seed starts at 32. `containers_of` preserves zero, supports explicit unbounded reads, sizes frontiers from actual executor results, and uses hash deduplication instead of an O(n²) seen scan. Presentation-sized fact heads remain to be parameterized. |
| Managed plan reuse without heuristic caps | Implemented for typed reads and hot ingest cores | Npgsql auto-prepare is disabled instead of guessing `50` slots after `2` uses. Typed reads explicitly prepare; presence probes, attestation merge, consensus upsert, and mask deposit prepare their declared set statements and reuse commands within each connection. Remaining direct one-shot commands stay unprepared intentionally. |
| PostGIS-style generated extension package | Mostly done | Install/upgrade manifests are complete-gated, versions are content-derived, SQL chains are generated, and the native module is installed. Seed population is already separate. Upgrade/fresh-install parity and deployment receipts still need systematic proof. |
| Explicit governed operation registry | Not done | `ops.api()` is broad catalog discovery. It lacks explicit public/internal status, safety, cost, bounds, ordering, truncation, receipts, lifecycle, version, and parity metadata. |
| One dispatcher across MCP/OpenAI/HTTP | Not done | A parameter-bound read-only invoker exists, but #811/#812 remain open and product surfaces do not prove one canonical program or trace. |
| SQL/C# as thin orchestration wrappers | Partial | Important work is native and batched, but independent semantic SQL and endpoint-specific orchestration remain widespread. |
| Same recovered content means the same hash | Partial | Native hash/Merkle composition and codepoint-floor media values obey the law. Database identity keys and some source representations do not. |
| Codepoints as common building blocks | Implemented in core, incompletely enforced | Text, image digits, audio sample digits, and the dense 0-255 cache converge through codepoint content roots. Admission/schema gates do not yet prove this for every modality and source. |
| Recursive higher-order content composition | Partial | Tier trees and trajectories compose existing ids, but typed vocabulary, occurrences, source-specific schemas, and compatibility objects are not universally separated from content. |
| One logical entity row per content id | Not done | The physical key is `(id, tier)`, explicitly permitting the same id in multiple tier rows. |
| Recipe-specific realizations can coexist | Not done | There is no ratified recipe/realization key proving that multiple lawful structures for one content id coexist without arrival-order loss. |
| Canonical modality registry | Not done | Media, model, grammar, and gameplay meanings remain separate enumerations/namespaces. |
| Derived modality masks | Not done | Highway masks exist; a generated authoritative-evidence-to-modality-mask pipeline does not. |
| Cross-modal trajectories | Architectural law only | Generic trajectories can contain arbitrary ids, but there is no modality/interpretation model and acceptance fixture proving mixed trajectories without identity ambiguity. |
| Exact image reconstruction | Not done | Width and height reach `ImageIngestRecord` but are not deposited as binding content or scoped interpretation evidence. |
| Exact audio reconstruction | Not done | Sample rate reaches `AudioIngestRecord`; channel layout is not represented and neither survives the witness path. |
| Shared numeric floor for values such as 255 | Core implemented | The 0-255 perfcache maps decimal digit content to the same ids used by text; image numbers use the same root. Role/recipe claims can still collide or disappear in storage. |
| Unified perfcache bundle and lifecycle | Partial | T0, highway, modality-number, and chess caches have individual formats/loaders. One generated registry with checksums, generation, capacity, hit/miss/fallback, and cross-language parity is absent. |
| Deterministic staged greenfield consolidation | Not done | Ingest has bounded workers, journals, and bulk paths, but there is no global stage -> identity merge -> testimony merge -> fold-once -> derive-once program. |
| <=2-hour complete seed | Not done | The admitted phase budget exists only as an acceptance target. No combined bounded/full run has met it. |
| Native semantic-source fidelity | Partial, companion campaign | #1155 and the merged decomposer work preserve several scopes and references. #1153 and the decomposer ledger own the extensive remainder. |
| Stateful conversation through one forward program | Not done | #755, #921, and #924 remain open; merged substrate work does not prove dynamic per-constituent frontier mutation or MCP/OpenAI parity. |

## Delivered work mapped to this campaign

| Delivery | Contribution | Boundary |
| --- | --- | --- |
| #1136 | SQL audit, CI ratchet, cohesion report, issue graph | Measurement and governance, not broad remediation |
| #1141 | Removed a redundant full-tier entity roster scan from apply | One write-path defect, not the full read/scan audit |
| #1145/#1168 | Coalesced presence probes and removed duplicate root probes | Ingest-side database access only |
| #1147 | Fresh foundation index recovery | Seed lifecycle component, not a clean full seed proof |
| #1149/#1152 | Typed references and proposition-bound semantic roles | Partial identity normalization, not the global database key |
| #1158/#1159/#1160 | UD occurrence structure, bounded builders, durable file canonicals | Code/tests merged; actual UD database acceptance incomplete |
| #1164 | Canonical lexical peer batch and scalar adapter | One scalar/batch family |
| #1166/#1167 | Central batch sizing and truthful apply telemetry | One ordered set coordinator overlaps with compose-ahead; each apply internally fans across machine-derived partitions |
| #1170/#1172 | Bootstrap/amplification accounting | Initial metrics, not full LapSight gates |

## Known correctness and performance defects discovered during the campaign

### OpenSubtitles content identity

[#1180](https://github.com/SaltyPatron/Laplace/issues/1180) records the merged
identity defect. Sequence ids include an OpenSubtitles-specific schema, alignment
ids include language and source ordinals, and arbitrary 512-pair batching creates
durable content boundaries. Content, occurrence, and provenance must be split
before the 601-million-pair ingest.

### `structural.cluster`

[#1181](https://github.com/SaltyPatron/Laplace/issues/1181) recorded two runtime
correctness failures, a procedural batch loop, and a hidden
`GREATEST(p_limit * 20, 2000)` candidate multiplier. The canonical implementation
is now one relational core: distinct seeds share anchor lookup, coordinate-KNN
admission, curve realization, rendering, and recurrence aggregation; duplicate
inputs regain their ordinals at the output boundary. The scalar is a one-seed SQL
adapter. `p_limit` now honestly bounds both admitted candidates and maximum
survivors per seed. The non-empty regression proves duplicate, unresolved,
NULL/empty, scalar/batch, ordering, and recurrence behavior. Seeded warm/cold
plan and buffer receipts remain the final production acceptance gate.

### Explicit read budgets

Read adapters now preserve the caller's requested cardinality instead of
silently rewriting it. Chess paging has no hidden 200-row ceiling and zero means
zero; the installed-operation dispatcher has no hidden 2,000-row ceiling and
still fetches one extra row solely to report truncation; and
`generation.consensus_peer(p_id,p_k)` uses `p_k` for both relational election and
geometric admission instead of a fixed 48-candidate side pool. Defaults remain
surface choices, not lower-layer caps.

The same rule now covers mesh and taxonomy. `structural.mesh_position` no
longer owns fixed 40/60 relation/member pools, `taxonomy.tree` no longer owns a
fixed 24-child pool or ten-step climb, and the cycle-safe greedy
`consensus.walk_strongest` no longer truncates omitted depth at eight. SQL
omission means complete; HTTP/MCP publish their presentation defaults as
arguments and pass any non-negative caller value unchanged.

### Extension deployment path

The deploy bridge now writes to CMake's staged extension directory
`$prefix/share/postgresql/$major/extension` directly. The previous
`find $prefix -name control | head -1` could select the stale custom-toolchain
compatibility tree under `pgsql-18/share`; PostgreSQL never searched the bridge
written there, so every merge after the database remained on
`37244dffc7237ab1` failed with “no update path.” Library digest and symbol gates
now use the same derived major/version directory rather than hard-coding 18.

### UD acceptance

The code now has exact occurrence-grain representation and much safer sizing.
An isolated canary completed the ISO prerequisite in 15.1 seconds at about 408
MiB peak RSS, but the attempted UD EWT run was stopped before creating a UD journal
row. No claim that UD will no longer fail at scale is currently supported.

## Preserved unpublished work

- `fix/framenet-occurrence-spans` has local commit `a57f620b` for discontinuous
  targets, one commit beyond merged #1161. This belongs to the decomposer campaign.
- `agent/source-fidelity-audit` contains unpublished audit/index edits and an
  untracked seed-remediation sequence. Merged #1155 does not contain those edits.
- `fix/lexical-sense-integration` contains no unique work beyond an earlier
  WordNet commit superseded by merged #1157.
- The shared root worktree remains dirty on an older decomposer branch and must
  not be used as a clean base for this campaign.

## Correct remaining execution order

1. **Fix merged violations before adding data.** Correct #1180, deploy and measure
   #1181's set implementation, and audit other merged content preimages for
   source, label, language, ordinal, and batch salts.
2. **Ratify and enforce the global identity/materialization schema.** Complete
   #1052/#1008/#904/#1048 so content, realization, interpretation, occurrence,
   and testimony have compatible keys and admission gates.
3. **Land modality and media correctness.** Complete #1133/#1134, including the
   registry, generated mask algebra, exact image/audio round trips, and
   entry-order/cross-modal fixtures.
4. **Govern operations.** Complete #811/#812/#989/#1047: explicit registry,
   cardinality, cost, safety, bounds, ordering, receipts, lifecycle, and one
   dispatcher.
5. **Execute the measured SQL queue.** Fix the proven pruning/full-scan defects,
   then burn down scalar/batch and clone families using plan/buffer/result gates.
6. **Finish native source fidelity and normalized readers.** Coordinate #1153,
   #1177, and #1178 without restoring compatibility testimony.
7. **Complete observability and performance admission.** Finish #1175/#1080 and
   the SPI/index/apply audit; make every bounded run emit amplification, skew,
   bytes, throughput, and convergence receipts.
8. **Build the greenfield population program.** Stage by source, globally merge
   identities/testimony, fold once, derive masks/materializations once, then build
   serving indexes and analyze.
9. **Run bounded source gates.** Foundation, Wiktionary, WordNet/OMW, UD, media,
   model, chess, Tatoeba, and corrected OpenSubtitles must pass restart,
   idempotency, parity, amplification, and memory/IO budgets.
10. **Run the clean full seed and conversation experiment.** Meet the <=2-hour
    phase envelope, publish the receipt, then execute #755/#921/#924 against that
    certified substrate.

## Completion definition

The campaign is complete only when one content id cannot lose or duplicate a
tier, recipe, modality, realization, interpretation, or source claim; every
public operation is explicitly governed and shares its implementation across
surfaces; interactive plans satisfy result and work budgets; exact media and
cross-modal round trips pass; masks and perfcaches are rebuildable accelerators;
and a clean content-addressed seed plus serving indexes completes inside the
admitted hardware envelope with a reproducible acceptance receipt.
