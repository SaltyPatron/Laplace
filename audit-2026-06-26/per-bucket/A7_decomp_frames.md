## Bucket: A7_decomp_frames (FrameNet / VerbNet / PropBank / SemLink decomposers + tests)

### Files read (all 27 in bucket, in full)
- [x] app/Laplace.Decomposers.FrameNet.Tests/FrameNetDecomposerTests.cs
- [x] app/Laplace.Decomposers.FrameNet.Tests/Laplace.Decomposers.FrameNet.Tests.csproj
- [x] app/Laplace.Decomposers.FrameNet/FrameNetDecomposer.cs
- [x] app/Laplace.Decomposers.FrameNet/FrameNetLuIngest.cs
- [x] app/Laplace.Decomposers.FrameNet/Laplace.Decomposers.FrameNet.csproj
- [x] app/Laplace.Decomposers.PropBank.Tests/Laplace.Decomposers.PropBank.Tests.csproj
- [x] app/Laplace.Decomposers.PropBank.Tests/PropBankDecomposerTests.cs
- [x] app/Laplace.Decomposers.PropBank/Laplace.Decomposers.PropBank.csproj
- [x] app/Laplace.Decomposers.PropBank/PropBankDecomposer.cs
- [x] app/Laplace.Decomposers.SemLink.Tests/Laplace.Decomposers.SemLink.Tests.csproj
- [x] app/Laplace.Decomposers.SemLink.Tests/MapNetDecomposerTests.cs
- [x] app/Laplace.Decomposers.SemLink.Tests/SemLinkDecomposerTests.cs
- [x] app/Laplace.Decomposers.SemLink.Tests/WordFrameNetDecomposerTests.cs
- [x] app/Laplace.Decomposers.SemLink/FnLuSynsetBridgeIngest.cs
- [x] app/Laplace.Decomposers.SemLink/Laplace.Decomposers.SemLink.csproj
- [x] app/Laplace.Decomposers.SemLink/MapNetDecomposer.cs
- [x] app/Laplace.Decomposers.SemLink/MapNetIngest.cs
- [x] app/Laplace.Decomposers.SemLink/PredicateMatrixIngest.cs
- [x] app/Laplace.Decomposers.SemLink/SemLinkDecomposer.cs
- [x] app/Laplace.Decomposers.SemLink/SemLinkGrammarWitness.cs
- [x] app/Laplace.Decomposers.SemLink/SemLinkRoleMappingIngest.cs
- [x] app/Laplace.Decomposers.SemLink/WordFrameNetDecomposer.cs
- [x] app/Laplace.Decomposers.SemLink/WordFrameNetIngest.cs
- [x] app/Laplace.Decomposers.VerbNet.Tests/Laplace.Decomposers.VerbNet.Tests.csproj
- [x] app/Laplace.Decomposers.VerbNet.Tests/VerbNetDecomposerTests.cs
- [x] app/Laplace.Decomposers.VerbNet/Laplace.Decomposers.VerbNet.csproj
- [x] app/Laplace.Decomposers.VerbNet/VerbNetDecomposer.cs

### Helpers traced to verify nontrivial claims (outside bucket, read in full)
CategoryAnchor.cs, ContentEmitter.cs, ContentWitnessBatch.cs, SenseAnchor.cs, ConceptAnchor.cs,
SourceEntityIdConventions.cs, EntityTier.cs (all under app/Laplace.Decomposers.Abstractions + app/Laplace.SubstrateCRUD).

---

### KEY VERIFIED FACT (resolves the "scaffolded-to-fiction / near-zero yield" question)
`CategoryAnchor.Id(key)` → `ContentEmitter.RootId(key)` → `ContentWitnessBatch.RootId` →
`TextEntityBuilder.TryDecomposeRoot`. This is a **pure content function** of the key string (memoized);
it does NOT require prior registration and returns non-null for any non-empty key (given the codepoint
perfcache, which is loaded). VERIFIED by reading the full call chain. Consequence: the frame/class/
roleset/LU/sense **bridge ingests genuinely emit** their CORRESPONDS_TO edges; they do NOT silently drop
all rows the way the earlier scaffolded decomposers did. The synset side is gated on CILI (see below).
So **these decomposers are NOT the "emit near-zero entities, status=ok" sin** — they parse the real
formats and yield real attestations. The previously-flagged drop bugs are fixed (see INFO items).

---

### FINDINGS

