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

### Universal tier / trajectory boundary

The parser/provider boundary must not become a collection of source-private semantic object models, and it must not force every source through a materialized AST.

The shared structural contract is canonical recursive composition plus typed physicality trajectories:

```text
artifact bytes / decoded samples
-> optional qualified parser / standards reader / codec when needed
-> exact source observations, spans, fields, errors, packaging facts
-> versioned boundary/composition/role recipe
-> canonical tiered compositions + physicality trajectories
-> generic Laplace lowering
```

UAX #29 is the text boundary case: deterministic lower-tier observations compose upward while singleton/span-identical scaffolding collapses to the same canonical identity. Other modalities use different admitted rules over the same mechanism.

Provider CST/AST nodes are evidence/projections, not a mandatory universal ontology. Grammar productions, precedence, associativity, delimiters, field roles and similar structural rules may themselves be admitted as ordinary knowledge and used to derive higher-tier composition from the ordered trajectory. When a standards streaming reader exposes the required XML/CSV/table facts directly, materializing an equivalent Tree-sitter tree is redundant work.

Tree-sitter therefore serves as a reusable grammar estate, compatibility parser, validator/realizer and grammar-knowledge source. It is appropriate when its grammar contributes structural information that the tier recipe needs; it is not a blanket container-format router.

Recipes explicitly disposition recovered facts into canonical content/physicality, occurrence, typed reference, provenance, attributed testimony, deterministic calculation, packaging/reconstruction or unresolved state. Unknown provider structure remains unresolved; it must not be silently dropped or coerced into content.

Once canonical compositions are admitted, the shared machine owns identity, physicality/trajectory, occurrences, references, provenance, testimony, calculations, reconstruction and deposition. AST/CST/IR can be derived or exported when an external tool needs that view.

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

Runtime source selection binds the entire selected artifact graph to authority,
release, syntax-provider configuration, semantic recipes and exact artifact identities.
`Decomposer<TRecipe>` executes that configuration through shared scheduling and
admission. A single XML field map is one artifact recipe; it cannot stand in for
Unicode or any other complete logical source. Source-specific bootstrap and codec
algorithms remain provider operations; source filenames, releases and semantic
field inventories must not select private decomposer implementations.

Native COPY tuple buffers are transport into the shared writer, not delivered
knowledge or cognition. Repeated identical canonical rows should coalesce within
the working set before crossing that boundary, while distinct interpretations,
occurrences and attributed testimony retain their semantics. Emitted row volume
does not establish unique durable content or a witnessed answer.

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

## Source-generation dependency law

A usable source is not just a directory and not just a parser. One selected source generation is the bound tuple:

```text
authority + release/version + exact artifact graph
+ provider/grammar/codec generation
+ semantic recipe/profile generation
```

The source-estate refresh and the recipe/admission work are therefore one dependency chain, not sequential projects where decomposers keep targeting an obsolete active tree until dataset cleanup is declared finished.

1. Stage and verify the selected release/artifact graph.
2. Recover its native schema with the qualified provider/parser.
3. Declare or update the semantic recipe against that exact staged release.
4. Qualify complete field/role disposition, reconstruction/loss, and physical-plan invariance.
5. Activate the release and recipe together as the selected source generation.
6. Run the common admission machine; source-specific code remains only the irreducible recovery kernel.

A staged newer release should be used to develop and validate its recipe before activation. Do not extend a bespoke decomposer against a superseded source merely because that directory is still the active path. Conversely, do not switch the active source path to a new release whose provider/recipe cannot yet account for its native fields.

Release/version participates in source/provenance/profile identity, not canonical content identity. The same literal sentence, definition, source-code fragment or other canonical content seen in two releases converges on the same content identity while the source occurrences and source claims remain release-attributable.

## Recipe lowering: one recovered object may contribute several state classes

The recipe does not choose exactly one bucket for a parser record. It declares how each recovered field/role contributes to the shared substrate. A single source object can produce canonical content **and** source-attributed testimony about that content.

| Recovered source value/role | Generic lowering | What it must not become |
| --- | --- | --- |
| sentence, definition text, example text, prose, literal source-code/media content | canonical content entity/composition with its normal physicality/trajectory; separately retain artifact/span occurrence | a high-trust semantic fact merely because the source contains the bytes |
| source says `frame X HAS_DEFINITION text Y` or `sense X HAS_EXAMPLE sentence Y` | ensure X/Y endpoints exist under their declared identity/realization laws, then emit attributed testimony `(X, relation, Y, source, context)` | a private decomposer-only edge or duplicated text identity |
| sense key, synset id, frame id, roleset id, external record key | typed reference/governed identity; attach declared realization/physicality only when that state class participates geometrically | ordinary text content just because the identifier is UTF-8 |
| row/file/span/annotation occurrence, token ordinal, gap, containment | occurrence/trajectory/provenance state over canonical identities | independent consensus witness count |
| release, license, file path, archive member, parser version | source/provenance/packaging coordinates | semantic content or truth unless explicitly declared by the recipe |
| deterministic parser/normalizer/geometry consequence | versioned calculation/structural state with its recipe/provider identity | empirical source testimony |
| field whose semantics are not mapped | explicit unresolved disposition | silent fallback to content, string label, or dropped field |

