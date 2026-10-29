# Model ingestion — current design contract


This document defines how conventional checkpoints enter Laplace. It supersedes the 2026-08 audit-era reduction that said a checkpoint was “not content,” that all useful model information was reducible to token-pair votes, and that model ingestion should be described through the pre-COUPLE forward-pass sequence.

Historical measurements and the old model-lane audit remain available under `docs/archive/` and Git history. They are useful counterexamples, not current invention authority.

The machine is `docs/INVENTION.md`, `docs/INVENTIONS.md`, `docs/specs/36_Laplace_Forward_Pass.md`, `docs/specs/37_Substrate_Operation_ISA.md`, and `AGENTS.md`.

---

## 1. A checkpoint has exact artifact identity **and** is an attributed source

A conventional model artifact has several simultaneous roles that must not be flattened:

```text
checkpoint bytes / files                 exact artifact fingerprint/occurrence; payload remains source packaging
config/tokenizer/architecture            exact structured content / references
layers/heads/experts/tensors/components  addressable source structure
numeric values / slices / factors        transient decode/calculation operands; derived physicality/evidence
model-local role/location                occurrence / source-scoped structural coordinate
separately requested model execution      optional deterministic/versioned calculation trajectory
induced semantic/circuit effects         calculated / witnessed provider evidence
model claims/completions                 source-attributed observations/testimony when admitted
```

The checkpoint is therefore not an opaque runtime authority, but neither is it “nothing but testimony.” Its exact bytes establish artifact identity/provenance without requiring the payload bytes themselves to become durable substrate storage. Its tokenizer/config/header/component structure is decomposable content. Weight values may be consumed transiently to derive circuit physicalities and evidence without being retained as raw weights or making bit-perfect checkpoint reconstruction an ingest goal.

### Durable model representation is physicality/trajectory, not tensor payload

The durable circuit representation is Laplace-native. A circuit/layer/head/expert or
other admitted source structure is a canonical entity/composition with a typed
physical realization. Ordered salient/coupled canonical entities are retained in that
physicality trajectory; placement supplies coordinate/locality state such as centroid
and Hilbert index; source-scoped claims produced by the numeric reduction are retained
as typed evidence.

The current `ModelTokenEdgeETL.BuildCircuitObservation` writes a
`PhysicalityType.Projection` with `TrajectoryXyzm`, `CoordX/Y/Z/M`,
`HilbertIndex`, and `NConstituents`. Those fields and a receipt saying
`raw_weight_bytes_retained=0` do not establish faithful model ingestion.
The current circuit walks summarize per-token salience; they do not retain a
contextual attention/FFN computation that a native SQL forward pass can execute.
This is an implementation gap, not a completed representation or an ingestion
failure attributable to the model's vocabulary or the inventor's requirements.

This is not generic binary-blob storage. SafeTensors/GGUF/checkpoint files are source
packaging used by the decomposer. Tensor bytes and raw weight arrays may be decoded
inside the bounded ingest calculation, but they do not become the durable model
ontology, a payload column, or a reconstruction archive. If a future implementation
needs an opaque source artifact for provenance, that artifact identity/occurrence is
separate from the Laplace-native circuit representation and must not be queried as the
semantic model state.

Laplace must preserve enough decomposed structure to answer what source structure was observed, where a component occurs, and which canonical entities/paths that component couples to under the declared decomposition recipe. A separately requested model-execution measurement may also be witnessed, but executing prompts through the source model is not model ingestion.

The requested serving result is stronger than circuit inspection: after ingestion,
PostgreSQL must be able to execute a native forward operation over the admitted
Laplace records and produce responses using that model's learned computation.
Reading the checkpoint, reconstituting its tensors for an external inference
runtime, returning circuit summaries, or querying stored sample completions does
not demonstrate that result. Comparisons with a source runtime are measurements
of the implementation; they are not the serving implementation.

The ingestion input is the complete SafeTensors model package: configuration,
tokenizer and its declared decoding rules, all weight shards and their index where
present, and result-affecting metadata. A single tensor file is not the entire
model. These package inputs remain distinct from the canonical content and the
source occurrence that owns their provenance.

