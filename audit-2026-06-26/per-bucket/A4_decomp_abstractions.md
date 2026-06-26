## Bucket: A4 — Laplace.Decomposers.Abstractions + Containers.Abstractions

The shared framework every source decomposer builds on. This bucket sets the contract, so
violations here are systemic — they propagate into all ~24 decomposers.

### Files read (coverage checklist — all read IN FULL)
- [x] ArenaRmsTracker.cs — clean (RMS scale tracker; thread-safe).
- [x] BootstrapIntentBuilder.cs — VIOLATIONS (tier-as-kind; invented namespace; ignored params).
- [x] CategoryAnchor.cs — clean-ish (frame/roleset keys anchored on trimmed surface key content; honest comment about "convention not lookup").
- [x] CiliMapMissingException.cs — clean.
- [x] CompositionalTypes.cs — text-only notion of "compositional" (see INFO).
- [x] ConceptAnchor.cs — CORRECT anchor (ILI string → content id).
- [x] ConcurrentIdSet.cs — clean.
- [x] ContentEmitter.cs — clean (thin facade over ContentWitnessBatch).
- [x] ContentWitnessBatch.cs — clean; routes surfaces through the text decomposer (intended for text content).
- [x] DecomposerOptions.cs — clean.
- [x] DelimitedContent.cs — clean.
- [x] DocumentDecomposer.cs — reads whole files into memory (see LOW); text path OK.
- [x] DocumentRouter.cs — clean (markdown fence splitter); not actually wired into DocumentDecomposer (see INFO).
- [x] EntityTypeRegistry.cs — invented `substrate/type/X/v1` for ALL type ids incl. POS/ISO639/Language (see HIGH).
- [x] EtlDecomposer.cs — generic driver; convergence largely aspirational (see MEDIUM fork).
- [x] EtlManifest.cs — invented `substrate/source|trust_class/X/v1`; most rows GrammarReady=false (see MEDIUM).
- [x] EtlSource.cs — clean (record/enum defs).
- [x] EtlWitness.cs — content-field values routed through text composer (see INFO).
- [x] EtlWitnessFactory.cs — clean (registry).
- [x] GrammarEntityBuilder.cs — DEAD method; tier `substrate/type/grammar/...` ids; C# attestation build (see findings).
- [x] GrammarRowComposer.cs — parallel compose-extract impl (fork w/ GrammarEntityBuilder); native drain (good).
- [x] GrammarRowReader.cs — clean.
- [x] IDecomposer.cs — clean.
- [x] IDecomposerContext.cs — clean.
- [x] IGrammarWitness.cs — clean.
- [x] IliMap.cs — clean (silently skips malformed lines, LOW).
- [x] IngestInventory.cs — clean.
- [x] IngestParallelism.cs — clean (auto-scale, env override).
- [x] IngestRecordStage.cs — clean (transitional Phase-2/4 notes).
- [x] JsonGrammarHelper.cs — O(n²) child scan (see MEDIUM perf); correct content-root convergence.
- [x] LanguageEntityId.cs — invented `language:{iso3}` prefix (see HIGH/MEDIUM invariant-6).
- [x] LanguageFilter.cs — clean.
- [x] LanguageReference.cs — silent miss → "und" (see MEDIUM).
- [x] NativeAttestation.cs — clean; native-built attestations (good altitude).
- [x] NgramTrajectory.cs — n-gram compose in C# (Merkle/centroid/Hilbert managed-side; altitude, see LOW/INFO).
- [x] PCoreParallelCompose.cs — per-thread P-core pin (matches stated intent; good).
- [x] PosReference.cs — tier-as-kind (EntityTier.Vocabulary); POS ids resolved natively (good).
- [x] RelationTripleDecomposerBase.cs — clean.
- [x] RelationTypeRegistry.cs — native resolve (good); tier-as-kind on seeds; dbpedia rank hardcoded.
- [x] ResponseContent.cs — tier-as-kind on bootstrap; invented source/trust namespace.
- [x] SafetensorSnapshotWitness.cs — clean (validation only).
- [x] SenseAnchor.cs — CORRECT (sense-key content anchor).
- [x] SourceEntityIdConventions.cs — mostly correct (ILI/sense); TatoebaSentence id-from-provenance (see MEDIUM).
- [x] StreamingUtf8LineReader.cs — clean (pooled streaming; note lineBuf reuse semantics, INFO).
- [x] StructuredGrammarIngest.cs — main ETL pump; compose-skip swallow to stderr (see MEDIUM); good back-pressure.
- [x] TextEntityBuilder.cs — C#-side PRECEDES "bigram generator" (see HIGH invention-concern/altitude).
- [x] TsvSpan.cs — clean.
- [x] UserPromptContent.cs — tier-as-kind on bootstrap; invented source/trust namespace.
- [x] VocabularyAnchor.cs — tier-as-kind baked into the *canonical* helper (see CRITICAL).
- [x] VocabularyNames.cs — invented namespaces (`substrate/type`, `language:`, `substrate/pos/probationary`).
- [x] WitnessConstants.cs — clean (rank/trust constants; honest "was stale" comment).
- [x] Laplace.Decomposers.Abstractions.csproj — clean.
- [x] ExplodedViewItem.cs — clean (container item records).
- [x] IContainerParser.cs — clean.
- [x] IContainerRegistry.cs — clean (registry + impl).
- [x] Laplace.Decomposers.Containers.Abstractions.csproj — clean.

