<!-- STATUS COLUMN STALE 2026-07-20 — this is a REFERENCE CATALOG of transformer
     primitives (formulations, tensor names, mutation-check gates, sources). Keep it.
     Its 'Laplace status' column is NOT a tracker and is already wrong in four places:
     ATT-BIAS, EMB-POS, EMB-SEG and NRM-LN are fixed (ModelTokenEdgeETL.cs:243,
     ArchitectureProfile.cs:183-186).
     Live gaps are GitHub issues: #476 gate matrix, #477 rope_scaling, #478 sliding_window,
     #479 shard index.json, #480 __metadata__, #481 unknown model_type, #482 fused layouts,
     #483 GELU kernels, #484 generation_config, #485 WordPiece, #383/#384 family coverage,
     #537 quant refusal, #538 BPE encode-replay, #539 tokenizer sidecars, #540 hidden_act,
     #541 norm epsilon, #542 per-projection bias, #543 Bert FinalNorm, #544 norm placement,
     #545 phi profile.
     Open work lives in GitHub issues. -->

# 27a — Primitive Index: Attention

Reference catalog of every attention-related primitive found in transformer checkpoints,
used as the test checklist for the model ETL: scrape a checkpoint into the substrate,
replay its math exactly at read time. Each entry is one gate. All math is C/C++/SQL —
this document contains no code, only the contracts the code must satisfy.

**Notation.** `d` = hidden size (`hidden_size`), `H` = query heads
(`num_attention_heads`), `H_kv` = KV heads (`num_key_value_heads`), `d_h` = head dim
(`head_dim`, default `d/H`), `n` = sequence length, `i` = query position, `j` = key
position, `x ∈ R^d` = a token's residual-stream vector. HF `nn.Linear` stores weights
`[out, in]`; `y = xWᵀ + b`. GPT-2's `Conv1D` stores the TRANSPOSE `[in, out]` — layout
is witnessed content, not a detail (see ATT-QKV-PACKED). "Verbatim" = record exactly
what the checkpoint asserts; anything derived is calculated-layer, per doc 08.

**Laplace status baseline**: `app/Laplace.Decomposers/Model/ArchitectureProfile.cs`
holds four profiles (llama, phi, qwen2, bert) with per-layer weight-tensor patterns for
Q/K/V/O + MLP + norms + embeddings, a `HasBiases`/`BiasOf` bias convention, and a
`RightIsKv` flag on the ATTENDS bilinear path (GQA width awareness). It records NO
config.json scalar (no `rope_theta`, no `sliding_window`, no `num_key_value_heads`
value, no scaling factors) and no positional scheme of any kind. That absence is the
dominant gap across this index.

---

## Table of contents

**A. Projections & head geometry** — ATT-QPROJ, ATT-KPROJ, ATT-VPROJ, ATT-OPROJ,
ATT-PROJBIAS, ATT-QKV-PACKED, ATT-HEADSLICE, ATT-HEADDIM

**B. Head-sharing topologies** — ATT-MHA, ATT-MQA, ATT-GQA, ATT-MLA

**C. Score path (scale, cap, norm, softmax)** — ATT-SCALE, ATT-SCALE-CFG, ATT-SOFTCAP,
ATT-QKNORM, ATT-QKNORM-FULL, ATT-SOFTMAX, ATT-SINK, ATT-TEMP, ATT-LAYERSCALE,
ATT-DROPOUT

**D. Masking & context topology** — MSK-CAUSAL, MSK-BIDIR, MSK-PAD, MSK-SWA, MSK-ALT,
MSK-CHUNK, MSK-PREFIX

**E. Positional schemes** — POS-LEARNED, POS-SINUSOID, POS-ROPE, POS-ROPE-PARTIAL,
POS-ROPE-LINEAR, POS-ROPE-DYNNTK, POS-ROPE-YARN, POS-ROPE-LONGROPE, POS-ROPE-LLAMA3,
POS-ROPE-DUAL, POS-MROPE, POS-ALIBI, POS-T5BUCKET, POS-SHAW, POS-NOPE, POS-SEGMENT

**F. Layout & wiring** — LAY-PARALLEL, LAY-SANDWICH, LAY-CROSS, LAY-GATE, LAY-HYBRID

50 entries. Checklist at the end.

---

## A. Projections & head geometry

### ATT-QPROJ
- **ID**: ATT-QPROJ
- **What/Why**: The learned matrix that turns each token's vector into its "question" —
  what this token is looking for in the rest of the context. Without it every token
  would ask the same question and attention could not specialize.
- **Formulation**: `q = x W_qᵀ + b_q`, `W_q ∈ R^{(H·d_h) × d}`. Per head `h`, rows
  `[h·d_h, (h+1)·d_h)` of `W_q` are that head's query subspace (ATT-HEADSLICE).
- **Variants/used-by**: Universal. Separate tensor in Llama/Mistral/Qwen/Gemma/Phi/BERT;
  packed in GPT-2/GPT-NeoX/Falcon (ATT-QKV-PACKED); factored in DeepSeek MLA (ATT-MLA).
  Config: `hidden_size`, `num_attention_heads`, `head_dim`.
- **Tensor names**: `model.layers.{L}.self_attn.q_proj.weight` (Llama family);
  `encoder.layer.{L}.attention.self.query.weight` (BERT);
  `transformer.h.{L}.attn.c_attn.weight` slice (GPT-2);
  `encoder.block.{L}.layer.0.SelfAttention.q.weight` (T5).
- **Witnessed content**: full weight matrix verbatim (row/col addressed), its shape,
  dtype, and the `[out,in]` vs `[in,out]` layout law of its container.
- **Read-side mechanism**: dense mat-vec `xW_qᵀ` (+bias) per query token, then head
  slicing.
- **Laplace status**: **covered** — all four profiles carry the pattern; feeds the
  ATTENDS `BilinearPath` left side.
- **Gate**: reconstructed `W_q` from substrate is byte-identical (same content hash) to
  the safetensors tensor; `xW_qᵀ` on a probe vector matches fp64 reference to 0 ULP in
  the declared accumulation order.
- **Sources**: https://arxiv.org/abs/1706.03762

### ATT-KPROJ
- **ID**: ATT-KPROJ
- **What/Why**: The matrix that turns each token into its "label" — how it advertises
  itself to other tokens' questions (ATT-QPROJ). Q and K must live in the same `d_h`
  space so their dot product is meaningful.
- **Formulation**: `k = x W_kᵀ + b_k`, `W_k ∈ R^{(H_kv·d_h) × d}`. Note the OUT width is
  `H_kv·d_h`, not `H·d_h`, under ATT-GQA/ATT-MQA.
- **Variants/used-by**: Universal; same packing/factoring variants as ATT-QPROJ.
  Config: `num_key_value_heads` sets the width.
- **Tensor names**: `model.layers.{L}.self_attn.k_proj.weight`;
  `encoder.layer.{L}.attention.self.key.weight` (BERT);
  `...SelfAttention.k.weight` (T5).
- **Witnessed content**: weight verbatim, shape (the `H_kv·d_h` width IS the GQA
  witness), dtype, bias if present.
- **Read-side mechanism**: `xW_kᵀ` per context token; results are position-dependent
  only after POS-ROPE — the projection itself is position-free.
- **Laplace status**: **covered** (patterns present; `RightIsKv` flags the narrower
  width for llama/qwen2).
- **Gate**: same as ATT-QPROJ; additionally the recorded K width divided by `d_h` equals
  the recorded `num_key_value_heads`.
- **Sources**: https://arxiv.org/abs/1706.03762

### ATT-VPROJ
- **ID**: ATT-VPROJ
- **What/Why**: The matrix that produces what a token actually *hands over* once
  attention selects it — the payload, distinct from the label (ATT-KPROJ) used to find
  it. Without a separate V, what you match on and what you copy would be forced to be
  the same thing.
- **Formulation**: `v = x W_vᵀ + b_v`, `W_v ∈ R^{(H_kv·d_v) × d}`; `d_v = d_h` except
  MLA (`v_head_dim`, ATT-MLA).
- **Variants/used-by**: Universal; width follows `num_key_value_heads` like K.
- **Tensor names**: `model.layers.{L}.self_attn.v_proj.weight`;
  `encoder.layer.{L}.attention.self.value.weight` (BERT);
  `...SelfAttention.v.weight` (T5).
- **Witnessed content**: weight verbatim, shape, dtype, bias.
- **Read-side mechanism**: `xW_vᵀ` per context token; the attention-weighted sum of
  these vectors is the head output.
- **Laplace status**: **covered** — feeds OV_RELATES `ProjectionPath`.
- **Gate**: as ATT-QPROJ; and the composed per-head OV map `W_o[h]·W_v[h]` recomputed
  from substrate matches the reference composition elementwise.
- **Sources**: https://arxiv.org/abs/1706.03762

### ATT-OPROJ
- **ID**: ATT-OPROJ
- **What/Why**: The matrix that merges all heads' outputs back into one residual-stream
  vector. It decides how much each head's opinion is allowed to write back; without it
  heads could not be mixed or down-weighted.
- **Formulation**: `out = concat(head_1..head_H) W_oᵀ + b_o`,
  `W_o ∈ R^{d × (H·d_v)}`. Per head `h`, COLUMNS `[h·d_v, (h+1)·d_v)` are that head's
  write-back map.
- **Variants/used-by**: Universal. Named `o_proj` (Llama family), `dense` (Phi, NeoX,
  BERT-output), `c_proj` (GPT-2), `out_proj` (BART/OPT), `wo` (some T5 dumps as `o`).
- **Tensor names**: `model.layers.{L}.self_attn.o_proj.weight`;
  `model.layers.{L}.self_attn.dense.weight` (Phi);
  `encoder.layer.{L}.attention.output.dense.weight` (BERT);
  `transformer.h.{L}.attn.c_proj.weight` (GPT-2, `[in,out]` layout).
- **Witnessed content**: weight verbatim; the column-per-head slicing law; bias.
- **Read-side mechanism**: one dense mat-vec after head concat; per-head replay uses the
  column slice.
