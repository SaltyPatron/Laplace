## Bucket: A9 — Model/Code/Image/Audio decomposers (+ tests)

### Files read (coverage proof)
- [x] app/Laplace.Decomposers.Audio/AudioDecomposer.cs — STUB (yield break)
- [x] app/Laplace.Decomposers.Audio/Laplace.Decomposers.Audio.csproj
- [x] app/Laplace.Decomposers.Code/CodeDecomposer.cs
- [x] app/Laplace.Decomposers.Code/Laplace.Decomposers.Code.csproj
- [x] app/Laplace.Decomposers.Code/RepoDecomposer.cs
- [x] app/Laplace.Decomposers.Code/StackDecomposer.cs
- [x] app/Laplace.Decomposers.Code/TabularDecomposer.cs
- [x] app/Laplace.Decomposers.Code/TinyCodesDecomposer.cs
- [x] app/Laplace.Decomposers.Image/ImageDecomposer.cs — STUB (yield break)
- [x] app/Laplace.Decomposers.Image/Laplace.Decomposers.Image.csproj
- [x] app/Laplace.Decomposers.Model.Tests/Laplace.Decomposers.Model.Tests.csproj
- [x] app/Laplace.Decomposers.Model.Tests/ModelTokenEdgeETLTests.cs — real test (needs native libs)
- [x] app/Laplace.Decomposers.Model.Tests/TokenRoleTests.cs — real unit tests
- [x] app/Laplace.Decomposers.Model/ArchitectureProfile.cs
- [x] app/Laplace.Decomposers.Model/HeadClassifier.cs
- [x] app/Laplace.Decomposers.Model/Laplace.Decomposers.Model.csproj
- [x] app/Laplace.Decomposers.Model/LlamaRecipeExtractor.cs
- [x] app/Laplace.Decomposers.Model/LlamaTokenizerParser.cs
- [x] app/Laplace.Decomposers.Model/ModelConfigReader.cs
- [x] app/Laplace.Decomposers.Model/ModelDecomposer.cs
- [x] app/Laplace.Decomposers.Model/ModelManifest.cs
- [x] app/Laplace.Decomposers.Model/ModelPathSpec.cs
- [x] app/Laplace.Decomposers.Model/ModelTokenEdgeETL.cs
- [x] app/Laplace.Decomposers.Model/RecipeDecomposer.cs
- [x] app/Laplace.Decomposers.Model/RecipeDescriptor.cs
- [x] app/Laplace.Decomposers.Model/RecipeExtractor.cs
- [x] app/Laplace.Decomposers.Model/RecipeSynthesizer.cs
- [x] app/Laplace.Decomposers.Model/SafetensorsContainerParser.cs
- [x] app/Laplace.Decomposers.Model/SvdExactBench.cs — dev/bench console utility
- [x] app/Laplace.Decomposers.Model/TensorRoleClassifier.cs
- [x] app/Laplace.Decomposers.Model/WeightTensorETL.cs
- [x] app/Laplace.Decomposers.Model/dev-recipes/chat-rebuild-v1.json — fixture

---

### Findings

#### F1 — AudioDecomposer is a non-functional stub presented as a registered decomposer
- FILE:LINE: app/Laplace.Decomposers.Audio/AudioDecomposer.cs:35-41
- SEVERITY: HIGH
- CATEGORY: invention-violation / dead-code
- CLAIM: `DecomposeAsync` is an empty iterator (`#pragma warning disable CS1998 ... yield break;`). `EstimateUnitCountAsync` returns null. The class registers a `SourceId`, `LayerOrder=12`, a trust class, and bootstraps types (`Audio_Sample/Frame/Track/Voice`) + relations (`IS_AT_SAMPLE`, `HAS_FREQUENCY_PEAK`, `TRANSCRIBES_AS`) in `InitializeAsync`, so it appears in the decomposer registry as a working source but ingests zero data. Answers the bucket question: Audio is a SCAFFOLD, not a real decomposer.
- VERIFIED: read full file; `InitializeAsync` (lines 20-32) writes the bootstrap intent, `DecomposeAsync` (35-41) only does `yield break`.
- CONFIDENCE: high