An endpoint being an entity with a physicality does not prevent it from being the subject/object of attestations. That is the intended pattern. For example, a FrameNet definition sentence is ordinary canonical text with a content physicality; FrameNet's assertion that a particular frame has that definition is a separate attributed attestation pointing at the same sentence entity.

This distinction is the generic replacement for source-local `ContentEmitter`/`AddAttestation` policy. Parsers recover native values and source roles; recipes declare their disposition; the common machine performs canonical composition, physicality/trajectory creation, reference admission, occurrence/provenance lowering, testimony creation and persistence.

## Current implementation gap and existing Refactor evidence

Current `Laplace` has a generic driver/scheduler/apply spine, but its `IngestSourceProfile` is a resource-sizing record, not this semantic source profile. `ISourceManifest` declares source identity/trust, relation/type rosters, license and sizing; source-named decomposers still hand-code many field dispositions and attestation decisions. That is the exact non-success case this law is intended to remove.

`Laplace-Refactor` contains implementation evidence closer to this contract: `laplace_source_profile_manifest` binds authority/release, artifact graph, recipe-program, witnessing, denominator, conformance and reconstruction fingerprints/counts; `tools/admit_source.py` compiles a selected source profile and drives shared native `source_admit_*` entry points. That repository is not semantic authority, but this machinery should be reused/ported where it satisfies the invention rather than inventing a third source-admission model.

## Concrete source-estate evidence — 2026-09-19

`vault-data-inventory.tsv` at commit `c19f5021` is a complete `find` inventory of `/vault/Data` including hidden paths. It confirms that the dependency law above is not hypothetical:

- **Tree-sitter/grammar estate:** `TreeSitter/` contains 303 top-level provider/source directories and about 30,208 files. This includes programming languages and general digital syntaxes such as CSV, XML, Markdown, SQL, disassembly and configuration formats. The exact qualified/locked provider set still has to be reconciled; directory presence alone is not authority.
- **OMW:** the active uppercase and lowercase trees are duplicated legacy estates (each 1,455 files, including 1,259 `.tab` files plus `.git` and checkpoint state). The staged OMW 2.0 generation contains 32 WN-LMF XML lexicons plus per-lexicon license/citation/readme material. Recipe/provider work belongs against the staged WN-LMF generation, not deeper legacy-tab special cases.
- **Open English WordNet:** OEWN 2025+ is already staged as `english-wordnet-2025-plus.xml.gz`. It should share the generic WN-LMF provider/recipe core with OMW rather than creating another WordNet-specific semantic engine.
- **CILI:** the active estate is a mutable Git checkout; the staged current snapshot contains immutable archives plus extracted ILI/WordNet mapping artifacts. Recipe qualification should target the staged snapshot before activation.
- **Universal Dependencies:** active v2.17 contains 686 `.conllu` files; staged v2.18 contains 712 `.conllu` files among 2,502 files. Semantic/tier-composition recipe work must target v2.18 now, while activation waits for the release+recipe boundary.
- **Tatoeba:** the active estate exposes a smaller subset; the staged 2026-08-29 generation contains 15 selected core/sidecar artifacts (compressed exports plus CSV sidecars). One recipe/profile must disposition those fields/sidecars instead of treating each as a new private ingest lane.
- **Wiktionary:** the active tree contains older raw/extracted and English-specific material; the selected 2026-08-28 raw Wiktextract gzip is staged. Schema/recipe work should bind that selected raw generation instead of filename-specific legacy selection.
- **Mapping estate:** FrameBase 2.0, VerbAtlas 1.1 and pinned SemLink/VerbNet/PropBank snapshots are already staged alongside the current active sources. Their coverage/supersession work and their recipes are one dependency graph.
- **Chess/publication feeds:** TWIC 1651–1660 and pinned Lichess openings are staged source generations. Their feed provenance and parser/recipe identity must remain bound rather than treated as ambient files.
- **Commonsense:** ATOMIC10x is staged separately from Atomic2020 and therefore remains a separate machine-generated witness/profile, not a transparent replacement.

`staged` means "not yet activated into the selected world generation." It does **not** mean "ignore the staged release while continuing to design semantics against the superseded active source."

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

Every selected source generation must declare, before activation, at least:

```text
artifact/release authority
provider/grammar/codec identity
artifact and source-object framing
canonical composition/occurrence grain
field / structural-role disposition
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