- **Laplace status**: **covered** (patterns in all four profiles, OV_RELATES right
  side).
- **Gate**: per-head column slices reassemble to the exact stored tensor; head-sliced
  replay sum equals whole-matrix replay to fp64 exactness.
- **Sources**: https://arxiv.org/abs/1706.03762

### ATT-PROJBIAS
- **ID**: ATT-PROJBIAS
- **What/Why**: Constant offsets added after each projection. Most modern decoders drop
  them; where they exist (or exist only on Q/K/V but not O, as in Qwen2) they shift
  every score and payload, so skipping them breaks exactness silently.
- **Formulation**: `y = xWᵀ + b`, `b ∈ R^{out}` per projection. Presence is per-family
  AND per-projection (Qwen2: bias on q/k/v, none on o).
- **Variants/used-by**: BERT/GPT-2/T5-era (T5: no biases anywhere), Phi, Qwen2 (qkv
  only), gpt-oss (all attn projections), Falcon-variants. Config: `attention_bias`
  (Llama-family, default false), `qkv_bias`, implicit for BERT/GPT-2.
- **Tensor names**: sibling `.bias` of every weight above
  (`...q_proj.bias`, `...c_attn.bias`, `...attention.self.query.bias`).
- **Witnessed content**: each bias vector verbatim; per-projection presence/absence map
  (an explicit zero-vs-absent distinction).