Recover the source's logical structure before deriving effects. SafeTensors shape,
dtype and data offsets describe flattened arrays; their row/column/higher-axis
slices can be decoded on the CPU in bounded working windows. This structural
decomposition does not need dimensionality reduction, pattern-based record
elimination, a GPU, or one durable record per scalar. Relational normalization is
the inventor's analogy: recover constituents and their wiring from the flattened
tables, then compute their Laplace representation. Recovered float arrays alone
are still transient operands, not the completed ingestion.

---

## 2. Conventional architecture roles are source roles, not native ontology

Names such as:

```text
embedding
Q / K / V / O
attention head
MLP / FFN
Gate / Up / Down
router / expert
norm / bias
layer
MLA / Conv / diffusion block / multimodal tower
```

are meaningful coordinates of a conventional source architecture and may be retained exactly where the source recipe requires them.

They do **not** define the native ontology of Laplace cognition.

Laplace’s native functional correspondence remains:

```text
Q  = active admitted observation + discourse/open obligations
K  = indexed typed addresses able to respond
QK = query-relative coupling / response field
V  = responding physicalities, facts, evidence, calculations
O  = receipted fold into updated orientation/bindings/frontier/obligations
```

A target compiler/exporter may later materialize selected substrate operators into conventional Q/K/V/O/FFN/etc. roles. That “pour” is downstream of the native program.

---

## 3. Tokenizers decode; they do not own world identity

A tokenizer-local integer or fragment is not automatically a canonical word/entity in the shared world.

The admitted path is:

```text
model-local token id / piece
-> exact tokenizer recipe + byte/string decoding
-> canonical underlying digital/text content where decodable
-> source-local token occurrence/reference retained separately
```

A model-local BPE fragment such as `str`, `Ġfoo`, a byte fallback token, or a sentencepiece boundary marker must not be promoted to “word” merely because the tokenizer calls it a token.

At the same time, the model-local token itself remains addressable as source structure when needed to reconstruct the checkpoint/runtime and to study its behavior.

Thus:

```text
canonical text/content identity
!= tokenizer-local token identity/reference
!= token occurrence in one model vocabulary
```

Cross-model comparison resolves through shared canonical content/structure plus explicit source-local occurrence/role state rather than integer equality.

---

## 4. Preserve irreducible model structure; derive combinatorial views

Do not eagerly explode a checkpoint into every possible token-pair/head/layer relation merely because a downstream query could ask for such a pair.

The storage law is:

> store irreducible admitted information once; retain exact reusable source structure; derive deterministic query views; materialize only consumer artifacts or measured rebuildable accelerations that earn their cost.

Examples:

- one factor/circuit score walk may be one exact trajectory rather than millions of independent token-pair attestations;
- equal tensor slices/components may reuse canonical content while each checkpoint occurrence retains layer/head/expert/tensor-role coordinates;
- dense pair matrices can be calculated from retained factors where that is cheaper and exact enough under the declared recipe;
- target Q/K/V/O/FFN tensors may be materialized for export without requiring the native substrate to persist a dense V² world.

Structural identity and measured functional behavior are separate. Two heads with the same ordinal are not “the same circuit”; two differently numbered components can be functionally correlated.

For TinyLlama, structural sharing includes the declared grouped-query wiring:
eight query heads share each key/value head. Q/K couplings depend on position,
RoPE and the live contextual state; V/O effects must retain their output direction.
Each SwiGLU unit couples one gate row, one up row and the matching down column.
Its conditional effect cannot be recovered from three norms or independent
per-token rankings. Norms, residual paths and the independent output readout also
affect the response. The finite count of heads or units is not, by itself, a proof
that their conditional functions have been extracted.

Reductions must obey the operator-generation contract in `INVENTIONS.md` #106.
In particular, an output dimension or buffer capacity is not permission to lose
information. `tensor_svd_truncate` treats `kmax` as capacity: if its required rank
exceeds that capacity under the caller's `rel_err_tol`, it returns `-3` and reports
the required rank without writing factors. A zero truncation budget can still
incur F32 decomposition arithmetic error; it is not a bit-perfectness claim.
Any deliberately lossy construction or comparison must state and measure that
loss rather than silently adopting it as the ingestion representation.

---

## 5. Model ingestion is decomposition; execution is a separate optional calculation

