# Seed and substrate remediation sequence — 2026-08-19

This is the broad program around the semantic-source audit. It prevents the
WordNet/FrameNet findings from being mistaken for the whole seed, ingest, SQL,
cache, read-path, and product task.

The sequence is dependency-ordered. It is not a claim that every item is
unimplemented, nor permission to replace working primitives. Each phase begins
by retaining what already satisfies the contract and measuring the remaining
gap.

## Current truth, kept as four separate states

- **Implemented routes:** the source tree contains 28 decomposers in
  `Laplace.Decomposers` and eight chess decomposers. The shared
  `SeedIngestComposition` registry covers the ordinary semantic, corpus, code,
  tabular, and media decomposers; model, recipe, and chess routes have dynamic
  or domain-specific construction at the CLI edge.
- **Locally available artifacts:** `/vault/Data` contains UCD, ISO 639, CILI,
  PWN 3.0, legacy/current-build OMW material, VerbNet, PropBank, FrameNet,
  SemLink, Predicate Matrix, MapNet, WordFrameNet, ConceptNet, ATOMIC 2020, UD,
  Wiktionary, Tatoeba, OpenSubtitles, Project Gutenberg, documents, code
  authorities, tree-sitter grammars, and chess corpora. `/vault/models`
  contains text/code, embedding/reranking, vision, speech/audio/music, and
  detection families plus code/export artifacts.
- **Declared canonical seed:** the Windows ladder declares floor, document,
  knowledge, usage, code, and three model witnesses. It does not include every
  implemented route: media, tabular/parquet, recipe, most chess passes, generic
  model admission, and some document corpora remain separate. The Windows
  ladder, `decomposer-gates.json`, GitHub seed workflows, and
  `witness-manifest.json` disagree about ordering, layer numbering, membership,
  and the meaning of "all".
- **Resident database on 2026-08-19:** `ops.source_status(NULL)` reports only
  Unicode, ISO639, CILI, WordNet, VerbNet, PropBank, FrameNet, MapNet,
  WordFrameNet, SemLink, and Predicate Matrix. OMW, ConceptNet, ATOMIC, UD,
  Wiktionary, Tatoeba, OpenSubtitles, documents, code, media, chess, and model
  witnesses are not resident in that database snapshot.

## Context boundary

The completed deep field audit covers CILI, PWN 3.0, OMW, FrameNet, VerbNet,
PropBank, SemLink, Predicate Matrix, MapNet, WordFrameNet, and Wiktionary. It
does **not** yet establish field-complete extraction for:

- Unicode/UCD and ISO language/script authorities;
- ConceptNet and ATOMIC 2020;
- Universal Dependencies;
- Tatoeba and OpenSubtitles;
- documents, Project Gutenberg, and document/package metadata;
- code, repository, Stack v2, Tiny Codes, tree-sitter, tabular, and parquet;
- image, audio, and video;
- chess games, openings, books, analysis, evaluation, trajectories, and
  tablebases;
- recipe admission; or
- the model families under `/vault/models` beyond a roster and admission-level
  review.

Those are not assumed correct. They receive the same source-schema, identity,
scope, provenance, field-disposition, and read-reachability audit below.

## One dependency-ordered implementation program

### 0. Preserve a reproducible baseline before changing identity again

- Record repository commit, extension version, source artifact hashes, source
  release/revision, effective environment, perfcache generations, schema
  generation, seed profile, and database snapshot identifier.
- Export the live source roster, ingest journals, exact/approximate counts,
  relation census, rejected/unresolved counters, wall time, WAL, and index size.
- Keep a small, fast, deterministic smoke database. Do not use another ten-hour
  bulk seed as the inner development loop.
- Freeze full production reseeding only for changes that would knowingly mint
  incorrect identities or unrepeatable evidence; keep isolated source probes
  and the minimal capability seed running.

### 1. Establish one machine-readable authority for sources and seed profiles