- **Read-side mechanism**: vector add after each mat-vec.
- **Laplace status**: **partial** — `HasBiases` flag + `BiasOf()` naming helper exist,
  but presence is a single per-profile boolean (cannot express Qwen2's qkv-not-o) and
  the Path math does not consume biases.
- **Gate**: for a family with mixed presence (Qwen2), replay with recorded biases
  matches HF fp32 attention output; replay with biases dropped does NOT (mutation
  check).
- **Sources**: https://huggingface.co/docs/transformers/model_doc/qwen2

### ATT-QKV-PACKED
- **ID**: ATT-QKV-PACKED
- **What/Why**: Older/fused checkpoints store Q, K, V as ONE tensor. The ETL must know
  the split-and-ordering law or it will attribute rows to the wrong projection —
  everything downstream would be silently scrambled.
- **Formulation**: GPT-2: `c_attn.weight ∈ R^{d × 3d}` (`[in,out]` Conv1D layout), split
  thirds along OUT → Q|K|V. GPT-NeoX: `query_key_value.weight ∈ R^{3d × d}` with
  PER-HEAD INTERLEAVED layout `[H, 3, d_h]` on the out axis (q,k,v alternate within each
  head block). Falcon: fused `query_key_value` with GQA grouping layout.
- **Variants/used-by**: GPT-2 (`c_attn`), GPT-NeoX/Pythia (`query_key_value`), Falcon,
  BLOOM (`query_key_value`, per-head interleaved), MPT (`Wqkv`), ChatGLM. Signal:
  absence of separate q/k/v tensors.
- **Tensor names**: `transformer.h.{L}.attn.c_attn.weight` (GPT-2);
  `gpt_neox.layers.{L}.attention.query_key_value.weight`;
  `transformer.h.{L}.self_attention.query_key_value.weight` (Falcon/BLOOM);
  `transformer.blocks.{L}.attn.Wqkv.weight` (MPT).
- **Witnessed content**: the unpacking law itself (contiguous-thirds vs per-head
  interleave vs GQA-grouped), the transpose law (Conv1D), then the three unpacked
  matrices addressed as ATT-QPROJ/KPROJ/VPROJ content.
- **Read-side mechanism**: none extra — unpack happens at ingest; read side sees
  canonical Q/K/V.
- **Laplace status**: **missing** — no packed-family profile, no split logic.
- **Gate**: unpacked Q/K/V re-packed under the recorded layout law reproduces the
  original fused tensor byte-identically, per family (GPT-2, NeoX, BLOOM each).
- **Sources**: https://github.com/huggingface/transformers/blob/main/src/transformers/models/gpt2/modeling_gpt2.py ; https://github.com/huggingface/transformers/blob/main/src/transformers/models/gpt_neox/modeling_gpt_neox.py

### ATT-HEADSLICE
- **ID**: ATT-HEADSLICE
- **What/Why**: One big projection matrix is really `H` independent small ones stacked.
  Getting the stacking axis and order right is what makes "head 3 of layer 10" a real,
  addressable object — the circuit coordinate the model lane attests APPEARS_IN against.
- **Formulation**: reshape `[n, H·d_h] → [n, H, d_h]`; head `h` owns out-rows
  `[h·d_h,(h+1)·d_h)` of `W_q/W_k/W_v` and IN-columns of `W_o`. Scores and softmax are
  strictly per-head; heads interact only through ATT-OPROJ.
- **Variants/used-by**: Universal. Complications: packed interleaves (ATT-QKV-PACKED),
  GQA group mapping `h → ⌊h / (H/H_kv)⌋` (ATT-GQA), decoupled `head_dim` (ATT-HEADDIM).
- **Tensor names**: none — a derived view; the coordinate `(L, h)` is the entity.
- **Witnessed content**: `num_attention_heads`, `num_key_value_heads`, `head_dim`, and
  the slice law per family, so every attested circuit coordinate is reconstructible.
- **Read-side mechanism**: index arithmetic; per-head GEMV on the row/column slice.
- **Laplace status**: **partial** — circuit coordinates exist in the model lane
  (layer/head anchors per CLAUDE.md), but ArchitectureProfile records no `H`, `H_kv`, or
  `d_h`, so slicing is not derivable from the profile alone.
- **Gate**: sum over per-head replays equals whole-matrix replay exactly; head count ×
  head dim equals recorded tensor width for every layer.
- **Sources**: https://arxiv.org/abs/1706.03762

### ATT-HEADDIM
- **ID**: ATT-HEADDIM
- **What/Why**: Some families set the per-head width independently of
  `hidden_size / num_heads`, so the Q/K width is NOT the hidden width. Assuming
  `d_h = d/H` mis-slices those checkpoints (ATT-HEADSLICE) and wrongly scales scores
  (ATT-SCALE).
- **Formulation**: `d_h = head_dim` (config), projection out-width `H·d_h ≠ d` allowed.
  Gemma-2-9B: `d=3584, H=16, d_h=256` (Q width 4096); gpt-oss: `d=2880, d_h=64`.
- **Variants/used-by**: Gemma 1/2/3, gpt-oss, some Qwen3 sizes. Config key: `head_dim`.
- **Tensor names**: visible as "wrong-looking" shapes on the ATT-QPROJ..OPROJ tensors.
- **Witnessed content**: `head_dim` scalar per model; derived check against tensor
  shapes.
- **Read-side mechanism**: use recorded `d_h` in slicing and in `1/√d_h` scaling —
  never infer from `d/H`.
- **Laplace status**: **missing** (no scalar capture in profile).
- **Gate**: for Gemma-2-9B, replay using recorded `head_dim=256` matches HF; replay
  using inferred `3584/16=224` fails (mutation check).
- **Sources**: https://arxiv.org/abs/2408.00118 ; https://huggingface.co/docs/transformers/model_doc/gemma2

---

## B. Head-sharing topologies

### ATT-MHA
- **ID**: ATT-MHA
- **What/Why**: The baseline: every query head has its own private key/value head
  (`H_kv = H`). Multiple heads let the layer ask several different questions at once;
  everything else in this section is a cheaper sharing scheme of the same math.
- **Formulation**: per head `h`: `s_ij = (q_i[h]·k_j[h]) · scale`;
  `head_h(i) = Σ_j softmax_j(s_ij) v_j[h]`; heads independent (ATT-HEADSLICE),
  recombined by ATT-OPROJ.
- **Variants/used-by**: GPT-2, BERT, T5, Llama-2-7B/13B, Phi-1/2, Mistral pre-GQA
  models... Signal: `num_key_value_heads == num_attention_heads` or key absent.
- **Tensor names**: as section A; K/V widths equal Q width.
- **Witnessed content**: nothing beyond section A + head counts; the topology is the
  witnessed fact `H_kv = H`.
- **Read-side mechanism**: `H` independent score/softmax/mix lanes.
- **Laplace status**: **partial** — ATTENDS path computes the QKᵀ bilinear at
  whole-matrix grain; per-head lanes (the circuit grain) come from the model lane's
  coordinate scheme, not the profile.
- **Gate**: full attention output for a short probe context matches HF fp32 to ≤1e-6
  L∞ with heads replayed independently.
- **Sources**: https://arxiv.org/abs/1706.03762

### ATT-MQA
- **ID**: ATT-MQA
- **What/Why**: All query heads share ONE key/value head (`H_kv = 1`) — a memory
  optimization for serving. The ETL must expand that single K/V lane to every query head
  or scores are computed against the wrong keys.
- **Formulation**: `s_ij[h] = (q_i[h]·k_j[0]) · scale` for all `h`; one `k`,`v` per
  position, broadcast across heads.
- **Variants/used-by**: Falcon-7B, StarCoder/SantaCoder (`multi_query: true`), PaLM.
  Signal: `num_key_value_heads: 1` or `multi_query: true`.
- **Tensor names**: `transformer.h.{L}.self_attention.query_key_value.weight` (Falcon,
  fused, K/V segment width `d_h`); `transformer.h.{L}.attn.c_attn` (StarCoder fused
  `d + 2·d_h`).
- **Witnessed content**: `H_kv=1`; the fused-layout law (usually rides
  ATT-QKV-PACKED); K/V tensors once, not per head.
- **Read-side mechanism**: broadcast — evaluate K/V lane once per position, reuse for
  every query head.
- **Laplace status**: **missing** (no MQA-family profile; `RightIsKv` machinery would
  carry it once a profile exists).
- **Gate**: replay stores K/V once and still matches HF output; storage audit shows no
  duplicated K/V rows per head.
- **Sources**: https://arxiv.org/abs/1911.02150

### ATT-GQA
- **ID**: ATT-GQA
- **What/Why**: The middle ground: query heads are partitioned into groups and each
  group shares one K/V head (`1 < H_kv < H`). It is the modern default; the ETL must
  record the group map so each query head reads the RIGHT shared key.
- **Formulation**: group of head `h` is `g(h) = ⌊h / (H/H_kv)⌋`;
  `s_ij[h] = (q_i[h]·k_j[g(h)]) · scale`. HF materializes this as `repeat_kv`
  (each KV head tiled `H/H_kv` times, block-contiguous — NOT round-robin).
- **Variants/used-by**: Llama-2-70B, Llama-3.x (8 KV heads), Mistral, Mixtral, Qwen2/3,
  Gemma 2/3, gpt-oss, Phi-3. Signal: `num_key_value_heads < num_attention_heads`.
- **Tensor names**: standard `k_proj`/`v_proj` with out-width `H_kv·d_h`.
- **Witnessed content**: `num_key_value_heads`, the block-contiguous group law; K/V
  weights at their native (narrow) width — never pre-expanded.
- **Read-side mechanism**: indexed expansion `h → g(h)` at score time; K/V lanes
  computed `H_kv` times only.
- **Laplace status**: **partial** — `RightIsKv: true` on llama/qwen2 ATTENDS paths
  acknowledges the narrow K width; the group count and the `h→g(h)` map are not
  recorded.
- **Gate**: for Llama-3-8B (H=32, H_kv=8), per-head replay with recorded group map
  matches HF; a round-robin (wrong) map fails (mutation check).
- **Sources**: https://arxiv.org/abs/2305.13245

### ATT-MLA
- **ID**: ATT-MLA
- **What/Why**: DeepSeek's latent attention compresses K/V (and Q) through a small
  bottleneck and splits each key into a content part and a separate position-carrying
  part. Five projection tensors and two norms replace the classic four; an ETL that only
  knows q/k/v/o cannot even find the weights.
- **Formulation**: Query: `c_Q = x W_DQᵀ` (`q_a_proj`, width `q_lora_rank`), RMSNorm
  (`q_a_layernorm`), `q = c_Q W_UQᵀ` (`q_b_proj`) → per head split
  `[q_nope (qk_nope_head_dim) | q_rope (qk_rope_head_dim)]`.
  Key/Value: `x W_DKVᵀ` (`kv_a_proj_with_mqa`) → `[c_KV (kv_lora_rank) | k_rope
  (qk_rope_head_dim, ONE per position, shared across all heads — MQA-style)]`; RMSNorm
  on `c_KV` (`kv_a_layernorm`); `c_KV W_UKVᵀ` (`kv_b_proj`) → per head
  `[k_nope (qk_nope_head_dim) | v (v_head_dim)]`.
  POS-ROPE applies ONLY to `q_rope`/`k_rope` (decoupled RoPE).
  `s_ij[h] = (q_nope·k_nope[h] + q_rope[h]·k_rope) / √(qk_nope_head_dim +
  qk_rope_head_dim)`, times the YaRN mscale when POS-ROPE-YARN is active.
  Output: heads (width `v_head_dim`) concat → `o_proj`.
- **Variants/used-by**: DeepSeek-V2/V2-Lite (Lite: `q_lora_rank: null` → direct
  `q_proj`), DeepSeek-V3/R1, Kimi K2. Config: `q_lora_rank` (1536),
  `kv_lora_rank` (512), `qk_nope_head_dim` (128), `qk_rope_head_dim` (64),
  `v_head_dim` (128).
- **Tensor names**: `model.layers.{L}.self_attn.q_a_proj.weight`,
  `.q_a_layernorm.weight`, `.q_b_proj.weight`, `.kv_a_proj_with_mqa.weight`,
  `.kv_a_layernorm.weight`, `.kv_b_proj.weight`, `.o_proj.weight`
  (V2-Lite: `.q_proj.weight` instead of the q_a/q_b pair).
- **Witnessed content**: all five (or four) projections + two RMSNorm weights verbatim;
  the five rank/dim scalars; the split offsets (nope|rope, k_nope|v) as explicit laws;
  the shared-`k_rope` (across heads) fact.
- **Read-side mechanism**: two-stage down/up projection; two separate dot products
  summed per score; rotation applied only on the rope slice; scale uses the COMBINED
  qk head dim (192), not `v_head_dim`.
- **Laplace status**: **missing** entirely.
- **Gate**: on DeepSeek-V2-Lite, replayed `s_ij` (nope+rope sum, combined-dim scale)
  matches HF fp32 logits to ≤1e-5; dropping the decoupled-rope term or scaling by
  `√v_head_dim` fails (mutation checks).
- **Sources**: https://arxiv.org/abs/2405.04434 ; https://github.com/deepseek-ai/DeepSeek-V3/blob/main/inference/model.py

---

## C. Score path (scale, cap, norm, softmax)

### ATT-SCALE
- **ID**: ATT-SCALE
- **What/Why**: Dot products grow with vector length, so raw scores are divided by
  `√d_h` to keep the softmax (ATT-SOFTMAX) from saturating. It is a constant, but the
  WRONG constant reshapes every attention distribution.
- **Formulation**: `s_ij ← (q_i·k_j) / √d_h`. Exceptions: T5 uses scale = 1 (folded
  into weight init at training time — POS-T5BUCKET models must NOT be scaled); MLA uses
  the combined qk dim (ATT-MLA); configured overrides in ATT-SCALE-CFG.
- **Variants/used-by**: Universal default. Signal: absence of any override key.
- **Tensor names**: none (derived from `head_dim`).
- **Witnessed content**: the effective scale as a recorded scalar per model (never
  re-derived at read time), sourced from `head_dim` or its override.
- **Read-side mechanism**: one multiply per score, `laplace_score_fp` precision.
- **Laplace status**: **missing** (no scalar capture).
- **Gate**: recorded scale equals HF `module.scaling` for each profiled model; T5
  replay with scale=1 matches, with `1/√d_h` fails (mutation check).
- **Sources**: https://arxiv.org/abs/1706.03762 ; https://arxiv.org/abs/1910.10683

### ATT-SCALE-CFG
- **ID**: ATT-SCALE-CFG
- **What/Why**: Families that tuned training with widths in mind override the default
  divisor (ATT-SCALE) with an explicit config scalar. Reading the key wrong (or
  defaulting to `√d_h`) skews every score by a constant factor.
- **Formulation**: Gemma-2/3: `scale = query_pre_attn_scalar^{-1/2}` (Gemma-2-9B: 224,
  27B: 144 — deliberately ≠ `d_h`). Granite (μP): `s_ij = (q·k) · attention_multiplier`
  (plain multiplier, no sqrt). Others: `softmax_scale` (MPT), `attention_factor` from
  rope scaling (POS-ROPE-YARN/LONGROPE) multiplies here too.
- **Variants/used-by**: Gemma 2/3 (`query_pre_attn_scalar`), IBM Granite 3.x
  (`attention_multiplier`, with sibling `embedding_multiplier`/`logits_scaling`), MPT
  (`attn_config.softmax_scale`), Llama-4 length-dependent term (ATT-TEMP).
- **Tensor names**: none — config scalars.
- **Witnessed content**: the override key, its value, and its composition law
  (replace-sqrt vs multiply-after).
- **Read-side mechanism**: apply the recorded effective scale; compose with softcap
  (ATT-SOFTCAP) in the recorded order (scale, then cap).
- **Laplace status**: **missing**.
- **Gate**: Gemma-2-27B replay with `144^{-1/2}` matches HF; with `1/√128` (its actual
  head_dim) it fails (mutation check).
- **Sources**: https://arxiv.org/abs/2408.00118 ; https://github.com/huggingface/transformers/blob/main/src/transformers/models/granite/configuration_granite.py

### ATT-SOFTCAP
- **ID**: ATT-SOFTCAP
- **What/Why**: Squashes attention scores smoothly through tanh so no single score can
  run away before the softmax (ATT-SOFTMAX). Skipping it changes distributions exactly
  where the model relied on saturation.
- **Formulation**: `s ← c · tanh(s / c)`, `c = attn_logit_softcapping` (Gemma-2: 50.0;
  Grok-1: 30.0). Applied AFTER scaling, BEFORE mask add and softmax. (Sibling
  `final_logit_softcapping` = 30.0 caps lm_head logits — out of attention scope, same
  law.)
- **Variants/used-by**: Gemma-2 (both caps), Grok-1. REMOVED in Gemma-3 (replaced by
  ATT-QKNORM). Signal: `attn_logit_softcapping` non-null.
- **Tensor names**: none — config scalar.
- **Witnessed content**: the cap constant; the fact of its position in the score
  pipeline (scale → cap → mask → softmax).
- **Read-side mechanism**: one tanh per score when active; identity when null.
- **Laplace status**: **missing**.
- **Gate**: Gemma-2 replay with cap matches HF fp32 attention probs ≤1e-6; cap
  omitted fails on any score with |s|>~25 (mutation check).
- **Sources**: https://arxiv.org/abs/2408.00118 ; https://huggingface.co/docs/transformers/model_doc/gemma2

### ATT-QKNORM
- **ID**: ATT-QKNORM
- **What/Why**: Normalizes each head's query and key vectors to unit RMS (with a learned
  per-dimension gain) right after projection, so score magnitude is controlled at the
  source instead of capped after the fact (ATT-SOFTCAP). Skipping it feeds raw,
  unnormalized vectors into scores — completely different attention.
- **Formulation**: per head vector `u ∈ R^{d_h}`:
  `u ← u / RMS(u) ⊙ w`, `w ∈ R^{d_h}` (ONE weight vector shared across heads, applied
  per-head-vector), separately for q (`q_norm`) and k (`k_norm`); applied BEFORE
  POS-ROPE. Qwen3-Next stores the zero-centered variant (`⊙ (1 + w)`).
- **Variants/used-by**: Qwen3/Qwen3-MoE/Qwen3-Next, Gemma-3 (its RMSNorm is also
  zero-centered `1+w`), gpt-oss does NOT; ViT-22B origin. Signal: presence of the
  tensors.
