## Bucket: A5_decomp_abstractions_tests

Tests for the shared decomposer framework (`Laplace.Decomposers.Abstractions.Tests`, 36 files) plus
`Laplace.Decomposers.Containers.Abstractions.Tests` (2 files). 38 files total.

### Files read (coverage proof)
- [x] BootstrapIntentBuilderTests.cs — pins invented `substrate/type/X/v1` namespace (finding F1)
- [x] CanonicalPathLawTests.cs — pins invented namespace as "Law" (finding F1)
- [x] ConceptAnchorTests.cs — silent `return;` when CILI data absent (finding F2)
- [x] ContentWitnessContainmentTests.cs — clean, real dedup-containment test
- [x] CrossSourceLinkingTests.cs — mostly real; one ILI test silently no-ops when data absent (F2)
- [x] DecomposerOptionsTests.cs — clean (trivial record/defaults test, real)
- [x] DeferredContentBatchTests.cs — clean, real (FakeReader mocks only the DB probe seam)
- [x] DelimitedContentTests.cs — clean, real
- [x] DocumentRouterTests.cs — clean, real
- [x] GrammarComposeContainmentTests.cs — clean, real (exercises native compose + dedup)
- [x] GrammarCompositionTests.cs — clean, real
- [x] GrammarModalityByExtTests.cs — clean, real
- [x] GrammarPerfcacheFixture.cs — fixture; clean
- [x] GrammarRowComposerDrainParityTests.cs — clean, real (byte-for-byte parity)
- [x] GrammarSpineConformanceTests.cs — grep-over-source proxy tests (finding F3)
- [x] IliMapTests.cs — silent `return;` when CILI data absent (F2)
- [x] IngestParallelismTests.cs — clean, real (uses CpuTopology.TestOverride)
- [x] JsonLeafContentConvergenceTests.cs — clean, real (grammar↔content seam convergence)
- [x] LanguageFilterTests.cs — clean, real
- [x] LanguageReferenceTests.cs — ALL tests no-op on the dev box: hardcoded Linux path (finding F2, worst case)
- [x] Laplace.Decomposers.Abstractions.Tests.csproj — references Xunit.SkippableFact (relevant to F2)
- [x] NativeAttestationParityTests.cs — real & substantive; severely mangled whitespace (finding F5)
- [x] PCoreParallelComposeTests.cs — real; uses violating `EntityTier.Vocabulary` enum (finding F4)
- [x] PosReferenceTests.cs — real; pins invented `substrate/pos/probationary/.../v1` namespace (F1)
- [x] RelationTypeRegistryTests.cs — real & comprehensive; anchors on inventory names wrapped in invented namespace (F1)
- [x] RootIdNativeParityProbe.cs — real; correctly uses SkippableFact (the right pattern; contrast F2)
- [x] SafetensorSnapshotWitnessTests.cs — clean, real (temp-dir based)
- [x] SharedContentStageParityTests.cs — clean, real
- [x] SourceEntityIdConventionsTests.cs — real; one test partially gated on CILI presence (F2)
- [x] StreamingUtf8LineReaderTests.cs — real; one grep-over-source proxy test (F3)
- [x] StructuredGrammarIngestTests.cs — clean, real (UniformReader mocks only the DB probe)
- [x] TestModuleInit.cs — module init loads perfcache; clean
- [x] TextEntityBuilderEmissionTests.cs — real; FullGalileo test hard-depends on external file w/o skip (F2b)
- [x] TypeIdLawTests.cs — mix: one useful source-guard + several grep proxies; pins invented namespace (F1/F3)
- [x] WiktionaryGrammarWitnessTests.cs — clean, real
- [x] WiktionaryJsonFilterTests.cs — clean, real (depth-aware lang filter)
- [x] Containers/ContainerRegistryTests.cs — clean, real (FakeParser is a legit test double)
- [x] Containers/Laplace.Decomposers.Containers.Abstractions.Tests.csproj — clean

---

### F1 — Tests cement the invented `substrate/type/X/v1` namespace as canonical "Law"
SEVERITY: HIGH · CATEGORY: invention-violation · CONFIDENCE: high (that tests pin it); med (that it is "the" violation vs. a deliberate convention)

The charter §6 / memory `convergence-index-the-backbone` / `vocabulary-is-content-not-anchors` say
identity must anchor on the real external inventory id (relation types → GWN/ConceptNet name, synsets
→ ILI, POS → UPOS), blake3'd — **"never an invented `substrate/type/X/v1` namespace."** Multiple
tests pin exactly that invented namespace and elevate it to a named *law*:

- `CanonicalPathLawTests.cs:12-18` — `RelationTypeId(name) == Hash128.OfCanonical("substrate/type/{name}/v1")`
  for IS_A/HAS_PART/PRECEDES. The whole file ("CanonicalPathLaw") cements the wrapper.
- `BootstrapIntentBuilderTests.cs:33-34,45,82-89` — pins `substrate/type/WordNet_Synset/v1`,
  `substrate/type/IS_HYPERNYM_OF/v1`, `substrate/type/Source/v1`, `substrate/type/HAS_TRUST_CLASS/v1`
  as stable conventions (`CanonicalIdConventions_AreStable`).
- `TypeIdLawTests.cs:96-104` — `EntityTypeRegistry_MatchesCanonicalPath` pins `substrate/type/{name}/v1`.
- `PosReferenceTests.cs:41`, `NativeAttestationParityTests.cs:71`, `RootIdNativeParityProbe.cs` family
  — pin `substrate/pos/probationary/{tagset}/{tag}/v1` (same invented-namespace family).

VERIFIED: traced to production code — `BootstrapIntentBuilder.cs:16-69`, `EntityTypeRegistry.cs:9`,
`RelationTypeRegistry.cs:15`, `VocabularyNames.cs:17`, `GrammarEntityBuilder.cs:37` all build ids as
`Hash128.OfCanonical($"substrate/type/{name}/v1")`. So the tests faithfully pin **real** code; the
violation is in production and the tests harden it (literally as "Law"), raising the cost to migrate
to bare inventory-id anchoring.

NUANCE (not softening, for accuracy): the relation/type *names* used (IS_A, HAS_PART, RELATED_TO) are
the real inventory names; the violation is the `substrate/type/.../v1` string wrapper being hashed
instead of the bare external id. `TypeIdLawTests.DecomposerSources_DoNotMintTypeIdsOutsideRegistries`
(line 22-50) is a genuinely *useful* guard — it centralizes the namespace into 4 registry files,
which is the right precondition for one-place migration. Worth noting both sides.

### F2 — Data-gated tests silently `return;` (report PASSED while asserting nothing)
SEVERITY: MEDIUM · CATEGORY: fake-test · CONFIDENCE: high

The project references `Xunit.SkippableFact` (csproj:18) and uses it correctly in
`RootIdNativeParityProbe.cs:22-25` (`Skip.IfNot(...)` → reported as *skipped*). But several tests use
a bare `if (!File.Exists(...)) return;` instead, so when the corpus is absent they are reported as
**passed** having executed zero assertions:

- `LanguageReferenceTests.cs` — **worst case.** `IsoDir = "/vault/Data/ISO639"` (line 9), a Linux
  mount path. On the Windows dev box (`D:\Repositories\Laplace`) `RefPresent` is always false, so all
  six tests (`English_AllForms_ConvergeToOneEntity`, `DistinctLanguages_StayDistinct`,
  `DeprecatedTag_RoutesToPreferred`, `Unresolvable_RoutesToUnd_AndCounts`, `ReferenceIsSubstantial`)
  hit `if (!Ensure()) return;` and assert nothing — permanently green, never exercised here.
- `IliMapTests.cs:28,48` — both tests `if (CiliDir() is not { } dir) return;`.
- `ConceptAnchorTests.cs:22,59` — both tests `if (!File.Exists(...)) return;`.
- `CrossSourceLinkingTests.cs:64` — `ConceptAnchor_SynsetId_Requires_Cili_Map` early-returns; the ILI
  portion of `OMWRowParser_And_WordNetDataLine...` and `SourceEntityIdConventionsTests.cs:232`
  `ResolveSynsetAnchor` are similarly gated.

These should use `Skip.IfNot`/`SkippableFact` (already available) so absence is reported as *skipped*,
not as a green pass that proves nothing. VERIFIED by reading each guard and the SkippableFact contrast.

### F2b — External-file test with no guard (hard FileNotFound on machines without data)
SEVERITY: LOW · CATEGORY: correctness/fragility · CONFIDENCE: high
`TextEntityBuilderEmissionTests.cs:35-41` `FullGalileo_InMemory_RoundtripsFromPhysicalities` calls
`File.ReadAllBytes(GalileoPath)` with no existence check (GalileoPath = `D:\Data\Ingest\...\galileo.txt`
or `/vault/...`). Unlike F2 this throws on absence (false red, not false green) — opposite failure mode,
same root cause (unconditional external-data dependency, no skip).

