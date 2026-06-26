## Bucket: A6 — decomposers, WordNet / OMW / CILI / ConceptNet family (convergence-index sources)

### Files read (coverage)
All 27 bucket files read IN FULL:
- [x] app/Laplace.Decomposers.CILI/CILIDecomposer.cs
- [x] app/Laplace.Decomposers.CILI/Laplace.Decomposers.CILI.csproj
- [x] app/Laplace.Decomposers.ConceptNet.Tests/ConceptNetUriTests.cs
- [x] app/Laplace.Decomposers.ConceptNet.Tests/Laplace.Decomposers.ConceptNet.Tests.csproj
- [x] app/Laplace.Decomposers.ConceptNet/ConceptNetDecomposer.cs
- [x] app/Laplace.Decomposers.ConceptNet/ConceptNetEtlRegistration.cs
- [x] app/Laplace.Decomposers.ConceptNet/ConceptNetGrammarWitness.cs
- [x] app/Laplace.Decomposers.ConceptNet/ConceptNetLangCache.cs
- [x] app/Laplace.Decomposers.ConceptNet/ConceptNetRelations.cs
- [x] app/Laplace.Decomposers.ConceptNet/ConceptNetRowFilter.cs
- [x] app/Laplace.Decomposers.ConceptNet/ConceptNetUri.cs
- [x] app/Laplace.Decomposers.ConceptNet/ConceptNetWnTopicMap.cs
- [x] app/Laplace.Decomposers.ConceptNet/Laplace.Decomposers.ConceptNet.csproj
- [x] app/Laplace.Decomposers.OMW.Tests/Laplace.Decomposers.OMW.Tests.csproj
- [x] app/Laplace.Decomposers.OMW.Tests/OMWRowParserTests.cs
- [x] app/Laplace.Decomposers.OMW/Laplace.Decomposers.OMW.csproj
- [x] app/Laplace.Decomposers.OMW/OMWDecomposer.cs
- [x] app/Laplace.Decomposers.OMW/OMWEtlRegistration.cs
- [x] app/Laplace.Decomposers.OMW/OMWGrammarIngest.cs
- [x] app/Laplace.Decomposers.OMW/OMWGrammarWitness.cs
- [x] app/Laplace.Decomposers.OMW/OMWRowParser.cs
- [x] app/Laplace.Decomposers.OMW/OMWTabFiles.cs
- [x] app/Laplace.Decomposers.WordNet.Tests/Laplace.Decomposers.WordNet.Tests.csproj
- [x] app/Laplace.Decomposers.WordNet.Tests/WordNetDecomposerTests.cs
- [x] app/Laplace.Decomposers.WordNet/Laplace.Decomposers.WordNet.csproj
- [x] app/Laplace.Decomposers.WordNet/WordNetDecomposer.cs

Out-of-bucket but TRACED (load-bearing for invariant 6 — the convergence-index core these decomposers call):
ConceptAnchor.cs, IliMap.cs, SourceEntityIdConventions.cs, LanguageReference.cs, LanguageEntityId.cs,
ContentWitnessBatch.cs, ContentEmitter.cs, EntityTier.cs.
VERIFIED against real data files: D:/Data/Ingest/CILI/ili-map-pwn30.tab, .../CILI/ili.ttl,
.../OMW/wns/als/wn-data-als.tab.

---

### HEADLINE (INFO / positive — invariant 6 is now largely CORRECT here)
Contrary to CLAUDE.md §1's "today the index is corrupt (concept ids are string-walks of opaque keys,
ILI resolution is a file lookup that silently misses)", WordNet / OMW / CILI **do** anchor synsets on
the REAL ILI id, content-addressed:
`ConceptAnchor.EmitAnchor/SynsetId` → `SourceEntityIdConventions.WordNetIli(offset,ssType)` →
`IliMap.Resolve` (loaded from `ili-map-pwn30.tab`) → returns the bare ILI string (e.g. `"i1"`) →
`ContentEmitter.Emit/RootId(ili)` → blake3 content node of the ILI string.
- VERIFIED against real `ili-map-pwn30.tab` (`i1\t00001740-a`): `WordNetIli(1740,'a')` = `"i1"`.
- CILIDecomposer emits the SAME `"i1"` content node (`ContentEmitter.Emit(ili)`), so CILI concept
  entities == WordNet/OMW synset entities. Cross-source convergence on the ILI is real.