- **Tensor names**: `model.layers.{L}.self_attn.q_norm.weight`,
  `model.layers.{L}.self_attn.k_norm.weight` (shape `[head_dim]`).
- **Witnessed content**: both gain vectors verbatim; the per-head-application law; the
  epsilon (`rms_norm_eps`); the plain-vs-zero-centered weight convention per family.
- **Read-side mechanism**: RMS normalize each head's q and k slice, multiply by gain,
  then rotate (POS-ROPE), then dot.
- **Laplace status**: **missing** (no qwen3/gemma3 profile; qwen2 profile has no norm
  slot in attention).
- **Gate**: Qwen3 replayed q·k after norm matches HF ≤1e-6; norm-after-RoPE ordering
  fails (mutation check).
- **Sources**: https://arxiv.org/abs/2302.05442 ; https://github.com/huggingface/transformers/blob/main/src/transformers/models/qwen3/modeling_qwen3.py

### ATT-QKNORM-FULL
- **ID**: ATT-QKNORM-FULL
- **What/Why**: Same idea as ATT-QKNORM but normalized across the WHOLE projection
  width (all heads at once), not per head. The two variants produce different numbers
  from identical weights, so the width law must be witnessed, not assumed.
- **Formulation**: `q ← RMSNorm_{H·d_h}(q) ⊙ w_q`, `w_q ∈ R^{H·d_h}` (full width, one
  RMS statistic over the concatenated heads), likewise k; before POS-ROPE.
- **Variants/used-by**: OLMo-2. Signal: `q_norm.weight` shape `H·d_h` instead of `d_h`.
- **Tensor names**: `model.layers.{L}.self_attn.q_norm.weight`,
  `.k_norm.weight` (shape `[H·d_h]`).
- **Witnessed content**: gain vectors; the FULL-WIDTH statistic law (shape is the
  discriminator vs ATT-QKNORM).
- **Read-side mechanism**: one RMS over the full q (and full k) vector, then slice
  heads.
- **Laplace status**: **missing**.
- **Gate**: OLMo-2 replay with full-width RMS matches HF; per-head RMS on the same
  weights fails (mutation check).
- **Sources**: https://arxiv.org/abs/2501.00656 ; https://sebastianraschka.com/llm-architecture-gallery/qk-norm/

### ATT-SOFTMAX
- **ID**: ATT-SOFTMAX
- **What/Why**: Turns a row of scores into a probability distribution over context —
  the "who gets copied, and how much" decision. It is the only nonlinearity between
  scores and the value mix; replaying it exactly (including its stabilization) is the
  heart of read-side attention.
- **Formulation**: `p_ij = exp(s_ij − max_j s_ij) / Σ_{j'} exp(s_ij' − max)`, over
  unmasked `j` only (masks add −∞ pre-softmax, section D); sinks add a phantom
  denominator term (ATT-SINK).
- **Variants/used-by**: Universal. Numerically: HF upcasts to fp32 for softmax
  regardless of weight dtype — the replay must match that law.
- **Tensor names**: none.
- **Witnessed content**: nothing per-checkpoint beyond dtype law; the primitive itself
  is pipeline law.
- **Read-side mechanism**: max-subtracted exp/sum in fp32+ over exactly the visible key
  set (mask topology decides visibility).
- **Laplace status**: **missing** as a replay mechanism (the substrate's fold replaces
  softmax ranking with Glicko by design; EXACT replay for the model lane still owes
  this).
- **Gate**: attention probability rows sum to 1 within 1e-6 and match HF fp32 rows
  ≤1e-6 L∞ on a probe context.
- **Sources**: https://arxiv.org/abs/1706.03762

### ATT-SINK
- **ID**: ATT-SINK
- **What/Why**: A learned per-head "escape valve": a phantom score added to the softmax
  denominator so heads can dump probability nowhere instead of being forced to attend to
  something. Ignoring the sink tensor renormalizes every attention row upward.
- **Formulation**: `p_ij = exp(s_ij) / (Σ_{j'} exp(s_ij') + exp(σ_h))` with learned
  scalar `σ_h` per head (equivalently a virtual key with score `σ_h` and zero value).
  Distinct from StreamingLLM "sink tokens" (an inference-time cache policy, no
  weights — not a checkpoint primitive).
- **Variants/used-by**: gpt-oss-20b/120b. Signal: presence of `sinks` tensors.
- **Tensor names**: `model.layers.{L}.self_attn.sinks` (shape `[H]`, fp32 in GGUF as
  `attn_sinks`).
- **Witnessed content**: the `[H]` sink vector per layer verbatim.
- **Read-side mechanism**: add `exp(σ_h)` to each softmax denominator (or append the
  virtual column); value sum unchanged.
- **Laplace status**: **missing**.
- **Gate**: gpt-oss replayed rows sum to `< 1` by exactly `exp(σ_h)/denominator` and
  match HF; sink omitted → rows sum to 1 and fail (mutation check).
- **Sources**: https://huggingface.co/docs/transformers/model_doc/gpt_oss ; https://arxiv.org/abs/2309.17453

### ATT-TEMP
- **ID**: ATT-TEMP
- **What/Why**: Llama-4 sharpens attention as the context grows by scaling queries with
  a length-dependent temperature on its position-free layers (POS-NOPE). Without it,
  very long contexts blur those layers' attention.
- **Formulation**: `q_i ← q_i · (1 + attn_scale · log(⌊i / floor_scale⌋ + 1))`, applied
  ONLY on NoPE layers (not RoPE layers), before scoring. Defaults: `attn_scale = 0.1`,
  `floor_scale = 8192`.
- **Variants/used-by**: Llama-4 Scout/Maverick ("iRoPE"). Config:
  `attn_temperature_tuning: true`, `attn_scale`, `floor_scale`.
- **Tensor names**: none — config scalars.
- **Witnessed content**: the two scalars + the flag + which layers it applies to
  (coupled to the POS-NOPE layer list).
- **Read-side mechanism**: position-dependent scalar multiply on q before dot.
- **Laplace status**: **missing**.
- **Gate**: Llama-4 NoPE-layer scores at position 100k replayed with and without
  temperature differ; with-temperature matches HF (mutation check).
- **Sources**: https://huggingface.co/docs/transformers/model_doc/llama4 ; https://blog.vllm.ai/2025/04/05/llama4.html

### ATT-LAYERSCALE
- **ID**: ATT-LAYERSCALE
- **What/Why**: A legacy GPT-2-family option dividing scores by the 1-based layer index
  to tame deep-stack magnitudes. Rare, but a checkpoint that sets it silently rescales
  every layer differently.
- **Formulation**: `s ← s / (L_idx + 1)` when `scale_attn_by_inverse_layer_idx: true`;
  companion `scale_attn_weights: false` DISABLES the `1/√d_h` of ATT-SCALE entirely.
- **Variants/used-by**: GPT-2 config options (Mistral-GPT2 lineage, some fine-tunes);
  defaults false/true respectively.
- **Tensor names**: none.
- **Witnessed content**: both booleans per model.
- **Read-side mechanism**: constant per-layer multiply.
- **Laplace status**: **missing**.
- **Gate**: recorded flags reproduce HF `GPT2Attention` scaling product exactly for a
  checkpoint that sets them.
- **Sources**: https://github.com/huggingface/transformers/blob/main/src/transformers/models/gpt2/modeling_gpt2.py

### ATT-DROPOUT
- **ID**: ATT-DROPOUT
- **What/Why**: Training-time randomness that zeroes some attention probabilities;
  at inference it is a NO-OP. Cataloged so the ETL provably ignores it rather than
  accidentally modeling it.
- **Formulation**: inference: identity. (Training: `p ← p ⊙ Bernoulli(1−ρ)/(1−ρ)`,
  ρ = `attention_dropout` / `attn_pdrop`.)
- **Variants/used-by**: config key present nearly everywhere (`attention_dropout`,
  `attn_pdrop`, BERT `attention_probs_dropout_prob`); always inactive in eval mode.
- **Tensor names**: none.
- **Witnessed content**: the scalar, recorded for provenance completeness only; flagged
  inference-inert.
- **Read-side mechanism**: none — MUST be identity.
- **Laplace status**: **missing** (harmless; record-only).
- **Gate**: replay output is bit-identical whether the recorded dropout scalar is 0.0
  or its checkpoint value.
- **Sources**: https://arxiv.org/abs/1706.03762

---

## D. Masking & context topology

### MSK-CAUSAL
- **ID**: MSK-CAUSAL
- **What/Why**: Decoder rule: a token may only look at itself and the past. It is what
  makes generation well-defined; a wrong mask leaks the future into every score row.
- **Formulation**: score `s_ij` kept iff `j ≤ i`; else `+= −∞` before ATT-SOFTMAX
  (equivalently excluded from the sum).
- **Variants/used-by**: every decoder-only model (GPT-2 → Llama-4);
  `is_decoder`/architecture class is the signal, not a config scalar.
- **Tensor names**: none (GPT-2 stores a `attn.bias` tril BUFFER — ignore, it is the
  mask constant, not learned).
- **Witnessed content**: the per-layer mask topology tag (causal | bidirectional |
  sliding | chunked), one enum per layer.
- **Read-side mechanism**: iterate keys `j ∈ [0, i]` only — in graph terms, restrict
  the walk frontier to attested-visible pairs.
- **Laplace status**: **missing** (no mask semantics anywhere in the profile).
- **Gate**: replayed row for query `i` assigns exactly zero probability to every
  `j > i`.
- **Sources**: https://arxiv.org/abs/1706.03762

### MSK-BIDIR
- **ID**: MSK-BIDIR
- **What/Why**: Encoder rule: every token sees every token, both directions. BERT-style
  understanding models need it; applying MSK-CAUSAL to them halves their information.
- **Formulation**: all `(i,j)` pairs visible (minus MSK-PAD).
- **Variants/used-by**: BERT/RoBERTa, T5 encoder, embedding models (BGE, GTE),
  encoder half of LAY-CROSS models.