---

### FINDINGS

#### 1. `app/Laplace.SubstrateCRUD/EntityTier.cs:20` + ~20 call sites in this bucket — CRITICAL — invention-violation (invariant 3: tier = compositional depth ONLY)
CLAIM: `EntityTier.Vocabulary = 5` jams KIND into the DEPTH axis, and this bucket is where it is
wired into the contract for every decomposer. The enum:
```
public const byte Codepoint=0; Grapheme=1; Word=2; Sentence=3; Document=4;
public const byte Vocabulary = 5;   // "Abstract vocabulary (POS, morphology, languages, ... relation types)"
```
The comment itself admits it is a category ("Abstract vocabulary … Must not share tier 0").
VERIFIED: traced every emit in the bucket that stamps tier 5 on a *kind* of node:
- `VocabularyAnchor.Emit` (VocabularyAnchor.cs:42,46) — the single "canonical" vocabulary helper the docstring says "every dynamic family … routes through" — hardcodes `EntityTier.Vocabulary`.
- `BootstrapIntentBuilder` (37 source, 52 type, 70 relation-type).
- `PosReference` (61 probationary POS, 79 POS meta, 84 each UPOS tag).
- `RelationTypeRegistry.SeedCanonical/SeedDynamic` (174,203,208,211).
- `UserPromptContent.BuildBootstrapChange` (24–28) and `ResponseContent.BuildBootstrapChange` (27–31) stamp tier 5 on Source + the *text* type metas (Grapheme/Word/Sentence/Document types-as-entities).
CONFIDENCE: high. This is the live violation named in CLAUDE.md §2 and is the single worst issue
in the bucket: the framework forces every source to encode a category in the depth number.

#### 2. `TextEntityBuilder.cs:253-297` (`BuildDistributionalAttestations`) — HIGH — invention-concern / altitude
CLAIM: This is a C#-side **PRECEDES bigram generator** — it walks the tier tree, filters "content
words" (`IsContentWord`/`HasAlphanumericLeaf`), counts adjacent word→word pairs, and emits
`PRECEDES` aggregated attestations with `contextId = natural-unit id`. This is exactly the
"bigram generator" the iridescent plan says to retire and the kind of co-occurrence backfill the
`consensus-folds-inline-not-drain` memory flags as an anti-pattern; it is also heavy compute
(tree walk + dictionary aggregation) sitting in C# rather than the native engine.
VERIFIED: method body lines 253-297; called from `TryBuildContentWitness` (239) → used by
`UserPromptContent`/`ResponseContent`. Grammar path computes PRECEDES *natively*
(`ComposePrecedesCount`), so two PRECEDES sources exist (one native, one managed) — inconsistent.
CONFIDENCE: high (code traced). Whether it should exist at all is a design call, but it is managed-side compute that the architecture says belongs native.