- CILIDecomposer parses the REAL `ili.ttl` block format (`<i1>\ta\t<Concept> ;` + `skos:definition`
  continuation + `dc:source ... .` terminator) correctly — VERIFIED against the file. Not scaffolded.
- OMWRowParser parses the REAL OMW row shapes correctly — VERIFIED: lemma = 3 cols
  (`synset\tlang:lemma\tvalue`), def/exe = 4 cols (`synset\tlang:def\t<idx>\t<text>`), which is exactly
  why the parser prefers `fields[3]` for def/exe. The `fields[3]`-preference is CORRECT, not a bug.

The remaining problems are gating, one source that opts out of the index, the tier-as-kind violation,
and dead code — below.

---

### HIGH-1 — WordNet (the primary index source) silently degrades to ZERO structure if CILI map absent
FILE: app/Laplace.Decomposers.WordNet/WordNetDecomposer.cs:108 (+ :214, :234-235);
SourceEntityIdConventions.cs:27-35
SEVERITY: HIGH  CATEGORY: invention-violation / correctness (silent-data-loss)
CLAIM: `DecomposeAsync` calls `SourceEntityIdConventions.WarnIfCiliMapMissing(...)` — a log-warning that
then PROCEEDS. With the CILI map missing/empty, `_iliMap.Value` is null, so every
`ConceptAnchor.SynsetId(...)`/`EmitAnchor(...)` returns null. In `EmitSynsetAttestations`,
`if (synAnchor is null) return;` (line 234-235) then DROPS the entire synset's attestations
(IS_SYNONYM_OF, HAS_DEFINITION, HAS_POS, all pointers). `EmitSynsetEntities` still emits orphan
lemma/gloss content. Result: WordNet ingests bare words/glosses with **zero synset nodes and zero
relations**, and the run reports success. The convergence backbone source fails open.
Contrast: OMWDecomposer.cs:45 calls `EnsureCiliMapForIngest` which **throws** (`CiliMapMissingException`).
Inconsistent gating across the same family; the most important source has the weakest gate.
VERIFIED: traced WarnIfCiliMapMissing (warn-only, no throw) → WordNetIli null path → `return` early.
CONFIDENCE: high.