#### F2 — ImageDecomposer is a non-functional stub presented as a registered decomposer
- FILE:LINE: app/Laplace.Decomposers.Image/ImageDecomposer.cs:37-44
- SEVERITY: HIGH
- CATEGORY: invention-violation / dead-code
- CLAIM: identical pattern to F1 — `DecomposeAsync` is `yield break`, `EstimateUnitCountAsync` returns null, but `InitializeAsync` bootstraps `Pixel/Patch/Region/Image/Image_Collection` types and `DEPICTS/CAPTIONS/IS_PIXEL_OF/HAS_COLOR/ADJACENT_TO_PIXEL` relations and the class is wired with `LayerOrder=11`. Image is a SCAFFOLD, not a real decomposer.
- VERIFIED: read full file.
- CONFIDENCE: high

#### F3 — `EntityTier.Vocabulary = 5` (tier-as-kind) used pervasively across the Model + Code decomposers
- FILE:LINE: RepoDecomposer.cs:84; TabularDecomposer.cs:68,160,172,188,224; LlamaRecipeExtractor.cs:92,102,113; RecipeExtractor.cs:82,87; LlamaTokenizerParser.cs:330,421; ModelDecomposer.cs:223; HeadClassifier.cs:92  (definition: app/Laplace.SubstrateCRUD/EntityTier.cs:20 `public const byte Vocabulary = 5;`)
- SEVERITY: HIGH
- CATEGORY: invention-violation
- CLAIM: Invariant 3 — tier is compositional depth ONLY (codepoint=0, `tier=max(child)+1`); KIND belongs in `type_id`/physicality/trust. Every entity these decomposers mint as an "anchor" (repo root, tabular column/value/outcome/pair, model recipe, scalar, tokenizer entity, fallback word tokens, merge sides, circuit) stamps the fabricated depth value `5` instead of an emergent tier. This is the live violation named in CLAUDE.md, here instantiated ~15 times in this bucket. Note: in LlamaTokenizerParser line 330/421 it is the *fallback* path when `TryBuildTreeRows` fails (the real composed-tree path on line 325-327 emits correct emergent tiers), so the tokenizer at least tries the right thing first; the recipe/tabular/repo/circuit anchors do not.
- VERIFIED: confirmed enum value at EntityTier.cs:20; traced each call site reads `EntityTier.Vocabulary` as the tier arg of `new EntityRow(...)` / `AddEntity(...)`.
- CONFIDENCE: high

#### F4 — TabularDecomposer is scaffolded to one specific dataset (Kaggle bank-churn); wrong CSV silently yields all-negative outcomes
- FILE:LINE: app/Laplace.Decomposers.Code/TabularDecomposer.cs:41 (ctor defaults `targetColumn="Exited", positiveValue="1"`), :132 (`bool positive = rec.TryGetValue(_targetColumn, out var tv) && tv.Trim() == _positiveValue;`)
- SEVERITY: MEDIUM
- CATEGORY: correctness / invention-violation (imagined-format scaffolding)
- CLAIM: The target column name and positive value are hardcoded to the "Churn_Modelling.csv" Kaggle set. If pointed at any other CSV, `_targetColumn` is absent from every row, so `positive` is always false, and every `PREDICTS` attestation is folded with `sumScoreFp = M*FpScale = 0` (line 192-194, 226-228) — i.e. it ingests a full pairwise feature graph whose consensus signal is uniformly "never predicts", silent garbage with `status=ok`. The `IdLike` set (`id/customerid/rownumber`, line 33-34) is likewise dataset-specific.
- VERIFIED: traced ctor defaults → `_targetColumn`/`_positiveValue` usage in the count loop; `featureCols` (line 98-100) only excludes the target by name, so a missing target just means no positive labels.
- CONFIDENCE: high (behavior); med (whether it is ever constructed with non-default args — defaults are what the registry would use)