Model ingestion does **not** prompt the source checkpoint or run sentences through it to discover what falls out. The decomposer reads tokenizer/config/header structure and uses checkpoint numerics only as transient operands for declared native circuit reductions. Durable output is Laplace-native content/occurrence/physicality/evidence state.

If a conventional forward pass is separately requested as a provider measurement, it can be admitted like any other versioned calculation when its complete result-affecting boundary is declared:

```text
checkpoint/model content
+ exact input
+ operator/runtime recipe
+ tokenizer/config generation
+ numeric representation / precision
+ implementation/provider generation
+ deterministic/stochastic seed and decoding policy where relevant
= calculated transformation trajectory + receipt
```

If an omitted coordinate changes the result beyond the declared contract, it was not irrelevant and belongs in the recipe/receipt.

Repeated execution of the same closed deterministic calculation does not automatically create independent semantic corroboration. Run/provenance occurrences can remain distinct while the calculated result converges.

This separation also allows Laplace to compare conventional models without treating any one model as the final judge.

---

## 6. Model-derived testimony and standing

A conventional model can supply useful graded evidence about entities/relations/circuits, but that evidence must retain source and calculation provenance.

Useful derived state can include, under explicit recipes:

- token/entity effects;
- ranked factor/circuit trajectories;
- source-scoped relation/coupling estimates;
- completions or classifications;
- feature activations/statistics;
- cross-source component correlations;
- measured behavior on fixed fixtures.

Those are not all the same state class.

A deterministic calculation is not automatically testimony. A generated completion observed from a model can be an attributed observation. A derived semantic edge can be calculated/witnessed under its recipe. Glicko standing may summarize admitted evidence where the relation law calls for it, but it does not turn model frequency into truth.

Refutation/contradiction cannot be inferred from frequency/co-occurrence alone.

---

## 7. Ingestion runs through the shared operation/admission machine

Model ingestion does not get a private ETL/database semantics.

The conceptual path is:

```text
exact artifact enumeration
-> container/codec parse
-> exact model structure + tokenizer/config decomposition
-> shared canonical composition / occurrence/reference lowering
-> versioned calculation/evidence derivation where requested
-> set-sized persistence/fold
-> receipt
```

For cognition/model-analysis operations, the current semantic program is:

```text
RESOLVE -> COUPLE -> ORIENT -> ROUTE -> SCAN -> COMPOSE
        -> PROPOSE -> STEER -> SELECT -> REALIZE -> WITNESS
```

Ingestion does not need to execute every opcode or produce external realization. It must, however, lower its content/occurrence/reference/calculation/testimony through the same typed state laws rather than inventing a parallel ontology.

A model-analysis request that asks Laplace to determine what matters must let the full admitted observation/circuit/query state participate in `COUPLE` before unconstrained provider/relation policy is frozen.

### Dependency order through the existing implementation

Source trace, 2026-10-29. The inventor's current direction is CPU file-scrape ETL
with limited calculations, developed one dependency at a time. The tokenizer
decomposer is an existing input to this work. The implementation task starts by
connecting its output to the recognized model structure.
The table names the existing owners and the required handoff between them;
the product column defines what to carry forward as the wiring is completed.