#### 3. `EntityTypeRegistry.cs:8-52`, `BootstrapIntentBuilder.cs:15-65`, `EtlManifest.cs:21-22,50`, `VocabularyNames.cs:16,32`, `LanguageEntityId.cs:10` — HIGH — invention-violation (invariant 6: anchor on real external ids, not invented namespaces)
CLAIM: Identity for cross-source convergence anchors is minted from invented namespace strings
rather than the real external inventory id:
- `EntityTypeRegistry.Id(name) => Hash128.OfCanonical($"substrate/type/{name}/v1")` for ALL types
  including `POS`, `ISO639Code`, `Language`, `UD_XPOS`, `FrameNet_Frame`, `WordNet_Synset`.
- `LanguageEntityId.FromIso639_3 => OfCanonical($"language:{iso3}")` — the language *entity* id is
  `blake3("language:eng")`, i.e. ISO code wrapped in an invented prefix, not the bare ISO 639 id.
- `EtlManifest`: sources `substrate/source/{name}/v1`, trust classes `substrate/trust_class/{cls}/v1`.
- `VocabularyNames.RelationType => substrate/type/{canonical}/v1`; `ProbationaryPos => substrate/pos/probationary/{ns}/{tag}/v1`.
VERIFIED: definitions read directly. NOTE the *instance* anchors that matter most ARE correct —
synsets via `ConceptAnchor.EmitAnchor` use the resolved ILI string (ConceptAnchor.cs:25-29),
sense keys via `SenseAnchor`/`CategoryAnchor` use the normalized key, and POS *tag* entities are
resolved natively (`PosReference.CanonicalId` → `NativeInterop.PosResolveEntity`). So the violation
is concentrated on the *type/meta/language/source* layer. The charter explicitly lists "languages→ISO
639, POS→UPOS … blake3'd, never an invented `substrate/type/X/v1` namespace" — `substrate/type/POS/v1`
and `language:eng` are exactly the named anti-pattern.
CONFIDENCE: high. Severity HIGH for the language-entity case (it is an instance anchor), MEDIUM for the pure meta-type ids (gray area, but still the named pattern).

#### 4. `GrammarEntityBuilder.cs:250-278` (`BuildSequenceAttestations`) — MEDIUM — dead-code
CLAIM: Private method, never called. `Extract` (line 239) uses native `ComposePrecedes` +
`BuildTagAttestations` only. Grepped whole repo: the only hit for `BuildSequenceAttestations` is its
own definition. Dead PRECEDES-in-C# path left behind.
VERIFIED: Grep across repo → single definition hit, zero call sites.
CONFIDENCE: high.

#### 5. `GrammarEntityBuilder.cs` vs `GrammarRowComposer.cs` — MEDIUM — fork (converge, don't fork)
CLAIM: Two parallel implementations of "native compose result → EntityRow/PhysicalityRow/PRECEDES +
TrunkShortcircuit dedup". `GrammarEntityBuilder.Extract` and `GrammarRowComposer.Materialize`/
`DrainInto` duplicate the same entity loop, physicality loop, precedes loop, and bitmap/novelty logic.
VERIFIED: GrammarEntityBuilder is used only by the Code decomposers (Code/Repo/Stack/TinyCodes — grep
confirms `new GrammarEntityBuilder` in those + tests); GrammarRowComposer is used by
StructuredGrammarIngest (TSV/JSON rows). Same job, two code paths kept in sync by hand — exactly the
"each new lane is the disease" pattern. GrammarEntityBuilder additionally does code-tag attestations
(`BuildTagAttestations`), which is the only real difference.
CONFIDENCE: high (both bodies read; usage grepped).

#### 6. `EtlManifest.cs:67-173` + `EtlDecomposer` docstrings — MEDIUM — fork / prose-vs-code defect (invariant 8)
CLAIM: The framework's headline claim ("Sources as data … Adding a source is a manifest row, not a
new class" — EtlDecomposer.cs:14, EtlSource.cs:51-57) is largely aspirational. Of the ~24 manifest
rows, the majority carry `EtlModality(GrammarReady: false)` (unicode, iso639, ud, opensubtitles,
code, repo, tabular, tiny-codes, stack, document, wordnet, cili, framenet, propbank, verbnet,
semlink, mapnet, wordframenet, predicatematrix) → `EtlSource.IsComplete` is false → `IsRoutable`
false → the generic `EtlDecomposer` cannot drive them; they still run as bespoke decomposer classes.
The few "complete" rows (conceptnet, tatoeba, wiktionary, omw) route to **bespoke witness factories**
(`EtlWitnessFactory`), i.e. the bespoke class still exists and is delegated to. So the generic
manifest and the per-source classes coexist as two systems.
VERIFIED: read `IsComplete` (EtlSource.cs:89-90), every Row() call's modality flag, and the
factory-delegation in `EtlWitness.WalkRow` (EtlWitness.cs:33-39).
CONFIDENCE: high. The docstrings overstate convergence; report the prose as a defect per charter §0.