#### F5 — TabularDecomposer loads the entire CSV into RAM and does O(features²) pairwise stats in C#
- FILE:LINE: app/Laplace.Decomposers.Code/TabularDecomposer.cs:81-95 (`var rows = new List<Dictionary<string,string>>(); ... rows.Add(rec)`), :144-150 (nested pair loop into `counts2`)
- SEVERITY: MEDIUM
- CATEGORY: perf / altitude
- CLAIM: Violates invariant 7's "peak RAM O(batch + fixed tables), independent of corpus size" — the whole table is materialized as a `List<Dictionary<string,string>>`, then quantiles/cardinality/per-value counts/pairwise co-occurrence are all computed in managed C# (the heavy lifting that invariant 5 says belongs in native libs). Also `vals.Distinct()`/`vals.Where(...).Select(...)` re-scan the full column repeatedly inside the `foreach (c in featureCols)` loops (lines 104-124). Acceptable only because the intended corpus is tiny; architecturally it is O(corpus) RAM + O(rows·features²) compute in the orchestrator.
- VERIFIED: read full DecomposeAsync.
- CONFIDENCE: high

#### F6 — ModelDecomposer mixes two write lanes: direct `Writer.ApplyAsync` calls inside the streaming `DecomposeAsync`
- FILE:LINE: app/Laplace.Decomposers.Model/ModelDecomposer.cs:181-185, 197-198, 224, 237, 249 (direct `context.Writer.ApplyAsync(...)`) vs 266-270 (`yield return change`)
- SEVERITY: MEDIUM
- CATEGORY: fork / invention-violation
- CLAIM: The IDecomposer contract is to *yield* `SubstrateChange`es for the host to apply (ordered/partitioned/batched). ModelDecomposer instead writes the legacy recipe scalars, the synthesized recipe, the tokenizer entity, the vocab batches, and the merge batches *directly* via `context.Writer.ApplyAsync` from within `DecomposeAsync`, then *also* yields the token-edge changes. Two write paths in one decomposer (the "converge, don't fork" smell) — the directly-applied entities bypass whatever Hilbert-ordering/partition discipline the host applies to yielded changes.
- VERIFIED: traced the two mechanisms in DecomposeAsync; other decomposers in the bucket (Code/Stack/TinyCodes/Repo/Tabular/Recipe) only `yield return`.
- CONFIDENCE: high (that two lanes exist); med (whether host treats them differently — depends on the orchestrator, outside this bucket)

#### F7 — Circuit/architecture/special-token entity ids bake name/position into the id via invented `substrate/...` namespaces
- FILE:LINE: HeadClassifier.cs:40-45 (`Hash128.OfCanonical($"substrate/entity/{_modelName}/circuit/{layer}{head}.{c.Plane}/v1")`), :25 (`substrate/type/Model_Circuit/v1`); ModelDecomposer.cs:63-66 (`substrate/entity/Architecture_Llama/v1`); LlamaTokenizerParser.cs:80 (`substrate/token/special/{raw}/v1`); TabularDecomposer.cs:55-57,223 (`tabular/column/{col}/v1`, `tabular/value/{col}={tok}/v1`, `tabular/pair/{pa}={ta}&{pb}={tb}/v1`)
- SEVERITY: MEDIUM
- CATEGORY: invention-violation
- CLAIM: Invariant 1 — no source/position/index/name/order in any entity id; invariant 6 — concepts anchor on real external ids, never an invented `substrate/type/X/v1` namespace. `CircuitEntityId` embeds model name + layer index + head index + plane string. The tabular `pair` id is a string-walk of two `col=tok` keys (composition encoded as a formatted string, not a content hash of constituent ids). These are deliberate "anchor" ids; the comments (TabularDecomposer 161-180, 220-222; RepoDecomposer 73-76) argue the *content* (column/value names) is emitted separately and linked via IS_INSTANCE_OF, so the anchor is structural — but the anchor id itself still carries name/position, the exact pattern the invention forbids.
- VERIFIED: traced each `Hash128.OfCanonical` interpolation.
- CONFIDENCE: high (literal violation); med (some are arguably acceptable provenance anchors per existing design debate — see memory `vocabulary-is-content-not-anchors`)

#### F8 — Blanket `catch { continue; }` silently drops whole files / parse failures across all code decomposers
- FILE:LINE: CodeDecomposer.cs:54 (`catch { continue; }` read), :72-75 (`catch { continue; }` parse); RepoDecomposer.cs:108,124; StackDecomposer.cs:130,145; TinyCodesDecomposer.cs:105,121
- SEVERITY: MEDIUM
- CATEGORY: correctness (swallowed exception / silent scope-cut)
- CLAIM: A grammar that throws for an entire language, or an I/O fault, is caught with a bare `catch { continue; }` and the unit is skipped with no log, no counter, no status. If `GrammarDecomposer.Parse`/`GrammarEntityBuilder.BuildAsync` regress for one modality, every file of that language is dropped silently while the run reports success — the "near-zero entities, status=ok" failure mode flagged in memory `decomposers-scaffolded-to-imagined-formats`.
- VERIFIED: read each catch block; none log or count.
- CONFIDENCE: high