- **Tensor names**: none.
- **Witnessed content**: topology tag = bidirectional for the encoder stack.
- **Read-side mechanism**: full key set per query.
- **Laplace status**: **partial** — the Bert profile exists (weights), but nothing
  records that its attention is bidirectional.
- **Gate**: BERT replayed attention row for token 0 has nonzero mass on the last token.
- **Sources**: https://arxiv.org/abs/1810.04805

### MSK-PAD
- **ID**: MSK-PAD
- **What/Why**: Batch padding tokens must be invisible to real tokens. Pure runtime
  bookkeeping — no checkpoint content — but cataloged so replay defines its behavior
  for ragged inputs.
- **Formulation**: `s_ij += −∞` where `j` is padding (from the runtime attention_mask).
- **Variants/used-by**: universal at serving time; irrelevant for batch-of-one replay.
- **Tensor names**: none.
- **Witnessed content**: none (explicitly: nothing to witness).
- **Read-side mechanism**: substrate replay operates on exact token sequences —
  padding never exists; a no-op by construction, asserted as such.
- **Laplace status**: **covered by construction** (no padded batches on the read side).
- **Gate**: replay of a sequence equals replay of the same sequence "padded" — trivially
  true because padding is unrepresentable; the gate is that the pipeline rejects
  padding tokens as input.
- **Sources**: https://huggingface.co/docs/transformers/glossary#attention-mask

### MSK-SWA
- **ID**: MSK-SWA
- **What/Why**: Sliding-window (local) attention: each token sees only the last `W`
  tokens instead of the whole past. Cheap long context; the exact window boundary
  (inclusive/exclusive) differs by family and is a classic off-by-one exactness trap.
- **Formulation**: visible iff `j ≤ i` AND `i − j < W` (HF Mistral/gpt-oss law:
  strictly-less; token attends to itself plus `W−1` predecessors). Gemma's
  implementations have historically used an inclusive variant — the boundary law is
  witnessed per family, not assumed.
- **Variants/used-by**: Mistral-7B (W=4096), Mixtral, Gemma-2 (4096, alternating),
  Gemma-3 (1024, 5:1), gpt-oss (128, alternating), Phi-3 (2047/262144 longrope combos),
  Qwen2.5-1M variants. Config: `sliding_window`, sometimes `max_window_layers`.
- **Tensor names**: none.
- **Witnessed content**: `W` per model AND per layer-type; the boundary inclusivity
  law; interaction with MSK-CAUSAL (window is causal-side only).
- **Read-side mechanism**: key iteration bounded to `[max(0, i−W+1), i]`.
- **Laplace status**: **missing**.
- **Gate**: replayed row at `i = W+10` has zero mass at `j = i−W` and nonzero at
  `j = i−W+1` (exact boundary test against HF).
- **Sources**: https://arxiv.org/abs/2310.06825 ; https://arxiv.org/abs/2004.05150

### MSK-ALT
- **ID**: MSK-ALT
- **What/Why**: Modern models alternate local layers (MSK-SWA) with full layers
  (MSK-CAUSAL) in a fixed repeating pattern, so cheap layers handle nearby syntax and
  rare full layers carry long-range links. The per-layer pattern must be witnessed or
  half the layers get the wrong mask.
- **Formulation**: `layer_types[L] ∈ {sliding_attention, full_attention, ...}`;
  Gemma-2 1:1, Gemma-3 5:1 (five local then one global), gpt-oss even/odd, Llama-4
  3 chunked : 1 full (MSK-CHUNK+POS-NOPE), Qwen3-Next 3 linear : 1 full (LAY-HYBRID).
- **Variants/used-by**: Gemma-2/3, gpt-oss, Llama-4, Qwen3-Next, Ministral. Config:
  `layer_types` (modern HF), or derived from `sliding_window_pattern` /
  `full_attention_interval` / `no_rope_layer_interval`.
- **Tensor names**: none.
- **Witnessed content**: the FULL resolved per-layer list (never the generator rule
  alone) — one topology enum + window per layer, attested at the layer coordinate.
- **Read-side mechanism**: dispatch mask law per layer from the recorded list.
- **Laplace status**: **missing**.
- **Gate**: recorded list length equals `num_hidden_layers` and per-layer replay
  matches HF for one local and one global layer of the same checkpoint.
- **Sources**: https://arxiv.org/abs/2503.19786 ; https://huggingface.co/docs/transformers/model_doc/gemma3

### MSK-CHUNK
- **ID**: MSK-CHUNK
- **What/Why**: Llama-4's local variant: the context is cut into fixed blocks and a
  token sees only its own block (causally). Different geometry from MSK-SWA — the
  visible set jumps at block edges instead of sliding.
- **Formulation**: visible iff `j ≤ i` AND `⌊j/C⌋ = ⌊i/C⌋`, `C =
  attention_chunk_size` (8192), applied on RoPE layers only (NoPE layers are full,
  POS-NOPE).
- **Variants/used-by**: Llama-4 Scout/Maverick. Config: `attention_chunk_size`.
- **Tensor names**: none.
- **Witnessed content**: `C`; the chunked-vs-full per-layer assignment (rides MSK-ALT's
  resolved list).
- **Read-side mechanism**: key range `[C·⌊i/C⌋, i]`.
- **Laplace status**: **missing**.
- **Gate**: at `i = C+1`, replayed row has zero mass on `j = C−1` (previous chunk) and
  nonzero on `j = C` — distinguishing chunked from sliding with the same width.
- **Sources**: https://huggingface.co/docs/transformers/model_doc/llama4

### MSK-PREFIX
- **ID**: MSK-PREFIX
- **What/Why**: Prefix-LM: the prompt region is fully visible in both directions
  (MSK-BIDIR) while the generated region stays causal (MSK-CAUSAL). Used by T5-style
  and GLM-style training; the boundary is an input property, not a weight.
- **Formulation**: visible iff `j ≤ i` OR `j < P` (prefix length `P`).
- **Variants/used-by**: UL2/PrefixLM objectives, GLM/ChatGLM, T5 in prefix-LM mode.
  No standard config key — architecture class + runtime `P`.
- **Tensor names**: none.
- **Witnessed content**: the capability tag on the architecture; `P` is query-time
  input, not witnessed.
- **Read-side mechanism**: two-zone key iteration per query.
- **Laplace status**: **missing** (no such family profiled).
- **Gate**: with `P=4`, replayed row for `i=1` has nonzero mass on `j=3` and zero on
  `j≥P` beyond `i` — both zones verified against a reference implementation.
- **Sources**: https://arxiv.org/abs/1910.10683 ; https://arxiv.org/abs/2103.10360

---

## E. Positional schemes

### POS-LEARNED
- **ID**: POS-LEARNED
- **What/Why**: The oldest scheme: a lookup table with one learned vector per absolute
  position, added to the token embedding before layer 0. Attention itself is
  position-blind; this is where BERT/GPT-2 get order from. Hard ceiling at the trained
  table length.
- **Formulation**: `x_i ← e(token_i) + P[i]`, `P ∈ R^{n_max × d}`,
  `n_max = max_position_embeddings` (BERT 512, GPT-2 1024). BERT adds POS-SEGMENT too,
  then a LayerNorm.
- **Variants/used-by**: BERT/RoBERTa (note RoBERTa's `padding_idx` offset: positions
  start at 2), GPT-2 (`wpe`), OPT (offset 2), BLOOM uses POS-ALIBI instead. Signal:
  `position_embedding_type: absolute` or family default.
- **Tensor names**: `embeddings.position_embeddings.weight` (BERT);
  `transformer.wpe.weight` (GPT-2); `model.decoder.embed_positions.weight` (OPT).
- **Witnessed content**: full position table verbatim (it is content like any
  embedding); the index-offset law (RoBERTa/OPT +2); `n_max`.
- **Read-side mechanism**: table lookup + vector add at position `i` before layer 0;
  nothing inside attention.
- **Laplace status**: **missing** — the Bert profile records `word_embeddings` only;
  position and token-type tables are absent.
- **Gate**: `P` rebuilt from substrate hashes equal to checkpoint tensor; replayed
  layer-0 input for position 7 matches HF embedding output exactly.
- **Sources**: https://arxiv.org/abs/1810.04805 ; https://openai.com/research/language-models

### POS-SINUSOID
- **ID**: POS-SINUSOID
- **What/Why**: Fixed (non-learned) sine/cosine waves of geometrically spaced
  frequencies added to embeddings — position encoding with zero parameters. Must be
  regenerated bit-exactly (same dtype path) even though nothing is learned.
- **Formulation**: `P[i, 2t] = sin(i / 10000^{2t/d})`, `P[i, 2t+1] = cos(·)`;
  `x_i ← e(token_i) + P[i]` (some families scale `e` by `√d` first — witnessed law).
- **Variants/used-by**: original Transformer, Marian/M2M100, Whisper ENCODER (stored as
  a buffer; decoder is POS-LEARNED). Signal: family; sometimes
  `position_embedding_type: sinusoidal`.
- **Tensor names**: often absent (recomputed); Whisper stores
  `model.encoder.embed_positions.weight`.
- **Witnessed content**: the generator law + `d`, base 10000, interleave-vs-concat
  layout; if the checkpoint stores the buffer, witness the buffer and verify the
  regeneration matches it.
- **Read-side mechanism**: closed-form evaluation at query time; add before layer 0.
- **Laplace status**: **missing**.
- **Gate**: regenerated table equals the stored buffer (when present) to 0 ULP in the
  buffer's dtype.
- **Sources**: https://arxiv.org/abs/1706.03762

### POS-ROPE
- **ID**: POS-ROPE
- **What/Why**: Rotary embedding: instead of adding position to the embedding, rotate
  each q/k head vector by an angle proportional to its position, pairwise per frequency.
  The q·k dot product then depends only on the DISTANCE `i−j` — relative position for
  free. The default of every modern decoder.