#### 7. `LanguageReference.cs:44-49` (`Resolve`) — MEDIUM — correctness / silent data degradation
CLAIM: On an unresolvable language input, `Resolve` silently substitutes `"und"` (undetermined) and
bumps a counter: `if (code is null) { Interlocked.Increment(ref _resolveMisses); code = "und"; }`.
Distinct unknown languages all collapse to one `language:und` entity — the "silently misses" pattern
CLAUDE.md warns about for ILI. `ResolveCode` (the lower call) correctly returns null; only the
id-minting wrapper forces "und".
VERIFIED: lines 44-49; `_resolveMisses` only readable via `ResolveMisses` (no hard fail).
CONFIDENCE: high. Mitigated (counter exists) but data is degraded silently per row.

#### 8. `SourceEntityIdConventions.cs:201-202` (`TatoebaSentence`) — MEDIUM — invention-violation (invariant 1/2: identity from provenance)
CLAIM: `TatoebaSentence(id) => OfCanonical($"tatoeba/sentence/{id}")` keys a sentence *entity* on its
source-specific numeric id (provenance), not on its content. The same sentence text ingested from
another source cannot converge with the Tatoeba node, and the namespace bakes the source into the id —
the precise thing invariant 1 forbids ("No source/position/index/name in any entity id").
VERIFIED: definition read; used by TatoebaGrammarWitness (grep). Also `EtlManifest.AtomicSplit` mints
`atomic/split/{stem}` ids — acceptable as a *context* id (provenance), but Tatoeba's is an *entity*.
CONFIDENCE: med-high. (Sentence linking needs a stable handle, but the architecture's answer is
content-address + attestation, not an id namespace.)

#### 9. `StructuredGrammarIngest.cs:220-225,250-256,481-486,506-512` — MEDIUM — correctness / silent scope-cut (logged to stderr)
CLAIM: When `laplace_grammar_compose` throws, the row is dropped with a `Console.Error.WriteLine
("[COMPOSE_SKIP] …")` + `continue`. Failed rows silently leave the corpus; the write goes to stderr,
not `IDecomposerContext.Logger`, so it bypasses the run's logging and any metric. At scale this can
drop arbitrary fractions of a source with no surfaced count.
VERIFIED: four catch sites read (serial + parallel paths). No counter, no logger.
CONFIDENCE: high.

#### 10. `JsonGrammarHelper.cs:278-286` (`ChildrenOf`) — MEDIUM — perf footgun
CLAIM: `ChildrenOf` scans ALL `ast.NodeCount` nodes to find children of one parent (linear filter on
`node.Parent == parent`). It is called inside `EnumerateObjectPairs`, `ObjectNodesInArray*`,
`FindNestedObject`, `PairKeyChild`, etc., which are themselves called per pair/child — making JSON
object walking O(n²) in AST node count. The deep Wiktionary sense/etymology trees (the 9 GB source)
are exactly where this bites; AUDIT notes corroborate an n² compose concern.
VERIFIED: method body + all callers traced in-file.
CONFIDENCE: high.

#### 11. `BootstrapIntentBuilder.cs:74-75` — LOW — correctness / misleading API
CLAIM: `AddRelationType(string name, double typeRank, double sourceTrust) => AddRelationType(name);`
silently discards the rank and trust arguments. A caller passing tuned rank/trust gets them ignored.
VERIFIED: one-line body.
CONFIDENCE: high.