#### F9 — Model edge ETL: code-path is correct re invariants 2/4/5 and 7 (NOT routed through text composer) — positive finding
- FILE:LINE: ModelTokenEdgeETL.cs:328-329, 364-366 (`NativeAttestation.Aggregated(..., games:1, sumScoreFp, weight)`), :207-216/392-437 (native DynInterop/SynInterop kernels for SVD/projection/bilinear/ffn), :89-98 (dedup tokens onto distinct content entity ids)
- SEVERITY: INFO
- CATEGORY: invention-violation (compliance note)
- CLAIM: Contrary to the broad worry, the model path does NOT re-route domain content through the UAX29 text composer: weights are decoded and the heavy math runs in the native libs (invariant 5 satisfied), each circuit pair is folded as a single Glicko game streamed inline (invariant 4), and identical-content tokens collapse to one entity id (invariant 2). `WeightTensorETL`/`SafetensorsContainerParser` deposit nothing — weights are input only. This is genuinely-built, not scaffold. (The tokenizer vocab DOES go through `TextDecomposer.Run` + `HashComposer` per token in LlamaTokenizerParser.TryBuildTreeRows:163 — that is the correct grammar compose for *text* tokens, not a misuse.)
- VERIFIED: traced EmitAsync planes → native interop → Aggregated attestations; WeightTensorETL has no deposit.
- CONFIDENCE: high

#### F10 — TensorRoleClassifier / ModelConfigReader are real generic shape-inference, not imagined-format scaffolding — positive finding
- FILE:LINE: TensorRoleClassifier.cs:66-145 (shape-vs-config "magic number" classification with name tiebreak); ModelConfigReader.cs:62-117 (multi-alias key fallback, coverage verdict)
- SEVERITY: INFO
- CATEGORY: other (compliance note)
- CLAIM: These generalize across model_type by matching tensor shapes against config anchors (V, d, H·hd, Hkv·hd, I, E) and tolerate missing fields (`Coverage` verdict, never throw). This is the opposite of the "scaffolded to an imagined format" anti-pattern; it is robust real code. Heavy logic in C# (altitude-borderline) but it is lightweight metadata classification, not numeric heavy lifting.
- VERIFIED: read both files in full.
- CONFIDENCE: high