- **Formulation**: pair dims into 2-D planes with frequencies
  `θ_t = base^{−2t/d_r}`, `t ∈ [0, d_r/2)`, `base = rope_theta`, `d_r` = rotary width
  (= `d_h` unless POS-ROPE-PARTIAL). At position `m`, rotate each plane by `m·θ_t`:
  `q'_i = R(i)q_i`, `k'_j = R(j)k_j` ⇒ `q'_i·k'_j` depends on `i−j`. TWO pairing
  layouts exist: HF/Llama "half-split" (`rotate_half`: dim `t` pairs with `t+d_r/2`)
  vs GPT-J/NeoX "interleaved" (dim `2t` pairs with `2t+1`) — same math, different
  permutation; the layout is witnessed per family.
- **Variants/used-by**: Llama 1-4, Mistral, Qwen, Gemma, Phi, DeepSeek (decoupled slice,
  ATT-MLA), gpt-oss, Falcon-40B+. Config: `rope_theta` (10k classic; 500k Llama-3; 1M
  Mistral-Nemo/Gemma-3-global; 150k gpt-oss), `max_position_embeddings`.
- **Tensor names**: none learned (some dumps carry `rotary_emb.inv_freq` buffers —
  verify-only, never authoritative).
- **Witnessed content**: `rope_theta`, rotary width, pairing-layout law, application
  point (after ATT-QKNORM where present; q and k only, never v).
- **Read-side mechanism**: per-plane 2-D rotation of q at `i` and k at `j` — or
  equivalently one relative rotation by `i−j` folded into the score; cos/sin computed
  in fp32 like HF.
- **Laplace status**: **missing** — the llama/qwen2 profiles carry no `rope_theta`, no
  rotary law; the ATTENDS bilinear path is currently position-free.
- **Gate**: replayed `q'_i·k'_j` equals HF fp32 for probe positions ≤1e-6; swapping
  pairing layout on the same weights fails (mutation check).
- **Sources**: https://arxiv.org/abs/2104.09864

### POS-ROPE-PARTIAL
- **ID**: POS-ROPE-PARTIAL
- **What/Why**: Only the first fraction of each head's dims gets rotated (POS-ROPE);
  the rest pass through position-free. Rotating the full width on these checkpoints
  scrambles the unrotated dims' contribution.
- **Formulation**: `d_r = ⌊d_h · partial_rotary_factor⌋`; rotate dims `[0, d_r)`, pass
  `[d_r, d_h)` unchanged; concatenate.
- **Variants/used-by**: GPT-NeoX/Pythia (`rotary_pct: 0.25`), GPT-J (`rotary_dim: 64`),
  Phi-1/2 (`partial_rotary_factor: 0.4/0.5`), Persimmon (0.5), Nemotron (0.5),
  ChatGLM. Config keys: `partial_rotary_factor` | `rotary_pct` | `rotary_dim`
  | `rotary_ndims`.
- **Tensor names**: none.
- **Witnessed content**: the resolved rotary width `d_r` (record the number, not just
  the fraction); which slice is rotary (leading, by convention — witnessed).
- **Read-side mechanism**: rotation applied to the slice only.
- **Laplace status**: **missing** — the Phi profile exists (weights) but records no
  rotary fraction; scores replayed from it would be wrong for Phi today.
- **Gate**: Phi-2 replayed scores match HF; full-width rotation on the same weights
  fails (mutation check).
- **Sources**: https://github.com/huggingface/transformers/blob/main/src/transformers/models/gpt_neox/configuration_gpt_neox.py ; https://arxiv.org/abs/2104.09864

### POS-ROPE-LINEAR
- **ID**: POS-ROPE-LINEAR
- **What/Why**: The simplest context-window stretch: divide every position index by a
  constant before rotating (POS-ROPE), squeezing a longer sequence into the trained
  angle range. Trades resolution for length.
- **Formulation**: rotate at angle `(m / f)·θ_t`, `f = rope_scaling.factor`.
- **Variants/used-by**: early long-context fine-tunes (Vicuna-16k, LongChat,
  CodeLlama-ish variants). Config: `rope_scaling: {rope_type|type: linear, factor: f}`.
- **Tensor names**: none.
- **Witnessed content**: `f`; the fact that it applies to all layers uniformly.
- **Read-side mechanism**: position divide before rotation.
- **Laplace status**: **missing**.
- **Gate**: replay at position 8192 with `f=4` equals unscaled replay at position 2048
  exactly, and matches HF.
- **Sources**: https://huggingface.co/docs/transformers/main/en/internal/rope_utils ; https://arxiv.org/abs/2306.15595

### POS-ROPE-DYNNTK
- **ID**: POS-ROPE-DYNNTK
- **What/Why**: NTK-aware stretch: instead of dividing positions, RAISE the rotation
  base so high frequencies (local order) stay sharp while low frequencies stretch.
  "Dynamic" recomputes the base as the running sequence grows — replay must reproduce
  that length-dependent base, or long-context scores drift.
- **Formulation**: static NTK: `base' = base · f^{d_r/(d_r−2)}`. Dynamic (HF
  `dynamic`): for current length `L > L_orig`:
  `base' = base · ((f·L/L_orig) − (f−1))^{d_r/(d_r−2)}`, re-derived per length;
  identity when `L ≤ L_orig`.
- **Variants/used-by**: Qwen(1) long, many community long-context models. Config:
  `rope_scaling: {rope_type: dynamic, factor, original_max_position_embeddings}`.
- **Tensor names**: none.
- **Witnessed content**: `f`, `L_orig`, base; the recompute-per-length law tagged
  explicitly (the only positional scheme whose constants depend on query-time length).
- **Read-side mechanism**: derive `base'` from the ACTUAL replay context length, then
  standard rotation.
- **Laplace status**: **missing**.
- **Gate**: replayed scores at `L = 2·L_orig` match HF with recomputed base; frozen
  base fails (mutation check).
- **Sources**: https://github.com/huggingface/transformers/blob/main/src/transformers/modeling_rope_utils.py ; https://arxiv.org/abs/2309.00071

### POS-ROPE-YARN
- **ID**: POS-ROPE-YARN
- **What/Why**: The refined stretch: per-frequency interpolation (high frequencies kept,
  low frequencies scaled, smooth ramp between) PLUS a small global score multiplier
  ("mscale") compensating for the stretched geometry. Two coupled effects — replaying
  only the frequency part misses the score temperature.
- **Formulation**: per dim `t` with wavelength `λ_t = 2π/θ_t`, ramp
  `γ_t ∈ [0,1]` between `beta_fast` (=32) and `beta_slow` (=1) revolution counts:
  `θ'_t = (1−γ_t)·θ_t/f + γ_t·θ_t`. Attention scores additionally multiplied by
  `mscale = 0.1·ln(f) + 1` (or config `attention_factor`; DeepSeek adds
  `mscale_all_dim` in its own variant).
- **Variants/used-by**: DeepSeek-V2/V3 (with MLA), Qwen2.5-1M, many 128k+ fine-tunes.
  Config: `rope_scaling: {rope_type: yarn, factor, beta_fast, beta_slow,
  original_max_position_embeddings, attention_factor|mscale, mscale_all_dim}`.
- **Tensor names**: none.
- **Witnessed content**: all YaRN scalars; the resolved per-dim `θ'_t` table (calculated
  layer, versioned); the mscale composed into the effective score scale (ATT-SCALE-CFG).
- **Read-side mechanism**: rotation with `θ'_t`; score multiply by mscale.
- **Laplace status**: **missing**.
- **Gate**: DeepSeek-V2-Lite long-context scores match HF; dropping mscale fails
  (mutation check).
- **Sources**: https://arxiv.org/abs/2309.00071

### POS-ROPE-LONGROPE
- **ID**: POS-ROPE-LONGROPE
- **What/Why**: Microsoft's variant: a SEARCHED per-dimension rescale vector (not a
  formula), with two regimes — one factor list for short contexts, another for long —
  plus its own score multiplier. The factor arrays are checkpoint content.
- **Formulation**: `θ'_t = θ_t / factor_t`, with `factor_t` from `short_factor[]` when
  `L ≤ original_max_position_embeddings`, else `long_factor[]`; score multiplier
  `√(1 + ln(f)/ln(L_orig))` (or explicit `attention_factor`).
- **Variants/used-by**: Phi-3/3.5/4-mini (128k). Config: `rope_scaling: {rope_type|type:
  longrope, short_factor: [d_r/2 floats], long_factor: [...],
  original_max_position_embeddings}`.
- **Tensor names**: none (the factor ARRAYS live in config.json — witnessed like
  tensors).
- **Witnessed content**: both factor arrays verbatim, `L_orig`, the regime-switch law,
  the score multiplier.
- **Read-side mechanism**: pick regime by replay context length; per-dim divided
  frequencies; score multiply.
- **Laplace status**: **missing**.
- **Gate**: Phi-3-mini-128k replay matches HF in BOTH regimes; regimes swapped fails
  (mutation check).
- **Sources**: https://arxiv.org/abs/2402.13753 ; https://huggingface.co/docs/transformers/main/en/internal/rope_utils

### POS-ROPE-LLAMA3
- **ID**: POS-ROPE-LLAMA3
- **What/Why**: Meta's piecewise stretch for Llama-3.1+: frequencies with short
  wavelengths untouched, long wavelengths divided by the factor, smooth blend between —
  no score multiplier. A distinct named `rope_type` the ETL must dispatch on.
- **Formulation**: with `λ_t = 2π/θ_t`, `lo = L_orig/low_freq_factor`,
  `hi = L_orig/high_freq_factor`: `λ_t < hi` → keep `θ_t`; `λ_t > lo` → `θ_t/f`; else
  interpolate by `s = (L_orig/λ_t − low)/(high − low)` between the two.
  Values: `f=8, low=1.0, high=4.0, L_orig=8192`.
- **Variants/used-by**: Llama-3.1/3.2/3.3. Config: `rope_scaling: {rope_type: llama3,
  factor, low_freq_factor, high_freq_factor, original_max_position_embeddings}`.
- **Tensor names**: none.
- **Witnessed content**: the four scalars; resolved `θ'_t` table (calculated,
  versioned).
