# Laplace C3PO Linking Audit — Ingest / Convergence Layer

_Audit date: 2026-06-21 · Scope: cross-source semantic convergence ("the heart")_

## Executive summary

**Overall: structurally sound hub-and-spoke model with targeted gaps.**

The *Net knowledge layer correctly centers on **ILI/CILI-resolved WordNet synsets** (`ConceptAnchor`) with **category keys** (`CategoryAnchor`) for senses, Levin classes, PropBank rolesets, and FrameNet frames. PropBank ↔ VerbNet ↔ FrameNet alignment via SemLink uses shared content-addressed ids and `CORRESPONDS_TO` / `ROLE_CORRESPONDS_TO`. OMW converges per-language lemmas onto the same ILI synset via `IS_SYNONYM_OF` (not translation edges). Cross-batch and cross-source forward references are legal after referential EXISTS pre-check removal.

**Healthy:** WordNet hub, OMW ILI binding, VerbNet/PropBank/SemLink category convergence, FrameNet frame/LU anchors, UD universal deprel/feature routing, Atomic2020 RelationTypeRegistry alignment, attestation policy native-only in witnesses.

**Gaps (ranked):**
1. ~~**ConceptNet `/wn/<topic>` → ILI**~~ — **Fixed (2026-06-21):** coarse lexicographer topics (e.g. `communication`, `animal`) → `CORRESPONDS_TO` ILI via `ConceptNetWnTopicMap` (WordNet 3.0 lexname-derived table).
2. **FrameNet LU → WordNet** — LU XML has no WN sense keys in current ingest; frame→synset via Predicate Matrix / SemLink only.
3. ~~**JSON compose path vs ContentWitnessBatch**~~ — **Fixed (2026-06-21):** `grammar_compose.cpp` JSON scalar leaves adopt `laplace_content_root_id` (decoded UTF-8, including `\u` escapes); `JsonGrammarHelper` resolves string properties via `ContentWitnessBatch.RootId` so Wiktionary/ConceptNet witnesses converge with WordNet/OMW for multi-grapheme surfaces.
4. **CILI map optional at runtime** — `LAPLACE_CILI_DIR` / default path; without map, `ConceptAnchor.Emit*` returns null and synset convergence silently skips.

---

## Intended convergence model

| Layer | Identity key | Anchor surface | Primary relations |
|-------|-------------|----------------|-------------------|
| Synset (concept) | CILI ILI string (e.g. `i46531`) | `ConceptAnchor` → `ContentEmitter` | `IS_SYNONYM_OF`, pointer graph, `IS_TYPED_AS` → WordNet_Synset |
| Sense | Normalized sense key (`lemma%2:40:00`) | `SenseAnchor` / `CategoryAnchor` | `HAS_SENSE`, `IS_SENSE_OF`, `CORRESPONDS_TO` (VN) |
| VerbNet class | Numeric class id (`13.1-1`) | `CategoryAnchor` | `IS_A` hierarchy, `MEMBER_OF_VERBNET_CLASS`, `CORRESPONDS_TO` (PB/SemLink) |
| PropBank roleset | Roleset id (`give.01`) | `CategoryAnchor` | `HAS_SENSE`, `CORRESPONDS_TO`, `ROLE_CORRESPONDS_TO` |
| FrameNet frame | Frame name (`Giving`) | `CategoryAnchor` | `EVOKES_FRAME`, frame relations |
| OMW lemma | Decomposed UTF-8 surface | `ContentWitnessBatch` | `IS_SYNONYM_OF` → ILI synset, `HAS_LANGUAGE`, `HAS_POS` |
| ConceptNet term | `/c/<lang>/<term>` surface | `ContentWitnessBatch` | RelMap → native relation types, `HAS_LANGUAGE`, `HAS_POS` (when URI includes POS) |
| UD token | Form UTF-8 | `ContentWitnessBatch` | `ResolveDeprel`, `ResolveFeature`, UPOS via `PosReference` |

**anchor_law compliance:** No witness-namespace `OfCanonical` blobs for concepts/categories. Violations are limited to vocabulary scaffolding (ordinals, framenet coreness, atomic splits, XPOS namespace ids in UD) — acceptable typed vocabulary, not concept blobs.

---

## Per-source audit

### WordNet (`WordNetDecomposer`, layer 2)

- **Synsets:** `ConceptAnchor.EmitAnchor` / `SynsetId` — offset+ss_type → CILI ILI via `IliMap`; content-addressed, not offset-keyed. ✓
- **Senses:** `index.sense` → normalized key → `SenseAnchor`; `lemma HAS_SENSE sense`, `sense IS_SENSE_OF synset`. ✓
- **Lemmas:** `ContentWitnessBatch` / `IS_SYNONYM_OF` synset. ✓
- **Pointers:** Typed relations via `PointerTypes`; verb `@` remapped to `MANNER_OF`. ✓
- **Dependency:** Requires `LAPLACE_CILI_DIR` with `ili-map-pwn30.tab`.

