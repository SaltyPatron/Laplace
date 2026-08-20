# Semantic source fidelity audit — 2026-08-19

Status: measured against merged `main` at `ea45d509` (PR #1151) and the
semantic resources present under `/vault/Data` on 2026-08-19. This is an input
and decomposition audit, not a claim about rows currently resident in a seeded
database.

## Conclusion

The source roster is broader and healthier than the current substrate makes it
look. Most of the primary curated sources are the right public releases:
FrameNet 1.7, VerbNet 3.4, PropBank 3.4, SemLink 2.0, current CILI data, and a
recent OMW build checkout. The dominant problem is **semantic loss between the
source format and emitted substrate evidence**, compounded by version-oblivious
source identity.

The genuinely weak or obsolete inputs are:

- legacy OMW `.tab` files when the same package is available as WN-LMF 1.4;
- MapNet 0.1 (FrameNet 1.3, WordNet 1.6, automatically generated, measured
  precision 0.794);
- the old WFN/XWFN mappings, especially when admitted only through exact
  FrameNet 1.7 LU keys;
- Princeton WordNet 3.0 when treated as the *only* English WordNet rather than
  a required compatibility coordinate system.

Princeton WordNet 3.0 must remain available because OMW, CILI, ConceptNet,
Predicate Matrix, MapNet, WordFrameNet, and other bridges refer to its offsets
or sense keys. It should not be silently replaced. Add current Open English
WordNet as another versioned witness and converge both through exact source
identities plus ILI mappings.

WN-LMF is not “another WordNet.” It is the canonical Global WordNet interchange
model. A generic WN-LMF 1.4 decomposer should become the shared core for Open
English WordNet, OMW 2 releases, and individual WordNets that publish the
format. Native PWN WNDB remains a compatibility adapter into the same internal
contract.

Upstream references used for this audit:

- [Global WordNet formats and WN-LMF 1.4](https://globalwordnet.github.io/schemas/)
  and its [DTD](https://globalwordnet.github.io/schemas/WN-LMF-1.4.dtd)
- [Open English WordNet](https://github.com/globalwordnet/english-wordnet) and
  [2025 release](https://github.com/globalwordnet/english-wordnet/releases/tag/2025-edition)
- [OMW data](https://github.com/omwn/omw-data) and
  [OMW 2.0 release](https://github.com/omwn/omw-data/releases/tag/v2.0)
- [CILI](https://github.com/globalwordnet/cili)
- [VerbNet](https://github.com/cu-clear/verbnet)
- [PropBank frames](https://github.com/propbank/propbank-frames)
- [Berkeley FrameNet project](https://icsi.berkeley.edu/projects/framenet-project/)
- [MapNet](https://nlplab.fbk.eu/tools-and-resources/lexical-resources-and-corpora/mapnet)
- [Russian FrameBank](https://github.com/olesar/framebank),
  [PreMOn](https://premon.fbk.eu/), and
  [FrameBase](https://www.framebase.org/data)

Reproduce the vault measurements with `just audit-source-fidelity`. Set
`LAPLACE_DATA_ROOT` when the semantic corpus is mounted somewhere other than
`/vault/Data`.

## Identity law for this work

This audit does not propose namespaced content blobs.

- The same content has the same content hash. `dog`, a gloss, an example, and a
  sentence remain decomposed content regardless of witness.
- External identifiers are typed references, not content identity. A full PWN
  sense key, CILI ILI, FrameNet FE ID, FrameNet LU ID, VerbNet class ID, and
  PropBank roleset/argument ID belong behind `ReferenceAnchor`-style admission.
- Scoped external objects stay scoped. `Buyer` in `Commerce_buy` and `Buyer` in
  another frame may share the content `Buyer`; they are not thereby the same
  FrameNet FE.
- Source, release, language, POS, confidence, and mapping method are evidence
  dimensions. They do not perturb the content hash.
- A repackaging is not an independent witness. OMW `.tab` and WN-LMF generated
  from the same source rows must not receive two consensus votes.

## Vault inventory

| Resource | Vault artifact | Assessment |
|---|---|---|
| CILI | `globalwordnet/cili` at `dfc99e15`, PWN 1.5–3.1 and ODWN mappings | Correct authority; parser loses concept/instance typing and source metadata |
| Princeton WordNet | `WordNet-3.0/dict` | Required compatibility anchor; not current English coverage |
| OMW | `omwn/omw-data` at `406bf83b` (2026-03-28) | Current build repository, but Laplace reads only legacy `wns/*.tab`, not the WN-LMF 2.0 release |
| VerbNet | 3.4 XML, fetched 2026-06-05 | Current public release; decomposer is shallow |
| PropBank | 3.4 frame repository snapshot, fetched 2026-06-05 | Current frame lexicon; decomposer is shallow |
| FrameNet | 1.7, 1,221 frames, 13,572 LUs, 107 full-text files | Correct public release; most annotation structure is discarded |
| SemLink | 2.0 repository snapshot | Useful mappings; only three JSON maps are present, plus an old 2006 VN/FN role map |
| Predicate Matrix | 1.3, 426,696 rows, 27 columns | Old but uniquely rich; current parser discards most rows and columns |
| MapNet | 0.1, 2009 | Legacy bridge: FrameNet 1.3 → WordNet 1.6; retain only as versioned, calibrated evidence |
| WFN/XWFN | archive files with no release metadata in the unpack | Legacy bridge/expansion; version and confidence must be explicit |
| Wiktionary | 21.9 GB multilingual wiktextract JSONL, 2.9 GB English Kaikki JSONL, and an 11.5 GB raw XML dump | Correct supported inputs exist; merged-main parser is lossy and lacks per-sense identity |
| Russian FrameBank | absent | No decomposer or vault artifact |
| NomBank | absent | No decomposer or vault artifact |
| Open English WordNet / Namenet | absent | Add current WN-LMF release; do not replace PWN 3.0 anchors |
| PreMOn / FrameBase | absent | Potential normalized supplemental witnesses, not substitutes for current native releases |

### Model witness vault

`/vault/models` has 38 top-level directories. It is a separate witness/artifact
vault, not a hidden lexical-authority tree:

- generative/code families: Qwen 2.5 Coder, Qwen 3 Coder, Qwen 3.8, DeepSeek
  Coder, TinyLlama, and Phi-2;
- text/code embedding and reranking: Qwen 3, Jina, and MiniLM families;
- visual embedding/reranking and detection: Qwen 3 VL, DETR, Conditional DETR,
  RT-DETR, Florence 2, Grounding DINO, and YOLO;
- speech/audio/music: SAM Audio, Fish Speech, Granite Speech, Canary-Qwen, and
  Music Flamingo; and
- corpora/export space: `code-corpus`, `stack-v2`, `tiny-codes`, and `gguf`.

It contains no additional WordNet, FrameNet, PropBank, VerbNet, SemLink, or
Predicate Matrix authority that closes the gaps above. Twenty-five top-level
model directories use a Hugging Face `snapshots` layout, but only 16 expose a
`refs` directory; other downloads use local-directory/cache layouts. Admission
must therefore resolve and record an exact snapshot/revision and artifact hash,
not infer identity or chronology from the directory name.

Older models such as Phi-2, TinyLlama, or older coder families are not
automatically bad when admitted as exact, heterogeneous testimony. They are bad
only if an unpinned directory name is treated as witness identity, or if their
age/size/family is ignored when weighting evidence. None should be mistaken for
a native semantic authority or used to repair decomposer loss by model vote.

## Measured extraction losses

### WordNet and CILI

The WordNet decomposer reads all four `data.*` files, `index.sense`, exception
files, `frames.vrb`, `sentidx.vrb`, and `sents.vrb`. It emits synsets, lemmas,
definitions, examples, POS, lexicographer categories, pointer relations,
morphology, senses, tag counts, and verb-frame text. That is broader than a
surface-only importer, but several identity losses invalidate the apparent
coverage:

1. `NormalizeSenseKey()` keeps only `lemma%ss_type:lex_filenum:lex_id` and drops
   satellite head word/head ID. On the local PWN 3.0 `index.sense`, 206,941 full
   sense keys become 202,359 normalized keys. There are 2,949 collision buckets
   involving 7,531 source rows: 4,582 distinct source identities disappear.
2. PWN contains 92,244 lexical pointers with non-zero source and target word
   numbers. The parser reads both numbers, but emission uses the source surface
   and target synset. All 92,244 lose exact target-word/target-sense identity.
3. `index.sense` supplies a sense number for all 206,941 rows. It is skipped;
   only tag count is retained.
4. At least 1,055 adjective word tokens carry `(a)`, `(p)`, or `(ip)` position
   markers. They are not modeled as adjective position and can contaminate the
   surface form.
5. Sense-indexed verb sentence witnesses and word-indexed verb frames are
   attached to a synset or surface, losing exact sense scope.
6. Exception morphology loses the POS file that asserted the exception.
7. Lexicon/release/license/citation metadata is not represented, and the source
   ID is the decomposer name rather than the authority release/artifact.

CILI contributes 117,659 definitions and PWN/version crosswalks. Its `ili.ttl`
distinguishes 109,929 Concepts and 7,730 Instances; the decomposer types all of
them as `WordNetSynset`. It also drops each ILI's `dc:source` and the resource
metadata. PWN `.tab` and RDF maps are admitted under inconsistent labels such as
`pwn30` and `wn30`, even though they are two serializations of the same map.

### OMW

The vault has 1,253 legacy tab files. The decomposer recognizes only `lemma`,
`def`, and `exe` rows and emits a language-scoped surface→ILI-synset edge,
definition, or example.

Losses:

- no direct WN-LMF/XML/JSON-LD/RDF importer exists;
- `lemma:root`, `lemma:brokenplural`, and other lemma subtypes are reduced to
  plain lemma;
- the new fourth-column alternative forms supported by the OMW 2.0 build are
  ignored;
- individual WordNet ID, label, version, license, URL, citation, dependency,
  and confidence are collapsed into one `OMWDecomposer` witness;
- no lexical-entry or per-WordNet sense identity is retained; and
- derived CLDR/Wiktionary rows and curated WordNets are assigned the same trust
  and provenance surface.

Use the OMW 2.0 WN-LMF 1.4 release as the preferred package input. Keep legacy
tabs only as a compatibility/fallback adapter. Where an individual WordNet has
a newer native WN-LMF release, prefer it and record the OMW package as a
distribution, not another vote.

### FrameNet 1.7

FrameNet has the right data in the vault. The decomposition is the shallow part.

- 11,428 frame-scoped FE declarations collapse to 1,285 name-only category
  anchors. FrameNet FE identity is frame-scoped; 10,143 scoped identities are
  erased by the current key.
- 381 FE core sets, 5,971 semantic-type references, the 109 semantic-type
  inventory, and 12,393 global FE-to-FE relation records are not ingested.
- LU parsing retains a flattened `GF/PT/FE` valence string, but discards the
  structure and most of 6,942,789 LU annotation labels.
- Full-text parsing retains sentence, target substring, and frame. It discards
  29,638 FE layers and the rest of 500,524 labels, including FE spans and other
  annotation layers.
- Target identity is substring content in sentence context rather than an
  occurrence/span reference, so repeated identical targets in a sentence are
  ambiguous.
- Release metadata, annotation status, corpus/document IDs, annotator/date,
  LU status/counts, lexeme structure, incorporated FE, and confidence are lost.

### VerbNet 3.4

The current source is correct, but the decomposer primarily emits class
hierarchy, member surfaces, WordNet links, role names, a flat primary syntax
description, examples, and semantic predicate names.

- 6,730 of 6,740 members have `fn_mapping`; the code reads the nonexistent
  attribute `fnframe`. Direct VerbNet→FrameNet member mappings therefore emit
  zero evidence.
- 1,046 selectional restrictions, 559 syntactic restrictions, and the ordered
  syntax structures in 1,603 frames are discarded.
- The source has 6,745 semantic predicates and 18,694 arguments, including
  7,664 event arguments. Emission keeps predicate names and only thematic-role
  arguments; event variables, argument order/type, polarity/boolean value, and
  predicate grouping are lost.
- Member `verbnet_key`, PropBank `grouping`, features, and exact mapping context
  are dropped.

### PropBank 3.4

The decomposer keeps predicate lemma, roleset ID/name, role descriptions,
ordinal/function tag, some role links, and whole example sentences. The source
contains substantially more:

- 17,431 aliases and their POS are dropped;
- 16,250 roleset-level lexical links, their versions, source method, and
  confidence are dropped;
- 67,246 per-release usage/in-use assertions are dropped;
- 56,914 annotated example arguments and their spans, plus predicate spans,
  are dropped; and
- 15,385 notes and example provenance fields are dropped.

The 28,622 role declarations are represented by role-description content
(17,702 unique strings), not a roleset-scoped argument reference. Use
`roleset/ARGn` as typed identity, attach the description as content, and retain
the existing ordinal/function evidence.

### SemLink and Predicate Matrix

The SemLink vault currently provides `pb-vn2.json`, `vn-fn2.json`,
`external_vn2pb.json`, and `VN-FNRoleMapping.txt`. Optional PB/VN/FN→WordNet JSON
files named by the decomposer are absent.

The three JSON maps are mostly consumed for the topology they actually contain.
The role mappings are not:

- PB ARG→VN role edges do not retain the PropBank roleset in edge context;
- the VN/FN role file supplies both VN class and FrameNet frame, but emission
  ignores `fnframe`, making same-named FEs across frames ambiguous; and
- version/source metadata is absent.

Predicate Matrix 1.3 has 426,696 data rows and 27 columns. The current importer:

- accepts only English verbs: 66,881 rows (15.7%), rejecting 359,815 rows
  (84.3%) before synset admission;
- reads 11 of 27 columns;
- drops predicate ID, row-role ID, FrameNet lexical entry, PropBank argument,
  base-concept flag, domain, SUMO, top ontology, lexname, BLC, sense frequency,
  relation count, ESO class, and ESO role; and
- maps VN role→FN FE but omits the same row's PB argument, so it fails to emit
  the central three-way role alignment.

### MapNet and WordFrameNet

These are useful historical bridge witnesses only if versions and confidence
survive.

- MapNet contains 609 unique FrameNet 1.3 frame keys; 577 resolve by name in
  FrameNet 1.7. Its 4,381 unique LU keys resolve to 4,002 current LU keys
  (91.3%). The rest silently disappear before the WordNet 1.6 crosswalk.
- WFN has 9,328 computed LU keys; 8,256 resolve in FrameNet 1.7 (88.5%).
- XWFN has 19,597 computed expanded LU keys; only 8,392 resolve (42.8%). Requiring
  an already-declared FrameNet LU defeats much of an *extended* WFN's purpose.
- MapNet's published automatic precision (0.794) is discarded and its evidence
  receives the same `AcademicCurated` trust as manual primary data.

### Wiktionary

Both supported decomposer inputs exist:
`raw-wiktextract-data.jsonl` (21.9 GB) and
`kaikki.org-dictionary-English.jsonl` (2.9 GB). The raw MediaWiki XML under
`Wiktionary/en` is an additional upstream artifact, not the only input.

Merged-main extraction retains word, source language, POS, gloss text, example
text, selected lexical relations, selected tags, IPA, form text/tags, selected
etymology templates, and translation word text. Material losses include:

- senses have no separate identity; definitions, examples, registers, lexical
  relations, and cross-references are attached to the word/POS surface;
- translation objects lose target language/code, target sense, romanization,
  tags, and source metadata, and emit an unscoped word→word edge;
- `wikidata` and `senseid` are treated as possible WordNet synset keys rather
  than distinct typed references;
- example type, citation/reference, bold/target offsets, and attestations are
  discarded;
- audio URLs/files, non-IPA pronunciation systems, notes, and dialect metadata
  outside `tags` are discarded;
- raw glosses, sense IDs, categories/topics, head templates, redirects, source
  pages, and most etymology-template semantics are discarded; and
- the whole Wiktionary/Kaikki release is one constant decomposer witness with
  `Unknown` license rather than an artifact/revision-aware source.

The separate in-flight Wiktionary sense-identity refactor was not counted as
merged behavior in this report. It addresses the first and third bullets in
part; target-language translation scope and the remaining source fields still
require an explicit coverage contract.

## Required shared core

Build one schema-driven lexical/semantic admission core with thin format
adapters:

1. **Exact reference admission** — full external ID plus authority/release;
   never a lossy normalized key as identity.
2. **Content admission** — lemma/form/gloss/example/annotation text through the
   common decomposed-content path; same content, same hash.
3. **Scoped object admission** — FrameNet FE, PropBank argument, sense, LU,
   syntactic behaviour, and annotation span retain their parent scope.
4. **Structured relation admission** — ordered syntax, semantic predicate
   arguments, selectional restrictions, valence patterns, and annotated spans
   remain queryable structure rather than flattened labels.
5. **Provenance admission** — resource, release, artifact hash, upstream ID,
   license, citation, language, confidence, mapping method, and derivation.
6. **Coverage receipts** — per source/phase: rows seen, accepted, rejected by
   reason, fields present, fields emitted, unresolved references, and bridge
   coverage. Silent no-op is failure.
7. **Single and batch surfaces** — the same admission program supports one
   record and many records. CLI, API, OpenAI-compatible diagnostics, and MCP
   call the same inspect/validate operations instead of bespoke code paths.

WN-LMF 1.4 should be the first adapter over this core because it exercises
Lexicon metadata, LexicalEntry/Lemma/Form/Sense/Synset, pronunciations/tags,
sense and synset relations, definitions/examples, counts, syntactic behaviour,
LexiconExtension, external references, and confidence/status metadata.

## Action order

This section is the lexical/semantic source-fidelity slice, not the complete
seed or product program. The dependency-ordered cross-cutting sequence is
[`seed-substrate-remediation-sequence-2026-08-19.md`](seed-substrate-remediation-sequence-2026-08-19.md),
which also covers the unaudited source families, identity/storage boundary,
modality reconstruction, models, SQL/native operation cores, perfcaches,
greenfield reseed execution, read reachability, and conversational acceptance.

This is the measured semantic-source acceptance slice of
[#1045](https://github.com/SaltyPatron/Laplace/issues/1045)'s ingestion-recipe
architecture and extends
[#1041](https://github.com/SaltyPatron/Laplace/issues/1041)'s reference-versus-content
finding across the native schemas. It does not replace either issue. Execution
and acceptance are tracked in
[#1153](https://github.com/SaltyPatron/Laplace/issues/1153).

### P0 — correctness before another full seed

1. Preserve full WordNet sense keys; provide explicit compatibility aliases for
   shortened third-party keys. Prove zero identity collisions on PWN 3.0.
2. Preserve source and target sense scope for lexical pointers, adjective
   position, sense number, sense examples, verb frames, and morphology POS.
3. Add a generic streaming WN-LMF 1.4 decomposer and ingest Open English WordNet
   plus OMW 2 packages as versioned resources without double-voting derived
   serializations.
4. Scope FrameNet FE identity by frame and ingest FE relations, semantic types,
   core sets, LU/full-text FE spans, and structured valence annotations.
5. Fix VerbNet `fn_mapping`; ingest ordered syntax, restrictions, full semantic
   predicate arguments, polarity, member keys, and PropBank grouping.
6. Scope PropBank arguments by roleset; ingest aliases/POS, lexlinks,
   confidence/version, usage flags, and example predicate/argument spans.
7. Complete Wiktionary per-sense identity; preserve target-language translation
   scope, typed Wikidata/sense references, example spans/provenance,
   pronunciation/audio, and artifact provenance. Keep the existing exact-input
   admission check and make a missing JSONL an explicit failed source receipt.

### P1 — cross-resource fidelity

8. Ingest all Predicate Matrix languages/POS and all 27 columns, especially PB
   argument and three-way VN/FN/PB role alignment.
9. Preserve roleset and FrameNet frame context in SemLink role mappings.
10. Version and calibrate MapNet/WFN/XWFN bridges; retain unresolved expansion
    targets as references instead of silently dropping them.
11. Preserve CILI Concept vs Instance, `dc:source`, release metadata, and one
    canonical version name per equivalent serialization.
12. Replace constant decomposer-name witness IDs with release/artifact-aware
    provenance, and populate known licenses instead of `Unknown`.

### P2 — missing complementary witnesses

13. Evaluate Russian FrameBank, NomBank, current OEWN-aligned SemCor, Global
    FrameNet resources, PreMOn, and FrameBase as separately versioned witnesses.
    Their value is complementary annotation and mappings, not additional votes
    for data already imported from the primary authority.

## Acceptance gate

A source is not “supported” because a directory exists or an ingest command
returns zero. For each primary or bridge source, a clean seed must prove:

- the expected artifact and declared release were selected;
- all schema fields are classified as emitted, intentionally ignored with a
  reason, or rejected with a count;
- external identities do not collide after normalization;
- content hashes do not vary by source or modality wrapper;
- scoped identities remain distinct;
- every unresolved cross-version reference is counted;
- license/citation/version/artifact hash are queryable;
- a sample record round-trips through single-record and batch admission with
  identical evidence; and
- API, OpenAI-compatible diagnostics, and MCP expose the same coverage receipt.