- **Read-side mechanism**: rotation with the piecewise table; no score change.
- **Laplace status**: **missing**.
- **Gate**: Llama-3.1-8B replayed scores at 32k match HF; plain-RoPE table fails
  (mutation check).
- **Sources**: https://arxiv.org/abs/2407.21783 ; https://github.com/huggingface/transformers/blob/main/src/transformers/modeling_rope_utils.py

### POS-ROPE-DUAL
- **ID**: POS-ROPE-DUAL
- **What/Why**: Gemma-3 runs TWO rope constants in one model: local layers (MSK-SWA)
  keep base 10k, global layers use 1M (plus linear factor 8 for 128k). The base becomes
  a per-layer fact, not a per-model fact.
- **Formulation**: `base(L) = rope_local_base_freq` (10k) if `layer_types[L] =
  sliding_attention`, else `rope_theta` (1e6) with `rope_scaling {linear, 8}` on the
  large sizes.
- **Variants/used-by**: Gemma-3 (4B+). Config: `rope_local_base_freq`, `rope_theta`,
  `rope_scaling`, `layer_types`.
- **Tensor names**: none.
- **Witnessed content**: BOTH bases, attested at the layer coordinate (rides MSK-ALT's
  per-layer list).
- **Read-side mechanism**: per-layer frequency table dispatch.
- **Laplace status**: **missing**.
- **Gate**: replays of one local and one global layer both match HF; single-base replay
  fails on one of them (mutation check).
- **Sources**: https://arxiv.org/abs/2503.19786

### POS-MROPE
- **ID**: POS-MROPE
- **What/Why**: Multimodal RoPE (Qwen-VL): the head's frequency dims are partitioned
  into temporal/height/width sections rotated by separate position counters. For pure
  text all three counters coincide and it degenerates to POS-ROPE — the ETL must
  witness the sections to know that degeneration is exact.
- **Formulation**: `mrope_section = [s_t, s_h, s_w]` splits the `d_r/2` frequencies;
  each section rotates by its own position id. Text-only: all ids equal `i` ⇒ standard
  rotation.
- **Variants/used-by**: Qwen2-VL/Qwen2.5-VL/Qwen3-VL text towers. Config:
  `rope_scaling: {type: mrope|default, mrope_section: [...]}`.
- **Tensor names**: none.
- **Witnessed content**: the section triple; the text-degeneration law.
- **Read-side mechanism**: standard rotation for text replay; sectioned rotation only
  if/when non-text position ids enter scope.
- **Laplace status**: **missing** (VL towers out of current scope; witnessed so text
  replay of these checkpoints is still exact).
- **Gate**: text-only replay of a Qwen2-VL text tower equals plain-RoPE replay with the
  same base, and matches HF.
- **Sources**: https://arxiv.org/abs/2409.12191

### POS-ALIBI
- **ID**: POS-ALIBI
- **What/Why**: No embeddings, no rotation: just subtract a per-head constant times the
  distance from every score — closer is better, each head with a different slope. Whole
  scheme is `H` scalars derived from a fixed formula.
- **Formulation**: `s_ij ← s_ij − m_h·(i−j)`; slopes `m_h = 2^{−8(h+1)/H}` for `H` a
  power of 2 (defined interpolation otherwise); MPT generalizes via
  `alibi_bias_max` (=8): `m_h = 2^{−alibi_bias_max·(h+1)/H}`. Added after scale,
  before softmax.
- **Variants/used-by**: BLOOM (`alibi: true` implicit), MPT (`attn_config.alibi`),
  Falcon-7B-instruct variants (`alibi` key), Baichuan-13B, JinaBERT. Signal:
  `alibi`/`attn_config.alibi` true; absence of rope keys.
- **Tensor names**: none (slopes derived; never stored).
- **Witnessed content**: `H`, `alibi_bias_max` if present; the resolved slope vector
  (calculated layer, versioned) so replay never re-derives the interpolation edge
  cases.
- **Read-side mechanism**: per-score fused multiply-subtract with the head's slope and
  the offset `i−j`.
- **Laplace status**: **missing**.
- **Gate**: BLOOM replayed scores match HF ≤1e-6; slope vector for H=32 matches HF's
  `build_alibi_tensor` exactly.
- **Sources**: https://arxiv.org/abs/2108.12409

### POS-T5BUCKET
- **ID**: POS-T5BUCKET
- **What/Why**: T5 learns a small table of per-head score offsets indexed by BUCKETED
  distance (near distances exact, far distances log-grouped), stored only in layer 0
  and shared by all layers. It is the only positional scheme here whose parameters are
  a learned attention-side tensor.
- **Formulation**: `s_ij ← q_i·k_j + B[bucket(j−i), h]` (note: NO `1/√d_h`,
  ATT-SCALE). Bucketing: `relative_attention_num_buckets` (32),
  `relative_attention_max_distance` (128); encoder bidirectional (sign gets half the
  buckets), decoder causal (negatives only); exact below `num_buckets/2` (per sign),
  log-spaced to max_distance, clamped beyond. Cross-attention (LAY-CROSS) carries NO
  bias.
- **Variants/used-by**: T5/mT5/Flan-T5/T5Gemma-lineage, UL2. Config keys above +
  family.
- **Tensor names**:
  `encoder.block.0.layer.0.SelfAttention.relative_attention_bias.weight`
  `[num_buckets, H]`; decoder twin at `decoder.block.0.layer.0.SelfAttention...`;
  blocks > 0 have none (shared).
- **Witnessed content**: both bias tables verbatim; the bucket function's three
  constants + bidirectional flag; the layer-0-shared law; the no-scale law.
- **Read-side mechanism**: compute bucket from `j−i` (integer math), table lookup, add
  to raw dot product, softmax.
- **Laplace status**: **missing** (no T5 profile).
- **Gate**: bucket function reproduces HF `_relative_position_bucket` for all offsets
  in [−300, 300]; replayed layer-5 scores use layer-0's table and match HF.
- **Sources**: https://arxiv.org/abs/1910.10683 ; https://github.com/huggingface/transformers/blob/main/src/transformers/models/t5/modeling_t5.py

### POS-SHAW
- **ID**: POS-SHAW
- **What/Why**: The pre-T5 relative scheme still selectable in BERT configs: a learned
  embedding per clipped relative distance, dotted with the query (and optionally key)
  inside the score. Rare, but silently different from both POS-LEARNED and
  POS-T5BUCKET.
- **Formulation**: `relative_key`: `s_ij ← q_i·k_j + q_i·a_{clip(j−i)}` with learned
  `a ∈ R^{(2·n_max−1) × d_h}`; `relative_key_query` adds the symmetric `k_j·a` term.
  Scaled per ATT-SCALE (unlike T5).
- **Variants/used-by**: BERT/ELECTRA variants with `position_embedding_type:
  relative_key | relative_key_query`.
- **Tensor names**: `encoder.layer.{L}.attention.self.distance_embedding.weight`.
- **Witnessed content**: the distance-embedding table per layer; the clip range; which
  of the two score terms is active.
- **Read-side mechanism**: extra dot product per score against the distance row.
- **Laplace status**: **missing** (Bert profile assumes absolute only).
- **Gate**: a `relative_key` checkpoint replays to HF parity; treating it as absolute
  fails (mutation check).
- **Sources**: https://arxiv.org/abs/1803.02155

### POS-NOPE
- **ID**: POS-NOPE
- **What/Why**: No positional signal at all — the layer relies purely on the causal
  mask's asymmetry (MSK-CAUSAL) to sense order. In Llama-4 every 4th layer is NoPE and
  doubles as the full-context layer; replay must NOT rotate there.
- **Formulation**: `s_ij = (q_i·k_j)·scale` with raw projections; optionally
  ATT-TEMP's length-dependent query scale.
- **Variants/used-by**: Llama-4 (`no_rope_layers` list / every
  `no_rope_layer_interval`=4), research models (Kallini et al.), some hybrid lanes.
  Signal: explicit layer list.
- **Tensor names**: none.
- **Witnessed content**: the resolved rope/no-rope per-layer list (rides MSK-ALT's
  resolved `layer_types`).
- **Read-side mechanism**: skip rotation on listed layers; full-context key range.
- **Laplace status**: **missing**.
- **Gate**: Llama-4 NoPE-layer replayed scores are invariant to a global position shift
  of the probe context; RoPE-layer scores are not.
- **Sources**: https://arxiv.org/abs/2305.19466 ; https://huggingface.co/docs/transformers/model_doc/llama4

### POS-SEGMENT
- **ID**: POS-SEGMENT
- **What/Why**: BERT's third embedding table: marks which SENTENCE (segment A/B) a
  token belongs to, added alongside POS-LEARNED so paired-sentence tasks can tell the
  halves apart. Not positional in the ordering sense, but part of the same additive
  input sum — replay of BERT is wrong without it.
- **Formulation**: `x_i ← e(token_i) + P[i] + S[seg_i]`, `S ∈ R^{type_vocab_size × d}`
  (usually 2), then embedding LayerNorm.
- **Variants/used-by**: BERT, ALBERT, ELECTRA; dropped by RoBERTa (table exists,
  all-zeros index) and all decoder families. Config: `type_vocab_size`.
- **Tensor names**: `embeddings.token_type_embeddings.weight`.
- **Witnessed content**: table verbatim; the default-segment law (all-A when the input
  is unsegmented).
- **Read-side mechanism**: lookup + add at the embedding sum; segment ids are
  query-time input.
- **Laplace status**: **missing** (Bert profile has no token-type slot).
- **Gate**: single-segment replay equals HF with `token_type_ids=0`; two-segment probe
  matches HF with the recorded table.
- **Sources**: https://arxiv.org/abs/1810.04805

---

## F. Layout & wiring

### LAY-PARALLEL
- **ID**: LAY-PARALLEL
- **What/Why**: Instead of running attention then feeding its output to the FFN branch
  (sequential residual), some families run attention and FFN side by side on the SAME
  normalized input and add both to the residual. Same tensors, different wiring —
  replaying sequentially from a parallel checkpoint computes the wrong function.
