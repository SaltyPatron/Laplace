# 27c — Primitive Index: FFN / Normalization / Embeddings

Reference catalog and test checklist for the model decomposer (ETL) and its read-side
replay. Companion to the attention/positional primitive index. Scope: everything in a
transformer checkpoint that is NOT Q/K/V/O attention math — the MLP stack, every norm,
the embedding/unembedding pair, and the structural conventions (residual order, weight
layouts, fused tensors) that decide whether a scraped tensor means what its name says.

Contract being tested: **one-time scrape; compute at ingest; store meaning only** — and
at read time, replay the checkpoint's math *exactly*. Every entry ends with a **Gate**:
a single testable exactness assertion. "Laplace status" is measured against
`app/Laplace.Decomposers/Model/ArchitectureProfile.cs` (profiles today: Llama, Phi,
Qwen2, Bert; flags: `HasGate`, `HasBiases`, `RmsNorm`; names: `EmbedTokens`, `LmHead`,
`FinalNorm`, `PerLayerNorms`, Q/K/V/O, Gate/Up/Down; helper `BiasOf`).

---

## TOC

**FFN core**
- [FFN-MLP2](#ffn-mlp2) — two-matrix MLP (fc1/fc2)
- [FFN-ACT-RELU](#ffn-act-relu) — ReLU activation
- [FFN-ACT-GELU-EXACT](#ffn-act-gelu-exact) — erf GELU
- [FFN-ACT-GELU-TANH](#ffn-act-gelu-tanh) — tanh-approx GELU (`gelu_new`/`gelu_pytorch_tanh`)
- [FFN-ACT-SILU](#ffn-act-silu) — SiLU/Swish
- [FFN-ACT-QUICKGELU](#ffn-act-quickgelu) — sigmoid-approx GELU
- [FFN-ACT-CONFIG](#ffn-act-config) — activation-function config keys (ACT2FN)
- [FFN-GLU-SWIGLU](#ffn-glu-swiglu) — SiLU-gated FFN (gate/up/down)
- [FFN-GLU-GEGLU](#ffn-glu-geglu) — GELU-gated FFN
- [FFN-GLU-REGLU](#ffn-glu-reglu) — ReLU-gated FFN
- [FFN-GLU-CLAMP](#ffn-glu-clamp) — GPT-OSS clamped/interleaved SwiGLU
- [FFN-BIAS](#ffn-bias) — FFN bias vectors
- [FFN-PARALLEL](#ffn-parallel) — parallel attention+FFN block (GPT-J/Phi)

**Mixture of Experts**
- [MOE-ROUTER](#moe-router) — router/gating tensor
- [MOE-TOPK](#moe-topk) — top-k token-choice selection
- [MOE-SOFTMAX-ORDER](#moe-softmax-order) — softmax before vs after top-k
- [MOE-NORM-GATES](#moe-norm-gates) — gate-weight renormalization + routed scaling
- [MOE-SIGMOID-GROUP](#moe-sigmoid-group) — sigmoid scoring, correction bias, group-limited routing (DeepSeek-V3)
- [MOE-SHARED](#moe-shared) — shared/always-on experts
- [MOE-EXPERT-CHOICE](#moe-expert-choice) — expert-choice vs token-choice
- [MOE-AUXLOSS](#moe-auxloss) — router auxiliary losses (inference no-op)
- [MOE-LAYOUT](#moe-layout) — per-expert vs packed 3-D expert tensor layouts
- [MOE-DENSE-MIX](#moe-dense-mix) — dense/sparse layer interleaving keys

**Normalization**
- [NORM-LAYERNORM](#norm-layernorm) — LayerNorm (γ, β, ε, mean subtraction)
- [NORM-RMS](#norm-rms) — RMSNorm (gain, ε, no mean)
- [NORM-RMS-OFFSET](#norm-rms-offset) — Gemma (1+weight) RMSNorm
- [NORM-NONPARAM](#norm-nonparam) — non-parametric LayerNorm (OLMo-1)
- [NORM-PRE](#norm-pre) — pre-LN placement
- [NORM-POST](#norm-post) — post-LN placement (BERT/GPT-1)
- [NORM-POST-REORDERED](#norm-post-reordered) — norm-on-output-before-residual (OLMo2)
- [NORM-SANDWICH](#norm-sandwich) — sandwich/double norm (Gemma 2: 4 norms/layer)
- [NORM-FINAL](#norm-final) — final norm before lm_head
- [NORM-EMBED](#norm-embed) — embedding LayerNorm (BERT)
- [NORM-QK](#norm-qk) — QK-norm (cross-ref to attention doc)
- [NORM-EPS](#norm-eps) — epsilon config keys

**Embeddings / unembedding**
- [EMB-WORD](#emb-word) — word/token embedding matrix
- [EMB-TIED](#emb-tied) — tied vs untied lm_head
- [EMB-SCALE](#emb-scale) — embedding multiplier √d (Gemma) / embedding_multiplier
- [EMB-POS-LEARNED](#emb-pos-learned) — learned absolute position embeddings
- [EMB-TOKENTYPE](#emb-tokentype) — token-type/segment embeddings
- [EMB-MLMHEAD](#emb-mlmhead) — BERT MLM prediction head (transform + decoder + bias)
- [EMB-SOFTCAP](#emb-softcap) — final logit soft-capping (Gemma 2)
- [EMB-LOGIT-SCALE](#emb-logit-scale) — final logit scaling (Cohere/Granite)
- [EMB-VOCAB-PAD](#emb-vocab-pad) — vocab padding rows

**Structural conventions**
- [RES-PRENORM](#res-prenorm) — residual-stream add order
- [RES-SCALE](#res-scale) — residual multiplier (Granite)
- [DROPOUT-NOOP](#dropout-noop) — dropout locations (inference no-ops)
- [LAYOUT-ROWMAJOR](#layout-rowmajor) — nn.Linear [out,in] row-major convention
- [LAYOUT-CONV1D](#layout-conv1d) — GPT-2 transposed Conv1D weights
- [LAYOUT-FUSED-QKV](#layout-fused-qkv) — fused qkv tensors (c_attn, qkv_proj)
- [LAYOUT-FUSED-GATEUP](#layout-fused-gateup) — fused gate_up_proj tensors

---

## 1. FFN core

### FFN-MLP2
- **ID**: FFN-MLP2
- **What/Why**: The classic two-layer feed-forward block: widen the hidden vector ~4x, bend it with a nonlinearity, project back down. It is where a transformer stores most of its factual/associative knowledge per layer; without it the model is just attention mixing with no per-token computation.
- **Formulation**: `FFN(x) = W2 · act(W1 · x + b1) + b2`, with `W1 ∈ R[d_ff × d]`, `W2 ∈ R[d × d_ff]`, typically `d_ff = 4d`. Activation per FFN-ACT-*.
- **Variants/used-by**: GPT-2 (gelu_new), BERT (gelu exact), Phi-1/1.5/2 (gelu_new), OPT (relu), GPT-J/NeoX, Falcon. Config keys: `intermediate_size` (or `n_inner`/`ffn_dim`), `hidden_act`/`activation_function`.
- **Tensor names**:
  - GPT-2: `h.{L}.mlp.c_fc.{weight,bias}`, `h.{L}.mlp.c_proj.{weight,bias}` (Conv1D — see LAYOUT-CONV1D)
  - BERT: `encoder.layer.{L}.intermediate.dense.*`, `encoder.layer.{L}.output.dense.*`
  - Phi: `model.layers.{L}.mlp.fc1.*`, `model.layers.{L}.mlp.fc2.*`
  - OPT: `model.decoder.layers.{L}.fc1.*`, `fc2.*`; NeoX: `mlp.dense_h_to_4h.*`, `mlp.dense_4h_to_h.*`
- **Witnessed content**: both weight matrices, both bias vectors (if present), `intermediate_size`, the exact activation identifier string.
- **Read-side mechanism**: matvec → exact activation function → matvec, biases added, in the checkpoint's declared dtype semantics.
- **Laplace status**: **partial** — Phi (`mlp.fc1`/`fc2`) and Bert (`intermediate.dense`/`output.dense`) mapped via Up/Down slots and `ContractionPath(GatePattern: null, …)`; the activation identifier is not read from config, so replay cannot distinguish relu/gelu variants.
- **Gate**: For a fixed input vector, substrate replay of one FFN block matches the HF forward output bitwise-in-fp32 (or ≤1 ulp per element).
- Src: [HF activations.py](https://github.com/huggingface/transformers/blob/main/src/transformers/activations.py), Vaswani et al. 2017.

### FFN-ACT-RELU
- **ID**: FFN-ACT-RELU
- **What/Why**: The simplest nonlinearity — zero out negatives. Without any nonlinearity the two FFN matrices collapse into one linear map and the layer learns nothing compositional.
- **Formulation**: `relu(x) = max(0, x)`.
- **Variants/used-by**: original Transformer, OPT, T5 (v1.0), Switch Transformer. Config: `hidden_act`/`activation_function`/`feed_forward_proj` = `"relu"`.
- **Tensor names**: none — activation is config-only, no parameters.
- **Witnessed content**: the activation identifier attached to the FFN plane (scalar recipe fact).
- **Read-side mechanism**: elementwise `max(0,x)` between the two FFN matvecs.
- **Laplace status**: **missing** — no activation identity captured in ArchitectureProfile.
- **Gate**: Replay of an OPT/T5-v1.0 FFN uses hard ReLU; any GELU substitution shows as nonzero output for a negative pre-activation and fails.
- Src: [HF activations.py](https://github.com/huggingface/transformers/blob/main/src/transformers/activations.py).

### FFN-ACT-GELU-EXACT
- **ID**: FFN-ACT-GELU-EXACT
- **What/Why**: Smooth ReLU that weights inputs by their Gaussian CDF, computed with the true error function. It is a *different function* from the tanh approximation (FFN-ACT-GELU-TANH) — the two disagree in low-order bits, so exact replay must know which one the checkpoint trained with.
- **Formulation**: `gelu(x) = x · Φ(x) = 0.5 · x · (1 + erf(x/√2))`.
- **Variants/used-by**: BERT/RoBERTa (HF `"gelu"` → erf-based `nn.GELU()`), many encoders. Config: `hidden_act: "gelu"`.
- **Tensor names**: none (config-only).
- **Witnessed content**: activation identifier distinguishing `gelu` (erf) from `gelu_new`/`gelu_pytorch_tanh` (tanh). MUST NOT be normalized to a generic "gelu".
- **Read-side mechanism**: native `erf`-based evaluation (C `erf()`/`erff()`), not the tanh polynomial.
- **Laplace status**: **missing** — profile carries no activation identity; Bert profile implies GELU only by convention.
- **Gate**: `|replay_gelu(x) − x·0.5·(1+erf(x/√2))| == 0` at fp32 for a sweep of x, and replay_gelu(2.0) differs from tanh-approx GELU(2.0) in the expected low bits (they must NOT be equal).
- Src: [HF activations.py](https://github.com/huggingface/transformers/blob/main/src/transformers/activations.py), Hendrycks & Gimpel 2016.

### FFN-ACT-GELU-TANH
- **ID**: FFN-ACT-GELU-TANH
- **What/Why**: The tanh polynomial approximation of GELU that OpenAI GPT/GPT-2 shipped and Gemma standardized on. Checkpoints trained on it embed its exact bit pattern; replaying with erf-GELU is a silent correctness bug.
- **Formulation**: `gelu_tanh(x) = 0.5 · x · (1 + tanh(√(2/π) · (x + 0.044715·x³)))`.
- **Variants/used-by**: GPT-2 (`activation_function: "gelu_new"`), Phi-1/2 (`gelu_new`), Gemma 1/2/3 (`hidden_activation: "gelu_pytorch_tanh"`, gated — see FFN-GLU-GEGLU). HF names `gelu_new`, `gelu_pytorch_tanh`, `gelu_fast` (a further-reordered variant) are all tanh-family but not bit-identical to each other.
- **Tensor names**: none (config-only).
- **Witnessed content**: the *specific* identifier (`gelu_new` vs `gelu_pytorch_tanh` vs `gelu_fast`), since HF implements them with slightly different expression trees.
- **Read-side mechanism**: evaluate the tanh polynomial with the same constant (0.044715) and operation order as the named HF variant.
- **Laplace status**: **missing** — no activation identity; Phi profile replays would need `gelu_new` specifically.
- **Gate**: Replay matches HF `NewGELUActivation`/`GELUTanh` bitwise at fp32 on a grid of inputs including large |x| and denormals.
- Src: [HF issue #1347](https://github.com/huggingface/transformers/issues/1347), [activations.py](https://github.com/huggingface/transformers/blob/main/src/transformers/activations.py).

### FFN-ACT-SILU
- **ID**: FFN-ACT-SILU
- **What/Why**: x times its own sigmoid ("Swish") — the activation inside every Llama-family gated FFN. It is the default `hidden_act` for nearly all post-2023 decoder LLMs; getting it wrong corrupts every FFN-GLU-SWIGLU replay.
- **Formulation**: `silu(x) = x · σ(x) = x / (1 + e^(−x))`.
- **Variants/used-by**: Llama 1/2/3, Mistral/Mixtral, Qwen2/3, DeepSeek, Phi-3. Config: `hidden_act: "silu"` (alias `"swish"`).
- **Tensor names**: none (config-only).
- **Witnessed content**: activation identifier.
- **Read-side mechanism**: elementwise `x·σ(x)` on the gate branch inside FFN-GLU-SWIGLU.
- **Laplace status**: **partial** — Llama/Qwen2 `ContractionPath` assumes the gated shape, but SiLU is an unwitnessed convention, not a recorded fact.
- **Gate**: `replay_silu(x) == x*sigmoid(x)` bitwise at fp32; a SwiGLU block replayed with GELU instead of SiLU must fail the FFN-MLP2 block gate.
- Src: [activations.py](https://github.com/huggingface/transformers/blob/main/src/transformers/activations.py), Shazeer 2020 (GLU Variants).

### FFN-ACT-QUICKGELU
- **ID**: FFN-ACT-QUICKGELU
- **What/Why**: A cheap sigmoid-based GELU stand-in (`x·σ(1.702x)`) from CLIP-era code. Some vision/multimodal towers ship it; it is numerically distinct from both GELU variants, so it needs its own identifier.
- **Formulation**: `quick_gelu(x) = x · σ(1.702 · x)`.
- **Variants/used-by**: CLIP text/vision towers, some VLM vision encoders (`hidden_act: "quick_gelu"`). The same `α = 1.702` sigmoid form reappears inside GPT-OSS's clamped SwiGLU (FFN-GLU-CLAMP).
- **Tensor names**: none (config-only).
- **Witnessed content**: activation identifier + the 1.702 constant as a recipe scalar.
- **Read-side mechanism**: elementwise `x·σ(1.702x)`.
- **Laplace status**: **missing**.
- **Gate**: Replay matches `x*sigmoid(1.702*x)` bitwise at fp32 and is provably not substituted by gelu_tanh.
- Src: [activations.py](https://github.com/huggingface/transformers/blob/main/src/transformers/activations.py).

### FFN-ACT-CONFIG
- **ID**: FFN-ACT-CONFIG
- **What/Why**: The config.json key that names the activation is itself a primitive — different families spell it differently, and the string routes through HF's ACT2FN table to a concrete function. If the ETL doesn't witness this string, every FFN gate above is unverifiable.
- **Formulation**: `act = ACT2FN[config.<key>]`; the string is the sole authority.
- **Variants/used-by**: `hidden_act` (Llama/BERT/Qwen/Phi-3), `activation_function` (GPT-2/GPT-J/OPT), `hidden_activation` (Gemma — note Gemma1 configs that said `hidden_act: gelu` were corrected to `gelu_pytorch_tanh` via `hidden_activation`), `feed_forward_proj` (T5: `"relu"` or `"gated-gelu"`), `activation` (some encoders). Known values: `relu`, `gelu`, `gelu_new`, `gelu_fast`, `gelu_pytorch_tanh`, `quick_gelu`, `silu`/`swish`, `gelu_10`, `relu2` (ReLU², e.g. some efficient LMs), `laplace`, `mish`.
- **Tensor names**: n/a — config only.
- **Witnessed content**: the raw key name AND value string, attested verbatim (record layer), plus the resolved function identity (calculated layer).
- **Read-side mechanism**: dispatch table in native code mirroring ACT2FN exactly, one implementation per named function.
- **Laplace status**: **missing** — ArchitectureProfile reads no config keys; model_type alone selects the profile.
- **Gate**: For every supported model_type, ETL emits an attestation carrying the verbatim activation string, and read-side dispatch resolves it to the bit-exact function (spot-check one input per function).
- Src: [activations.py ACT2FN](https://github.com/huggingface/transformers/blob/main/src/transformers/activations.py).

### FFN-GLU-SWIGLU
- **ID**: FFN-GLU-SWIGLU
- **What/Why**: The gated FFN used by the entire Llama lineage: a second "gate" matrix modulates the up-projection elementwise, letting the layer multiplicatively switch features on/off. Three matrices instead of two; d_ff is usually ~8/3·d to keep parameter count even.
- **Formulation**: `FFN(x) = W_down · ( silu(W_gate · x) ⊙ (W_up · x) )`. Usually bias-free.
- **Variants/used-by**: Llama 1/2/3, Mistral, Qwen2/3, DeepSeek, Gemma (with GELU — see FFN-GLU-GEGLU), Phi-3 (fused — see LAYOUT-FUSED-GATEUP). Config signals: `hidden_act: "silu"` + presence of gate tensor; `intermediate_size` is the gated width.
- **Tensor names**: `model.layers.{L}.mlp.gate_proj.weight`, `mlp.up_proj.weight`, `mlp.down_proj.weight`. T5 v1.1 gated: `wi_0` (gate), `wi_1` (up), `wo`. Mixtral experts: `w1` (gate), `w3` (up), `w2` (down) — note the non-obvious w3=up ordering.
- **Witnessed content**: all three matrices, activation id, `intermediate_size`; per MoE expert where applicable.
- **Read-side mechanism**: two matvecs, elementwise `silu(gate) ⊙ up`, then down matvec.
- **Laplace status**: **partial** — Llama/Qwen2 profiles map gate/up/down into `ContractionPath("COMPLETES_TO", …)` and `HasGate=true`; activation and elementwise-product replay semantics are implicit, not attested; the Mixtral w1/w3/w2 naming is unmapped.
- **Gate**: Substrate replay of `down(silu(gate·x) ⊙ up·x)` matches HF `LlamaMLP.forward` at fp32 for a fixed x; swapping gate/up tensors must fail the gate (asymmetry check).
- Src: Shazeer 2020, [HF modeling_llama.py](https://github.com/huggingface/transformers/blob/main/src/transformers/models/llama/modeling_llama.py).

### FFN-GLU-GEGLU
- **ID**: FFN-GLU-GEGLU
- **What/Why**: Same gated shape as FFN-GLU-SWIGLU but the gate goes through GELU instead of SiLU. Gemma's FFN is exactly this with the tanh GELU — misreading it as SwiGLU changes every FFN output.
- **Formulation**: `FFN(x) = W_down · ( gelu(W_gate · x) ⊙ (W_up · x) )`.
- **Variants/used-by**: Gemma 1/2/3 (`hidden_activation: "gelu_pytorch_tanh"` + gate/up/down), T5 v1.1 / Flan-T5 (`feed_forward_proj: "gated-gelu"`, erf GELU), some diffusion text encoders.
- **Tensor names**: Gemma: `model.layers.{L}.mlp.{gate_proj,up_proj,down_proj}.weight`. T5 v1.1: `block.{L}.layer.{last}.DenseReluDense.{wi_0,wi_1,wo}.weight`.
- **Witnessed content**: three matrices + the *specific* GELU variant id (tanh for Gemma, erf for T5 v1.1).
- **Read-side mechanism**: as SwiGLU with the gate nonlinearity swapped to the witnessed GELU variant.
- **Laplace status**: **missing** — no Gemma/T5 profile; Llama fallback (`For` default) would assume SiLU.
- **Gate**: Gemma FFN replay uses gelu_pytorch_tanh on the gate branch and matches HF at fp32; a SiLU substitution must produce a detectable mismatch.
- Src: Shazeer 2020, [HF Gemma2 docs](https://huggingface.co/docs/transformers/main/en/model_doc/gemma2).

### FFN-GLU-REGLU
- **ID**: FFN-GLU-REGLU
- **What/Why**: The ReLU member of the GLU family — gate through hard ReLU. Rare in shipped LLM checkpoints but part of the same paper family; the ETL should recognize it rather than crash or mislabel.
- **Formulation**: `FFN(x) = W_down · ( relu(W_gate · x) ⊙ (W_up · x) )`.
- **Variants/used-by**: Shazeer 2020 ablations; occasional research checkpoints; signaled by gated tensor shape + `hidden_act: "relu"`.
- **Tensor names**: same gate/up/down or wi_0/wi_1/wo patterns as the other GLU variants.
- **Witnessed content**: three matrices + relu id.
- **Read-side mechanism**: `down(relu(gate·x) ⊙ up·x)`.
- **Laplace status**: **missing** (would incorrectly ride the Llama/SiLU assumption).
- **Gate**: A gated FFN with `hidden_act=relu` replays with hard ReLU on the gate; negative gate pre-activations contribute exactly zero.
- Src: Shazeer 2020 (GLU Variants Improve Transformer).

### FFN-GLU-CLAMP
- **ID**: FFN-GLU-CLAMP
- **What/Why**: GPT-OSS's production SwiGLU: gate and up are *interleaved* in one packed tensor, both are clamped to a fixed range (quantization headroom), the sigmoid uses α=1.702, and the up branch gets a +1 offset so the block has a linear bypass. Four separate deviations from textbook SwiGLU — each one breaks bit-exact replay if missed.
- **Formulation**: `gu = W_gate_up·x + b`; `gate = gu[..., 0::2]`, `up = gu[..., 1::2]`; `gate ← min(gate, limit)`, `up ← clip(up, −limit, +limit)`; `out = W_down · ((up + 1) ⊙ gate·σ(1.702·gate)) + b_down`, `limit = 7.0`.
- **Variants/used-by**: GPT-OSS 20B/120B (`model_type: "gpt_oss"`; config `swiglu_limit: 7.0`). Interleaving is even/odd stride, NOT a halves split.
- **Tensor names**: `model.layers.{L}.mlp.experts.gate_up_proj` `[E, d, 2·d_ff]`, `experts.gate_up_proj_bias`, `experts.down_proj`, `experts.down_proj_bias` (packed 3-D, biased — see MOE-LAYOUT).
- **Witnessed content**: packed tensors + biases, `swiglu_limit`, α=1.702, the interleave convention itself (recipe fact).
- **Read-side mechanism**: strided deinterleave, clamp, `(up+1)·gate·σ(1.702·gate)`, down matvec — exactly this order.
- **Laplace status**: **missing**.
- **Gate**: Replay clamps at ±7 (a pre-activation of 100 saturates exactly to the limit-path output) and matches HF `GptOssExperts` at fp32; halves-split deinterleaving must fail.
- Src: [HF blog: faster-transformers](https://huggingface.co/blog/faster-transformers), [ceramic.ai gpt-oss-swiglu](https://www.ceramic.ai/blog/gpt-oss-swiglu).

### FFN-BIAS
- **ID**: FFN-BIAS
- **What/Why**: Per-output-row additive constants on FFN matrices. GPT-2/BERT/Phi/Falcon have them; the Llama family dropped them. A replay that ignores a present bias is wrong on every token; one that invents a zero bias is harmless but must not be attested as witnessed.
- **Formulation**: `y = W·x + b`, `b ∈ R[out]`.
- **Variants/used-by**: present: GPT-2, BERT, OPT, Phi-1/2, Qwen1 (also Qwen2 attention qkv biases but NOT its FFN), GPT-OSS (expert biases). Absent: Llama/Mistral/Gemma/DeepSeek FFNs (`mlp_bias: false` where the key exists). Config: `mlp_bias` (Llama-class), otherwise implied by checkpoint contents.
- **Tensor names**: sibling `.bias` of each `.weight` (`mlp.fc1.bias`, `mlp.c_fc.bias`, `intermediate.dense.bias`, …).
- **Witnessed content**: every bias vector present in the checkpoint, attested as witnessed; absence recorded as absence (never a synthesized zero row attested as content).
- **Read-side mechanism**: add bias after matvec, before activation (b1) / after down-projection (b2).
- **Laplace status**: **partial** — `HasBiases` flag per profile + `BiasOf()` name derivation exist; correct for Phi/Qwen2/Bert, but bias application order is replay convention only, and Qwen2's flag (`HasBiases=true`) conflates attention biases with FFN biases (Qwen2 FFN is bias-free).
- **Gate**: For a biased model, zeroing the witnessed bias changes replay output (proves it is applied); for Llama, the substrate contains no fabricated FFN bias attestation.
- Src: HF per-model configs; [modeling_phi.py](https://github.com/huggingface/transformers/blob/main/src/transformers/models/phi/modeling_phi.py).

### FFN-PARALLEL
- **ID**: FFN-PARALLEL
- **What/Why**: GPT-J/GPT-NeoX/Phi run attention and FFN side-by-side off the *same* normed input and add both to one residual, instead of sequentially. One norm per layer instead of two. Replaying it sequentially double-applies the FFN's view of attention output and diverges immediately.
- **Formulation**: `y = x + Attn(LN(x)) + FFN(LN(x))` — one shared `LN`, both branches read the same normed x. Sequential (Llama) is `h = x + Attn(N1(x)); y = h + FFN(N2(h))`.
- **Variants/used-by**: GPT-J, GPT-NeoX (`use_parallel_residual: true`), Phi-1/1.5/2, Falcon (`parallel_attn: true`, `new_decoder_architecture` adds a second norm feeding the MLP branch), PaLM (paper).
- **Tensor names**: signature is a *single* per-layer norm: Phi `model.layers.{L}.input_layernorm.*` with no `post_attention_layernorm`; GPT-J `h.{L}.ln_1.*` only.
- **Witnessed content**: the block topology (parallel vs sequential) as a recipe fact; inferable from the per-layer norm census but must be attested explicitly.
- **Read-side mechanism**: compute both branch outputs from the same normed input; one residual add of their sum.
- **Laplace status**: **partial** — Phi profile lists exactly one per-layer norm, which *implies* the parallel block, but no explicit topology flag exists; a replay engine reading only the profile could still wire it sequentially.
- **Gate**: Phi-2 single-layer replay matches HF fp32 output; wiring the same tensors sequentially must produce a mismatch (topology is load-bearing, not cosmetic).
- Src: [HF modeling_gptj.py / modeling_phi.py](https://github.com/huggingface/transformers), GPT-NeoX config docs.

---

## 2. Mixture of Experts

### MOE-ROUTER
- **ID**: MOE-ROUTER
- **What/Why**: A small linear layer that scores every expert for every token — the switchboard of an MoE layer. Its tiny matrix decides which FFN experts (each an FFN-GLU-SWIGLU) run; scraping experts without the router yields anatomy with no wiring.
- **Formulation**: `logits = W_r · x (+ b_r)`, `W_r ∈ R[n_experts × d]`. Usually bias-free; GPT-OSS router has a bias.
- **Variants/used-by**: Mixtral, Qwen2/3-MoE, DeepSeek-V2/V3, GPT-OSS, Granite-MoE. Config: `num_local_experts` / `n_routed_experts` / `num_experts`.
- **Tensor names**: Mixtral `model.layers.{L}.block_sparse_moe.gate.weight`; Qwen-MoE/DeepSeek `model.layers.{L}.mlp.gate.weight` (+ DeepSeek-V3 `mlp.gate.e_score_correction_bias`); GPT-OSS `mlp.router.{weight,bias}`.
- **Witnessed content**: router matrix (+ bias / correction bias), expert count, per-layer.
- **Read-side mechanism**: one matvec per token producing the expert-score vector that MOE-TOPK consumes.
- **Laplace status**: **missing** — no MoE profile exists.
- **Gate**: For a fixed x, replayed router logits match HF bitwise at fp32 (this gate is upstream of every other MoE gate).
- Src: [Mixtral paper](https://arxiv.org/pdf/2401.04088), [HF MoE blog](https://huggingface.co/blog/moe-transformers).

### MOE-TOPK
- **ID**: MOE-TOPK
- **What/Why**: Each token keeps only its k best-scoring experts and runs just those FFNs — the sparsity that makes MoE cheap. Tie-breaking and k are exactness-critical: pick a different expert and the output is from a different function entirely.
- **Formulation**: `E(x) = TopK(scores, k)` by value, ties broken by lower index (torch.topk semantics); output `y = Σ_{i∈E} g_i · Expert_i(x)`.
- **Variants/used-by**: Mixtral k=2 (`num_experts_per_tok: 2`), Qwen3-MoE k=8 of 128, DeepSeek-V3 k=8 of 256, GPT-OSS k=4 (`experts_per_token`/`num_experts_per_tok`). Config keys: `num_experts_per_tok`, `top_k`, `moe_top_k`.
- **Tensor names**: none beyond MOE-ROUTER — selection is pure math.
- **Witnessed content**: k as a recipe scalar; per-(token,layer) selected experts belong to the calculated layer if attested at all.
- **Read-side mechanism**: exact top-k with deterministic index tie-break on the replayed logits.
- **Laplace status**: **missing**.
- **Gate**: On a probe batch, the replayed selected-expert index sets equal HF's for 100% of tokens, including a constructed tie case.
- Src: [Mixtral paper](https://arxiv.org/pdf/2401.04088), HF modeling code.

### MOE-SOFTMAX-ORDER
- **ID**: MOE-SOFTMAX-ORDER
- **What/Why**: Families disagree on whether the router softmax runs over *all* experts before top-k, or only over the *selected* k after. The two orders give different gate weights whenever more than k experts have mass — a per-family fact the ETL must record, not assume.
- **Formulation**: (a) softmax-then-topk: `p = softmax(logits)` over all E, select top-k of p (Mixtral, Qwen2/3-MoE, DeepSeek-V2). (b) topk-then-softmax: select top-k logits, `g = softmax(logits[topk])` over just k (GPT-OSS). (c) sigmoid scoring: no softmax at all (DeepSeek-V3, see MOE-SIGMOID-GROUP).
- **Variants/used-by**: as above; no dedicated config key — the order is a property of the modeling class, keyed off `model_type` (+ `scoring_func` for DeepSeek).
- **Tensor names**: none.
- **Witnessed content**: the order as an explicit recipe fact per model_type.
- **Read-side mechanism**: replay the witnessed order exactly.
- **Laplace status**: **missing**.
- **Gate**: For a token where expert #k+1 carries nonzero softmax mass, replayed gate weights match HF (order (a) and (b) provably differ on this input; the replay must pick the right one).
- Src: [HF modeling_mixtral.py / modeling_qwen3_moe.py / modeling_gpt_oss.py](https://github.com/huggingface/transformers), [DeepWiki MoE architecture](https://deepwiki.com/huggingface/transformers/5.4-mixture-of-experts-architecture).

### MOE-NORM-GATES
- **ID**: MOE-NORM-GATES
- **What/Why**: After top-k, the surviving gate weights may be renormalized to sum to 1, and some families multiply the routed output by a fixed scaling factor. Skipping either rescales every MoE layer output by a token-dependent constant.
- **Formulation**: `g_i ← g_i / Σ_{j∈topk} g_j` (if enabled); DeepSeek additionally `y_routed ← routed_scaling_factor · y_routed`.
- **Variants/used-by**: Mixtral: always renormalizes. Qwen2/3-MoE: `norm_topk_prob: true/false` (Qwen3-MoE true). DeepSeek-V2/V3: `norm_topk_prob` + `routed_scaling_factor` (V3: 2.5).
- **Tensor names**: none — config scalars.
- **Witnessed content**: `norm_topk_prob` boolean, `routed_scaling_factor` scalar, per layer/model.
- **Read-side mechanism**: conditional renormalization then scalar multiply, in that order, before adding MOE-SHARED output.
- **Laplace status**: **missing**.
- **Gate**: Replayed gate weights sum to 1 iff the config says so, and the routed-branch norm matches HF to fp32 for a probe token.
- Src: [Fossies configuration_qwen3_moe.py](https://fossies.org/linux/transformers/src/transformers/models/qwen3_moe/configuration_qwen3_moe.py), [DeepSeek-V3 report](https://arxiv.org/pdf/2412.19437).

### MOE-SIGMOID-GROUP
- **ID**: MOE-SIGMOID-GROUP
- **What/Why**: DeepSeek-V3's router scores experts with sigmoid (not softmax), adds a learned per-expert bias *only for selection* (aux-loss-free load balancing), and constrains selection to the best few of `n_group` expert groups. Three coupled deviations; replaying V2-style softmax routing on a V3 checkpoint selects different experts.
- **Formulation**: `s = σ(W_r·x)`; selection score `s' = s + e_score_correction_bias`; keep `topk_group` of `n_group` groups by summed top-2 `s'` per group; top-k experts within kept groups by `s'`; gate weights use *unbiased* `s` (renormalized, × `routed_scaling_factor`).
- **Variants/used-by**: DeepSeek-V3/R1 (`scoring_func: "sigmoid"`, `topk_method: "noaux_tc"`, `n_group: 8`, `topk_group: 4`); V2 used softmax + aux loss; V4 drops the group constraint.
- **Tensor names**: `model.layers.{L}.mlp.gate.weight`, `model.layers.{L}.mlp.gate.e_score_correction_bias` `[n_routed_experts]`.
- **Witnessed content**: router weight, correction bias vector, `scoring_func`, `n_group`, `topk_group`, `routed_scaling_factor`, `norm_topk_prob`.
- **Read-side mechanism**: sigmoid → bias-adjusted grouped top-k for *selection*, unbiased scores for *weights* — the bias must not leak into gate values.
- **Laplace status**: **missing**.
- **Gate**: For a probe token, replay selects the same 8 experts as HF AND its gate weights are computed from unbiased sigmoid scores (a bias-in-weights bug changes the output measurably).
- Src: [DeepSeek-V3 report](https://arxiv.org/pdf/2412.19437), [DeepSeek-V3 model.py](https://github.com/deepseek-ai/DeepSeek-V3/blob/main/inference/model.py), [vLLM PR #13474](https://github.com/vllm-project/vllm/pull/13474).

### MOE-SHARED
- **ID**: MOE-SHARED
- **What/Why**: One or more experts that run for *every* token alongside the routed ones, holding common knowledge so routed experts can specialize. Their output adds unconditionally; Qwen2-MoE additionally gates it with a learned sigmoid.
- **Formulation**: `y = Σ routed + SharedExpert(x)` (DeepSeek, `n_shared_experts=1` fused as one wider FFN); Qwen2-MoE: `y = Σ routed + σ(W_sg·x) · SharedExpert(x)`.
- **Variants/used-by**: DeepSeek-V2/V3 (`n_shared_experts`), Qwen1.5/2-MoE (`shared_expert_intermediate_size` + gate; Qwen3-MoE has NO shared expert), Granite-MoE-Shared (`shared_intermediate_size`).
- **Tensor names**: DeepSeek `model.layers.{L}.mlp.shared_experts.{gate_proj,up_proj,down_proj}.weight`; Qwen2-MoE `mlp.shared_expert.{gate_proj,up_proj,down_proj}.weight` + `mlp.shared_expert_gate.weight` `[1 × d]`; Granite `shared_mlp.*`.
- **Witnessed content**: shared-expert matrices, the sigmoid gate vector (Qwen2-MoE), shared-expert count/width scalars.
- **Read-side mechanism**: unconditional FFN-GLU-SWIGLU replay added to the routed sum (through the sigmoid gate where witnessed).
- **Laplace status**: **missing**.
- **Gate**: Replay of a DeepSeek MoE layer includes the shared branch (zeroing it must change output); Qwen3-MoE replay must NOT include one.
- Src: [DeepSeek-V3 report](https://arxiv.org/pdf/2412.19437), [HF GraniteMoeShared docs](https://huggingface.co/docs/transformers/v4.50.0/en/model_doc/granitemoeshared).

### MOE-EXPERT-CHOICE
- **ID**: MOE-EXPERT-CHOICE
- **What/Why**: The inverted routing regime: experts pick their top tokens (fixed per-expert capacity) instead of tokens picking experts. Great load balance, but non-causal — an expert's choice depends on the whole batch — so no mainstream autoregressive open checkpoint ships it. Recorded here so the ETL can *recognize and refuse* it rather than misreplay.
- **Formulation**: per expert e: `T(e) = TopK_over_tokens(scores[:, e], capacity)`; token output sums contributions of experts that chose it (a token may get 0..E experts).
- **Variants/used-by**: Google Expert-Choice MoE / Brainformer (research); all cataloged open LLM checkpoints (Mixtral, Qwen, DeepSeek, GPT-OSS, Granite) are token-choice.
- **Tensor names**: same router shape as MOE-ROUTER; the regime is a modeling-class property.
- **Witnessed content**: routing regime as a recipe fact (`token_choice` for everything currently supported).
- **Read-side mechanism**: token-choice replay only; expert-choice input should hard-fail ingest with a named unsupported-regime error, never silently replay as token-choice.
- **Laplace status**: **missing** (no MoE at all; regime flag also absent).
- **Gate**: Ingest of a token-choice model attests `routing=token_choice`; a synthetic expert-choice config is rejected, not mis-ingested.
- Src: Zhou et al. 2022 (Expert Choice Routing), [MoE routing practitioner's guide](https://frontiercheckpoint.com/explainers/moe-routing-practitioners-guide/).

### MOE-AUXLOSS
- **ID**: MOE-AUXLOSS
- **What/Why**: Load-balancing and router z-losses exist only to shape training gradients; at inference they compute nothing. The ETL must know their config keys so it can classify them as inference no-ops instead of unknown primitives.
- **Formulation**: training-only: `L_aux = α·E·Σ f_e·P_e` (Switch load balance), `L_z = β·(logsumexp(logits))²` (router z-loss). Inference: identity.
- **Variants/used-by**: config keys `router_aux_loss_coef` (Mixtral/Qwen-MoE), `aux_loss_alpha`, `seq_aux` (DeepSeek), `router_jitter_noise` (Mixtral — also a training/eval-off no-op), `output_router_logits`. DeepSeek-V3's *replacement* (bias-based, aux-free) does affect inference — see MOE-SIGMOID-GROUP.
- **Tensor names**: none.
- **Witnessed content**: keys recorded verbatim in the witnessed recipe, flagged `inference_noop=true`.
- **Read-side mechanism**: none — asserting the absence of any effect is the mechanism.
- **Laplace status**: **missing**.
- **Gate**: Replay output is bit-identical whether these keys are present or stripped from the witnessed recipe.
- Src: Fedus et al. 2021 (Switch), [Mixtral modeling code](https://github.com/huggingface/transformers).

### MOE-LAYOUT
- **ID**: MOE-LAYOUT
- **What/Why**: Expert weights ship in two physical shapes: one module per expert (Mixtral/DeepSeek: hundreds of small 2-D tensors) or one packed 3-D tensor for all experts (GPT-OSS, Qwen3.5-MoE: `[E, …, …]`). Same math, different scrape paths; packed layouts may also transpose the per-expert matrix and interleave gate/up.
- **Formulation**: per-expert: `experts.{E}.W ∈ R[out × in]` each. Packed: `experts.gate_up_proj ∈ R[E × d × 2·d_ff]` (note **[in, out] per expert slice** in GPT-OSS — einsum convention, not nn.Linear), `experts.down_proj ∈ R[E × d_ff × d]`.
- **Variants/used-by**: per-expert modules: Mixtral (`experts.{E}.w1/w2/w3`), Qwen2/3-MoE + DeepSeek (`experts.{E}.gate_proj/up_proj/down_proj`), Granite (packed `input_linear/output_linear`). Packed 3-D: GPT-OSS, Qwen3.5-MoE (`experts.gate_up_proj`, `experts.down_proj`). Expert-parallel sharding never changes checkpoint names — it is a runtime concern.
- **Tensor names**: as above; discriminator is tensor rank (2-D per expert vs 3-D packed) plus presence of `{E}` in the name.
- **Witnessed content**: every expert's matrices under a per-expert identity (layer, expert index); the physical layout itself is scrape plumbing, NOT content — packed and per-expert checkpoints of the same weights must produce identical attestations.
- **Read-side mechanism**: replay indexes experts logically; layout differences are fully absorbed at ingest (including any per-slice transpose and FFN-GLU-CLAMP interleave).
- **Laplace status**: **missing**.
- **Gate**: Re-serializing a packed checkpoint as per-expert modules and re-ingesting yields byte-identical substrate content (hash collision at every expert entity).
- Src: [Qwen3.5-MoE modeling](https://github.com/huggingface/transformers/blob/main/src/transformers/models/qwen3_5_moe/modeling_qwen3_5_moe.py), [gpt-oss internals](https://medium.com/@chris.p.hughes10/inside-openais-gpt-oss-how-production-moe-really-works-cfa5f6a23caa).

### MOE-DENSE-MIX
- **ID**: MOE-DENSE-MIX
- **What/Why**: Many MoE models keep some layers as ordinary dense FFNs — usually the first few (routing is unstable near the embedding) or every Nth. The ETL must route each layer to the right decomposition (dense FFN-GLU-SWIGLU vs MoE) by these keys.
- **Formulation**: layer L is dense iff `L < first_k_dense_replace` (DeepSeek), or `L ∈ mlp_only_layers`, or `(L+1) % decoder_sparse_step ≠ 0` (Qwen-MoE).
- **Variants/used-by**: DeepSeek-V3 `first_k_dense_replace: 3`; Qwen-MoE `decoder_sparse_step`, `mlp_only_layers`; Mixtral/GPT-OSS: all layers sparse.
- **Tensor names**: dense layers carry plain `mlp.{gate_proj,up_proj,down_proj}`; sparse layers carry `mlp.gate` + `mlp.experts.*` — the name shape per layer is itself the witness.
- **Witnessed content**: the per-layer dense/sparse map (recipe), consistent with the observed tensor census.
- **Read-side mechanism**: per-layer dispatch to dense-FFN or MoE replay.
- **Laplace status**: **missing**.
- **Gate**: For DeepSeek-V3, layers 0–2 replay as dense FFN and layer 3+ as MoE; a census/config disagreement fails ingest loudly.
- Src: [DeepSeek-V3 config](https://deepwiki.com/deepseek-ai/DeepSeek-V3/6.1-configuration-options), [Qwen3-MoE config](https://fossies.org/linux/transformers/src/transformers/models/qwen3_moe/configuration_qwen3_moe.py).

---

## 3. Normalization

### NORM-LAYERNORM
- **ID**: NORM-LAYERNORM
- **What/Why**: Classic normalization: subtract the mean across the hidden dim, divide by the standard deviation, then apply a learned per-channel scale (γ) and shift (β). It keeps activation magnitudes stable layer to layer; without it deep transformers do not train, and without *exact* ε/mean semantics replay drifts.
- **Formulation**: `y = ((x − μ) / √(σ² + ε)) ⊙ γ + β`, `μ, σ²` = mean/variance over the hidden dimension (biased variance, i.e. divide by d).
- **Variants/used-by**: GPT-2, BERT/RoBERTa, OPT, Phi-1/2, Falcon, GPT-J/NeoX. Config: `layer_norm_eps` (BERT: 1e-12) / `layer_norm_epsilon` (GPT-2: 1e-5).
- **Tensor names**: GPT-2 `h.{L}.ln_1.{weight,bias}`, `h.{L}.ln_2.*`, final `ln_f.*`; BERT `…LayerNorm.{weight,bias}`; Phi `model.layers.{L}.input_layernorm.{weight,bias}`, final `model.final_layernorm.*`.
- **Witnessed content**: γ AND β vectors per norm, ε scalar, and which norm sites exist (see NORM-PRE/POST/SANDWICH).
- **Read-side mechanism**: fp32 mean subtraction, biased variance, rsqrt(σ²+ε), scale, shift — matching torch `F.layer_norm` semantics.
- **Laplace status**: **partial** — Phi/Bert `PerLayerNorms`/`FinalNorm` name the γ tensors and `RmsNorm=false` flags the family; β is reachable via `BiasOf()` but not listed as a witnessed slot, and ε is not captured at all.
- **Gate**: Single-norm replay matches `torch.nn.functional.layer_norm` bitwise at fp32, including an input with large mean offset (catches a missing mean-subtract, i.e. an RMSNorm substitution).
- Src: Ba et al. 2016, [HF BERT/GPT-2 configs](https://huggingface.co/docs/transformers).

### NORM-RMS
- **ID**: NORM-RMS
- **What/Why**: LayerNorm minus the mean subtraction and the β shift: divide by root-mean-square, multiply by a gain. Cheaper, and what every modern decoder (Llama onward) uses. Replaying it as LayerNorm (or vice versa) shifts every activation with nonzero mean.
- **Formulation**: `y = (x / √(mean(x²) + ε)) ⊙ g`. No μ, no β. HF computes the reduction in fp32 then casts back.
- **Variants/used-by**: Llama, Mistral/Mixtral, Qwen2/3, DeepSeek, T5 ("T5LayerNorm" IS RMSNorm — the original), Gemma (offset variant, see NORM-RMS-OFFSET), OLMo2. Config: `rms_norm_eps` (1e-5 or 1e-6), T5 `layer_norm_epsilon: 1e-6`.
- **Tensor names**: `model.layers.{L}.input_layernorm.weight`, `post_attention_layernorm.weight`, final `model.norm.weight` (no `.bias` siblings — their absence is the family signature).
- **Witnessed content**: gain vector per norm, ε, the RMS (no-mean) semantics as a recipe fact.
- **Read-side mechanism**: fp32 `x·rsqrt(mean(x²)+ε)` then gain, matching `LlamaRMSNorm`.
- **Laplace status**: **partial** — `RmsNorm=true` + norm tensor names for Llama/Qwen2; ε unwitnessed; fp32-compute-then-cast convention unrecorded.
- **Gate**: Replay matches `LlamaRMSNorm.forward` bitwise at fp32; on an input of all 5.0s, output must NOT be zero (proves no mean subtraction).
- Src: Zhang & Sennrich 2019, [modeling_llama.py](https://github.com/huggingface/transformers/blob/main/src/transformers/models/llama/modeling_llama.py).

### NORM-RMS-OFFSET
- **ID**: NORM-RMS-OFFSET
- **What/Why**: Gemma stores its RMSNorm gain as an offset from 1: the applied scale is `(1 + weight)`, with weights initialized near 0. Reading the stored tensor as a direct gain (NORM-RMS) multiplies activations by ~0 and destroys the model — the single most notorious conversion bug in the Gemma family.
- **Formulation**: `y = (x / √(mean(x²) + ε)) ⊙ (1 + g)`; HF computes the whole product in fp32 before casting.
- **Variants/used-by**: Gemma 1/2/3 (all norms, incl. QK-norms in Gemma3). No config key — a modeling-class convention keyed to `model_type: gemma*`.
- **Tensor names**: same patterns as NORM-RMS (`input_layernorm.weight` etc.) — indistinguishable by name; the +1 is invisible in the checkpoint.
- **Witnessed content**: gain vector verbatim (witnessed layer) plus an explicit `gain_convention=one_plus_weight` recipe fact (calculated/recipe layer). Never bake `1+g` into the stored content — that would break cross-checkpoint hash identity with a raw-convention model that happened to share values.
- **Read-side mechanism**: apply `(1+g)` at replay; fp32 end-to-end then downcast.
- **Laplace status**: **missing** — no Gemma profile; the Llama fallback would silently apply the raw-gain convention.
- **Gate**: Gemma norm replay of a unit-RMS input with g≈0 returns ≈x (not ≈0); output matches HF `GemmaRMSNorm` bitwise at fp32.
- Src: [HF issue #40224](https://github.com/huggingface/transformers/issues/40224), [Gemma modeling](https://github.com/huggingface/transformers/blob/main/src/transformers/models/gemma/modeling_gemma.py).

### NORM-NONPARAM
- **ID**: NORM-NONPARAM
- **What/Why**: A LayerNorm with no learned parameters at all (γ=1, β=0 fixed) — OLMo-1's stability choice. The checkpoint contains *no tensor* for these norm sites; the ETL must witness the site from config/modeling class, not from a tensor census.
- **Formulation**: `y = (x − μ) / √(σ² + ε)` — NORM-LAYERNORM with γ≡1, β≡0, non-elementwise-affine.
- **Variants/used-by**: OLMo-1 (`model_type: olmo`, modeling uses `F.layer_norm` with `weight=None`).
- **Tensor names**: none — that is the point.
- **Witnessed content**: norm site existence + `parameterless=true` + ε, as recipe facts.
- **Read-side mechanism**: mean/var normalize with no scale/shift.
- **Laplace status**: **missing**.
- **Gate**: OLMo-1 layer replay applies normalization at every site despite zero norm tensors in the checkpoint (skipping them fails the block-output comparison).
- Src: OLMo paper (Groeneveld et al. 2024), [HF modeling_olmo.py](https://github.com/huggingface/transformers).

### NORM-PRE
- **ID**: NORM-PRE
- **What/Why**: Pre-LN: normalize the *input* to each sublayer; the residual stream itself is never normalized mid-layer. This is the modern default (stable gradients at depth). Placement is topology, not a tensor — same tensors in post-LN order compute a different function.
- **Formulation**: `h = x + Attn(N1(x)); y = h + FFN(N2(h))`.
- **Variants/used-by**: GPT-2 onward: Llama/Mistral/Qwen/Phi/GPT-J/Gemma (Gemma adds post-norms too — NORM-SANDWICH). Two norms per layer (one in FFN-PARALLEL models).
- **Tensor names**: `input_layernorm` + `post_attention_layernorm` (the latter is *pre-FFN* despite its name — it normalizes the FFN input, not attention output, in Llama-class models).
- **Witnessed content**: per-layer norm census + an explicit placement fact (`pre_ln`).
- **Read-side mechanism**: replay per the formulation; residual adds take the raw stream, never the normed value.
- **Laplace status**: **partial** — norm names present for all four profiles; placement order is implicit convention, not attested (Bert's post-LN and Llama's pre-LN are distinguished only by name patterns).
- **Gate**: Zeroing N2's effect on the residual path is impossible in replay: `y − h` must equal `FFN(N2(h))` exactly, and `h` must contain un-normed x.
- Src: Xiong et al. 2020 (On Layer Normalization in the Transformer Architecture).

### NORM-POST
- **ID**: NORM-POST
- **What/Why**: The original placement: add the residual first, *then* normalize the sum. BERT and GPT-1 use it. Same tensor names as pre-LN in BERT's case — only the modeling class tells you the order — so it must be witnessed explicitly.
- **Formulation**: `h = N1(x + Attn(x)); y = N2(h + FFN(h))`.
- **Variants/used-by**: BERT/RoBERTa (`attention.output.LayerNorm` after the attn residual, `output.LayerNorm` after the FFN residual), original Transformer, GPT-1.
- **Tensor names**: BERT `encoder.layer.{L}.attention.output.LayerNorm.*`, `encoder.layer.{L}.output.LayerNorm.*`.
- **Witnessed content**: placement fact (`post_ln`) + the two norms' γ/β/ε.
- **Read-side mechanism**: residual add *before* normalization at both sites.
- **Laplace status**: **partial** — Bert profile lists both norm tensors, but nothing records that they run post-residual; a pre-LN replay of BERT would be silently wrong.
- **Gate**: BERT single-layer replay matches HF fp32; the same tensors wired pre-LN must fail (order is load-bearing).
- Src: Vaswani et al. 2017, [HF modeling_bert.py](https://github.com/huggingface/transformers/blob/main/src/transformers/models/bert/modeling_bert.py).

### NORM-POST-REORDERED
- **ID**: NORM-POST-REORDERED
- **What/Why**: OLMo2's hybrid: normalize the sublayer *output* (not input, not the post-residual sum), then add the residual. No input norm at all. A third distinct topology sharing tensor names with the other two.
- **Formulation**: `h = x + N1(Attn(x)); y = h + N2(FFN(h))`.
- **Variants/used-by**: OLMo2 (`post_attention_layernorm`, `post_feedforward_layernorm`; NO `input_layernorm`); Gemma2's post-norms follow the same output-norm-then-residual pattern (combined with pre-norms — NORM-SANDWICH).
- **Tensor names**: `model.layers.{L}.post_attention_layernorm.weight`, `model.layers.{L}.post_feedforward_layernorm.weight`.
- **Witnessed content**: placement fact (`post_reordered`) + gains + ε; the *absence* of input_layernorm is part of the witness.
- **Read-side mechanism**: sublayer → norm → residual add, both sublayers.
- **Laplace status**: **missing**.
- **Gate**: OLMo2 layer replay feeds *raw* x into attention (no input norm) and matches HF fp32.
- Src: [OLMo2 paper](https://arxiv.org/pdf/2501.00656), [vLLM olmo2 model](https://docs.vllm.ai/en/stable/api/vllm/model_executor/models/olmo2/).

### NORM-SANDWICH
- **ID**: NORM-SANDWICH
- **What/Why**: Gemma 2/3 norm *both* the input and the output of each sublayer — four RMSNorms per layer. The post-norms run on the sublayer output before the residual add. Skipping the extra pair (easy, since Llama-class code only expects two) breaks every layer.
- **Formulation**: `h = x + N_post_attn(Attn(N_in(x))); y = h + N_post_ffn(FFN(N_pre_ffn(h)))`.
- **Variants/used-by**: Gemma 2/3 (all norms are NORM-RMS-OFFSET). Signature keys: none — presence of the two extra tensor families is the signal.
- **Tensor names**: `model.layers.{L}.input_layernorm.weight`, `post_attention_layernorm.weight`, `pre_feedforward_layernorm.weight`, `post_feedforward_layernorm.weight`.
- **Witnessed content**: all four gains + ε + the sandwich placement fact. NOTE: in Gemma2, `post_attention_layernorm` norms attention *output* — same name as Llama's pre-FFN norm, entirely different role. Name-based mapping without placement facts mis-witnesses this.
- **Read-side mechanism**: four norm applications per layer in the exact order above.
- **Laplace status**: **missing** — `PerLayerNorms` lists could hold four names, but no profile does, and no placement semantics exist.
- **Gate**: Gemma2 layer replay applies exactly 4 norms; ablating either post-norm produces a mismatch vs HF fp32.
- Src: [Gemma 2 paper](https://arxiv.org/html/2408.00118v1), [HF Gemma2 docs](https://huggingface.co/docs/transformers/main/en/model_doc/gemma2).

### NORM-FINAL
- **ID**: NORM-FINAL
- **What/Why**: One last norm on the residual stream after the top layer, before the lm_head. Every pre-LN decoder has it (post-LN encoders like BERT do not). Skipping it feeds un-normalized activations into the unembedding and scrambles logits.
- **Formulation**: `logits_input = N_f(h_L)` per the family's norm type (NORM-LAYERNORM / NORM-RMS / NORM-RMS-OFFSET).
- **Variants/used-by**: GPT-2 `ln_f`, Llama/Qwen/Mistral `model.norm`, Phi `model.final_layernorm`, Gemma `model.norm` (1+w convention). BERT: none (its embedding LayerNorm is a different site — NORM-EMBED).
- **Tensor names**: `ln_f.{weight,bias}` / `model.norm.weight` / `model.final_layernorm.{weight,bias}`.
- **Witnessed content**: final-norm tensor(s) + ε + type.
- **Read-side mechanism**: one norm application between last block and EMB-TIED matvec.
- **Laplace status**: **covered** (names) / **partial** (semantics) — all four profiles fill `FinalNorm`; but Bert's `FinalNorm` points at `embeddings.LayerNorm.weight`, which is the *embedding* norm, not a final norm — a site misclassification (see NORM-EMBED). ε unwitnessed.
- **Gate**: Decoder replay applies the final norm exactly once, after layer L−1 and before lm_head; logits match HF fp32.
- Src: [HF modeling_llama.py / modeling_gpt2.py](https://github.com/huggingface/transformers).

### NORM-EMBED
- **ID**: NORM-EMBED
- **What/Why**: BERT normalizes (and dropout-regularizes) the *sum* of word + position + token-type embeddings before layer 0. It is the first norm the input meets; decoders don't have it. Misfiling it as a "final norm" applies it at the wrong end of the network.
- **Formulation**: `e = LN(E_word[id] + E_pos[pos] + E_type[seg])` — full NORM-LAYERNORM with γ/β/ε=1e-12.
- **Variants/used-by**: BERT/RoBERTa/ELECTRA family. Config: `layer_norm_eps`. (LLaMA-class models have nothing at this site; Gemma has EMB-SCALE instead.)
- **Tensor names**: `embeddings.LayerNorm.{weight,bias}` (BERT), `embeddings.LayerNorm.*` (RoBERTa).
- **Witnessed content**: γ, β, ε, and the site fact (`embedding_norm`, applied after the three-way embedding sum).
- **Read-side mechanism**: sum the three embedding rows, LayerNorm the sum, feed layer 0.
- **Laplace status**: **partial/misfiled** — the tensor is scraped (as Bert's `FinalNorm`), but at the wrong site: it must be attested as pre-layer-0 embedding norm, not post-encoder final norm.
- **Gate**: BERT replay normalizes the embedding sum before layer 0 (and applies no norm after the last layer); layer-0 input matches HF fp32.
- Src: [HF modeling_bert.py](https://github.com/huggingface/transformers/blob/main/src/transformers/models/bert/modeling_bert.py).

### NORM-QK
- **ID**: NORM-QK
- **What/Why**: An RMSNorm applied to query and key vectors inside attention (per-head or per-projection) to stop attention logits exploding. Owned by the attention doc; indexed here because its tensors live in the same norm census and its Gemma3 instances use the (1+w) convention.
- **Formulation**: `q ← RMSNorm_g_q(q)`, `k ← RMSNorm_g_k(k)` before RoPE/dot-product (details in attention doc).
- **Variants/used-by**: Qwen3 (`self_attn.q_norm.weight`, `k_norm.weight`), OLMo2, Gemma3, GPT-OSS-adjacent stacks. Cross-ref: attention/positional primitive index.
- **Tensor names**: `model.layers.{L}.self_attn.{q_norm,k_norm}.weight`.
- **Witnessed content**: q/k gain vectors + ε + head-dim vs full-dim application fact.
- **Read-side mechanism**: see attention doc.
- **Laplace status**: **missing** — Qwen2 profile predates Qwen3; no q_norm/k_norm slots.
- **Gate**: Norm census of a Qwen3 checkpoint classifies q_norm/k_norm as attention-internal norms, not per-layer stream norms.
- Src: [OLMo2 paper](https://arxiv.org/pdf/2501.00656), [Raschka architecture comparison](https://magazine.sebastianraschka.com/p/the-big-llm-architecture-comparison).

### NORM-EPS
- **ID**: NORM-EPS
- **What/Why**: The ε inside every norm's rsqrt differs by family and by *orders of magnitude* (1e-12 BERT vs 1e-5 GPT-2/Llama vs 1e-6 Gemma/T5). It is the smallest witnessed scalar with the largest silent-drift potential — near-zero-variance inputs make replay divergence proportional to the ε mismatch.
- **Formulation**: the `+ε` inside NORM-LAYERNORM/NORM-RMS denominators; variance vs RMS placement follows the norm type.
- **Variants/used-by**: config keys `layer_norm_eps` (BERT 1e-12), `layer_norm_epsilon` (GPT-2 1e-5, T5 1e-6), `rms_norm_eps` (Llama2 1e-5, Llama3 1e-5, Qwen2 1e-6, Gemma 1e-6, DeepSeek 1e-6), `norm_eps`/`layernorm_epsilon` (misc).
- **Tensor names**: none — config scalar.
- **Witnessed content**: verbatim key name + value per model (one ε may serve all sites; witness per-site if a model ever splits them).
- **Read-side mechanism**: use the witnessed ε, never a default.
- **Laplace status**: **missing** — no ε capture anywhere in ArchitectureProfile.
- **Gate**: Replay of a norm on a near-constant vector (variance ≈ 0) matches HF fp32 — this input isolates ε; any hardcoded default fails on at least one family.
- Src: HF per-model configuration classes.

---

## 4. Embeddings / unembedding

### EMB-WORD
- **ID**: EMB-WORD
- **What/Why**: The vocab × hidden matrix that turns a token id into the layer-0 input vector — the model's entire lexical ground floor, and (when tied, EMB-TIED) also its output vocabulary geometry. This is the tensor Laplace's SIMILAR_TO plane already scrapes.
- **Formulation**: `e = E[id]`, `E ∈ R[vocab × d]` (row lookup, no matmul).
- **Variants/used-by**: universal. Config: `vocab_size`, `hidden_size`.
- **Tensor names**: Llama-class `model.embed_tokens.weight`; GPT-2 `wte.weight`; BERT `embeddings.word_embeddings.weight`; T5 `shared.weight`; NeoX `gpt_neox.embed_in.weight`.
- **Witnessed content**: every row under the token's content identity (tokens are the SAME content entities the text lanes mint); `vocab_size`, `hidden_size` scalars.
- **Read-side mechanism**: row lookup; plus whatever EMB-SCALE/NORM-EMBED the family applies before layer 0.
- **Laplace status**: **covered** — `EmbedTokens` in all four profiles; `SelfSimilarityPath("SIMILAR_TO")` consumes it.
- **Gate**: For a probe token id, the substrate reproduces the checkpoint's embedding row exactly (lossless witness), and SIMILAR_TO attestations derive from those exact rows.
- Src: [HF modeling conventions](https://huggingface.co/docs/transformers).

### EMB-TIED
- **ID**: EMB-TIED
- **What/Why**: Most decoders reuse the embedding matrix as the output projection (logits = h·Eᵀ) instead of storing a separate lm_head — half the vocab parameters, and the checkpoint may simply *omit* the lm_head tensor. An ETL that requires `lm_head.weight` breaks on tied models; one that scrapes both as independent content double-witnesses one tensor.
- **Formulation**: tied: `logits = N_f(h) · Eᵀ`; untied: `logits = N_f(h) · W_lm` with independent `W_lm ∈ R[vocab × d]`.
- **Variants/used-by**: tied: GPT-2 (always), Gemma (always), Qwen2 small variants, Llama 3.2 1B/3B, Phi-3-mini — `tie_word_embeddings: true`; safetensors then usually omits `lm_head.weight`. Untied: Llama 2/3 70B-class, Qwen2 7B+, Phi-2 (`lm_head` with bias!), DeepSeek.
- **Tensor names**: `lm_head.weight` (present iff untied, usually); GPT-2 `wte.weight` doubles as unembedding; Phi-2 `lm_head.{weight,bias}`.
- **Witnessed content**: `tie_word_embeddings` verbatim; for untied heads, W_lm as its own content (+ bias if present); for tied, a tie fact referencing the EMB-WORD entity — never a duplicated copy.
- **Read-side mechanism**: matvec against Eᵀ or W_lm per the witnessed tie fact; add head bias if witnessed.
- **Laplace status**: **partial** — `LmHead` slot exists (nullable; Bert=null) but there is no tie handling: a tied checkpoint omitting `lm_head.weight` finds no tensor for Llama's declared `lm_head.weight`, and no tie fact is attested.
- **Gate**: Ingesting a tied model produces zero duplicate content (tie = one entity, one reference) and replayed logits equal `h·Eᵀ`; ingesting Phi-2 applies the lm_head bias.
- Src: Press & Wolf 2017 (weight tying), HF `tie_word_embeddings` config docs.

### EMB-SCALE
- **ID**: EMB-SCALE
- **What/Why**: Gemma multiplies embeddings by √hidden_size on the way into layer 0 (keeps residual-stream magnitudes balanced against sublayer outputs); Granite exposes the same idea as an explicit `embedding_multiplier`. Forgetting the multiplier scales the whole forward pass wrong from token one.
- **Formulation**: `x0 = √d · E[id]` (Gemma; the scalar is round-tripped through the model dtype — bf16 rounds √3072=55.4256→55.5, and exact replay must reproduce that rounding); Granite: `x0 = embedding_multiplier · E[id]`.
- **Variants/used-by**: Gemma 1/2/3 (implicit, modeling-class), Granite (`embedding_multiplier`, e.g. 12.0), T5 historically scaled at attention instead. Not present in Llama/GPT-2/BERT.
- **Tensor names**: none — scalar recipe fact.
- **Witnessed content**: the multiplier value AND its dtype-rounding convention (Gemma: cast to embedding dtype before multiply).
- **Read-side mechanism**: scalar multiply after row lookup, before layer 0 (after NORM-EMBED does not apply — these families are disjoint).
- **Laplace status**: **missing**.
- **Gate**: Gemma layer-0 input replay equals HF including the bf16-rounded 55.5 factor for d=3072 (an unrounded 55.4256 factor fails the bitwise check in bf16).
- Src: [HF issue #38702](https://github.com/huggingface/transformers/issues/38702), [Graphcore Gemma walkthrough](https://graphcore-research.github.io/posts/gemma/), [Granite config](https://github.com/huggingface/transformers/blob/main/src/transformers/models/granite/configuration_granite.py).

### EMB-POS-LEARNED
- **ID**: EMB-POS-LEARNED
- **What/Why**: A learned table of position → vector, added to word embeddings before layer 0 (GPT-2/BERT era). Rotary/ALiBi replaced it inside attention — those are the attention doc's primitives; this entry covers only the additive learned table, which caps context at the table length.
- **Formulation**: `x0 = E_word[id] + E_pos[position]` (+ EMB-TOKENTYPE for BERT, then NORM-EMBED).
- **Variants/used-by**: GPT-2 (`wpe`), BERT (`position_embeddings`, `position_embedding_type: "absolute"`), OPT (`embed_positions`, with a +2 offset quirk in position indexing), RoBERTa (offset `padding_idx+1`). Config: `max_position_embeddings`, `n_positions`. RoPE/ALiBi/NoPE → attention doc.
- **Tensor names**: `wpe.weight` (GPT-2), `embeddings.position_embeddings.weight` (BERT), `model.decoder.embed_positions.weight` (OPT).
- **Witnessed content**: the position table rows (positions are coordinate-like content, model is the source), `max_position_embeddings`, index-offset quirks (OPT +2, RoBERTa pad offset) as recipe facts.
- **Read-side mechanism**: add the position row for the token's absolute index before layer 0.
- **Laplace status**: **missing** — no wpe/position_embeddings slot in any profile (Bert profile omits `embeddings.position_embeddings` and `token_type_embeddings` entirely).
- **Gate**: GPT-2 layer-0 input for (token t, position p) equals `wte[t]+wpe[p]` exactly; OPT replay honors the +2 offset.
- Src: [HF modeling_gpt2.py / modeling_opt.py](https://github.com/huggingface/transformers).

### EMB-TOKENTYPE
- **ID**: EMB-TOKENTYPE
- **What/Why**: BERT's segment embedding: a tiny table (usually 2 rows) marking "sentence A vs sentence B", added into the embedding sum. Zero-segment inputs still add row 0 — it is never optional in replay even when all tokens are segment 0.
- **Formulation**: `x0 += E_type[segment_id]`, `E_type ∈ R[type_vocab_size × d]`.
- **Variants/used-by**: BERT (`type_vocab_size: 2`), ELECTRA, some rerankers; RoBERTa has the table with size 1 (degenerate). Decoders: absent.
- **Tensor names**: `embeddings.token_type_embeddings.weight`.
- **Witnessed content**: the table rows + `type_vocab_size`.
- **Read-side mechanism**: add the segment row into the pre-norm embedding sum.
- **Laplace status**: **missing** (Bert profile does not map it).
- **Gate**: BERT replay with all-zero segment ids still adds `E_type[0]` (omitting it must fail the layer-0 comparison).
- Src: [HF modeling_bert.py](https://github.com/huggingface/transformers/blob/main/src/transformers/models/bert/modeling_bert.py), Devlin et al. 2018.

### EMB-MLMHEAD
- **ID**: EMB-MLMHEAD
- **What/Why**: BERT's output head is not a bare unembedding: hidden states pass through a dense layer + activation + LayerNorm ("transform") before the tied decoder matmul, plus a standalone per-vocab bias. Scraping BERT for token predictions without the transform stack replays a different model.
- **Formulation**: `t = LN(gelu(W_t·h + b_t)); logits = t·Eᵀ + b_vocab`.
- **Variants/used-by**: BERT/RoBERTa MLM checkpoints (`BertForMaskedLM`); absent in plain `bert-base` feature-extractor checkpoints (the ETL must handle both).
- **Tensor names**: `cls.predictions.transform.dense.{weight,bias}`, `cls.predictions.transform.LayerNorm.{weight,bias}`, `cls.predictions.decoder.weight` (tied to word_embeddings), `cls.predictions.bias` (RoBERTa: `lm_head.dense.*`, `lm_head.layer_norm.*`, `lm_head.bias`).
- **Witnessed content**: transform weights/biases, LN γ/β, vocab bias; tie fact to EMB-WORD for the decoder.
- **Read-side mechanism**: dense → exact GELU → LayerNorm → tied matmul → +bias.
- **Laplace status**: **missing** — Bert profile sets `LmHead = null`, discarding the head entirely.
- **Gate**: MLM logits for a masked probe token match HF fp32 (a bare `h·Eᵀ` head fails).
- Src: [HF modeling_bert.py `BertLMPredictionHead`](https://github.com/huggingface/transformers/blob/main/src/transformers/models/bert/modeling_bert.py).

### EMB-SOFTCAP
- **ID**: EMB-SOFTCAP
- **What/Why**: Gemma 2 squashes final logits through a scaled tanh so no logit exceeds ±cap — changes every probability, especially confident ones. (Its sibling on attention scores is the attention doc's problem.) Gemma 3 dropped the attention-side cap in favor of NORM-QK; the final-side key still must be read per checkpoint, not assumed by family.
- **Formulation**: `logits ← cap · tanh(logits / cap)`, `cap = final_logit_softcapping` (Gemma2: 30.0; attn-side `attn_logit_softcapping = 50.0`).
- **Variants/used-by**: Gemma 2 (both caps); Gemma 3 (none/null). Config keys: `final_logit_softcapping`, `attn_logit_softcapping` (null ⇒ no-op).
- **Tensor names**: none — config scalars.
- **Witnessed content**: cap values verbatim (including explicit null).
- **Read-side mechanism**: apply scaled tanh after the lm_head matvec, before sampling/log-prob.
- **Laplace status**: **missing**.
- **Gate**: Gemma2 replayed logits never exceed ±30 in magnitude and match HF fp32; Gemma3 replay applies no cap.
- Src: [Gemma 2 paper](https://arxiv.org/html/2408.00118v1), [HF Gemma2 docs](https://huggingface.co/docs/transformers/main/en/model_doc/gemma2).

### EMB-LOGIT-SCALE
- **ID**: EMB-LOGIT-SCALE
- **What/Why**: Some families multiply (Cohere) or divide (Granite) the final logits by a config constant — a temperature baked into the architecture. It cancels in argmax but changes every sampled distribution and log-prob; exact replay must apply it.
- **Formulation**: Cohere: `logits ← logit_scale · (N_f(h)·Eᵀ)` (`logit_scale` ≈ 0.0625, multiplicative). Granite: `logits ← logits / logits_scaling`.
- **Variants/used-by**: Cohere Command-R (`logit_scale`), Granite/Granite-MoE (`logits_scaling`); everyone else: 1.0/absent.
- **Tensor names**: none — config scalars.
- **Witnessed content**: the scalar + its multiply-vs-divide sense per family.
- **Read-side mechanism**: scalar op after lm_head (and after EMB-SOFTCAP if both ever co-occur — order per modeling source).
- **Laplace status**: **missing**.
- **Gate**: Replayed Granite log-probs match HF fp32 (an unscaled replay matches argmax but fails log-prob comparison — the gate must compare log-probs, not just top-1).
- Src: [HF Cohere docs](https://huggingface.co/docs/transformers/model_doc/cohere), [Granite config](https://github.com/huggingface/transformers/blob/main/src/transformers/models/granite/configuration_granite.py).

### EMB-VOCAB-PAD
- **ID**: EMB-VOCAB-PAD
- **What/Why**: Checkpoints often carry more embedding rows than the tokenizer has tokens — vocab rounded up for GPU-friendly matmuls or reserved slots (GPT-2: 50257 used of 50257 but pads to 50304 in some retrains; Llama-3: 128000 tokenizer + 256 reserved = 128256 rows). Padding rows still produce logits; exact replay keeps them, but the substrate must not mint token identities for them.
- **Formulation**: rows `[len(tokenizer), vocab_size)` are structurally present, semantically unreachable from input; their logits exist but their ids are never emitted by the tokenizer.
- **Variants/used-by**: Llama-3 reserved specials, MPT/NeoX padded-to-multiple vocab (`pad_vocab_size_multiple`-style), models resized after token additions. Signal: `config.vocab_size` > tokenizer vocab length.
- **Tensor names**: extra rows of EMB-WORD / lm_head — no separate tensor.
- **Witnessed content**: both sizes (config vocab_size, tokenizer length); padding rows witnessed as opaque model-plane rows (needed for bit-exact logit replay) but bound to reserved/padding identities, never to text-lane token content.
- **Read-side mechanism**: full-width lm_head matvec (softmax denominators include padding logits — dropping rows changes probabilities); sampling masks nothing unless HF does.
- **Laplace status**: **missing**.
- **Gate**: Replayed softmax probabilities match HF fp32 with padding rows included; no text-content entity exists for any id ≥ tokenizer length.
- Src: [Llama-3 tokenizer/config](https://huggingface.co/meta-llama), HF resize_token_embeddings docs.

---

## 5. Structural conventions

### RES-PRENORM
- **ID**: RES-PRENORM
- **What/Why**: The residual stream is the accumulator every sublayer adds into; the *order* of norm → sublayer → add (and what exactly gets added to what) is the block's wiring diagram. All four norm placements (NORM-PRE/POST/POST-REORDERED/SANDWICH) and FFN-PARALLEL are values of this one convention — it must be witnessed as an explicit per-model topology fact.
- **Formulation**: canonical pre-norm: `x_{l+1} = x_l + Sub(N(x_l))` — the add always takes the *raw* stream; the normed value never re-enters the stream except through the sublayer.
- **Variants/used-by**: enumerated topology values: `pre_ln` (Llama/GPT-2/Qwen), `post_ln` (BERT), `post_reordered` (OLMo2), `sandwich` (Gemma2/3), `parallel` (Phi/GPT-J), `parallel+dual-norm` (Falcon new-decoder). Signaled by model_type + norm census.
- **Tensor names**: none — pure topology.
- **Witnessed content**: one topology enum per model (recipe fact), consistent with the witnessed norm census.
- **Read-side mechanism**: block executor dispatches on the witnessed topology; there is exactly one implementation per topology value (one-implementation-per-fact law).
- **Laplace status**: **partial** — topology is *implied* by `PerLayerNorms` shape (2 names = sequential, 1 = parallel Phi) but never stated; Bert's post-LN is indistinguishable from pre-LN in the profile data model.
- **Gate**: For each supported model_type, a one-layer replay matches HF fp32; permuting the topology enum for the same tensors fails for every pair of distinct topologies.
- Src: Xiong et al. 2020; per-model HF modeling sources.

### RES-SCALE
- **ID**: RES-SCALE
- **What/Why**: Granite multiplies every sublayer output by a constant before the residual add (μP-style depth scaling as a shipped config value). One scalar, applied 2×n_layers times — omit it and activations blow past the trained regime.
- **Formulation**: `x_{l+1} = x_l + residual_multiplier · Sub(N(x_l))`.
- **Variants/used-by**: Granite (`residual_multiplier`, e.g. 0.22), Granite-MoE; related but distinct: `attention_multiplier` (attention doc). Default 1.0 elsewhere.
- **Tensor names**: none — config scalar.
- **Witnessed content**: the scalar, per model.
- **Read-side mechanism**: scalar multiply on every sublayer output before its residual add.
- **Laplace status**: **missing**.
- **Gate**: Granite one-layer replay matches HF fp32; multiplier=1.0 substitution fails.
- Src: [Granite config](https://github.com/huggingface/transformers/blob/main/src/transformers/models/granite/configuration_granite.py).

### DROPOUT-NOOP
- **ID**: DROPOUT-NOOP
- **What/Why**: Checkpoints carry dropout *rates* (embedding, residual, attention-prob, FFN sites) but dropout is identity at inference. The ETL must classify these keys as inference no-ops — witnessing them as verbatim recipe facts, computing nothing — so an unknown-key audit stays clean.
- **Formulation**: train: `y = mask ⊙ x / (1−p)`; inference (eval mode): `y = x`. Always.
- **Variants/used-by**: keys: `hidden_dropout_prob`, `attention_probs_dropout_prob` (BERT), `resid_pdrop`, `embd_pdrop`, `attn_pdrop` (GPT-2), `attention_dropout` (Llama/Mistral, usually 0.0), `moe_*_dropout` variants. Sites: after embedding sum, after attention probs, after each sublayer output, inside FFN.
- **Tensor names**: none.
- **Witnessed content**: keys + values verbatim, flagged `inference_noop=true`.
- **Read-side mechanism**: none — replay applies no dropout ever.
- **Laplace status**: **missing** (harmless today only because nothing reads config keys at all).
- **Gate**: Replay is deterministic and bit-identical across runs; stripping all dropout keys from the witnessed recipe changes nothing downstream.
- Src: HF per-model configs; torch eval-mode semantics.

### LAYOUT-ROWMAJOR
- **ID**: LAYOUT-ROWMAJOR
- **What/Why**: PyTorch `nn.Linear` stores `weight ∈ R[out_features × in_features]` and computes `y = x·Wᵀ + b`. Every "row of W" is therefore an *output* neuron; a scraper that reads columns as outputs transposes the whole model. This is the default convention every entry above assumes unless overridden (LAYOUT-CONV1D, packed MoE slices).
- **Formulation**: `y = x·Wᵀ + b`, `W[i, :]` = weights *into* output i; safetensors stores W row-major with shape `[out, in]`.
- **Variants/used-by**: all HF PyTorch checkpoints except Conv1D families (LAYOUT-CONV1D) and per-slice einsum layouts (GPT-OSS packed experts are `[E, in, out]` — transposed per slice!).
- **Tensor names**: n/a — a property of every `.weight`.
- **Witnessed content**: the orientation convention per tensor family (recipe fact); row/column semantics baked into the token→token attestation directions (which axis is subject, which is object).
- **Read-side mechanism**: matvec honoring `[out, in]`; the ATTENDS/OV_RELATES/COMPLETES_TO planes must agree with HF's orientation or every asserted edge is backwards.
- **Laplace status**: **partial** — implicitly assumed by all Paths; not attested, and unvalidated against shape metadata at scrape time (a shape check `[out, in]` vs config dims is the cheap tripwire).
- **Gate**: For a rectangular projection (out ≠ in, e.g. GQA k_proj), scraped orientation validates against config dims, and one matvec replay matches HF (a transposed read fails on rectangular tensors by shape alone).
- Src: torch nn.Linear docs; safetensors format docs.

### LAYOUT-CONV1D
- **ID**: LAYOUT-CONV1D
- **What/Why**: GPT-2 (and GPT-1/CTRL-era code) uses HF's `Conv1D` module, which stores the weight **transposed** relative to nn.Linear: shape `[in, out]`, computing `y = x·W + b`. Reading GPT-2 tensors with the LAYOUT-ROWMAJOR assumption silently transposes q/k/v/o and both FFN matrices — the canonical checkpoint-scrape footgun.
- **Formulation**: `y = x·W + b`, `W ∈ R[in × out]` — no transpose in the forward.
- **Variants/used-by**: GPT-2 family (`h.{L}.attn.c_attn`, `attn.c_proj`, `mlp.c_fc`, `mlp.c_proj`), GPT-1, CTRL, ImageGPT. Square matrices (c_proj: 768×768) make the error *shape-silent* — only values catch it.
- **Tensor names**: any `c_*.weight` under the GPT-2 modeling family.
- **Witnessed content**: per-tensor orientation fact (`conv1d_transposed=true`); content witnessed in canonical orientation so GPT-2 and a hypothetical Linear-layout re-export of the same weights collide to the same content hash.
- **Read-side mechanism**: ingest normalizes to canonical orientation; replay is orientation-uniform thereafter.
- **Laplace status**: **missing** — no GPT-2 profile; adding one without this flag would scrape every matrix transposed.
- **Gate**: GPT-2 c_fc replay (rectangular 768×3072) matches HF fp32; and for square c_proj, a value-level probe (one known row) confirms orientation.
- Src: [HF Conv1D / pytorch_utils.py](https://github.com/huggingface/transformers), [modular-MAX GPT-2 weight-loading notes](https://llm.modular.com/step_11.html).

### LAYOUT-FUSED-QKV
- **ID**: LAYOUT-FUSED-QKV
- **What/Why**: Some checkpoints store Q, K, V as one concatenated tensor (GPT-2 `c_attn`: `[in, 3·d]`; Phi-3 `qkv_proj`; Falcon `query_key_value` with GQA-interleaved blocks). The scraper must split on the correct axis with the correct block order/interleave to feed the Q/K/V planes — a wrong split poisons ATTENDS and OV_RELATES silently.
- **Formulation**: GPT-2: `[q|k|v] = x·W_cattn + b`, split into thirds on the *output* axis. Phi-3: `qkv_proj.weight [d + 2·kv, d]` split by (q_size, kv_size, kv_size) rows. Falcon: per-group interleaved `[q…q k v]` blocks (GQA-aware split).
- **Variants/used-by**: GPT-2 (`c_attn`, Conv1D — compose with LAYOUT-CONV1D), GPT-NeoX (`attention.query_key_value`, head-interleaved!), Falcon, Phi-3 (`self_attn.qkv_proj`), MPT (`Wqkv`). Split-free families: Llama/Qwen/BERT (separate q/k/v).
- **Tensor names**: `attn.c_attn.weight`, `attention.query_key_value.weight`, `self_attn.qkv_proj.weight`, `attn.Wqkv.weight`.
- **Witnessed content**: the split recipe (axis, sizes, interleave order) per family; the resulting Q/K/V witnessed as three canonical tensors — fused vs separate storage must hash to identical content.
- **Read-side mechanism**: none at read time — the split is fully absorbed at ingest.
- **Laplace status**: **missing** — all four profiles assume separate q/k/v tensors; no fused pattern support.
- **Gate**: A model exported once fused and once split ingests to byte-identical Q/K/V content; NeoX's head-interleaved layout is deinterleaved (a thirds-split of NeoX must fail a known-value probe).
- Src: [HF modeling_gpt2.py / modeling_phi3.py / modeling_gpt_neox.py](https://github.com/huggingface/transformers).

### LAYOUT-FUSED-GATEUP
- **ID**: LAYOUT-FUSED-GATEUP
- **What/Why**: The FFN analog of LAYOUT-FUSED-QKV: gate and up projections stored as one tensor. Two sub-conventions exist — halves-concatenated (Phi-3: first half gate, second half up) and even/odd interleaved (GPT-OSS, FFN-GLU-CLAMP). Splitting with the wrong sub-convention swaps/scrambles gate and up.
- **Formulation**: Phi-3: `gate_up_proj.weight [2·d_ff, d]`; `gate = rows[0:d_ff]`, `up = rows[d_ff:2d_ff]`. GPT-OSS: `gate = cols[0::2]`, `up = cols[1::2]` of the packed slice.
- **Variants/used-by**: Phi-3/Phi-4 (`mlp.gate_up_proj`), Qwen3.5-MoE packed experts, GPT-OSS experts (interleaved), some vLLM-native exports.
- **Tensor names**: `model.layers.{L}.mlp.gate_up_proj.weight`, `mlp.experts.gate_up_proj`.
- **Witnessed content**: split convention (halves vs interleave, axis) per family; canonical gate/up witnessed separately, hash-identical to an unfused export.
- **Read-side mechanism**: absorbed at ingest; replay sees canonical gate/up.
- **Laplace status**: **missing** — no Phi-3 profile (the existing "phi" profile is Phi-1/2 fc1/fc2 style and would mismap Phi-3's tensor names entirely).
- **Gate**: Phi-3-mini FFN replay matches HF fp32; applying the interleave convention to Phi-3 (or halves to GPT-OSS) fails a known-row probe.
- Src: [HF modeling_phi3.py](https://github.com/huggingface/transformers), [gpt-oss internals](https://medium.com/@chris.p.hughes10/inside-openais-gpt-oss-how-production-moe-really-works-cfa5f6a23caa).

---

## Checklist

FFN core
- [ ] FFN-MLP2 — 2-matrix MLP block replay bit-exact (fp32)
- [ ] FFN-ACT-RELU — hard ReLU, no substitution
- [ ] FFN-ACT-GELU-EXACT — erf GELU, provably ≠ tanh variant
- [ ] FFN-ACT-GELU-TANH — tanh polynomial, exact constant & op order
- [ ] FFN-ACT-SILU — x·σ(x) bitwise
- [ ] FFN-ACT-QUICKGELU — x·σ(1.702x) bitwise
- [ ] FFN-ACT-CONFIG — verbatim activation key witnessed + dispatched
- [ ] FFN-GLU-SWIGLU — gate/up/down replay, gate-up asymmetry proven
- [ ] FFN-GLU-GEGLU — Gemma/T5 GELU-gated, correct GELU variant per family
- [ ] FFN-GLU-REGLU — ReLU-gated recognized
- [ ] FFN-GLU-CLAMP — GPT-OSS interleave + clamp(±7) + (up+1) + α=1.702
- [ ] FFN-BIAS — present biases applied; absent biases never fabricated
- [ ] FFN-PARALLEL — Phi/GPT-J parallel topology load-bearing in replay

Mixture of Experts
- [ ] MOE-ROUTER — router logits bit-exact
- [ ] MOE-TOPK — expert selection identical incl. tie-break
- [ ] MOE-SOFTMAX-ORDER — before/after-topk order per family, provably distinct
- [ ] MOE-NORM-GATES — norm_topk_prob + routed_scaling_factor honored
- [ ] MOE-SIGMOID-GROUP — DeepSeek-V3 sigmoid + correction-bias-selection-only + groups
- [ ] MOE-SHARED — shared experts added (with Qwen2-MoE sigmoid gate); absent for Qwen3-MoE
- [ ] MOE-EXPERT-CHOICE — token-choice attested; expert-choice rejected loudly
- [ ] MOE-AUXLOSS — aux-loss keys witnessed as inference no-ops
- [ ] MOE-LAYOUT — packed vs per-expert ingests to identical content
- [ ] MOE-DENSE-MIX — per-layer dense/sparse dispatch matches config + census

Normalization
- [ ] NORM-LAYERNORM — γ+β+ε, mean subtraction proven present
- [ ] NORM-RMS — gain+ε, mean subtraction proven absent
- [ ] NORM-RMS-OFFSET — Gemma (1+w) applied at replay, raw gain witnessed
- [ ] NORM-NONPARAM — OLMo-1 tensorless norm sites replayed
- [ ] NORM-PRE — pre-LN order, raw-stream residual adds
- [ ] NORM-POST — BERT post-residual order load-bearing
- [ ] NORM-POST-REORDERED — OLMo2 output-norm-then-residual, no input norm
- [ ] NORM-SANDWICH — Gemma2 4 norms/layer, both post-norms load-bearing
- [ ] NORM-FINAL — exactly one final norm before lm_head
- [ ] NORM-EMBED — BERT embedding-sum norm at pre-layer-0 site (not "final")
- [ ] NORM-QK — q/k norms classified to attention plane (cross-ref attention doc)
- [ ] NORM-EPS — witnessed ε per model; near-zero-variance probe passes

Embeddings / unembedding
- [ ] EMB-WORD — embedding rows lossless under token content identity
- [ ] EMB-TIED — tie fact, no duplicate content, omitted-lm_head handled, head bias applied
- [ ] EMB-SCALE — Gemma √d with bf16 rounding; Granite embedding_multiplier
- [ ] EMB-POS-LEARNED — wpe/position tables + index-offset quirks (OPT +2)
- [ ] EMB-TOKENTYPE — segment row always added (even all-zero segments)
- [ ] EMB-MLMHEAD — BERT transform stack + vocab bias replayed
- [ ] EMB-SOFTCAP — Gemma2 cap·tanh(logits/cap); Gemma3 none
- [ ] EMB-LOGIT-SCALE — Cohere multiply / Granite divide, log-prob-level gate
- [ ] EMB-VOCAB-PAD — padding rows kept for exact softmax, no text identity minted

Structural conventions
- [ ] RES-PRENORM — topology enum witnessed; permutation test fails all wrong wirings
- [ ] RES-SCALE — Granite residual_multiplier on every sublayer output
- [ ] DROPOUT-NOOP — dropout keys witnessed as no-ops; replay deterministic
- [ ] LAYOUT-ROWMAJOR — [out,in] orientation validated on rectangular tensors
- [ ] LAYOUT-CONV1D — GPT-2 Conv1D transposed reads normalized at ingest
- [ ] LAYOUT-FUSED-QKV — c_attn/qkv_proj/NeoX-interleaved splits; fused ≡ split content hash
- [ ] LAYOUT-FUSED-GATEUP — halves (Phi-3) vs interleave (GPT-OSS) split conventions

---

*51 entries. Companion doc: attention/positional primitive index (Q/K/V/O, RoPE/ALiBi/NoPE, GQA/MQA/MLA, attention softcap/sinks, sliding window). Laplace status measured against `app/Laplace.Decomposers/Model/ArchitectureProfile.cs` @ commit 1dfdbc9.*