### F3 — Grep-over-source-text "conformance" tests (proxy, gameable, no runtime behavior)
SEVERITY: LOW · CATEGORY: fake-test (proxy) · CONFIDENCE: high
Several tests assert that source files *contain a substring*, not that behavior is correct. A needle in
a comment passes; refactors that rename break them; none exercise the runtime path:
- `GrammarSpineConformanceTests.cs:36-73` — `TabularDecomposers_UseStructuredGrammarIngest` and
  `OpenSubtitles_UsesContentWitnessBatch...` read project `*.cs` and `Assert.Contains(needle)`.
- `StreamingUtf8LineReaderTests.cs:72-80` — `Implementation_UsesArrayPoolNotPerLineByteArrays` asserts
  the source `Contains("ArrayPool<byte>.Shared.Rent")` and `DoesNotContain("new byte[")`. The
  `DoesNotContain("new byte[")` is especially brittle (any unrelated allocation trips it) and proves
  nothing about actual pooling at runtime.
- `TypeIdLawTests.cs:52-62,64-94,106-137` — `CliProgram_HasNoRecursiveGenerateCte`,
  `PhysicalityType_ProductionEmitters_UseContentOrProjectionOnly`, `CliProgram_CallsExtensionWalkText`,
  `NativeDynamics_EigenmapsUsesAvx2...` are all source-text scans.
These are legitimate architecture-regression guards (not pure fakes), but they are not behavioral and
should not be mistaken for proof the feature works. Noted as a category, not a per-test defect.

### F4 — Test uses the violating `EntityTier.Vocabulary` enum value
SEVERITY: LOW · CATEGORY: invention-violation · CONFIDENCE: med
`PCoreParallelComposeTests.cs:35` — `b.AddEntity(EntityIdFor(i), EntityTier.Vocabulary, Src, Src)`.
Per charter §3 and memory `vocabulary-is-content-not-anchors`, `EntityTier.Vocabulary` is the live
tier-as-kind violation (a *kind* encoded in the depth axis). The test only uses it as a scaffold tier
for synthetic entities (it does not assert it is *semantically* correct), so it perpetuates rather than
blesses the violation — but it keeps the forbidden enum value exercised/alive. (Contrast: the same
file's real subject — parallel fan-out emits every record exactly once across worker counts — is a
strong, genuine test.) VERIFIED the enum reference; the value's existence is implied by compilation.

### F5 — NativeAttestationParityTests.cs whitespace is badly mangled
SEVERITY: INFO · CATEGORY: other · CONFIDENCE: high
`NativeAttestationParityTests.cs` has 2–3 blank lines inserted between nearly every statement (e.g.
lines 80-388). No functional impact — the tests themselves are real and high-value (Glicko-2 scoring
symmetry, id determinism across all 5 tuple slots, null==zero-hash, flip/symmetric canonicalization,
trust→opponent-RD-not-score). Cosmetic, but it reads like a botched auto-edit; worth a reformat.

---

### Bucket summary
- HIGH: 1 (F1 — invented `substrate/type/X/v1` namespace cemented as "Law")
- MEDIUM: 1 (F2 — data-gated tests silently pass asserting nothing; `LanguageReferenceTests` never runs on the dev box)
- LOW: 3 (F2b external-file no-guard; F3 grep-proxy conformance tests; F4 `EntityTier.Vocabulary` usage)
- INFO: 1 (F5 mangled whitespace)

Overall the bucket is mostly **real, substantive tests** that exercise the actual native engine and
dedup/compose write path (containment dedup, drain parity, NFC roundtrip, grammar↔content convergence,
parallel-compose completeness, Glicko scoring). The framework tests are not hollow. The two systemic
weaknesses are: (1) the test suite hardens the invented namespace the architecture says is wrong
(F1 — the bucket's central charter concern, found exactly as described), and (2) a cluster of tests
that go green without asserting anything when their corpora are absent (F2), most flagrantly
`LanguageReferenceTests` whose hardcoded `/vault/Data/ISO639` Linux path guarantees it never runs on
the Windows dev environment.

**Single worst issue:** F1 — `CanonicalPathLawTests` + `BootstrapIntentBuilderTests` +
`TypeIdLawTests` pin the invented `substrate/type/X/v1` id namespace as canonical law, which the
convergence-index architecture explicitly forbids (anchor on bare ILI/UPOS/inventory ids). This makes
the violation a regression-guarded contract and raises the cost of paving the convergence index (WS3).