| Order | Existing entry points | Product consumed by the next step |
| --- | --- | --- |
| 1. Hold the complete source package | `SafetensorSnapshotWitness.ResolveCompleteDir`; `SourceEntityIdConventions.OpenModelContentSnapshot` | A selected package and held source handles. Enumerate config, tokenizer, weight shards, their index and result-affecting metadata; retain their association with the source trunk. |
| 2. Read source structure | `SafetensorsContainerParser.ParseModel`; `ModelConfigReader.Read`; `LlamaTokenizerParser.IdSpace`; `ModelManifest.Recognize` | `TensorReference` offsets/dtypes/shapes and `ModelAnatomy`/`TensorRole` bindings. The tokenizer's id space constrains the vocabulary axis; shared axes and declared configuration constrain heads, FFN units and other source components. `engine/manifest/model_operators.toml` owns recognition templates. |
| 3. Consume the tokenizer decomposition | `LlamaTokenizerParser.Parse`; `ModelDecomposer.VocabPhase` and `MergesPhase`; `LlamaTokenizerParser.StageVocabulary` | Shared canonical content plus the source-local vocabulary order, decoded piece roles and merge occurrences. Use the existing text decomposition and perf-cache; carry source index-to-content bindings into the calculation. Corpus attestations can join through the same canonical identities. |
| 4. Decode connected components | `TensorReference` ranges; the current whole-tensor reader `WeightTensorETL.LoadTensorF32`; bindings consumed by `ModelCircuitEstate` | Use the recognized ranges to decode bounded native operands for a complete connected component. TinyLlama FFN unit j binds gate row j, up row j and down column j. Attention binds Q/K and V/O with the declared shared-KV layout. Activation, bias, norms, position and residual connections travel with the component they affect. |
| 5. Calculate the Laplace result | Native kernels in `engine/dynamics` and `engine/synthesis`; evidence and geometry contracts in §§4–6 and §9 | Final typed physicality/trajectory and relationship evidence over canonical content, with its applicable conditions and source scope. Carry intermediate arithmetic through to this result in native working memory. The next boundary receives the result rather than a saved dot-product table or an operator catalog. |
| 6. Compose, deduplicate and stage | Shared ordered composition and ingestion executor; `tier0.bin` and `highway.bin`; the tier traversal and streaming laws in `AGENTS.md` | Canonical result records composed bottom-up, known subtrees pruned top-down, novel records streamed in bounded sets. Pack the model's contributions under its containing source trunk; identical content and claims reuse identities across models. |
| 7. Fold and publish | Shared substrate writer and database-side Native standing fold | Graded contributions under the model witness, common consensus, and a source receipt. Follow the current `AGENTS.md` assembly-line order for staged updates and atomic source publication. Replay replaces/reuses the same source contribution. |
| 8. Inspect, compare and expand | Source-scoped A, B and pooled A+B reads; the native forward program (§10 and specs 36/37) | Inspect one connected result through PostgreSQL, then add Phi's independently attributed contribution to shared content. Expand by component family and measure complete-model CPU ETL after the component-to-record handoff is settled. Query execution and export consume the admitted results downstream. |

Execute these dependencies as a streaming pipeline. Read metadata once, carry
the tokenizer bindings and recognized axes forward in memory, and
decode the required source slices in bounded native windows. The source's fixed
component connections determine the work to schedule. Indexed containment and
existing relations provide targeted joins; a vocabulary-wide pair matrix is not
a prerequisite to building the component representation. Model-derived content
can also introduce relationships that the corpus has not yet witnessed.
The numerical stage consumes the carried bindings directly; PostgreSQL receives
the composed results rather than serving as an intermediate numerical scratchpad.

The current call chain supplies concrete integration points:

```text
ModelDecomposer.RunIngestAsync
  -> ParseModel + ModelConfigReader.Read + IdSpace + ModelManifest.Recognize
  -> StageCheckpoint + VocabPhase + MergesPhase + StageVocabulary
  -> ModelTokenEdgeETL.EmitAsync
       -> ModelCircuitEstate.Enumerate
       -> BuildCircuitObservation
       -> EmitSignificantClaims
  -> shared ingestion executor / substrate writer
```

The first implementation boundary to flesh out is the handoff from the recognized
`TensorRole`/`TensorReference` bindings and tokenizer content map to the native
component result in step 5. In the current caller, `BuildCircuitObservation`
receives a whole-vocabulary salience order and `EmitSignificantClaims` invokes
`bilinear_contraction_significant_pairs`. The direct connected-component
decomposition belongs at this junction. Keep the tokenizer and role recognizer
as inputs while replacing that reduction, rather than starting another model
parser or using a single similarity plane as the package's representation.

### Where the geometric calculations belong

The input to each calculation determines its place in the sequence:

| Calculation | Required input | Result passed onward |
| --- | --- | --- |
| Cosine / dot / contraction | Compatible, role-bound source operands and the applicable component conditions | A measured component effect used to construct the declared relationship or physicality. Basis changes must carry through the connected operands. |
| Laplacian eigenmaps | An established affinity graph | Graph-derived coordinates/functions. `laplacian_eigenmaps_from_sparse_graph` accepts that graph directly. |
| QR / Gram–Schmidt | The functions or vectors whose basis is being conditioned | An orthonormal basis and the transformation needed by dependent calculations. |
| Procrustes alignment | Corresponding anchors in the source and target frames | An aligned frame, with residuals and the complementary information required by the representation. Canonical entity coordinates remain content-derived. |
| Glicko-2 | The final declared relationship outcome, its witness/dependence root and governed trust | Consensus standing over the admitted evidence. |