- **Formulation**: sequential: `x ← x + Attn(LN1(x)); x ← x + FFN(LN2(x))`.
  Parallel: `x ← x + Attn(LN(x)) + FFN(LN(x))` — ONE shared pre-norm, both branches
  read it.
- **Variants/used-by**: GPT-J, GPT-NeoX (`use_parallel_residual: true`), Phi-1/2
  (single `input_layernorm` is the fingerprint), Falcon (`parallel_attn: true`,
  `num_ln_in_parallel_attn`), PaLM, StableLM variants, Cohere Command-R.
- **Tensor names**: signaled by the ABSENCE of a second per-layer norm
  (`post_attention_layernorm`) or by config flags.
- **Witnessed content**: the wiring law per layer (sequential | parallel), the shared
  norm's weights; config flags verbatim.
- **Read-side mechanism**: evaluate both branch inputs from the same normed vector; sum
  three terms into the residual.
- **Laplace status**: **partial** — the Phi profile's single-entry `PerLayerNorms` is
  the implicit fingerprint, but no explicit wiring law exists for replay.
- **Gate**: Phi-2 one-layer replay (attn+FFN+residual) matches HF hidden state ≤1e-5;
  sequential re-wiring of the same tensors fails (mutation check).
- **Sources**: https://github.com/kingoflolz/mesh-transformer-jax ; https://arxiv.org/abs/2204.02311

### LAY-SANDWICH
- **ID**: LAY-SANDWICH
- **What/Why**: Gemma-2/3 wrap the attention block in norms on BOTH sides: normalize
  the input AND normalize the attention output before adding it back. The extra norm is
  a learned tensor inside the attention residual path — skip it and the residual sums
  diverge.
- **Formulation**: `x ← x + PostNorm(Attn(PreNorm(x)))`; likewise
  `pre_feedforward_layernorm`/`post_feedforward_layernorm` around the FFN. Four norms
  per layer.
- **Variants/used-by**: Gemma-2, Gemma-3. Signal: presence of the four per-layer norm
  tensors.
- **Tensor names**: `model.layers.{L}.input_layernorm.weight`,
  `.post_attention_layernorm.weight` (here = attention OUTPUT norm, not the FFN input
  norm as in Llama), `.pre_feedforward_layernorm.weight`,
  `.post_feedforward_layernorm.weight`.
- **Witnessed content**: all four norm weights; the placement law (Gemma's
  `post_attention_layernorm` placement differs from Llama's identically-named tensor —
  the name is a false friend, the wiring is the witness).
- **Read-side mechanism**: apply output-side RMSNorm before the residual add.
- **Laplace status**: **missing** (no Gemma profile; and the profile's flat norm list
  cannot express placement).
- **Gate**: Gemma-2 layer replay matches HF; Llama-style placement of the same norm
  weights fails (mutation check).
- **Sources**: https://arxiv.org/abs/2408.00118

### LAY-CROSS
- **ID**: LAY-CROSS
- **What/Why**: Encoder-decoder models add a second attention block per decoder layer
  where queries come from the decoder but keys/values come from the ENCODER's final
  output — the bridge the decoder reads the input through. Same Q/K/V/O anatomy,
  different source tensors and no causal mask on the key side.
- **Formulation**: `q = y_dec W_qᵀ`, `k = h_enc W_kᵀ`, `v = h_enc W_vᵀ`; visibility:
  all encoder positions (MSK-BIDIR over keys), regardless of decoder position; T5
  cross-attention carries NO positional bias (POS-T5BUCKET absent here).
- **Variants/used-by**: T5/Flan-T5, BART/mBART, Marian, Whisper decoder. Signal:
  architecture class (`is_encoder_decoder: true`).
- **Tensor names**: `decoder.block.{L}.layer.1.EncDecAttention.{q,k,v,o}.weight` (T5);
  `model.decoder.layers.{L}.encoder_attn.{q_proj,k_proj,v_proj,out_proj}.weight`
  (BART/Whisper).
- **Witnessed content**: the four cross-projections per decoder layer, distinct from
  the self-attention four; the no-positional-bias law; the encoder-output source law.
- **Read-side mechanism**: two attention passes per decoder layer with different K/V
  sources; encoder K/V computed once per prompt.
- **Laplace status**: **missing** (no encoder-decoder profile).
- **Gate**: T5-small decoder-layer replay reading recorded encoder states matches HF
  cross-attention output ≤1e-6.
- **Sources**: https://arxiv.org/abs/1706.03762 ; https://arxiv.org/abs/1910.10683

### LAY-GATE
- **ID**: LAY-GATE
- **What/Why**: Qwen3-Next multiplies the attention output, per dimension, by a sigmoid
  gate computed from the SAME input token — letting the token itself throttle how much
  attention output enters the residual (also suppressing sink-like pathologies,
  cf. ATT-SINK). The gate weights hide INSIDE q_proj at double width.
- **Formulation**: `[q | g] = x W_qᵀ` where `W_q` out-width is `2·H·d_h`; chunked per
  head into query half and gate half; `attn_out ← attn_out ⊙ sigmoid(g)` BEFORE
  ATT-OPROJ. Zero-centered ATT-QKNORM on q,k.
- **Variants/used-by**: Qwen3-Next-80B-A3B (full-attention layers only; the other
  lanes are LAY-HYBRID). Signal: q_proj width = `2·H·d_h`.
- **Tensor names**: `model.layers.{L}.self_attn.q_proj.weight` (doubled width;
  query/gate chunk law), `.o_proj.weight`, `.q_norm/.k_norm`.
- **Witnessed content**: the doubled-width chunk law (which half is gate); gate rows as
  their own addressed sub-tensor; sigmoid placement (pre-o_proj).
- **Read-side mechanism**: extra mat-vec slice + sigmoid + elementwise multiply per
  token.
- **Laplace status**: **missing**.
- **Gate**: Qwen3-Next full-attn layer replay matches HF; treating the doubled q_proj
  as plain queries fails on shape alone (mutation check).
- **Sources**: https://github.com/huggingface/transformers/blob/main/src/transformers/models/qwen3_next/modeling_qwen3_next.py ; https://huggingface.co/Qwen/Qwen3-Next-80B-A3B-Instruct

### LAY-HYBRID
- **ID**: LAY-HYBRID
- **What/Why**: Newer models interleave softmax-attention layers with NON-attention
  sequence mixers (linear attention / state-space lanes: Gated DeltaNet, Mamba). Those
  lanes have their own primitive family (out of this index's scope); THIS entry
  witnesses the routing so the ETL knows exactly which layers the attention gates apply
  to and never mis-attests a DeltaNet tensor as a K projection.
- **Formulation**: `layer_types[L] ∈ {full_attention, linear_attention, mamba, ...}`;
  Qwen3-Next: 3 linear : 1 full (`full_attention_interval: 4`); Jamba/Zamba/
  GraniteMoeHybrid: config-specific layouts.
- **Variants/used-by**: Qwen3-Next (Gated DeltaNet), Jamba (Mamba), Zamba2,
  GraniteMoeHybrid, MiniMax-Text (lightning attention), Kimi-Linear. Signal:
  `layer_types` / `layers_block_type` / interval keys.
- **Tensor names**: non-attention lanes carry distinct stems (e.g.
  `model.layers.{L}.linear_attn.*` in Qwen3-Next; `...mamba.*` in Jamba) — the stem
  disjointness is the guard.
- **Witnessed content**: the resolved per-layer lane list; a hard assertion that
  attention primitives were attested ONLY on attention layers.
- **Read-side mechanism**: dispatch per layer; attention replay untouched on its own
  lanes.
- **Laplace status**: **missing**.
- **Gate**: for Qwen3-Next, count(layers with attested ATT-* content) equals
  count(full_attention entries in the recorded list) — no leakage either direction.
- **Sources**: https://blog.vllm.ai/2025/09/11-qwen3-next.html ; https://arxiv.org/abs/2403.19887

---

## Checklist

Every ID, one gate each. A checkmark means the gate's assertion has a passing test
against a live checkpoint.

### A. Projections & head geometry
- [ ] ATT-QPROJ
- [ ] ATT-KPROJ
- [ ] ATT-VPROJ
- [ ] ATT-OPROJ
- [ ] ATT-PROJBIAS
- [ ] ATT-QKV-PACKED
- [ ] ATT-HEADSLICE
- [ ] ATT-HEADDIM

### B. Head-sharing topologies
- [ ] ATT-MHA
- [ ] ATT-MQA
- [ ] ATT-GQA
- [ ] ATT-MLA

### C. Score path
- [ ] ATT-SCALE
- [ ] ATT-SCALE-CFG
- [ ] ATT-SOFTCAP
- [ ] ATT-QKNORM
- [ ] ATT-QKNORM-FULL
- [ ] ATT-SOFTMAX
- [ ] ATT-SINK
- [ ] ATT-TEMP
- [ ] ATT-LAYERSCALE
- [ ] ATT-DROPOUT

### D. Masking & context topology
- [ ] MSK-CAUSAL
- [ ] MSK-BIDIR
- [ ] MSK-PAD
- [ ] MSK-SWA
- [ ] MSK-ALT
- [ ] MSK-CHUNK
- [ ] MSK-PREFIX

### E. Positional schemes
- [ ] POS-LEARNED
- [ ] POS-SINUSOID
- [ ] POS-ROPE
- [ ] POS-ROPE-PARTIAL
- [ ] POS-ROPE-LINEAR
- [ ] POS-ROPE-DYNNTK
- [ ] POS-ROPE-YARN
- [ ] POS-ROPE-LONGROPE
- [ ] POS-ROPE-LLAMA3
- [ ] POS-ROPE-DUAL
- [ ] POS-MROPE
- [ ] POS-ALIBI
- [ ] POS-T5BUCKET
- [ ] POS-SHAW
- [ ] POS-NOPE
- [ ] POS-SEGMENT

### F. Layout & wiring
- [ ] LAY-PARALLEL
- [ ] LAY-SANDWICH
- [ ] LAY-CROSS
- [ ] LAY-GATE
- [ ] LAY-HYBRID