- Replace the conflicting source lists in the CLI registry, ETL manifest,
  Windows scripts, gate JSON, workflows, and witness manifest with generated
  adapters over one source/capability manifest.
- Give each source a stable key, decomposer factory, authority, release,
  artifact selection rule, license, citation, languages, modalities, input
  schema, layer/dependencies, emitted tiers/relations, trust policy, expected
  cardinality, and acceptance operation.
- Distinguish source authority, release, artifact, distribution/package,
  serialization, derivation, ingest run, and decomposer version. A directory
  name or decomposer class name is not a release identity.
- Define named profiles rather than one overloaded "all": minimal floor,
  lexical compatibility, linguistic, conversational, code, media, chess,
  model-consensus, evaluation, and full-production.
- Generate CLI help, default paths, workflows, seed scripts, dependency order,
  gates, and the MCP/HTTP capability roster from the same manifest.

### 2. Finish the greenfield identity and storage boundary

- Keep the governing law: the same recovered content has the same content hash,
  independent of source, modality, packaging, tier use, or external identifier.
- Admit opaque external identifiers as typed references carrying authority and
  release. Do not use a normalized PWN key, ILI string, FrameNet ID, model
  ordinal, path, or byte offset as content identity.
- Separate one immutable content entity from recipe-specific structural
  realizations, scoped occurrences/interpretations, and multi-valued typed
  testimony.
- Make tier meaningful only with the declared structural recipe/interpretation;
  a bare global tier ordinal cannot distinguish text tier 2 from image tier 2.
- Make every persistence, novelty, claimed, deduplication, and cache key match
  the complete storage key. Eliminate id-only shortcuts that suppress distinct
  structural or scoped claims.
- Make ordered sequence, unordered collection, record, scalar attribute, and
  independent multi-valued testimony explicit shapes. A set is a composition,
  not an edge fan; a record is not a set.
- Keep composition acyclic. Self-reference is later typed testimony about an
  identified node, never part of its Merkle preimage.

### 3. Complete the shared admission/decomposition core

- Retain the new shared scheduling, inventory, working-set, and decomposer base
  classes. They solve execution duplication; they do not prove semantic
  fidelity.
- Add shared exact-reference, content, scoped-object, structured-relation,
  provenance, occurrence/span, collection, and reconstruction admission APIs.
- Make single-record and batch admission call the same semantic program and
  prove identical output, order, duplicate handling, null/unknown handling, and
  receipts.
- Make adapters thin: WNDB, WN-LMF, XML, JSONL, TSV, CoNLL-U, tree-sitter,
  safetensors, codecs, and domain formats recover source records; common cores
  decide substrate shape.
- Emit a field-disposition receipt for every source and phase: records/fields
  seen, emitted, intentionally ignored with reason, malformed, filtered,
  unresolved, collision count, duplicate lineage, and bridge coverage.
- Treat a missing expected artifact, zero accepted rows, silent unsupported
  field, incomplete tracked-file count, or unexplained input denominator gap as
  failure—not a clean no-op.

### 4. Correct the universal floor and registries

- Audit all consumed UCD files/properties, UAX versioning, script/block/age/
  normalization/case/confusable evidence, aliases, omissions, and release
  provenance.
- Audit ISO 639 plus script/language registries and BCP 47/IANA relationships;
  preserve language, macrolanguage, script, region, variant, alias, retirement,
  and provenance as distinct facts.
- Preserve Unicode codepoints as the only tier-0 alphabet. Numeric values use
  invariant decimal codepoint compositions; private RGBA, PCM, token, square,
  or tensor alphabets do not become alternate T0 spaces.
- Define append-only canonical registries for relation/highway bits, modality
  family bits, representation/physicality kinds, recipe identities, reference
  authorities, trust classes, and operation identities. Generate native,
  managed, SQL, and documentation mirrors with parity gates.

### 5. Repair primary lexical authorities before bridge expansion