#### 12. `RelationTypeRegistry.cs:112-121` (`ResolveDbpedia`) — LOW — invention-concern
CLAIM: dbpedia relations get a hardcoded `RelationTypeRank.Associative` rank and a managed-built
canonical `"DBPEDIA_" + …`, bypassing the native relation resolver used by every other path
(`RelationResolveSurface`). Minor altitude/consistency drift (rank decided in C#, not the manifest).
VERIFIED: method body vs the native `Resolve` (26-44).
CONFIDENCE: med.

#### 13. `DocumentDecomposer.cs:36-37` — LOW — perf / memory
CLAIM: `File.ReadAllBytesAsync(file)` loads each document fully into memory before witnessing — fine
for prompts, but contradicts the "STREAM huge files, never hold" mandate if pointed at large docs.
The `document` source is text-modality and not the 9 GB case, so impact is bounded.
VERIFIED: lines 36-49.
CONFIDENCE: high (behavior), med (impact).

#### 14. `IliMap.cs:38-49` — LOW — silent skip
CLAIM: Malformed/short lines are silently `continue`d while building the ILI map; a truncated map
yields fewer anchors with no warning (the file-level emptiness IS checked elsewhere via
`EvaluateCiliMap`, so a wholly-missing map is caught — only partial corruption is silent).
CONFIDENCE: med.

#### 15. `CompositionalTypes.cs:7-21` — INFO — text-centric abstraction
CLAIM: `IsCompositional` returns true only for the 5 *text* type ids (Codepoint…Document). Any
caller using it to decide "is this node compositional" will mis-classify code/chess/grammar
composite nodes (whose type ids are `substrate/type/grammar/{modality}/{name}/v1`). Narrow utility;
flag in case it gates generic logic.
CONFIDENCE: med (no harmful call site found in this bucket).

#### 16. `DocumentRouter.cs` — INFO — apparently unused by DocumentDecomposer
CLAIM: DocumentRouter (prose/code fence splitter) is a fully-built component, but DocumentDecomposer
routes whole-file bytes straight through `UserPromptContent.TryBuildWitnessChange` (the text composer)
and never calls DocumentRouter. Code spans inside markdown are therefore composed as prose, not routed
to a code grammar. Possible scope gap or dead helper.
VERIFIED: DocumentDecomposer.cs:45 path; no DocumentRouter reference in the bucket.
CONFIDENCE: med.

#### 17. `EtlWitness.cs:70-71` / `ContentWitnessBatch` — INFO — text-composer on field values (bounded)
CLAIM: `EdgeRoleKind.Content` field values are appended via `ContentWitnessBatch.TryAppendToBuilder`,
which runs the UAX29 text decomposer on the value. For lexical TSV fields (lemmas, words) this is
correct (they ARE text). It is the *general* mechanism that, if a future non-prose domain field were
mapped as `Content`, would reproduce the chess-style "string → hundreds of graphemes" category error
the charter warns about (invariant 7). No current misuse in the manifest; flag the sharp edge.
CONFIDENCE: med.

---

### Bucket summary
- CRITICAL: 1  (tier-as-kind `EntityTier.Vocabulary = 5`, wired into the framework's canonical vocabulary helper and every bootstrap)
- HIGH: 2  (C#-side PRECEDES bigram generator; invented-namespace anchors incl. `language:{iso3}`)
- MEDIUM: 7  (dead `BuildSequenceAttestations`; GrammarEntityBuilder/GrammarRowComposer fork; manifest-vs-bespoke fork + overstated docstrings; silent `und` language fallback; Tatoeba id-from-provenance; stderr compose-skip data drop; O(n²) JSON child scan)
- LOW: 4  (ignored AddRelationType params; hardcoded dbpedia rank; whole-file document read; IliMap partial-skip)
- INFO: 4  (text-only CompositionalTypes; unused DocumentRouter; text-composer-on-field edge; StreamingUtf8LineReader buffer reuse)

SINGLE WORST ISSUE: `EntityTier.Vocabulary = 5` (Finding 1). It is invariant 3's named live
violation, and because `VocabularyAnchor.Emit` — the file documented as "the single substrate-native
way to make a vocabulary entity" — hardcodes it, the depth-as-kind error is the *contract* every
decomposer inherits. Fixing it must be done here, not per-decomposer.

NOTE on epistemics: the `AGENT_003_REPORT.md` / `AUDIT-*.md` / `docs/*.md` strings calling
`EntityTier.Vocabulary` "fabricated" are, in this one case, consistent with the charter — but they are
still prior-agent prose; the finding above is grounded in the actual enum + call sites, not those docs.