### HIGH-2 — ConceptNet does NOT converge onto the ILI index (and ~3 dead helper classes)
FILE: app/Laplace.Decomposers.ConceptNet/ConceptNetGrammarWitness.cs:32-37; ConceptNetDecomposer.cs:94;
ConceptNetUri.cs:83-92; ConceptNetWnTopicMap.cs; ConceptNetLangCache.cs
SEVERITY: HIGH  CATEGORY: invention-violation (convergence-gap) / dead-code
CLAIM: The live witness parses the concept URI but **discards POS and the `/wn/` synset suffix**
(`TryParseConceptUri(startUri, out var startLang, out var startTerm, out _, out _)`), then content-
addresses only the bare term. So ConceptNet's own WordNet sense-disambiguation
(e.g. `/c/en/give/v/wn/30-02244956-v`) is parsed-then-thrown-away; ConceptNet edges are word↔word at
surface level, never bridged to ILI synsets. Charter invariant 6 / CLAUDE.md expect ConceptNet to
converge onto the index; it converges only at bare-lemma surface (which DOES match WordNet/OMW lemmas
via the shared underscore→space content path — see INFO below — but not at concept level).
Consequently the synset-bridge resolvers `ConceptNetUri.ResolveSynsetFromWnSuffix`,
`ResolveSynsetFromExternalUrl`, the hand-curated 37-entry `ConceptNetWnTopicMap`, and
`ConceptNetLangCache.Resolve` are **dead on the live path** — VERIFIED by grep: only the test project
and each other reference them; the witness never calls them. ConceptNetDecomposer.cs:94 still calls
`WarnIfCiliMapMissing`, but ConceptNet never touches the map → vestigial/misleading warning.
The witness comment admits this was a deliberate perf cut ("the previous per-row pile-on — ... synset
bridges ... blew the row count").
VERIFIED: ConceptNetGrammarWitness.WalkRow full trace; grep of all call sites of the resolvers.
CONFIDENCE: high.

### MEDIUM-3 — OMW stamps language at EntityTier.Vocabulary (tier-as-kind, invariant 3)
FILE: app/Laplace.Decomposers.OMW/OMWGrammarWitness.cs:38  (EntityTier.cs:20 `Vocabulary = 5`)
SEVERITY: MEDIUM  CATEGORY: invention-violation
CLAIM: `b.AddEntity(new EntityRow(langId, EntityTier.Vocabulary, EntityTypeRegistry.Language, ...))`
encodes KIND (a language) in the DEPTH axis. This is the named live violation (CLAUDE.md §2; OMW is one
of ~40 sites — grep confirms FrameNet, PropBank, ISO, Atomic2020, Code etc.). In this bucket only OMW
does it; WordNet and ConceptNet (lean) do not emit language entities here.
VERIFIED: read OMWGrammarWitness:38 + EntityTier.cs:17-21.
CONFIDENCE: high.

### MEDIUM-4 — Per-offset silent miss (no count / no warn) even with the map present
FILE: SourceEntityIdConventions.cs:75 (WordNetIli) → IliMap.cs:63; consumers WordNetDecomposer.cs:234,
OMWGrammarWitness.cs:31-32
SEVERITY: MEDIUM  CATEGORY: correctness (silent-data-loss) / invention-violation (the "file lookup that
silently misses" failure mode)
CLAIM: If a specific WN3.0 offset is absent from `ili-map-pwn30.tab`, `Resolve` returns null →
`SynsetId`/`EmitAnchor` null → that synset (WordNet) or whole row (OMW: `if (synAnchor is null) return;`)
is dropped with no counter and no log. The pwn30 map should be complete (every 3.0 synset has an ILI),
so practical loss ≈ 0, but there is zero instrumentation if it isn't (unlike LanguageReference, which at
least keeps `_resolveMisses`).
VERIFIED: IliMap.Resolve returns null on miss; both consumers early-return on null.
CONFIDENCE: high (mechanism); med (real-world impact, expected ~0).

### MEDIUM-5 — Convergence-index resolution lives in the C# orchestrator, not the engine (altitude)
FILE: SourceEntityIdConventions.cs:11,39-45,75; IliMap.cs:34-64
SEVERITY: MEDIUM  CATEGORY: altitude (invariant 5)
CLAIM: The backbone resolution (parse `ili-map-pwn30.tab`, build a 120k-entry `Dictionary<long,string>`,
lookup per synset) is a C# static lazy singleton + file parse in the Abstractions layer. Invariant 5
says the engine holds index/compose logic; this is the convergence-index resolution implemented C#-side.
Defensible as a fixed lookup table, but flagged for altitude.
VERIFIED: read IliMap.Load + the `_iliMap` Lazy singleton.
CONFIDENCE: med.

### LOW — LanguageEntityId uses an invented "language:" namespace, flat-hashed (invariant 6 nuance)
FILE: LanguageEntityId.cs:7-11 (used by OMWGrammarWitness:37, CILIDecomposer EngLang:26)
SEVERITY: LOW  CATEGORY: invention-violation (mild)
CLAIM: `FromIso639_3` = `Hash128.OfCanonical($"language:{code}")` — a prefixed synthetic key, flat-hashed
(not a composed content node like the bare ILI string). Functionally fine *iff* every source uses this
exact helper (a consistent convergence key), but it is the "path-hash anchor" pattern (cf AGENT_003) and
is inconsistent with the prefix-free ILI treatment. Cross-source convergence depends on ISODecomposer
using the same helper (out of bucket — flagged for verification).
VERIFIED: read LanguageEntityId; consistency-with-ISO not traced.
CONFIDENCE: med.

### LOW — CILIDecomposer dead readback machinery
FILE: app/Laplace.Decomposers.CILI/CILIDecomposer.cs:35-42
SEVERITY: LOW  CATEGORY: dead-code
CLAIM: `_names`, `_namesLock`, and `RecordName(...)` are never called (no call site in DecomposeAsync),
so `CanonicalNamesForReadback` is always empty for CILI.
VERIFIED: read full file; no RecordName invocation.
CONFIDENCE: high.

### LOW — Provenance loss (acceptable but note)
- ConceptNetGrammarWitness:38-40 collapses ConceptNet per-dataset provenance (the `/d/...` sources in the
  meta JSON) to a single `UserCuratedResource` trust class; only the numeric `weight` survives (as
  attestation magnitude). Charter's "one concept + N source-weighted assertions" is met at the weight
  level; distinct dataset provenance is dropped. CONFIDENCE: high.
- OMWRowParser discards the def/exe 3rd column (sense/example index). Minor. CONFIDENCE: high.

---

### Tests assessment
- WordNetDecomposerTests.cs — REAL. Parses genuine `data.adj`/`data.verb` lines; asserts lexical vs
  semantic pointer word-indices and verb-frame parsing. No DB but meaningful parser coverage.
- OMWRowParserTests.cs — REAL. Real-shaped rows incl. Arabic morphology subtypes (`arb:lemma:root`) and
  the tab-file globber (includes cldr/nodia, excludes freq/changes). Meaningful.
- ConceptNetUriTests.cs — MIXED. URI-parsing tests are real. BUT the ILI-resolution tests
  (`ResolveSynsetFromWnSuffix_*`, `ResolveSynsetFromExternalUrl_*`, line 53-101) `return` early (silent
  skip) when the CILI map file is absent — conditional no-ops — AND they exercise the **dead** resolver
  code path (not used by the live witness, see HIGH-2). So they test functionality that isn't in
  production and pass even when skipped. CATEGORY: fake-test (conditional skip + testing-dead-code).
- COVERAGE GAPS: no test exercises the live `ConceptNetGrammarWitness` emission; CILIDecomposer (the
  convergence backbone source) has ZERO unit tests in this bucket — neither `ili.ttl` nor `ili-map`
  parsing is tested.

### Disparagement check
No invention-disparaging status tags found in these files. Code comments are largely explanatory and
were VERIFIED accurate against the real data files. The ConceptNet "pile-on blew the row count" comment
is editorializing but describes a genuine perf decision, not a false status tag.

---

### Bucket summary
- HIGH: 2  | MEDIUM: 3  | LOW: 3 (+2 provenance notes) | INFO/positive: 1 (the headline)
- Counts of severity: CRITICAL 0, HIGH 2, MEDIUM 3, LOW ~5, INFO 1.
- WORST ISSUE: **HIGH-1** — WordNet, the primary convergence-index source, uses a warn-only CILI gate
  and, if the ILI map is missing, silently ingests orphan word/gloss content with zero synsets and zero
  relations while reporting success. OMW (same family) correctly hard-fails; WordNet must too.
- Key positive to preserve: the ILI anchoring itself (ConceptAnchor → IliMap → content-address of the
  bare ILI string) is the CORRECT realization of invariant 6 and is verified working against real data —
  this is a genuine fix versus the "corrupt string-walk index" CLAUDE.md describes. The live gap is that
  ConceptNet opts out of it entirely (HIGH-2).