- Keep PWN 3.0 as a compatibility coordinate system; do not replace it or let
  it remain the only English WordNet.
- Finish PWN exact sense identity, lexical source/target pointer scope, sense
  number/rank, adjective position, POS-scoped morphology, sense-scoped verb
  frames/examples, and release/license provenance.
- Build the streaming WN-LMF 1.4 core and admit current Open English WordNet as
  a separately versioned English authority linked through exact references and
  ILI mappings.
- Prefer OMW 2 WN-LMF packages over legacy tabs. Preserve each constituent
  WordNet's ID, version, language, license, citation, dependency, confidence,
  entry/form/sense identity, and lemma subtype.
- Preserve CILI Concept versus Instance, `dc:source`, mapping release, and
  serialization equivalence. The ILI is a cross-lexicon reference/registry
  coordinate, not another vote for a gloss or synonym.
- Record PWN-to-OEWN and other cross-version mappings explicitly; never silently
  rewrite old offsets/sense keys to whichever current synset looks closest.

### 6. Repair predicate/frame authorities and their occurrence evidence

- FrameNet: frame-scoped FE identity, LU identity, semantic types, FE relations,
  core sets, structured valence, LU/full-text FE spans, target occurrences,
  documents/corpora, status/annotator/date, and release provenance.
- VerbNet: correct `fn_mapping`; preserve member keys, PB grouping, ordered
  syntax, selectional/syntactic restrictions, semantic predicate groups,
  argument type/order, event variables, polarity/value, and version.
- PropBank: roleset-scoped arguments, aliases/POS, lexical links with method/
  version/confidence, usage flags, notes, predicate/argument spans, examples,
  and corpus provenance.
- Audit whether NomBank, current OEWN-aligned SemCor, AMR, or another predicate
  authority adds genuinely complementary evidence and is legally distributable
  before admitting it.

### 7. Rebuild cross-resource bridges only after endpoint identities are exact

- SemLink: retain PB roleset/argument, VN class/role, FN frame/FE, mapping row,
  method/version, and occurrence context on every edge.
- Predicate Matrix: ingest all intended languages/POS and classify all 27
  columns, including PB argument and the VN/FN/PB three-way role alignment;
  preserve domain/ontology/frequency/confidence columns instead of filtering
  84.3% before admission.
- MapNet/WFN/XWFN: keep as calibrated historical witnesses with original
  FN/WN versions, confidence, and unresolved targets. Do not silently discard
  expansion targets or give automatic mappings primary-authority weight.
- Build an explicit derivation graph so SemLink, Predicate Matrix, PreMOn,
  FrameBase, OMW repackaging, or generated serializations cannot double-vote
  the same upstream assertion.
- Evaluate Russian FrameBank, Global FrameNet resources, PreMOn, FrameBase, and
  VerbAtlas as complementary/versioned sources—not automatic replacements or
  independent corroboration of their own upstream data.

### 8. Deep-audit the lexical, commonsense, syntax, and usage sources not yet covered

- Wiktionary: finish per-sense identity, target-language translation scope,
  typed Wikidata/sense references, forms and morphology collections, examples
  with offsets/citations, pronunciation systems/audio/dialect, etymology,
  redirects/pages, and artifact provenance.
- ConceptNet: account for every relation and accepted/rejected URI form;
  preserve language, POS/sense qualifiers, source dataset, license, weight,
  surface text, context, and derivation. Separate ConceptNet-redistributed data
  from independent ConceptNet testimony.
- ATOMIC 2020: preserve event template, PersonX/Y/Z role bindings, head/tail
  text, split, relation, polarity/validity, annotator aggregation, and source
  row identity. Prove the conversational read path can reach it.
- UD: preserve treebank/release/document/sentence/token occurrence, multiword
  token and empty-node identity, lemma, UPOS/XPOS, FEATS record, enhanced/basic
  dependencies, MISC, offsets, spacing, and comments. Do not collapse corpus
  occurrences into surface facts.