### OMW (`OMWGrammarWitness`, layer 3)

- **Convergence:** Same `ConceptAnchor` ILI path as WordNet; `IS_SYNONYM_OF` (not `IS_TRANSLATION_OF`) by design. ✓
- **POS:** Raw ss_type including satellite `s`. ✓
- **Order:** Order-independent; forward ILI refs legal.

### VerbNet (`VerbNetDecomposer`, layer 2)

- **Classes:** `CategoryAnchor.Emit(NumericVerbNetClassId(ID))`. ✓
- **Members:** `lemma MEMBER_OF_VERBNET_CLASS class` (de-conflated from IS_A; class hierarchy remains IS_A). ✓
- **WN bridge:** `CORRESPONDS_TO` → `SenseAnchor.Id(NormalizeSenseKey(wn))`. ✓
- **Duplication removed:** `NumericVerbNetClassId` consolidated to `SourceEntityIdConventions`.

### PropBank (`PropBankDecomposer`, layer 2)

- **Rolesets:** `CategoryAnchor.Emit(rsId)`. ✓
- **VN/FN rolelinks:** `CategoryAnchor.Id` only (no foreign typing rows). ✓
- **FrameNet class names:** NOT passed through `NumericVerbNetClassId` (correct — frame names are not VN ids). ✓

### FrameNet (`FrameNetDecomposer`, `FrameNetLuIngest`, layer 3)

- **Frames/FEs/LUs:** `CategoryAnchor` + `EVOKES_FRAME`. ✓
- **Subframe of:** Fixed orientation — parent `HAS_SUBEVENT` child (test added). ✓
- **Gap:** No WN sense key / ILI bridge from LU files.

### SemLink (`SemLinkGrammarWitness`, layer 3)

- **pb-vn2:** roleset `CORRESPONDS_TO` VN class; ARG `ROLE_CORRESPONDS_TO` theta with class context. ✓
- **vn-fn2:** VN class `CORRESPONDS_TO` FN frame. ✓
- **Staging:** SemLink re-emits category entity rows (order-independent dedup). ✓
- **Predicate Matrix:** `PredicateMatrixIngest.cs` ingests vault-root `PredicateMatrix.v1.3/PredicateMatrix.v1.3.txt` (or `PredicateMatrix.txt` under SemLink); WSD-aligned `CORRESPONDS_TO` PB/VN/FN → ILI synsets via CILI. ✓

### ConceptNet (`ConceptNetGrammarWitness`, layer 2)

- **Terms:** `/c/lang/term` → `ContentWitnessBatch`. ✓
- **Relations:** `RelMap` → `RelationTypeRegistry.Resolve`. ✓
- **TranslationOf:** Mapped in RelMap; cross-lingual via graph edges. ✓
- **Improvement (this refactor):** Parse POS from URI suffix; emit `HAS_POS` with WordNet tagset.
- **Improvement (follow-up):** `/wn/` MCR keys and ExternalURL WN-RDF tails → `CORRESPONDS_TO` ILI synset via `SourceEntityIdConventions.ResolveSynsetAnchor`.
- **Improvement (2026-06-21):** `/wn/<topic>` coarse lexicographer labels → `CORRESPONDS_TO` ILI via `ConceptNetWnTopicMap` (static topic|pos → synset table from WordNet lexnames).

### Wiktionary (`WiktionaryGrammarWitness`, layer 2)

- **Wordforms:** Grammar compose + `JsonGrammarHelper` → `ContentWitnessBatch` root ids. ✓
- **Relations:** Lemma-level semantic edges; verb hypernymy → `MANNER_OF`. ✓
- **Translations:** `IS_TRANSLATION_OF`. ✓
- **No direct ILI anchor** — relies on shared content ids with WordNet lemmas.

### UD (`UDDecomposer`, layer 2)

- **Forms/lemmas:** UTF-8 content. ✓
- **UPOS:** `PosReference` universal. ✓
- **XPOS:** Now emitted (`HAS_XPOS`). ✓
- **deprel/features:** `RelationTypeRegistry` native resolve. ✓

### Atomic2020 (`Atomic2020GrammarWitness`, layer 2)

- **Event heads:** Composed content + RelationTypeRegistry types aligned with ConceptNet (`CAUSES`, `AT_LOCATION`, etc.). ✓
- **Split context:** train/dev/test as `contextId`. ✓

---

## Shared abstractions