**1. FILE: app/Laplace.SubstrateCRUD/EntityTier.cs:20 (and stamped in 6 bucket files) — HIGH — invention-violation (#3 tier=kind)**
CLAIM: `EntityTier.Vocabulary = 5` is a KIND ("Abstract vocabulary (POS, morphology values, languages,
category anchors, relation types)") encoded in the depth axis. CLAUDE.md explicitly names this "the live
violation." The bridge ingests stamp it directly onto content-addressed ids:
- MapNetIngest.cs:192 `new EntityRow(subjectId.Value, EntityTier.Vocabulary, subjectType, ...)`
- FnLuSynsetBridgeIngest.cs:256 `new EntityRow(subjectId, EntityTier.Vocabulary, subjectType, source)`
- PredicateMatrixIngest.cs:291 same
- SemLinkGrammarWitness.cs:194 (StageCategory) same
- PropBankDecomposer.cs:157 ordinal entity `EntityTier.Vocabulary`
- FrameNetDecomposer.cs:103-106 coreness type + coreness values at `EntityTier.Vocabulary`
VERIFIED: read EntityTier (kind comment) + each call site. CONFIDENCE high. Tier here is a category, not
emergent compositional depth.

**2. FILE: cross-source (FrameNet vs SemLink/MapNet/PredicateMatrix) — HIGH — correctness + invention-violation**
CLAIM: the SAME content-addressed entity id receives DIFFERENT tiers from different sources.
FrameNetDecomposer emits frame "Giving" via `CategoryAnchor.Emit` → `ContentEmitter.Emit` → the content
tree's **emergent** word-tier root (not 5). The bridges emit that identical id (`CategoryAnchor.Id("Giving")`
== same root id) but stamp `EntityTier.Vocabulary=5` (finding 1). Two writers therefore disagree on the
tier of one id; dedup is by id, so the stored tier is ingest-order-dependent and the geometric radius is
nondeterministic. VERIFIED by tracing both paths to the same `ContentWitnessBatch.RootId` value and the
differing tier args. CONFIDENCE high. This is the concrete harm of the tier-as-kind model.