- Tatoeba: preserve sentence/source identity, language, author/license/status,
  tags, audio links, translation-link provenance, and deletion/update behavior.
- OpenSubtitles: preserve release, language pair, document/movie/subtitle and
  aligned utterance occurrence, timestamps/order, alignment confidence, and
  license/provenance rather than only sentence translation edges.

### 9. Make documents and code first-class witnessed structures

- Distinguish work, edition, package/file, document, section, paragraph,
  sentence, occurrence/span, author, title, language, license, and translation.
  Apply this to test documents and Project Gutenberg rather than treating a
  filesystem walk as the semantic model.
- Prove bit-perfect raw-content round trip separately from semantic extraction;
  both are required and neither substitutes for the other.
- Code: retain canonical source content plus repository/commit/path/file/
  language identity, tree-sitter grammar/revision, AST occurrence, symbol,
  definition, reference, call, type, import, build/test diagnostic, and license.
- Converge `code`, `repo`, Stack v2, Tiny Codes, authority repositories,
  tabular/parquet, and recipe inputs through shared content/occurrence APIs while
  preserving their different source trust and row/document boundaries.
- Complete self-ingest call-graph and executed code-feedback acceptance; text
  coincidence is not a code relation and static completion is not the finished
  code lane.

### 10. Finish modality-general structure and reconstruction

- Add authoritative modality-family and recipe/codec/grammar registries. A
  derived modality mask may accelerate routing but never enters content identity
  or replaces exact occurrence/recipe evidence.
- Image: number→channel→pixel→patch→region→image, with exact dimensions,
  channel order, color/alpha contract, scan/patch recipe, and lossless
  reconstruction evidence.
- Audio: signed number→sample→window/segment→phrase→track, with sample rate,
  channel layout/order, sample representation, timing, segmentation recipe, and
  lossless reconstruction evidence.
- Video: image-frame structure plus temporal order, frame rate/timestamps,
  audio-track binding, demux provenance, and scene/derived analysis as evictable
  testimony.
- Chess: keep position/move/game content distinct from player, result, opening,
  engine evaluation, analysis, books, trajectories, and tablebase testimony;
  give each pass a declared dependency and acceptance receipt.
- Add same-content/cross-container/cross-modal fixtures in both ingest orders.
  Equal recovered content must converge while differing shape/rate/recipe or
  scoped roles remain recoverable and non-ambiguous.

### 11. Complete model admission before claiming model decomposition

- Resolve an exact snapshot/revision and hash config, tokenizer, tensor index,
  every tensor payload, dtype/shape, architecture, quantization, processor, and
  license. Reject unpinned directory-name identity.
- Cover text, code, embedding, reranker, vision, detection, speech/audio/music,
  MoE/MLA, diffusion, and unsupported architectures explicitly; partial coverage
  must name omitted tensors/operators instead of looking successful.
- Keep tokenizer-local IDs, layer/head/expert ordinals, and dimensions
  source-local. Align models by shared content and witnessed functional
  behavior, never ordinal equality.
- Preserve exact source testimony and separate derived factors, projections,
  spectra, embeddings, evaluations, and synthesized recipes as versioned,
  evictable calculations.
- Prove deterministic import/export receipts and A-only, B-only, A+B source
  ablation before calling heterogeneous model evidence consensus.

### 12. Consolidate SQL/C#/native operations around the typed ISA

- Generate one operation registry carrying public/internal status, scalar/batch
  shape, input/output schema, cardinality, volatility, cost, bounds, truncation,
  safety/write policy, native/reference pairing, version, and receipt contract.
- Keep one relational/native semantic core per fact. Scalar, array, table, CLI,
  MCP, HTTP, and OpenAI-compatible forms are thin adapters with enforced parity
  and preserved input ordinal where needed.