#### F11 — LlamaRecipeExtractor throws on any missing required config field, but is wrapped in a swallow at the call site (dead-ish legacy lane)
- FILE:LINE: LlamaRecipeExtractor.cs:133-140 (`GetIntRequired` throws); ModelDecomposer.cs:178-190 (`try { LlamaRecipeExtractor.Parse... } catch { log.LogWarning(...) }`)
- SEVERITY: LOW
- CATEGORY: dead-code / fork
- CLAIM: Two parallel recipe lanes coexist: the "legacy HF config-scalar deposit" (`LlamaRecipeExtractor`, hardcoded Llama keys, throws on missing field) and the generic `ModelConfigReader`+`RecipeSynthesizer` lane (Lane A/D). The comment at ModelDecomposer.cs:176-177 calls the first "legacy"; it is kept only as best-effort and its exceptions are swallowed. This is an un-retired fork — the generic lane supersedes it.
- VERIFIED: both lanes present in ModelDecomposer.DecomposeAsync (180-204).
- CONFIDENCE: med (functionally redundant; "legacy" is the code's own label, treated as a code-smell not as truth)

#### F12 — `oV` output buffers allocated and filled by native kernels but never read
- FILE:LINE: ModelTokenEdgeETL.cs:315 (`var oV = new double[cap]`), :351, passed to RunFfnTile/RunBilinearTile (407,423) but only `oS` (score-fp) consumed in the fold loops (329,365)
- SEVERITY: LOW
- CATEGORY: perf / dead-code
- CLAIM: A `double[cap]` (cap = 256·n) is allocated per circuit and written by the native kernel, but the C# only uses `oS` (the fixed-point score). The `oV` raw-value buffer is pure overhead (allocation + native write bandwidth) for every plane/layer/head.
- VERIFIED: grepped usages of `oV` in the file — only declarations and fixed/ptr passes, never indexed read.
- CONFIDENCE: high

#### F13 — ModelTokenEdgeETLTests comment references RELATED_TO while asserting SIMILAR_TO (stale/contradictory comment)
- FILE:LINE: app/Laplace.Decomposers.Model.Tests/ModelTokenEdgeETLTests.cs:64-66 (reconciles to SIMILAR_TO) vs :87 (`// RELATED_TO is symmetric...`) and :30/:35 mention RELATED_TO
- SEVERITY: LOW
- CATEGORY: disparagement-adjacent / other (stale comment)
- CLAIM: The test correctly asserts `SIMILAR_TO`, but leftover comments still say `RELATED_TO`, a prior relation name. The test itself is real (writes a true safetensors via WriteSafetensors, runs the ETL through native SVD/projection, asserts within-cluster > cross-cluster separation and that no entities/physicalities are emitted) — it requires the native libs to pass, so it is not a fake/no-op test. Only the comments are stale.
- VERIFIED: read full test; assertions on lines 69-99 are substantive.
- CONFIDENCE: high

#### F14 — SvdExactBench is a dev-only console utility not wired into ingest
- FILE:LINE: app/Laplace.Decomposers.Model/SvdExactBench.cs:16 (`public static bool Run(string modelDir...)` writes to `Console.WriteLine`)
- SEVERITY: INFO
- CATEGORY: dead-code
- CLAIM: A benchmark that loads one tensor and checks SVD reconstruction residual, printing to stdout. Not part of any DecomposeAsync path; a developer tool. Harmless but worth noting as non-product code shipped in the decomposer assembly.
- VERIFIED: no references from the decomposer flow; only `Run` entry point.
- CONFIDENCE: med (no caller found in this bucket; a CLI elsewhere may invoke it)

#### F15 — RepoDecomposer puts an absolute on-disk path into an entity id (deliberate, documented)
- FILE:LINE: app/Laplace.Decomposers.Code/RepoDecomposer.cs:77-84 (`repo:{Path.GetFullPath(root)}/v1` → `Hash128.OfCanonical` → `EntityRow(..., EntityTier.Vocabulary, ...)`)
- SEVERITY: LOW
- CATEGORY: invention-violation
- CLAIM: The repo-root id is the machine-specific absolute path. The comment (73-76) argues this is intentional "local provenance" that must not converge across machines. Per invariant 1 (no source/position/name in id, provenance lives in attestations) this is still an id carrying provenance; the principled form would be a content/anchor entity with the path as an attestation. Combined with F3 (Vocabulary tier).
- VERIFIED: read lines 77-84.
- CONFIDENCE: high (literal); the design intent is documented so it is a knowing trade-off

---

### Bucket summary
- CRITICAL: 0
- HIGH: 3 (F1 Audio stub, F2 Image stub, F3 tier-as-kind pervasive)
- MEDIUM: 5 (F4 tabular dataset-scaffold, F5 tabular RAM/altitude, F6 model two write lanes, F7 invented-namespace ids, F8 silent catch-drop)
- LOW: 5 (F11 legacy recipe fork, F12 oV dead buffer, F13 stale test comment, F15 repo path-in-id) — plus F14
- INFO: 3 positive/compliance (F9 model edge path correct, F10 classifier real, F14 bench dead-code)

Worst issue: tie between **F1/F2** (Audio and Image are fully non-functional `yield break` stubs that nonetheless register as live sources with bootstrapped types/relations — the bucket's central question answered: they are scaffolds, not decomposers) and **F3** (the `EntityTier.Vocabulary = 5` tier-as-kind violation is reproduced ~15× across the Model and Code decomposers). F4 is the most insidious *correctness* bug: TabularDecomposer silently produces an all-negative prediction graph on any CSV other than the hardcoded Kaggle churn set, reporting success.

Note on the Model decomposer specifically: despite model i/o being the excepted area, it is the *best-built* code in this bucket — generic shape inference (F10), native heavy-lifting (F9), real tests (F13). Its violations are the shared tier-as-kind (F3), invented-namespace circuit/architecture ids (F7), and the dual write-lane (F6), not scaffolding.
