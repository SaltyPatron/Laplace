# Model ingestion — current design contract

This document defines how conventional checkpoints enter Laplace. It supersedes the 2026-08 audit-era reduction that said a checkpoint was “not content,” that all useful model information was reducible to token-pair votes, and that model ingestion should be described through the pre-COUPLE forward-pass sequence.

Historical measurements and the old model-lane audit remain available under `docs/archive/` and Git history. They are useful counterexamples, not current invention authority.

Current authority is `docs/INVENTION.md`, `docs/INVENTIONS.md`, `docs/INVENTION_PRESERVATION_2026-09-02.md`, `docs/specs/36_Laplace_Forward_Pass.md`, `docs/specs/37_Substrate_Operation_ISA.md`, and `AGENTS.md`.

---

## 1. A checkpoint is exact digital content **and** an attributed source

A conventional model artifact has several simultaneous roles that must not be flattened:

```text
checkpoint bytes / files                 exact digital content + artifact occurrence
config/tokenizer/architecture            exact structured content / references
layers/heads/experts/tensors/components  addressable source structure
numeric values / slices / factors        exact admitted numeric content under codec/precision recipe
model-local role/location                occurrence / source-scoped structural coordinate
measured forward behavior                deterministic/versioned calculation trajectory
induced semantic/circuit effects         calculated / witnessed provider evidence
model claims/completions                 source-attributed observations/testimony when admitted
```

The checkpoint is therefore not an opaque runtime authority, but neither is it “nothing but testimony.” Its exact structure is decomposable content. Its behavior and semantic effects are additional calculation/evidence planes.

Laplace must preserve enough exact structure to answer what the source model **is**, where a component occurs, and what a declared execution **did**, without making the checkpoint’s private tensor format the ontology of Laplace cognition.

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

---

## 5. Model execution is a deterministic calculation trajectory

A conventional forward pass can be admitted like any other versioned provider calculation when its complete result-affecting boundary is declared:

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

- [ ] exact checkpoint/config/tokenizer/tensor/component structure is admitted/reconstructable or any accepted loss is explicitly declared;
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
- “all model knowledge reduces to persisted token->token rows”;
- tokenizer-local integer/piece used as canonical world identity;
- source-local head/layer ordinal used as cross-model functional identity;
- storing combinatorial pair tables when an exact factor/trajectory representation already contains the irreducible information;
- copying a checkpoint tensor store and calling that Laplace native cognition;
- external model/LLM judge used to adjudicate the pooled result;
- per-tensor-cell SQL/SPI/PInvoke or per-value export loops;
- one loadable export file presented as proof of native/consumer behavioral equivalence.

Related implementation/proof owners include #927, #928, #1050, #1045, #1177, #1401, #1561 and the current model/export issues. Historical measurements remain evidence in the archive, not current status.