- Remove RBAR batches and repeated bodies; retain deliberate reference/native
  pairs only behind executable parity tests.
- Push bare partition/index predicates and candidate reduction before rendering,
  scoring, fan-out, function calls, sorts, and limits. Resolve type IDs/bands on
  the small side rather than wrapping partition keys.
- Classify every limit as semantic top-k, work budget, transport cap, or explicit
  sample. Report candidates examined, underfill, truncation, and abstention.
- Add measured `ROWS`/`COST` or planner support for SRFs; inventory expression
  indexes and every unavoidable full scan. Keep exact census/maintenance scans
  out of serving defaults.
- Move heavy reusable kernels to C/C++ behind stable C ABI and set-sized SPI;
  use SIMD/AVX/VNNI/Eigen/Spectra/oneMKL where measurement supports it. Do not
  hide per-row SPI loops inside native code.

### 13. Unify perfcaches as disposable accelerators

- Treat T0 as the universal identity-floor dependency, not a modality-specific
  cache name to copy.
- Give every blob one manifest/bundle entry: format/version, generator,
  dependency generations, checksum, size, loader, PostgreSQL/App visibility,
  prewarm policy, rebuild command, parity fixture, and fallback behavior.
- Finish wiring the existing highway, chess position/transition, and modality
  number blobs through that one lifecycle; extend the number cache deliberately
  for signed sample ranges when the recipe requires it.
- Add modality/recipe masks and hot lexical/reference/compose maps only as
  derived summaries with exact fallback and rebuild/eviction rules.
- A cache hit and canonical calculation must return identical identities,
  geometry, ordering, and evidence. Caches never become authority or survive
  source eviction unexplained.

### 14. Make a clean reseed a bulk-load program, not repeated serving work

- Separate install/bootstrap, artifact registration, base deposition, inline
  identity consolidation, derived projections, offline global maintenance,
  serve-index construction, analyze, and behavioral proof.
- Use one long-lived ingest host for compatible sequential phases so native
  initialization and immutable perfcache mapping are paid once. Schedule only
  genuinely independent work concurrently and retain the one-writer/merge law.
- Keep source/file checkpoints, resumable periods, exact terminal receipts,
  cancellation recovery, idempotency, and source eviction/rederive symmetry.
- Define a minimal load index set for identity and merge, then build the measured
  serve index set after bulk deposition. Do not rebuild roughly 123 GB of weakly
  observed indexes during every greenfield load without a serving-plan owner.
- Eliminate repeated full-tree folds, masks, global counts, physicality scans,
  and `ANALYZE` between sources when one set-sized phase can do the work once.
- Budget wall time, rows/bytes per second, WAL, memory, temp I/O, partitions,
  index-build time, cache-build time, and recovery. Enforce the two-hour full
  reseed target by phase, not only as one final stopwatch.

### 15. Prove seeded evidence is reachable through one read and product surface

- For every emitted relation/source family, provide a bounded inspect operation
  that can find a known positive and explain source, context, confidence,
  lineage, and structural occurrence.
- Expose source manifest, artifact/release, coverage receipt, rejects,
  unresolved references, source roster/status, idempotency, eviction, plans,
  and acceptance probes through the same typed operation catalog.
- Route CLI, MCP, HTTP, OpenAI-compatible diagnostics, and application code
  through those operations. Do not build endpoint-specific SQL or private C#
  query paths.
- Add reverse projection/highway exits: concept/sense/frame/role evidence must
  return to correct language, surface, occurrence, and realization. Resident
  but unreachable data does not count as a capability.
- Make capability/profile absence explicit. A foundation-only database must not
  advertise conversation, code, media, or model-consensus behavior.

### 16. Force the conversational vertical slice before more peripheral expansion

- Execute one canonical program for MCP and OpenAI-compatible endpoints:
  `RESOLVE → ORIENT → ROUTE → SCAN → COMPOSE → PROPOSE → STEER → SELECT →
  REALIZE → WITNESS`.