**3. FILE: SemLinkGrammarWitness.cs:190-197, FnLuSynsetBridgeIngest.cs:246-260, MapNetIngest.cs:182-196, PredicateMatrixIngest.cs:281-295 — MEDIUM — invention-violation (#7 conflicts≈0) + internal inconsistency**
CLAIM: the bridges re-emit ENTITY rows (`b.AddEntity(...)`) + `IS_TYPED_AS` for category anchors they do
NOT own (frames owned by FrameNet, classes by VerbNet, rolesets by PropBank). The main decomposers were
deliberately changed to NOT do this — PropBankDecomposer.cs:189-193, VerbNetDecomposer.cs:108-113 and
FrameNetDecomposer.cs:201-204 all carry comments stating the entity/typing row was "downgraded to Id
(no entity/typing row written for another source's anchor)" precisely to avoid the deleted referential
pre-check and to stop dangling/duplicate rows. The bridges contradict that decision and will produce
re-inserts/conflicts at apply for already-present foreign anchors (invariant: a correct ingest has
"conflicts ≈ 0"). VERIFIED by comparing the two patterns side by side. CONFIDENCE high (med on the
apply-time conflict magnitude, not measured).

**4. FILE: PredicateMatrixIngest.cs:63-68 — MEDIUM — correctness / silent scope-cut**
CLAIM: hardcoded `lang=="eng" && pos=="v"` filter silently drops every non-English and every non-verb
row of the Predicate Matrix (which is multilingual and not verb-only). It also makes the `langs` filter
on line 67 effectively dead: line 65 already forces eng, so `langs.MatchesRaw("eng")` is the only path
that can pass; a caller requesting `spa`/`fra` gets nothing with no diagnostic. No comment justifies the
noun/lang exclusion. VERIFIED by reading the filter and the LanguageFilter call. CONFIDENCE high (that
data is dropped); med on whether verb-only is intended (PB/VN are verb-centric, so plausibly deliberate —
but undocumented and the lang drop is a real convergence loss).

**5. FILES: MapNetDecomposerTests.cs:51-74, WordFrameNetDecomposerTests.cs:126-136, SemLinkDecomposerTests.cs:153-182 — MEDIUM — fake-test (asserts nothing on a real machine state)**
CLAIM: the `*_When_Cili_Present` tests begin `if (!File.Exists(<CILI map>)) return;` and then assert.
On any machine without `D:\Data\Ingest\CILI\<map>` (CI, a Pi, a clean checkout) they execute ZERO
assertions and report green — a false pass for the entire synset-linking surface (the core of these
bridges). VERIFIED by reading the early-return + the hardcoded default path. CONFIDENCE high. Note:
the non-skipping `Attestations_Are_CorrespondsTo_Only` tests for MapNet/WFN instead REQUIRE CILI to be
present (they assert `NotEmpty`, and without CILI `ConceptAnchor.SynsetId`→`WordNetIli`→null drops every
row) — so they FAIL on a CILI-less machine. Net: this test suite only behaves on Anthony's box with the
CILI vault mounted; it is environment-coupled, not self-contained.

**6. FILE: all source/trust/category ids — MEDIUM — invention-violation (#6, known WS3 debt)**
CLAIM: frames/classes/rolesets/LUs are anchored as the **text-composition root of the opaque key string**
(`CategoryAnchor.Id` = `ContentEmitter.RootId`), not on a real external registry id. Cross-source
convergence therefore rests entirely on every source producing byte-identical key strings + identical
normalization (NumericVerbNetClassId, FrameNetLuKey, NormalizeSenseKey). CategoryAnchor.cs:49-55 itself
admits: "unlike language/POS/sense keys, there is no canonical lookup table backing that agreement, only
convention." This is exactly the "concept ids are string-walks of opaque keys" corruption CLAUDE.md §1
flags as today's-index-is-corrupt / WS3 work. Within this bucket the sources ARE internally consistent
(all route through the shared SourceEntityIdConventions), so it is coherent, just not anchored on ILI/
registry ids. CONFIDENCE high. Severity medium = architectural, acknowledged, not a regression.

**7. FILE: VerbNetDecomposer.cs:202,213 vs :37-49 — LOW — correctness (bootstrap omission)**
CLAIM: VerbNet emits `ENTAILS` (line 202) and `HAS_SEMANTIC_ROLE` (line 213) attestations but
`InitializeAsync` never `AddRelationType("ENTAILS")` / `AddRelationType("HAS_SEMANTIC_ROLE")` (only
PropBank bootstraps HAS_SEMANTIC_ROLE). The relation-type id still resolves via the global
RelationTypeRegistry, so the edges are valid, but this source's vocab-readback / relation-type entity
declaration omits them. Not covered by the Bootstrap test (which checks only a subset). VERIFIED by
diffing the emit list against the bootstrap list. CONFIDENCE high (omission); low impact.

**8. FILE: PredicateMatrixIngest.cs:20,24 — LOW — disparagement-adjacent / comment drift**
CLAIM: inline column comments mislabel indices ("10_VN_ROLE" on `ColVnRole=9`, "15_FN_FRAME_ELEMENT" on
`ColFnFe=14`). The 1-based header labels run 1..16 but column 0 carries two "1_" labels (1_ID_LANG +
1_ID_POS), so 0-based indices are offset from the labels. I VERIFIED the actual integer indices
(4,6,8,9,10,11,12,14,15) against the test fixture header in SemLinkDecomposerTests.cs:38 — the **indices
are correct**; only the comments are off. CONFIDENCE high. Cosmetic.

**9. FILE: FrameNetDecomposer.cs:28-29 (coreness), PropBankDecomposer.cs:32 (ordinal), all Source/TrustClass — LOW — invention-violation (#1/#6, defensible)**
CLAIM: invented `Hash128.OfCanonical("framenet/coreness/X")`, `"ordinal/n/v1"`, `"substrate/source/.../v1"`,
`"substrate/trust_class/.../v1"` namespaces rather than content/real-id anchors. These are schema/enum/
provenance (closed FrameNet coreness vocab, arg-number ordinals, source & trust nodes), not convergence-
index concepts, and FrameNetDecomposer.cs:24-27 documents the rationale. Acceptable but noted for
completeness. CONFIDENCE high; low severity.

### Positive / resolved (INFO)
- **Previously-flagged silent-drop bugs are FIXED and tested.** WordFrameNet pipe-joined `lemma|pos`,
  multi-word lemmas, and satellite ss-type `s` are all parsed (FnLuSynsetBridgeIngest.cs:140-183) with
  explicit regression assertions (WordFrameNetDecomposerTests.cs:46-84). MapNet's trailing `$` offset
  terminator is handled (SourceEntityIdConventions.cs:160-166: "otherwise ... EVERY row drops"). The
  memory's "scaffolded to imagined formats" sin does not apply to the current code here.
- **Stage-1 = strip-input is respected.** SemLink JSON routes through the json tree-sitter grammar +
  GrammarRowComposer (SemLinkDecomposer.cs / SemLinkGrammarWitness.cs); TSV/native sources stream
  line-by-line with bounded batches. No domain content is re-routed through the UAX29 text composer as a
  surface string (the chess-style category error is absent). FrameNet fulltext sentences ARE composed as
  text, but that content genuinely is prose, so it is correct.
- **PropBank top-level frameset file (AMR-UMR-91-rolesets.xml) is scanned** alongside frames/
  (EnumerateFramesetFiles) with a real regression test — a genuine completeness fix, not a stub.
- Tests use a real codepoint perfcache and assert on real parse+compose output (no mocked-away core
  logic); they are real unit tests apart from finding 5's CILI coupling.

### Bucket summary
- HIGH: 2  | MEDIUM: 4  | LOW: 3  | INFO: 4 (positives)
- Single worst issue: **tier-as-kind (`EntityTier.Vocabulary=5`) stamped onto content-addressed ids by
  the bridge ingests, combined with the same id getting a DIFFERENT (emergent) tier from its owning
  decomposer** (findings 1+2) — a live invention violation that makes the stored tier/geometry of shared
  convergence-index nodes ingest-order-dependent.