Eigenmaps therefore follow graph construction; alignment follows correspondence
resolution; standing follows evidence derivation. There is no universal
"cosine first, dot products second" pass over every token. Each component recipe
selects the calculations its inputs and output require.

The Native implementations already exist: `engine/dynamics/src/eigenmaps.cpp`,
`gram_schmidt.cpp`, `procrustes.cpp`, and `engine/synthesis/src/basis.cpp`.
`FoundryBasis` and `FoundryExport.BuildBasis` are current export-side callers.
Their call order is useful implementation evidence; the ingestion recipe must
name its own operands and durable result. The four anchor coordinates orient
the geometry; the physicality's constituent identities, order, connections and
grades carry the rest of its admitted structure.

### History that fixes the order

- `02ce54f59` (2026-08-16) already separated tokenizer decomposition from the
  downstream admission problem. Its old admission policy was subsequently
  superseded; §3 above controls canonical content and source references.
- `0a7f1e3a` (2026-09-14) replaced the reduction that a model only votes on
  pre-existing corpus edges with the structured model-source contract.
- `4d009f2fc` (2026-09-25) added the governed shape recognizer and ordered
  vocabulary composition used by the current caller. It also introduced the
  exhaustive significance scan discussed in the roadmap's October 27 audit;
  that scan is an implementation choice to replace at the component-result
  boundary, not a dependency of tokenizer decomposition.
- `1313de5d0` (2026-10-18) corrected composition floors and config claim
  predicates. Preserve those source semantics when wiring the stages.
- `042a92d16` (2026-10-29) made SVD capacity obey the declared truncation budget.
  Geometry experiments select and measure their numerical contract explicitly.

Implementation references:
[model orchestration](../../app/Laplace.Decomposers/Model/ModelDecomposer.cs),
[tokenizer decomposition](../../app/Laplace.Decomposers/Model/LlamaTokenizerParser.cs),
[container parser](../../app/Laplace.Decomposers/Model/SafetensorsContainerParser.cs),
[role recognition](../../app/Laplace.Decomposers/Model/ModelOperatorRecognizer.cs),
[operator templates](../../engine/manifest/model_operators.toml),
[circuit bindings](../../app/Laplace.Decomposers/Model/ModelCircuitEstate.cs),
[current result handoff](../../app/Laplace.Decomposers/Model/ModelTokenEdgeETL.cs),
[native geometry](../../engine/synthesis/src/basis.cpp).

---

## 8. Native execution grain is mandatory for model scale

Model artifacts make per-element orchestration defects catastrophic.

Do not implement model admission/analysis/export as:

```text
tensor cell -> managed callback -> P/Invoke -> SQL/SPI row -> repeat
```

or:

```text
one token pair / one head / one scalar / one output value per high-level call
```

The intended physical shape is:

```text
container/tensor metadata parsed once
-> bulk/stream native decode
-> native numeric/tensor/factor kernels over contiguous tiles
-> coarse shared canonical composition/evidence batches
-> prepared set-sized PostgreSQL/SPI publication
-> bounded receipt
```

`engine/synthesis/` and `engine/dynamics/` already contain native format/numeric/algebra machinery. Managed code should orchestrate recipes/lifecycle, not become the tensor inner loop.

The same law applies to export: format writers must stream/materialize bulk tensors/metadata rather than pay one high-level boundary per scalar.

---

## 9. Geometry, trajectory and model structure

Model-lane physicalities obey the same substrate laws as every other modality.

- `coord` is the real geometric placement under the declared recipe;
- packed `trajectory` vertices are reversible constituent/source-structure carriers, **not child spatial coordinates**;
- realized curves resolve constituent ids to real coords in logical order;
- coordinate/Hilbert equality may nominate a structural bucket and never substitutes for exact identity/order.

A circuit/factor trajectory that is order-sensitive must preserve that order in trajectory state; a centroid cannot recover it.

The repository currently has a real placement-law divergence: native composition uses Euclidean centroid while current managed/model/chess paths include Karcher mean. #1050 owns reconciliation. This document does not declare the old “open question” resolved by whichever lane was inspected first.