- Condition every next emitted constituent on the updated frontier and witnessed
  conversation trajectory; streaming and non-streaming paths share trace
  semantics.
- Run multi-turn correction, anaphora, topic return, source ablation, lexical →
  commonsense → usage traversal, corpus occurrence, conflicting testimony, and
  unknown/abstention probes.
- Follow failures to the exact identity, missing edge, unreachable evidence,
  candidate reduction, selection, realization, or witness invariant. Do not add
  a new ontology/scoring term merely because one response is poor.

### 17. Promote only after adversarial acceptance

- Clean-seed each named capability profile from pinned artifacts and prove its
  declared row/field/identity/coverage gates.
- Compare single versus batch, native versus reference, cached versus fallback,
  CLI versus MCP versus HTTP, streaming versus non-streaming, and source-present
  versus source-ablated behavior.
- Include negative controls for identity collisions, scoped-object collapse,
  derived-source double voting, path/package-dependent hashes, unordered top-k,
  partition-pruning loss, silent no-op, source erasure, template fallback, and
  unsupported-parameter theater.
- Capture plans and workload-attributed scans after the serve indexes exist;
  keep intentional deep audits and maintenance passes explicitly offline.
- Publish seed, source, operation, conversation, and export receipts so the
  result is reproducible without trusting agent prose.

## Target clean-seed execution order after the fixes

This is the intended runtime order, distinct from the implementation program
above:

1. Install the extension/schema and load append-only canonical registries.
2. Build/load verified T0, highway, number, and other prerequisite perfcaches.
3. Register every selected source artifact/release/license/hash and seed profile.
4. Seed Unicode/UCD, then language/script registries.
5. Seed the profile's raw documents early, preserving work/edition/package/
   occurrence structure, so distributional trajectories exist independently of
   curated knowledge.
6. Seed CILI/reference mappings, PWN 3.0 compatibility, current OEWN, then OMW 2
   WordNet packages through the shared WN-LMF core.
7. Seed primary predicate/frame authorities: VerbNet, PropBank, FrameNet, plus
   any admitted complementary primary sources.
8. Seed cross-resource bridges only after their endpoint references are
   registered: SemLink, Predicate Matrix, calibrated MapNet/WFN/XWFN, and any
   admitted PreMOn/FrameBase/VerbAtlas bridges.
9. Seed Wiktionary, ConceptNet, ATOMIC, and UD with exact sense/occurrence/source
   scope.
10. Seed Tatoeba and OpenSubtitles as usage/translation/dialogue occurrences.
11. Seed code corpora and repositories through the full grammar/AST/call-graph
    lane.
12. Seed selected media and domain profiles: image/audio/video and chess
    recorders first, calculated analyzers afterward.
13. Seed exact model snapshots last, then run declared derived circuit/factor
    analyses and deterministic export probes.
14. Run global derived maintenance once, build the measured serve-index set,
    `ANALYZE`, build derived masks/caches, and record the final snapshot receipt.
15. Run source-fidelity, read-reachability, MCP/HTTP parity, conversation,
    code-feedback, model-ablation, round-trip, performance, and recovery gates.

## Existing evidence this sequence composes

- `semantic-source-fidelity-audit-2026-08-19.md` supplies the measured deep
  lexical/semantic extraction findings.
- `sql-cohesion-audit-2026-08-18.md` supplies the measured SQL duplication,
  pruning, scan, planner, index-estate, native-core, modality, cache, and reseed
  findings.
- `specs/38_Collections_Are_Compositions.md` supplies the set/record/multi-value
  storage distinction.
- `invention/modality-ladder-law.md` supplies the universal codepoint floor and
  modality reconstruction law.
- `plan/REAL_CONVERSATION_AND_MODEL_CONSENSUS_FINISH_LINE.md` supplies the
  vertical product and behavioral acceptance contract.