| Type | Role |
|------|------|
| `ConceptAnchor` | ILI-resolving synset emit/id + `IS_TYPED_AS` WordNet_Synset |
| `SenseAnchor` | **New** — normalized WN sense keys + WordNet_Sense typing |
| `CategoryAnchor` | Key-as-content + categorical `IS_TYPED_AS` |
| `SourceEntityIdConventions` | ILI map, sense key normalize, **ResolveSynsetAnchor**, **NumericVerbNetClassId**, **VerbNetClassFromSemLinkKey** |
| `ContentEmitter` / `ContentWitnessBatch` | Decomposed surface root ids |
| `PosReference` | Omniglottal POS with tagset disambiguation |
| `LanguageReference` | ISO language axis |
| `RelationTypeRegistry` | Native relation resolve (decomposers bootstrap only) |
| `StructuredGrammarIngest` | TSV/JSON row pipeline + `IGrammarWitness` |

---

## Content-addressed vs string-keyed

| Entity | Key | anchor_law |
|--------|-----|------------|
| Synset | ILI string | ✓ content |
| Sense | sense key string | ✓ content |
| VN/PB/FN category | class/rs/frame string | ✓ content |
| Lemma | UTF-8 surface | ✓ content |
| UD XPOS | `substrate/pos/xpos/{tag}/v1` | ⚠ vocabulary blob (scoped) |
| PropBank ordinal | `ordinal/{n}/v1` | ⚠ vocabulary blob |
| FrameNet coreness | `framenet/coreness/{type}` | ⚠ vocabulary blob |

---

## Refactors implemented (this pass)

1. **`SenseAnchor`** — single surface for WN sense key id/emit/typing.
2. **`SourceEntityIdConventions.NumericVerbNetClassId` / `VerbNetClassFromSemLinkKey`** — deduplicated from VerbNet, PropBank, SemLink.
3. **ConceptNet URI POS parsing** — `TryParseConceptUri` + `HAS_POS` attestation.
4. **Cross-source tests** — `CrossSourceLinkingTests`, `ConceptNetUriTests`, FrameNet subframe orientation, convention unit tests.
5. **JSON compose root-id unification** — `grammar_compose.cpp` adopts `laplace_content_root_id` for JSON scalar leaves (with `\u` unescape); `JsonGrammarHelper.TryContentRootFromJsonStringSpan` decodes full quoted string nodes and resolves via `ContentWitnessBatch.RootId` before compose span lookup.
6. **Tests:** `JsonLeafContentConvergenceTests` (multi-word, `\u` escape, witness property path), Wiktionary word-id convergence assertion.
7. **`SourceEntityIdConventions.ResolveSynsetAnchor`** — shared MCR/WN-RDF/sense-key resolver (SemLink + ConceptNet).
8. **ConceptNet ILI bridge** — `/wn/<mcr-key>`, ExternalURL WN-RDF, and `/wn/<topic>` lexicographer labels → term `CORRESPONDS_TO` ILI synset.
9. **VerbNet membership relation** — `MEMBER_OF_VERBNET_CLASS` manifest type; lemma→class edges de-conflated from IS_A (subclass hierarchy unchanged).

---

## Recommended follow-ups (by impact)

1. ~~**Unify JSON compose root-id with ContentWitnessBatch**~~ — **Done:** native compose + witness helper (see gap #3).
2. ~~**ConceptNet WN-RDF / offset bridge (partial)**~~ — **Done:** ExternalURL WN-RDF tails, `/wn/<mcr-key>`, and `/wn/<topic>` lexicographer labels → `CORRESPONDS_TO` ILI via `ResolveSynsetAnchor` + `ConceptNetWnTopicMap`.
3. **FrameNet LU WN links** — standard FrameNet 1.x LU XML has no WN sense keys; frame→synset covered by Predicate Matrix; LU-level bridge deferred.
4. **Fail-fast when CILI missing** — log/warn at WordNet/OMW startup if ILI map absent.

---

## Manifest alignment

`scripts/win/witness-manifest.json` **anchor_law** and knowledge-layer **links** fields match post-refactor behavior. SemLink **links** documents Predicate Matrix at vault-root `PredicateMatrix.v1.3/PredicateMatrix.v1.3.txt` (implemented via `PredicateMatrixIngest.cs`). `seed-ladder.yml` semlink job **needs** wordnet (ILI/CILI precondition) plus verbnet/propbank/framenet.

`scripts/validate-pipeline.py` — passes.

---

## Test coverage

| Project | Result |
|---------|--------|
| Laplace.Decomposers.Abstractions.Tests | 174 passed |
| Laplace.Decomposers.VerbNet.Tests | 6 passed |
| Laplace.Decomposers.SemLink.Tests | 7 passed |
| Laplace.Decomposers.PropBank.Tests | 8 passed |
| Laplace.Decomposers.FrameNet.Tests | 8 passed |
| Laplace.Decomposers.WordNet.Tests | 2 passed |
| Laplace.Decomposers.ConceptNet.Tests | 1 passed |
