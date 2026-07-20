# 16 — Tier-correct attestation + hub unification (the "record it where it's true" spec)

Status: living spec. Opened 2026-07-06. Root cause behind the translation/POS/language/
provenance complaints tracked in this session. Companion to 08 (record-vs-calculate),
11 (three-layer model), 12 (mold-a-model). All findings below are code- or data-cited.

## 0. The one law

**Attest each fact ONCE, at the TIER, IDENTITY, and PROVENANCE where the source
actually asserts it.**

Every defect in this doc is a violation of exactly one of {tier, identity, provenance}.
Content-addressing already guarantees cross-occurrence folding (same fact, same id,
observation_count sums) and cross-source consensus. What it does NOT fix is a decomposer
that emits a fact at the wrong tier, against a non-content-addressed identity, or under
the wrong source. Those are decomposer bugs; the substrate faithfully records the mistake.

Corollary (the user's example): if Tatoeba says "The cat sat on the mat" is English,
that is ONE attestation `sentence --HAS_LANGUAGE--> en` at the SENTENCE tier. It is NOT
five word-tier attestations `cat→en, sat→en, the→en, …`. A treebank/corpus asserts the
language of the TEXT, not the independent language of each wordform. (`chat` is a French
AND English wordform; its language is a property of the USE, i.e. the sentence.)

Tier is a floor, not identity (05 Rule #1b): a fact attaches at the tier of the unit the
source is talking about. Sub-constituents do not inherit it as their own attestations.

## 1. Tier violations — facts recorded below the tier they belong to

### 1a. HAS_LANGUAGE attested per-word by corpus sources
- UD emits `HAS_LANGUAGE` per FORM token (`UdSentenceEmitter.cs:84-85`, plus per-token
  MISC `Lang=` at `:142-147`). A CoNLL-U treebank asserts ONE language for the sentence.
- Live: `IS_TRANSLATION_OF` subjects span every tier — codepoint 3 455, grapheme 1 490,
  Word 1.47M, Sentence 318 900, Document 55 — because the aligned unit's content root
  falls at whatever tier its length implies. Legit when the aligned unit genuinely IS a
  word (single-word subtitle line "Yes"); a BUG when a multi-word sentence also stamps
  its constituents.

Rule: **corpus/text sources (UD, Tatoeba, OpenSubtitles) attest language ONCE at the
content root of the unit they aligned/parsed.** Lexical sources (OMW, Wiktionary) may
attest `word --HAS_LANGUAGE--> lang` because a dictionary genuinely asserts the lemma's
language. Distinguish by source class, not by relation.

### 1b. Sentence facts must not fan out to constituents
The compose pipeline builds the tier tree (doc→sentence→word→grapheme→codepoint). A fact
about the sentence root attaches to the root only. Verify no decomposer loops the
sentence-scoped attestations (language, translation, document metadata) over child spans.

### 1c. Empirical audit (2026-07-06, live pre-reseed DB) — what is a bug vs what is NOT
Ran `attestations ⋈ entities` grouped by source × tier for tier ≤ 1. Findings:
- **Real bug (fixed): UD per-token HAS_LANGUAGE.** 3.27M word-tier + ~20K codepoint/
  grapheme-tier language rows. Language is a sentence property; fixed to attest once at
  the sentence root (§1a). The ~20K tier-0/1 were single-char tokens caught by the same
  per-token loop.
- **Real bug (fixed): Tatoeba ref-entity translation** (§2a).
- **NOT a bug — legitimate single-char-token attestation.** The remaining non-Unicode
  tier-0/1 attestations are correct. UD's are `DEP_PUNCT` (1.28M t1 + 262K t0), `DEP_CASE`,
  `DEP_CC`, `DEP_DET`, `DEP_NSUBJ`, `EDEP_*` — dependency relations whose DEPENDENT token is
  genuinely one character (a comma IS U+002C; CJK case/coord/determiner particles are single
  codepoints). Wiktionary/OMW/ConceptNet tier-0/1 rows are single-char lemmas/terms (`a`,
  CJK single-char words). Content-addressing correctly gives the single-char word the same
  id as the codepoint; the linguistic fact (this comma's deprel, this lemma's language) is
  genuinely about that content. This is NOT fixable without breaking content-addressing and
  MUST NOT be "fixed" — the facts are correct.

Conclusion: "only Unicode attests to codepoints" is the right principle for NOT polluting
codepoints with facts they didn't witness (the UD language bug), but it does not mean a
single-char linguistic unit is forbidden from carrying its own genuine attestation. The
codepoint concern that remains is PERFORMANCE — the fixed codepoint set being re-emitted
and DB-dedupe-checked per batch instead of served from the perfcache — see §7 P8, not a
tier-correctness issue.

## 2. Identity violations — non-content-addressed anchors (content-addressing break)

### 2a. Tatoeba anchors translation on ref-entities, not the UAX sentence
- Tatoeba mints `Tatoeba_Sentence` entities id = `SourceEntityIdConventions.TatoebaSentence
  (externalId)` = hash of the numeric id, entity type `Tatoeba_Sentence`, **tier 2 (Word)**
  (`TatoebaGrammarWitness.cs:49,75-78`). `IS_TRANSLATION_OF` links these ref entities
  (`:79-80`), NOT the content root.
- Consequence: the identical sentence text in Tatoeba vs OpenSubtitles (which anchors on
  the content root, `OpenSubtitlesZipIngest.cs:70-71`) vs a UAX tier-3 Sentence are THREE
  different ids. They never merge. Cross-source translation corroboration is lost, and a
  full sentence sits mis-tiered at the Word floor.
- Live confirms both shapes coexist under `IS_TRANSLATION_OF` (Word tier 1.47M vs Sentence
  tier 318 900).

Fix: resolve each Tatoeba link id THROUGH `sentences.csv` to the sentence text, decompose
via UAX (the content root the pipeline already produces), and anchor `IS_TRANSLATION_OF`
on that tier-3 content root. Keep the external numeric id as `HAS_EXTERNAL_ID` annotation
ON the content root (provenance), never as the translation anchor. OpenSubtitles is the
correct template.

## 3. Provenance violations — distinct sources collapsed into one witness

### 3a. PredicateMatrix emits under SemLink's source
- `PredicateMatrixIngest` stamps every row with `SemLinkDecomposer.Source`
  (`PredicateMatrixIngest.cs:104,105,109,289,294,298`). Driven as a sub-lane of
  `SemLinkDecomposer` (`SemLinkDecomposer.cs:58-63`).
- The standalone `["predicatematrix"] = Row("predicatematrix","PredicateMatrixDecomposer",
  3,…)` (`EtlManifest.cs:142`) points at a class **that does not exist** — `grep -r
  "PredicateMatrixDecomposer"` returns ONLY the EtlManifest string. Orphaned registration.
- Consequence: PredicateMatrix (arguably the richest cross-resource map — each row ties
  VN class + FN frame + PB roleset + WN sense + MCR/ILI) has NO independent provenance.
  PM and SemLink's own maps fold into one source, so consensus cannot see them as two
  witnesses corroborating the same VN↔FN↔synset link. That corroboration is the entire
  point of the EVIDENCE layer.

Fix: PredicateMatrix gets its own `Source` id + trust and stamps its rows with it even
when the SemLink orchestrator drives the file. Either build the missing
`PredicateMatrixDecomposer` or pass a distinct source into `PredicateMatrixIngest`.

## 4. Dropped hub links — the "catch up, it's already linked" gap

The shared hubs (ILI/synset, FrameNet frame, VerbNet class, PB roleset) already exist and
the sources already reference them. We connect in 4 and drop in 2.

Caught (route to hub, the model): SemLink JSON maps, PredicateMatrix, MapNet, WordFrameNet
(`CORRESPONDS_TO` → synset/ILI); WordNet/OMW/CILI (synset keyed by ILI-string hash).

Dropped:
- **ConceptNet** — URIs carry `/c/en/dog/n` POS AND a `/wn/…` synset suffix; the resolver
  `ConceptNetUri.ResolveSynsetFromWnSuffix` + `ConceptNetWnTopicMap` EXIST and the
  decomposer calls `TryParseConceptUri(…, out _, out _)` discarding both
  (`ConceptNetDecomposer.cs:136-137`). Every ConceptNet node is a bare phrase island,
  disconnected from the synset it names in its own URI. (Also: `HAS_POS`, `HAS_LANGUAGE`,
  `HAS_EXAMPLE`, `CORRESPONDS_TO` are REGISTERED as vocab at `:72` but never emitted.)
- **Wiktionary** — senses hung off the word by POS-context, translations pairwise word↔
  word, no synset/ILI routing (`WiktionaryGrammarWitness.cs` sense + translation lanes).

Fix: ConceptNet — wire the synset suffix, emit node→synset membership; capture POS
(already resolvable) via `HAS_POS`. Wiktionary — where a sense/translation resolves to a
synset, route to the hub.

## 5. POS over-differentiation — the 36 001

- `pos_tags.toml` ALREADY unifies the tagged tagsets to the 17 UPOS: WordNet `n/v/a/s/r`,
  Wiktionary labels, FrameNet, UPOS → one canonical entity per POS. The "proper noun"
  caveat is preserved: `proper noun`/`name` → PROPN. `HAS_POS` is unified. ✅
- The explosion is **XPOS**: UD emits the raw per-treebank tag via `HAS_XPOS` verbatim,
  UNMAPPED (`UdSentenceEmitter.cs:63-69`) — `NNG+NNB+VCP+ETM`, `Vmip3-----y`,
  `ppron3:pl:dat:m2:ter:akc:npraep`, `p,接尾辞,*,*`. 36 001 distinct `Pos` entities, all
  islanded from UPOS — even though the CoNLL-U row hands us UPOS and XPOS on the SAME line.

Fix: emit `xposEntity --IS_A--> uposEntity` (the row gives both), and split the morphology
XPOS also encodes into `FEAT_*` Key=Value. "Is X a noun" collapses 36 001 islands onto 17
hubs; PROPN stays distinct.

## 6. Seed ordering — "SemLink/PM before WordNet"

Current layer order (`EtlManifest.cs`): 0 unicode, 1 iso639, 2 {wordnet, cili, verbnet,
propbank, ud, wiktionary, tatoeba, opensubtitles, …}, 3 {omw, framenet, semlink, mapnet,
wordframenet, predicatematrix}. So the cross-resource maps run LAST.

**Verified fact: reordering is IDENTITY-NEUTRAL.** A synset id = BLAKE3(ILI string).
Whether SemLink (`CORRESPONDS_TO`→`ResolveSynsetAnchor`) or WordNet (`IS_TYPED_AS`) first
touches that id, content-addressing makes it the SAME entity; the second run dedups. The
final graph is byte-identical regardless of order. The only order-dependent columns are
`first_observed_by` / `created_at` (who minted the hub first).

Therefore reordering does NOT connect islands and does NOT change what merges. The island
problem is §4 (link-dropping), not order. Recommendation: do NOT reorder on a
correctness argument — fix §4. If the goal is provenance/conceptual ("the hub authority
owns the hub node"), that is a `first_observed_by` preference and a trivial layerOrder
swap, but it buys nothing functional. Open for the user to confirm intent before touching.

## 7. Fix sequence (all decomposer-side; realized on the next reseed)

P1. Tatoeba → anchor translation on the UAX content root; external id as annotation (§2a).
P2. Corpus HAS_LANGUAGE → attest once at the unit root, not per token (§1a); UD first.
P3. PredicateMatrix → own source id/trust (§3a); build or wire `PredicateMatrixDecomposer`.
P4. ConceptNet → capture `/wn/` synset + POS, node→synset membership (§4).
P5. UD XPOS → `IS_A` UPOS + `FEAT_*` split (§5).
P6. Wiktionary → route lexicalized senses/translations to synset hub (§4).
P7. (done) `SEMANTIC_EQUIVALENCE` family unifies the equivalence relation NAMES (relation_
    types.toml + relation_law.c regenerated, verified). Hub-shape unification is P1/P4/P6.

Each Pn is a decomposer edit; none needs a framework. Stage all, reseed once.

## 8. Validation results (2026-07-06, fresh foundation reseed on the fixed build)

Build: rebuild-all regenerated relation_law.c + highway perfcache to 182 relations
(historical build record; count later moved to 203 — see docs/INVENTORY.md, the count authority)
(SEMANTIC_EQUIVALENCE added). NOTE landmine hit + fixed: rebuild-all failed at phase 4
(a new SQL file `related_objects.sql.in` was unlisted in manifest.install/upgrade — added
it), which meant phase 6 (build app) never ran, so the CLI kept STALE 181-count native
DLLs and every seed died on `highway_table_load rc=-3 (count/size mismatch)`. Fix: rebuild
the app so the CLI copies the fresh 182 DLLs. Lesson: after a manifest relation-count
change, the CLI's own bin native DLLs must be refreshed (phase 6), not just build-win.

Validated live on the foundation seed (unicode/iso639/cili/wordnet/omw/verbnet/propbank/
framenet/semlink+PM):
- FIX (SEMANTIC_EQUIVALENCE family): PASS. `relation_type_in_family(IS_TRANSLATION_OF,
  'SEMANTIC_EQUIVALENCE')` and `(IS_SYNONYM_OF, …)` both true; 206,978 consensus rows
  reachable by the single family predicate; IS_A negative control false; RELATED_TO still
  true (no regression).
- FIX (PredicateMatrix distinct source): PASS. PM emitted 49,425 CORRESPONDS_TO + 1,247
  ROLE_CORRESPONDS_TO under source `PredicateMatrixDecomposer` (evidence_count 57,654),
  SemLink 8,350 under its own source; 21,044 CORRESPONDS_TO consensus nodes now at
  witness_count ≥ 2 — the PM↔SemLink corroboration that was structurally impossible when
  both folded into one source. (PM data lives in D:\Data\Ingest\PredicateMatrix.v1.3\;
  SemLink's VaultRoots search finds it — PM does run under the semlink seed step.)
- Tatoeba identity fix: PASS (unit test). IS_TRANSLATION_OF anchors on content roots that
  also carry HAS_LANGUAGE, never on Tatoeba_Sentence ref entities; link to an absent
  sentence id emits no dangling edge.
- Lexical sample after WordNet land (ordinary API usage, not a gate):
  `SELECT count(*) FROM senses(word_id('dog'));` returned 8. Note: text-literal
  `senses('dog')` casts to raw bytea and returns 0 — that is an API misuse, not a
  health ritual.

Pending live validation (need their seeds): UD HAS_LANGUAGE-at-sentence-tier + XPOS IS_A
UPOS; ConceptNet HAS_POS; Tatoeba translation-on-content-root at scale; Wiktionary < 10 min.