The weaker bounded-domain theorem still holds for valid centroid-composed children inside the 4-ball; Karcher/surface semantics are a separate as-built recipe issue.

---

## 10. Cross-model and cross-architecture comparison

Laplace should be able to ask, with exact source provenance:

```text
Which model components touch/respond to entity X?
Which trajectories/factors behave similarly across sources?
Which differently numbered heads/experts/components correlate functionally?
What behavior is shared or contradicted across model A, model B, corpora and tools?
What changed between checkpoint generations?
```

Do not align components solely by layer/head ordinal or tensor path string. Those are source-local structural coordinates.

Cross-source functional comparison can use exact shared entity coverage, trajectories, response/coupling behavior, Procrustes or other declared operators, and observed outcomes under typed contracts.

A model round table is pooled explicit evidence over one substrate, not runtime voting followed by another judge model.

---

## 11. Synthesis/export is downstream of native substrate state

SafeTensors, GGUF and other model/container formats are input/output codecs and consumer artifacts. They do not define the native model.

A target synthesis/export recipe may select substrate state and materialize conventional architecture roles:

```text
selected substrate operators/state
-> target embedding/position roles
-> target Q/K/V/O roles
-> target FFN/gate/expert roles
-> target normalization/output roles
-> exact format materialization
```

The target artifact owes:

- exact recipe/source/world/evidence scope;
- reproducible structural/value materialization;
- format-level roundtrip/readback;
- behavioral evaluation through an external/runtime consumer where the product claim requires it.

A loadable GGUF/SafeTensors file alone does not prove the synthesized model reproduces Laplace behavior.

---

## 12. Required receipts and benchmarks

A model-ingest/model-analysis/export receipt should expose useful work rather than only bytes and elapsed time.

Record, as applicable:

```text
artifacts / tensors / components / scalar values decoded
canonical entities/trajectories reused vs newly admitted
factor/circuit trajectories
calculation/evidence cells derived
native kernel invocations and batch/tile widths
managed/native/SPI/SQL boundary crossings
CPU / memory / I/O / DB / accelerator work
provider/precision/recipe identity
result/export fingerprints
```

Optional GPU/AVX providers must hold logical semantics constant and prove parity under the declared numeric contract. Installed accelerator hardware is not proof that the model/world is GPU-resident.

---

## 13. Acceptance

- [ ] tokenizer/config/header/component structure is admitted under an explicit loss contract; raw weight payload retention and bit-perfect checkpoint reconstruction are not silently implied;
- [ ] tokenizer-local pieces remain source-local references/occurrences while decodable underlying content resolves through shared canonical identity;
- [ ] arbitrary BPE fragments cannot silently become universal words merely because a tokenizer emitted them;
- [ ] component/tensor/factor structure remains addressable without eager V²/token-pair explosion;
- [ ] deterministic model execution has complete provider/precision/recipe/seed identity and reproducible receipts;
- [ ] model-derived observations/calculations/testimony remain distinct and source-attributable;
- [ ] Q/K/V/O/head/expert/layer labels remain conventional source/target roles, not native cognition ontology;
- [ ] cross-model comparison does not equate components solely by ordinal/path names;
- [ ] packed trajectories are never treated as realized coordinates;
- [ ] centroid/Karcher divergence remains visible until #1050 is resolved;
- [ ] ingestion/analysis/export hot paths use coarse native/bulk execution rather than per-cell/per-pair RBAR;
- [ ] synthesized consumer artifacts trace to exact substrate inputs/recipe and are behaviorally tested when behavior is claimed.

## Non-success

- “a checkpoint is only testimony; none of its structure is content”;
- “all model content reduces to persisted token->token rows”;
- tokenizer-local integer/piece used as canonical world identity;
- source-local head/layer ordinal used as cross-model functional identity;
- storing combinatorial pair tables when an exact factor/trajectory representation already contains the irreducible information;
- copying a checkpoint tensor store and calling that Laplace native cognition;
- external model/LLM judge used to adjudicate the pooled result;
- per-tensor-cell SQL/SPI/PInvoke or per-value export loops;
- one loadable export file presented as proof of native/consumer behavioral equivalence.

Related implementation/proof owners include #927, #928, #1050, #1045, #1177, #1401, #1561 and the current model/export issues. Historical measurements remain evidence in the archive, not current status.
