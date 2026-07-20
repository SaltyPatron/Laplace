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

# 27d — Primitive Index: Empirical Tensor-Role Inventory of Local Checkpoints

Date: 2026-07-15. Method: PowerShell only — first 8 bytes of each `.safetensors` = LE uint64
header length N, next N bytes = UTF-8 JSON `{tensor -> {dtype, shape, data_offsets}}`. Headers
only; zero tensor data touched. Sharded models: every shard header read (241 shards for the
480B, 163 for V3.2 — headers are cheap). Layer indices deduped to `{L}`, expert indices to
`{E}`.

**Scope**: 19 text models inventoried under `D:\Models\hub`. Skipped as non-text:
Qwen3-VL-Embedding-2B/8B, Qwen3-VL-Reranker-2B/8B (vision), Florence-2-base/large,
Conditional-DETR-R50, DETR-ResNet-101, RT-DETR-v1-R101, Grounding-DINO-Base, yolo11x,
FLUX.2-dev, sam-audio-large, fish-speech-1.5, granite-speech-3.3-8b, canary-qwen-2.5b,
music-flamingo-hf, and `datasets--nampdn-ai--tiny-codes` (a dataset, not a model).
Llama-4-Maverick is multimodal but its text tower is a first-class text model, so it is
included (vision tower rows are listed but bracketed).

UNMAPPED flag = the pattern cannot be expressed in the current `ArchitectureProfile` slot set
(`EmbedTokens`, `LmHead`, `FinalNorm`, `PerLayerNorms`, `Q/K/V/OProj`, `Gate/Up/DownProj`,
plus the `BiasOf` helper). NAME-VARIANT = known role, but the profile's hardcoded name string
would miss it.

---

## 1. all-MiniLM-L6-v2 (`models--sentence-transformers--all-MiniLM-L6-v2`)

| key | value |
|---|---|
| model_type / arch | bert / BertModel |
| hidden_size | 384 |
| layers | 6 |
| heads / kv_heads / head_dim | 12 / 12 / 32 |
| intermediate_size | 1536 |
| vocab_size | 30522 |
| experts | — |
| rope | — (absolute learned positions, max 512) |
| norm eps | layer_norm_eps 1e-12 (LayerNorm, with biases) |
| tie_word_embeddings | n/a (no LM head) |
| sliding_window | — |
| unusual | `type_vocab_size: 2` (segment embeddings), post-norm architecture, F32 weights |

1 file, 104 tensors, 24 patterns.

| pattern | shape | dtype | role | flag |
|---|---|---|---|---|
| embeddings.word_embeddings.weight | 30522x384 | F32 | EMB-WORD | |
| embeddings.position_embeddings.weight | 512x384 | F32 | EMB-POS | UNMAPPED |
| embeddings.token_type_embeddings.weight | 2x384 | F32 | EMB-TOKENTYPE | UNMAPPED |
| embeddings.position_ids | 1x512 | I64 | BUF-POSITION-IDS (non-parameter buffer) | UNMAPPED |
| embeddings.LayerNorm.weight / .bias | 384 | F32 | EMB-NORM / EMB-NORM-BIAS | |
| encoder.layer.{L}.attention.self.query.weight / .bias | 384x384 / 384 | F32 | ATT-QPROJ / -BIAS | |
| encoder.layer.{L}.attention.self.key.weight / .bias | 384x384 / 384 | F32 | ATT-KPROJ / -BIAS | |
| encoder.layer.{L}.attention.self.value.weight / .bias | 384x384 / 384 | F32 | ATT-VPROJ / -BIAS | |
| encoder.layer.{L}.attention.output.dense.weight / .bias | 384x384 / 384 | F32 | ATT-OPROJ / -BIAS | |
| encoder.layer.{L}.attention.output.LayerNorm.weight / .bias | 384 | F32 | NORM-ATT-OUT / -BIAS (post-norm) | |
| encoder.layer.{L}.intermediate.dense.weight / .bias | 1536x384 / 1536 | F32 | FFN-UP / -BIAS | |
| encoder.layer.{L}.output.dense.weight / .bias | 384x1536 / 384 | F32 | FFN-DOWN / -BIAS | |
| encoder.layer.{L}.output.LayerNorm.weight / .bias | 384 | F32 | NORM-FFN-OUT / -BIAS (post-norm) | |
| pooler.dense.weight / .bias | 384x384 / 384 | F32 | POOLER-DENSE / -BIAS | UNMAPPED (x2) |

## 2. TinyLlama-1.1B-Chat-v1.0

| key | value |
|---|---|
| model_type / arch | llama / LlamaForCausalLM |
| hidden_size | 2048 |
| layers | 22 |
| heads / kv_heads / head_dim | 32 / 4 / 64 |
| intermediate_size | 5632 |
| vocab_size | 32000 |
| rope_theta / scaling | 10000 / null |
| norm eps | rms 1e-5 |
| tie_word_embeddings | false |
| sliding_window | — |
| unusual | nothing — the canonical GQA Llama |

1 file, 201 tensors, 12 patterns. **Fully mapped by the Llama profile — zero UNMAPPED.**

| pattern | shape | dtype | role |
|---|---|---|---|
| model.embed_tokens.weight | 32000x2048 | BF16 | EMB-WORD |
| model.layers.{L}.self_attn.q_proj.weight | 2048x2048 | BF16 | ATT-QPROJ |
| model.layers.{L}.self_attn.k_proj.weight | 256x2048 | BF16 | ATT-KPROJ (GQA 4 kv-heads) |
| model.layers.{L}.self_attn.v_proj.weight | 256x2048 | BF16 | ATT-VPROJ |
| model.layers.{L}.self_attn.o_proj.weight | 2048x2048 | BF16 | ATT-OPROJ |
| model.layers.{L}.mlp.gate_proj.weight | 5632x2048 | BF16 | FFN-GATE |
| model.layers.{L}.mlp.up_proj.weight | 5632x2048 | BF16 | FFN-UP |
| model.layers.{L}.mlp.down_proj.weight | 2048x5632 | BF16 | FFN-DOWN |
| model.layers.{L}.input_layernorm.weight | 2048 | BF16 | NORM-ATT-IN |
| model.layers.{L}.post_attention_layernorm.weight | 2048 | BF16 | NORM-ATT-POST |
| model.norm.weight | 2048 | BF16 | NORM-FINAL |
| lm_head.weight | 32000x2048 | BF16 | LM-HEAD |

## 3. phi-2

| key | value |
|---|---|
| model_type / arch | phi / PhiForCausalLM |
| hidden_size | 2560 |
| layers | 32 |
| heads / kv_heads / head_dim | 32 / 32 / 80 |
| intermediate_size | 10240 |
| vocab_size | 51200 |
| rope_theta / scaling | 10000 / null, **partial_rotary_factor 0.4** |
| norm eps | layer_norm 1e-5 (LayerNorm with bias, parallel block: single per-layer norm) |
| tie_word_embeddings | false |
| unusual | `qk_layernorm: false` (the slot exists in the family), LM head has a **bias**, F16 |

2 files, 453 tensors, 19 patterns.

| pattern | shape | dtype | role | flag |
|---|---|---|---|---|
| model.embed_tokens.weight | 51200x2560 | F16 | EMB-WORD | |
| model.layers.{L}.self_attn.q_proj.weight / .bias | 2560x2560 / 2560 | F16 | ATT-QPROJ / -BIAS | |
| model.layers.{L}.self_attn.k_proj.weight / .bias | 2560x2560 / 2560 | F16 | ATT-KPROJ / -BIAS | |
| model.layers.{L}.self_attn.v_proj.weight / .bias | 2560x2560 / 2560 | F16 | ATT-VPROJ / -BIAS | |
| model.layers.{L}.self_attn.dense.weight / .bias | 2560x2560 / 2560 | F16 | ATT-OPROJ / -BIAS | |
| model.layers.{L}.mlp.fc1.weight / .bias | 10240x2560 / 10240 | F16 | FFN-UP / -BIAS (no gate) | |
| model.layers.{L}.mlp.fc2.weight / .bias | 2560x10240 / 2560 | F16 | FFN-DOWN / -BIAS | |
| model.layers.{L}.input_layernorm.weight / .bias | 2560 | F16 | NORM-ATT-IN / -BIAS (parallel attn+FFN share it) | |
| model.final_layernorm.weight / .bias | 2560 | F16 | NORM-FINAL / -BIAS | |
| lm_head.weight | 51200x2560 | F16 | LM-HEAD | |
| lm_head.bias | 51200 | F16 | LM-HEAD-BIAS | UNMAPPED |

## 4. Qwen2.5-Coder-3B-Instruct

| key | value |
|---|---|
| model_type / arch | qwen2 / Qwen2ForCausalLM |
| hidden_size | 2048 |
| layers | 36 |
| heads / kv_heads / head_dim | 16 / 2 / 128 |
| intermediate_size | 11008 |
| vocab_size | 151936 |
| rope_theta | 1e6 |
| norm eps | rms 1e-6 |
| tie_word_embeddings | **true — no lm_head tensor in the file** |
| sliding_window | 32768 (use_sliding_window false) |
| unusual | QKV biases only (no O bias) — Qwen2 signature |

2 files, 434 tensors, 14 patterns.

| pattern | shape | dtype | role | flag |
|---|---|---|---|---|
| model.embed_tokens.weight | 151936x2048 | BF16 | EMB-WORD (doubles as LM head, tied) | |
| model.layers.{L}.self_attn.q_proj.weight / .bias | 2048x2048 / 2048 | BF16 | ATT-QPROJ / -BIAS | |
| model.layers.{L}.self_attn.k_proj.weight / .bias | 256x2048 / 256 | BF16 | ATT-KPROJ / -BIAS (2 kv-heads) | |
| model.layers.{L}.self_attn.v_proj.weight / .bias | 256x2048 / 256 | BF16 | ATT-VPROJ / -BIAS | |
| model.layers.{L}.self_attn.o_proj.weight | 2048x2048 | BF16 | ATT-OPROJ | |
| model.layers.{L}.mlp.gate_proj.weight | 11008x2048 | BF16 | FFN-GATE | |
| model.layers.{L}.mlp.up_proj.weight | 11008x2048 | BF16 | FFN-UP | |
| model.layers.{L}.mlp.down_proj.weight | 2048x11008 | BF16 | FFN-DOWN | |
| model.layers.{L}.input_layernorm.weight | 2048 | BF16 | NORM-ATT-IN | |
| model.layers.{L}.post_attention_layernorm.weight | 2048 | BF16 | NORM-ATT-POST | |
| model.norm.weight | 2048 | BF16 | NORM-FINAL | |
| *(absent)* lm_head.weight | — | — | LM-HEAD via tie | profile declares `lm_head.weight` — must fall back to embed on tie |

## 5. Qwen2.5-Coder-7B-Instruct

Config: qwen2, hidden 3584, 28 layers, 28 heads / 4 kv / head_dim 128, intermediate 18944,
vocab 152064, rope_theta 1e6, rms 1e-6, tie false, sliding_window 131072 (off). 4 files,
339 tensors, 15 patterns — structurally identical to the 3B **plus an explicit
`lm_head.weight` (152064x3584)**. K/V rows 512x3584 (+bias 512), Q 3584x3584 (+bias). Zero
UNMAPPED; fully covered by the Qwen2 profile.

## 6. Qwen2.5-Coder-14B-Instruct

Config: qwen2, hidden 5120, 48 layers, 40 heads / 8 kv / head_dim 128, intermediate 13824,
vocab 152064, rope_theta 1e6, rms 1e-6, tie false, sliding_window 131072 (off). 6 files,
579 tensors, 15 patterns — same shape family as the 7B (K/V 1024x5120, lm_head
152064x5120). Zero UNMAPPED.

## 7. Qwen3-Embedding-0.6B

| key | value |
|---|---|
| model_type / arch | qwen3 / Qwen3ForCausalLM |
| hidden_size | 1024 |
| layers | 28 |
| heads / kv_heads / head_dim | 16 / 8 / **128 (explicit; 16x128=2048 ≠ hidden 1024 — head_dim decoupled from hidden/heads)** |
| intermediate_size | 3072 |
| vocab_size | 151669 |
| rope_theta | 1e6 |
| norm eps | rms 1e-6 |
| tie_word_embeddings | true (no lm_head) |
| unusual | **tensor names have NO `model.` prefix** (`embed_tokens.weight`, `layers.{L}...`) — sentence-transformers-style export; QK-norm; attention_bias false |

1 file, 310 tensors, 13 patterns.

| pattern | shape | dtype | role | flag |
|---|---|---|---|---|
| embed_tokens.weight | 151669x1024 | BF16 | EMB-WORD | NAME-VARIANT (no `model.` prefix) |
| layers.{L}.self_attn.q_proj.weight | 2048x1024 | BF16 | ATT-QPROJ | NAME-VARIANT |
| layers.{L}.self_attn.k_proj.weight | 1024x1024 | BF16 | ATT-KPROJ | NAME-VARIANT |
| layers.{L}.self_attn.v_proj.weight | 1024x1024 | BF16 | ATT-VPROJ | NAME-VARIANT |
| layers.{L}.self_attn.o_proj.weight | 1024x2048 | BF16 | ATT-OPROJ (in-dim 2048 = heads*head_dim, not hidden) | NAME-VARIANT |
| layers.{L}.self_attn.q_norm.weight | 128 | BF16 | QK-NORM-Q (per-head-dim RMSNorm) | UNMAPPED |
| layers.{L}.self_attn.k_norm.weight | 128 | BF16 | QK-NORM-K | UNMAPPED |
| layers.{L}.mlp.gate_proj.weight | 3072x1024 | BF16 | FFN-GATE | NAME-VARIANT |
| layers.{L}.mlp.up_proj.weight | 3072x1024 | BF16 | FFN-UP | NAME-VARIANT |
| layers.{L}.mlp.down_proj.weight | 1024x3072 | BF16 | FFN-DOWN | NAME-VARIANT |
| layers.{L}.input_layernorm.weight | 1024 | BF16 | NORM-ATT-IN | NAME-VARIANT |
| layers.{L}.post_attention_layernorm.weight | 1024 | BF16 | NORM-ATT-POST | NAME-VARIANT |
| norm.weight | 1024 | BF16 | NORM-FINAL | NAME-VARIANT |

## 8. Qwen3-Embedding-4B

Config: qwen3, hidden 2560, 36 layers, 32 heads / 8 kv / head_dim 128, intermediate 9728,
vocab 151665, rope_theta 1e6, rms 1e-6, tie true, max_pos 40960. 2 files, 398 tensors,
13 patterns — same pattern set as 7 including the **bare prefix** (`layers.{L}...`) and
QK-NORM (q_norm/k_norm 128, UNMAPPED x2). Q 4096x2560, K/V 1024x2560, O 2560x4096.

## 9. Qwen3-Reranker-0.6B

Config identical to Qwen3-Embedding-0.6B (qwen3, 1024/28/16h/8kv/hd128, vocab 151669, tie
true, max_pos 40960). 1 file, 310 tensors, 13 patterns. **Same pattern set as 7 but WITH the
standard `model.` prefix** (`model.embed_tokens.weight`, `model.layers.{L}...`) — the
embedding and reranker exports of the same architecture disagree on prefix. QK-NORM
UNMAPPED x2; everything else matches Llama-profile names except the missing lm_head (tied).

## 10. Qwen3-Reranker-4B

Config identical to Qwen3-Embedding-4B except vocab 151669. 2 files, 398 tensors,
13 patterns, `model.` prefix, no lm_head (tied). QK-NORM UNMAPPED x2.

## 11. DeepSeek-Coder-V2-Lite-Instruct — MLA + MoE

| key | value |
|---|---|
| model_type / arch | deepseek_v2 / DeepseekV2ForCausalLM |
| hidden_size | 2048 |
| layers | 27 (**layer 0 dense FFN, layers 1-26 MoE** — first_k_dense_replace 1) |
| heads / kv_heads | 16 / 16 (MLA — kv_heads nominal) |
| head_dim | qk_nope 128 + qk_rope 64 = 192 per head; v_head_dim 128 |
| MLA ranks | kv_lora_rank 512, **q_lora_rank null → full q_proj (no Q compression)** |
| intermediate_size | 10944 (dense) / moe_intermediate_size 1408 |
| experts | 64 routed + 2 shared, top-6, softmax scoring, greedy topk |
| vocab_size | 102400 |
| rope | theta 10000, **yarn scaling** (factor 40, mscale 0.707, orig 4096 → 163840) |
| norm eps | rms 1e-6 |
| tie_word_embeddings | false |
| unusual | routed_scaling_factor 1.0, aux_loss_alpha 0.001, custom `auto_map` modeling code |

4 files, 5291 tensors, 20 patterns.

| pattern | shape | dtype | role | flag |
|---|---|---|---|---|
| model.embed_tokens.weight | 102400x2048 | BF16 | EMB-WORD | |
| model.layers.{L}.self_attn.q_proj.weight | 3072x2048 | BF16 | MLA-Q-PROJ (16 heads x 192 = nope+rope fused) | UNMAPPED (name matches profile QProj but math is MLA-split) |
| model.layers.{L}.self_attn.kv_a_proj_with_mqa.weight | 576x2048 | BF16 | MLA-KV-DOWN (512 latent + 64 shared rope) | UNMAPPED |
| model.layers.{L}.self_attn.kv_a_layernorm.weight | 512 | BF16 | MLA-KV-NORM | UNMAPPED |
| model.layers.{L}.self_attn.kv_b_proj.weight | 4096x512 | BF16 | MLA-KV-UP (16 x (128 k_nope + 128 v)) | UNMAPPED |
| model.layers.{L}.self_attn.o_proj.weight | 2048x2048 | BF16 | ATT-OPROJ | |
| model.layers.0.mlp.gate_proj/up_proj.weight | 10944x2048 | BF16 | FFN-GATE / FFN-UP (dense layer 0 only) | |
| model.layers.0.mlp.down_proj.weight | 2048x10944 | BF16 | FFN-DOWN (layer 0 only) | |
| model.layers.{L}.mlp.gate.weight | 64x2048 | BF16 | MOE-ROUTER (L1-26) | UNMAPPED |
| model.layers.{L}.mlp.experts.{E}.gate_proj.weight | 1408x2048 | BF16 | MOE-EXPERT-GATE (26x64) | UNMAPPED |
| model.layers.{L}.mlp.experts.{E}.up_proj.weight | 1408x2048 | BF16 | MOE-EXPERT-UP | UNMAPPED |
| model.layers.{L}.mlp.experts.{E}.down_proj.weight | 2048x1408 | BF16 | MOE-EXPERT-DOWN | UNMAPPED |
| model.layers.{L}.mlp.shared_experts.gate_proj.weight | 2816x2048 | BF16 | MOE-SHARED-GATE (2 shared fused: 2x1408) | UNMAPPED |
| model.layers.{L}.mlp.shared_experts.up_proj.weight | 2816x2048 | BF16 | MOE-SHARED-UP | UNMAPPED |
| model.layers.{L}.mlp.shared_experts.down_proj.weight | 2048x2816 | BF16 | MOE-SHARED-DOWN | UNMAPPED |
| model.layers.{L}.input_layernorm.weight | 2048 | BF16 | NORM-ATT-IN | |
| model.layers.{L}.post_attention_layernorm.weight | 2048 | BF16 | NORM-ATT-POST | |
| model.norm.weight | 2048 | BF16 | NORM-FINAL | |
| lm_head.weight | 102400x2048 | BF16 | LM-HEAD | |

Note: there is NO `q_a_layernorm` / `q_b_proj` here (q_lora_rank null) — V2-Lite compresses
only KV. The full-fat V3 lineage compresses Q too (see model 18).

## 12. deepseek-coder-33b-instruct

Config: llama / LlamaForCausalLM, hidden 7168, 62 layers, 56 heads / 8 kv / head_dim 128,
intermediate 19200, vocab 32256, rope_theta 100000 with **linear scaling factor 4.0**,
rms 1e-6, tie false, max_pos 16384. 7 files, 561 tensors, 12 patterns — textbook Llama GQA,
zero UNMAPPED, fully covered by the Llama profile. (K/V 1024x7168, no biases.)

## 13. Qwen3-Coder-30B-A3B-Instruct — MoE

| key | value |
|---|---|
| model_type / arch | qwen3_moe / Qwen3MoeForCausalLM |
| hidden_size | 2048 |
| layers | 48 (every layer MoE — decoder_sparse_step 1, mlp_only_layers []) |
| heads / kv_heads / head_dim | 32 / 4 / 128 |
| intermediate_size | 6144 (unused dense value) / moe_intermediate_size 768 |
| experts | 128 routed, top-8, norm_topk_prob true, **no shared experts** |
| vocab_size | 151936 |
| rope_theta | 1e7 |
| norm eps | rms 1e-6 |
| tie_word_embeddings | false |
| unusual | QK-norm; max_pos 262144 |

16 files, 18867 tensors, 15 patterns.

| pattern | shape | dtype | role | flag |
|---|---|---|---|---|
| model.embed_tokens.weight | 151936x2048 | BF16 | EMB-WORD | |
| model.layers.{L}.self_attn.q_proj.weight | 4096x2048 | BF16 | ATT-QPROJ | |
| model.layers.{L}.self_attn.k_proj.weight | 512x2048 | BF16 | ATT-KPROJ | |
| model.layers.{L}.self_attn.v_proj.weight | 512x2048 | BF16 | ATT-VPROJ | |
| model.layers.{L}.self_attn.o_proj.weight | 2048x4096 | BF16 | ATT-OPROJ | |
| model.layers.{L}.self_attn.q_norm.weight | 128 | BF16 | QK-NORM-Q | UNMAPPED |
| model.layers.{L}.self_attn.k_norm.weight | 128 | BF16 | QK-NORM-K | UNMAPPED |
| model.layers.{L}.mlp.gate.weight | 128x2048 | BF16 | MOE-ROUTER | UNMAPPED |
| model.layers.{L}.mlp.experts.{E}.gate_proj.weight | 768x2048 | BF16 | MOE-EXPERT-GATE (48x128 = 6144 tensors) | UNMAPPED |
| model.layers.{L}.mlp.experts.{E}.up_proj.weight | 768x2048 | BF16 | MOE-EXPERT-UP | UNMAPPED |
| model.layers.{L}.mlp.experts.{E}.down_proj.weight | 2048x768 | BF16 | MOE-EXPERT-DOWN | UNMAPPED |
| model.layers.{L}.input_layernorm.weight | 2048 | BF16 | NORM-ATT-IN | |
| model.layers.{L}.post_attention_layernorm.weight | 2048 | BF16 | NORM-ATT-POST | |
| model.norm.weight | 2048 | BF16 | NORM-FINAL | |
| lm_head.weight | 151936x2048 | BF16 | LM-HEAD | |

## 14. jina-code-embeddings-1.5b

Config: qwen2 / Qwen2ForCausalLM, hidden 1536, 28 layers, 12 heads / 2 kv / head_dim 128,
intermediate 8960, vocab 151936, rope_theta 1e6, rms 1e-6, tie true (no lm_head). Unusual
config keys: **`matryoshka_dims: [64,128,256,512,896]`**, `prompt_names: [query, passage]`,
`task_names: [nl2code, qa, code2code, code2nl, code2completion]` — embedding-truncation and
instruction-prefix metadata living in config.json. 1 file, 338 tensors, 14 patterns.

Pattern set = exactly Qwen2.5 (QKV biases, gate/up/down, two norms) **but with the bare
prefix** (`embed_tokens.weight`, `layers.{L}...`, `norm.weight`) — every row NAME-VARIANT
against the Qwen2 profile; a `model_type == "qwen2"` lookup misses 100% of tensors.

## 15. jina-reranker-v3

Config: qwen3 / Qwen3ForCausalLM (auto_map → custom `JinaForRanking`), hidden 1024,
28 layers, 16 heads / 8 kv / head_dim 128, intermediate 3072, vocab 151936, rope_theta 1e6,
tie true, max_pos 131072, use_cache false. 1 file, 312 tensors, 15 patterns.

Standard `model.`-prefixed Qwen3 body (QK-NORM UNMAPPED x2, no lm_head) **plus a scoring
head that no profile slot can hold**:

| pattern | shape | dtype | role | flag |
|---|---|---|---|---|
| projector.0.weight | 512x1024 | BF16 | RERANK-PROJECTOR (2-layer MLP head, no biases) | UNMAPPED |
| projector.2.weight | 512x512 | BF16 | RERANK-PROJECTOR | UNMAPPED |

(Indices 0/2 imply an activation module at index 1 — an `nn.Sequential` export.)

## 16. zerank-2

Config: qwen3 / Qwen3ForCausalLM — byte-for-byte a Qwen3-4B-class reranker: hidden 2560,
36 layers, 32 heads / 8 kv / head_dim 128, intermediate 9728, vocab 151936, rope_theta 1e6,
tie true. Config quirks: uses the new `dtype` key (not `torch_dtype`), and materializes
`layer_types: ["full_attention" x36]` (transformers 4.57 style). 2 files, 398 tensors,
13 patterns, `model.` prefix, no lm_head. QK-NORM UNMAPPED x2; otherwise Llama-shaped names.
It scores via the LM head of tied embeddings over yes/no tokens — no extra head tensors.

## 17. Qwen3-Coder-480B-A35B-Instruct (bare dir `wQwen3-Coder-480B-A35B-Instruct`)

Config: qwen3_moe, hidden 6144, 62 layers, 96 heads / 8 kv / head_dim 128, moe_intermediate
2560, **160 routed experts, top-8, shared_expert_intermediate_size 0**, vocab 151936,
rope_theta 1e7, max_pos 262144, tie false, `use_qk_norm: true`, `qkv_bias: false`.
241 shards, 30321 tensors, 15 patterns — the exact same 15-pattern set as the 30B-A3B
(scaled: Q 12288x6144, K/V 1024x6144, O 6144x12288, experts 2560x6144, router 160x6144,
lm_head 151936x6144). QK-NORM + MOE rows UNMAPPED as in model 13. Confirms qwen3_moe is one
stable shape family from 30B to 480B.

## 18. DeepSeek-V3.2-Speciale (bare dir `zDeepSeek-V3.2-Speciale`) — MLA + MoE + DSA + MTP + FP8

| key | value |
|---|---|
| model_type / arch | deepseek_v32 / DeepseekV32ForCausalLM |
| hidden_size | 7168 |
| layers | 61 (+ **1 MTP layer stored as `model.layers.61`** — num_nextn_predict_layers 1) |
| heads / kv_heads | 128 / 128 (MLA) |
| head_dim | qk_nope 128 + qk_rope 64; v_head_dim 128 |
| MLA ranks | q_lora_rank **1536**, kv_lora_rank 512 |
| intermediate_size | 18432 (dense L0-2) / moe 2048 |
| experts | 256 routed + 1 shared, top-8, n_group 8 / topk_group 4, **sigmoid scoring, noaux_tc** (bias-corrected aux-free routing), routed_scaling_factor 2.5, first_k_dense_replace 3 |
| vocab_size | 129280 |
| rope | theta 10000, yarn (factor 40, mscale 1.0, orig 4096 → 163840) |
| tie_word_embeddings | false |
| quantization | **fp8 e4m3, block 128x128, ue8m0 scale format — every fp8 weight has a `weight_scale_inv` F32 companion** |
| sparse attention | **`index_n_heads: 64, index_head_dim: 128, index_topk: 2048`** — DSA lightning indexer |

163 shards, 92425 tensors, 50 patterns. Norms are F32 while weights are FP8/BF16 (three
dtypes in one checkpoint).

| pattern | shape | dtype | role | flag |
|---|---|---|---|---|
| model.embed_tokens.weight | 129280x7168 | BF16 | EMB-WORD | |
| model.layers.{L}.self_attn.q_a_proj.weight (+scale_inv) | 1536x7168 | F8_E4M3 | MLA-Q-DOWN | UNMAPPED |
| model.layers.{L}.self_attn.q_a_layernorm.weight | 1536 | F32 | MLA-Q-NORM | UNMAPPED |
| model.layers.{L}.self_attn.q_b_proj.weight (+scale_inv) | 24576x1536 | F8_E4M3 | MLA-Q-UP (128 x 192) | UNMAPPED |
| model.layers.{L}.self_attn.kv_a_proj_with_mqa.weight (+scale_inv) | 576x7168 | F8_E4M3 | MLA-KV-DOWN | UNMAPPED |
| model.layers.{L}.self_attn.kv_a_layernorm.weight | 512 | F32 | MLA-KV-NORM | UNMAPPED |
| model.layers.{L}.self_attn.kv_b_proj.weight (+scale_inv) | 32768x512 | F8_E4M3 | MLA-KV-UP (128 x 256) | UNMAPPED |
| model.layers.{L}.self_attn.o_proj.weight (+scale_inv) | 7168x16384 | F8_E4M3 | ATT-OPROJ (in = 128x128 v-dims) | |
| model.layers.{L}.self_attn.indexer.wk.weight (+scale_inv) | 128x7168 | F8_E4M3 | DSA-IDX-K | UNMAPPED |
| model.layers.{L}.self_attn.indexer.wq_b.weight (+scale_inv) | 8192x1536 | F8_E4M3 | DSA-IDX-Q-UP (64 idx-heads x 128, fed from q_a latent) | UNMAPPED |
| model.layers.{L}.self_attn.indexer.k_norm.weight / .bias | 128 | F32 | DSA-IDX-K-NORM / -BIAS (LayerNorm WITH bias inside an RMSNorm model) | UNMAPPED |
| model.layers.{L}.self_attn.indexer.weights_proj.weight | 64x7168 | BF16 | DSA-IDX-WEIGHTS (per-idx-head gate) | UNMAPPED |
| model.layers.0-2.mlp.gate/up_proj.weight (+scale_inv) | 18432x7168 | F8_E4M3 | FFN-GATE / FFN-UP (dense L0-2) | |
| model.layers.0-2.mlp.down_proj.weight (+scale_inv) | 7168x18432 | F8_E4M3 | FFN-DOWN | |
| model.layers.{L}.mlp.gate.weight | 256x7168 | BF16 | MOE-ROUTER (L3-61) | UNMAPPED |
| model.layers.{L}.mlp.gate.e_score_correction_bias | 256 | F32 | MOE-ROUTER-CORRECTION-BIAS (noaux_tc) | UNMAPPED |
| model.layers.{L}.mlp.experts.{E}.gate/up_proj.weight (+scale_inv) | 2048x7168 | F8_E4M3 | MOE-EXPERT-GATE / -UP (59x256) | UNMAPPED |
| model.layers.{L}.mlp.experts.{E}.down_proj.weight (+scale_inv) | 7168x2048 | F8_E4M3 | MOE-EXPERT-DOWN | UNMAPPED |
| model.layers.{L}.mlp.shared_experts.gate/up/down_proj.weight (+scale_inv) | 2048x7168 / 7168x2048 | F8_E4M3 | MOE-SHARED-GATE/UP/DOWN | UNMAPPED |
| model.layers.{L}.input_layernorm.weight | 7168 | F32 | NORM-ATT-IN (62 incl. MTP layer) | |
| model.layers.{L}.post_attention_layernorm.weight | 7168 | F32 | NORM-ATT-POST | |
| *.weight_scale_inv (16 distinct patterns) | per-128x128 block | F32 | QUANT-SCALE-INV (dequant companion) | UNMAPPED |
| model.layers.61.embed_tokens.weight | 129280x7168 | BF16 | MTP-EMB (MTP layer re-embeds) | UNMAPPED |
| model.layers.61.enorm.weight / hnorm.weight | 7168 | F32 | MTP-ENORM / MTP-HNORM | UNMAPPED |
| model.layers.61.eh_proj.weight | 7168x14336 | BF16 | MTP-EH-PROJ (concat[emb;hidden] → hidden) | UNMAPPED |
| model.layers.61.shared_head.norm.weight | 7168 | F32 | MTP-HEAD-NORM | UNMAPPED |
| model.layers.61.shared_head.head.weight | 129280x7168 | BF16 | MTP-HEAD | UNMAPPED |
| model.norm.weight | 7168 | F32 | NORM-FINAL | |
| lm_head.weight | 129280x7168 | BF16 | LM-HEAD | |

## 19. Llama-4-Maverick-17B-128E (`ymodels--meta-llama--Llama-4-Maverick-17B-128E`)

Multimodal (`Llama4ForConditionalGeneration`, nested `text_config` + `vision_config` — the
config summary keys live one level down, itself a parser gap). Text config: llama4_text,
hidden 5120, 48 layers, 40 heads / 8 kv / head_dim 128, intermediate 8192 (expert) /
16384 (dense mlp), vocab 202048, rope_theta 500000, rope_scaling null, **`no_rope_layers`
list + `attention_chunk_size: 8192`** (chunked local attention), rms 1e-5, use_qk_norm
false, **128 local experts, top-1 routing, `interleave_moe_layer_step: 2`** — odd layers
MoE, even layers dense. 55 shards, 1061 tensors, 44 patterns.

Text-tower rows:

| pattern | shape | dtype | role | flag |
|---|---|---|---|---|
| language_model.model.embed_tokens.weight | 202048x5120 | BF16 | EMB-WORD | NAME-VARIANT (`language_model.` prefix) |
| language_model.model.layers.{L}.self_attn.q/k/v/o_proj.weight | 5120x5120 / 1024x5120 / 1024x5120 / 5120x5120 | BF16 | ATT-QPROJ/KPROJ/VPROJ/OPROJ | NAME-VARIANT |
| language_model.model.layers.{L}.feed_forward.gate/up_proj.weight (even L) | 16384x5120 | BF16 | FFN-GATE / FFN-UP (**`feed_forward.`, not `mlp.`**) | NAME-VARIANT |
| language_model.model.layers.{L}.feed_forward.down_proj.weight (even L) | 5120x16384 | BF16 | FFN-DOWN | NAME-VARIANT |
| language_model.model.layers.{L}.feed_forward.router.weight (odd L) | 128x5120 | BF16 | MOE-ROUTER (named `router`, not `gate`) | UNMAPPED |
| language_model.model.layers.{L}.feed_forward.experts.gate_up_proj (odd L) | **128x5120x16384 (3-D)** | BF16 | MOE-EXPERT-FUSED-GATEUP (all 128 experts, gate+up fused, **no `.weight` suffix**) | UNMAPPED |
| language_model.model.layers.{L}.feed_forward.experts.down_proj (odd L) | **128x8192x5120 (3-D)** | BF16 | MOE-EXPERT-FUSED-DOWN | UNMAPPED |
| language_model.model.layers.{L}.feed_forward.shared_expert.gate/up/down_proj.weight (odd L) | 8192x5120 / 5120x8192 | BF16 | MOE-SHARED-GATE/UP/DOWN | UNMAPPED |
| language_model.model.layers.{L}.input_layernorm / post_attention_layernorm.weight | 5120 | BF16 | NORM-ATT-IN / NORM-ATT-POST | NAME-VARIANT |
| language_model.model.norm.weight | 5120 | BF16 | NORM-FINAL | NAME-VARIANT |
| language_model.lm_head.weight | 202048x5120 | BF16 | LM-HEAD | NAME-VARIANT |

Vision tower (bracketed — not text-lane work): `vision_model.patch_embedding.linear.weight`
(1408x588 = 14x14x3 flattened patches → VIS-PATCH-EMB), `class_embedding` (VIS-CLASS-EMB),
`positional_embedding_vlm` (577x1408, VIS-POS-EMB), 34 layers of biased QKV/O + fc1/fc2 +
biased norms, `layernorm_pre/post`, `vision_adapter.mlp.fc1/fc2`, and
`multi_modal_projector.linear_1.weight` (5120x4096, MM-PROJECTOR).

---

# MASTER ROLE LIST

Union of every distinct role observed. Text-side: 64 roles. With vision-tower coarse roles: 70.

| # | role | carried by |
|---|---|---|
| 1 | EMB-WORD | all 19 |
| 2 | EMB-POS | MiniLM (learned absolute) |
| 3 | EMB-TOKENTYPE | MiniLM |
| 4 | EMB-NORM | MiniLM |
| 5 | EMB-NORM-BIAS | MiniLM |
| 6 | BUF-POSITION-IDS | MiniLM (I64 buffer, not a parameter) |
| 7 | ATT-QPROJ | all except DeepSeek-V3.2 (MLA-split) and DSV2-Lite (MLA-fused) — 17 |
| 8 | ATT-QPROJ-BIAS | MiniLM, phi-2, Qwen2.5 x3, jina-code |
| 9 | ATT-KPROJ | same 17 |
| 10 | ATT-KPROJ-BIAS | MiniLM, phi-2, Qwen2.5 x3, jina-code |
| 11 | ATT-VPROJ | same 17 |
| 12 | ATT-VPROJ-BIAS | MiniLM, phi-2, Qwen2.5 x3, jina-code |
| 13 | ATT-OPROJ | all 19 (phi-2 names it `dense`, MiniLM `attention.output.dense`) |
| 14 | ATT-OPROJ-BIAS | MiniLM, phi-2 |
| 15 | QK-NORM-Q | Qwen3-Emb x2, Qwen3-Rer x2, jina-reranker-v3, zerank-2, Qwen3-Coder-30B, Qwen3-Coder-480B |
| 16 | QK-NORM-K | same 8 |
| 17 | NORM-ATT-IN | all 18 decoders |
| 18 | NORM-ATT-IN-BIAS | phi-2 |
| 19 | NORM-ATT-POST | all decoders except phi-2 (parallel block) — 17 |
| 20 | NORM-ATT-OUT (post-norm) | MiniLM |
| 21 | NORM-ATT-OUT-BIAS | MiniLM |
| 22 | NORM-FFN-OUT (post-norm) | MiniLM |
| 23 | NORM-FFN-OUT-BIAS | MiniLM |
| 24 | NORM-FINAL | all 18 decoders |
| 25 | NORM-FINAL-BIAS | phi-2 |
| 26 | FFN-GATE | TinyLlama, Qwen2.5 x3, Qwen3-Emb x2, Qwen3-Rer x2, DSV2-Lite (L0), DS-33B, jina x2, zerank-2, DSV3.2 (L0-2), Llama-4 (even L) — 15 |
| 27 | FFN-UP | all 19 (fc1 / intermediate.dense included) |
| 28 | FFN-UP-BIAS | MiniLM, phi-2 |
| 29 | FFN-DOWN | all 19 |
| 30 | FFN-DOWN-BIAS | MiniLM, phi-2 |
| 31 | MOE-ROUTER | DSV2-Lite, Qwen3-Coder-30B, Qwen3-Coder-480B, DSV3.2, Llama-4 |
| 32 | MOE-ROUTER-CORRECTION-BIAS | DSV3.2 (`e_score_correction_bias`, noaux_tc) |
| 33 | MOE-EXPERT-GATE | DSV2-Lite, Qwen3-Coder-30B, Qwen3-Coder-480B, DSV3.2 |
| 34 | MOE-EXPERT-UP | same 4 |
| 35 | MOE-EXPERT-DOWN | same 4 |
| 36 | MOE-EXPERT-FUSED-GATEUP | Llama-4 (3-D, all experts one tensor) |
| 37 | MOE-EXPERT-FUSED-DOWN | Llama-4 (3-D) |
| 38 | MOE-SHARED-GATE | DSV2-Lite, DSV3.2, Llama-4 |
| 39 | MOE-SHARED-UP | same 3 |
| 40 | MOE-SHARED-DOWN | same 3 |
| 41 | MLA-Q-PROJ (uncompressed-Q MLA) | DSV2-Lite |
| 42 | MLA-Q-DOWN (q_a_proj) | DSV3.2 |
| 43 | MLA-Q-NORM (q_a_layernorm) | DSV3.2 |
| 44 | MLA-Q-UP (q_b_proj) | DSV3.2 |
| 45 | MLA-KV-DOWN (kv_a_proj_with_mqa) | DSV2-Lite, DSV3.2 |
| 46 | MLA-KV-NORM (kv_a_layernorm) | DSV2-Lite, DSV3.2 |
| 47 | MLA-KV-UP (kv_b_proj) | DSV2-Lite, DSV3.2 |
| 48 | DSA-IDX-K (indexer.wk) | DSV3.2 |
| 49 | DSA-IDX-K-NORM | DSV3.2 |
| 50 | DSA-IDX-K-NORM-BIAS | DSV3.2 |
| 51 | DSA-IDX-Q-UP (indexer.wq_b) | DSV3.2 |
| 52 | DSA-IDX-WEIGHTS (indexer.weights_proj) | DSV3.2 |
| 53 | QUANT-SCALE-INV (fp8 block dequant companion) | DSV3.2 (16 patterns, 46,600 tensors) |
| 54 | MTP-EMB | DSV3.2 |
| 55 | MTP-ENORM | DSV3.2 |
| 56 | MTP-HNORM | DSV3.2 |
| 57 | MTP-EH-PROJ | DSV3.2 |
| 58 | MTP-HEAD | DSV3.2 |
| 59 | MTP-HEAD-NORM | DSV3.2 |
| 60 | LM-HEAD | TinyLlama, phi-2, Qwen2.5-7B/14B, DSV2-Lite, DS-33B, Qwen3-Coder-30B/480B, DSV3.2, Llama-4 — 10 (tied models carry none) |
| 61 | LM-HEAD-BIAS | phi-2 |
| 62 | POOLER-DENSE | MiniLM |
| 63 | POOLER-DENSE-BIAS | MiniLM |
| 64 | RERANK-PROJECTOR | jina-reranker-v3 |
| 65-70 | VIS-PATCH-EMB, VIS-CLASS-EMB, VIS-POS-EMB, VIS-NORM-PRE/POST, VIS-ADAPTER, MM-PROJECTOR | Llama-4 vision tower only |

---

# UNMAPPED / SURPRISES — gaps against ArchitectureProfile

`ArchitectureProfile.cs` declares exactly 4 profiles (Llama, Phi, Qwen2, Bert) with slots
{EmbedTokens, LmHead, FinalNorm, PerLayerNorms, Q/K/V/O, Gate/Up/Down} + `BiasOf`, and
**`For()` silently falls back to Llama for every unknown model_type** — qwen3, qwen3_moe,
deepseek_v2, deepseek_v32, llama4_text all take that fallback today.

Flagged-row totals: 90 UNMAPPED pattern rows across the corpus, collapsing to 30 distinct
unmapped roles (rows 15-16, 31-59, 64 above plus MiniLM's 2, 3, 6, 62-63 and phi-2's 61).

1. **Prefix drift inside one architecture family.** Qwen3-Embedding-0.6B/4B and
   jina-code-embeddings-1.5b ship with NO `model.` prefix (`embed_tokens.weight`,
   `layers.{L}...`) while Qwen3-Reranker-0.6B/4B and zerank-2 — the same graph — keep
   `model.`. Llama-4 adds a third prefix (`language_model.model.`). The profile's absolute
   name strings miss 100% of tensors in the bare-prefix files even where the roles are
   plain Llama roles. Pattern matching must be suffix/role-based, not absolute-name-based.
2. **QK-NORM (per-head-dim RMSNorm, shape = head_dim, not hidden).** Carried by all 8
   Qwen3-family models — the single most common unmapped role in the corpus. Fallback-to-
   Llama silently drops it, which mis-states the ATTENDS bilinear (Q and K are re-normalized
   per head before RoPE).
3. **head_dim decoupled from hidden/heads.** Qwen3: 16 heads x 128 = 2048 ≠ hidden 1024;
   o_proj is hidden x (heads*head_dim), NOT square. Any code assuming
   `head_dim = hidden/heads` mis-slices every Qwen3 head.
4. **MoE, three storage conventions.** (a) per-expert 2-D tensors under `experts.{E}`
   (DeepSeek, Qwen3-MoE — 15k-30k tensors/model); (b) Llama-4 fused 3-D tensors
   `[n_experts, in, out]` with gate+up concatenated and **no `.weight` suffix**; (c) shared
   experts as a separate fused branch (DSV2-Lite fuses 2 shared experts into one 2816-wide
   pair). Router weight is named `mlp.gate` (DeepSeek/Qwen) vs `feed_forward.router`
   (Llama-4). No profile slot exists for any of it.
5. **MLA (DeepSeek).** Q/KV low-rank factorization with a norm at the bottleneck:
   `q_a_proj → q_a_layernorm → q_b_proj` and `kv_a_proj_with_mqa (576 = 512 latent + 64
   shared-rope) → kv_a_layernorm → kv_b_proj`. V2-Lite skips Q compression (q_lora_rank
   null) so its `q_proj` name collides with the profile's ATT-QPROJ while carrying fused
   nope+rope dims — a name-matches/math-differs trap.
6. **DSA lightning indexer (DeepSeek-V3.2).** A parallel mini-attention per layer
   (`indexer.wk`, `indexer.wq_b`, `indexer.k_norm{.bias}`, `indexer.weights_proj`) that
   selects index_topk=2048 tokens — a tensor family that did not exist before V3.2, and the
   indexer k_norm is a biased LayerNorm inside an otherwise RMSNorm model.
7. **FP8 block quantization as tensor pairs.** Every quantized weight in V3.2 has a F32
   `weight_scale_inv` companion (ceil(shape/128) grid). 46,600 of its 92,425 tensors are
   scale companions — a reader that treats each tensor as a standalone weight double-counts
   the model.
8. **MTP layer stored past num_hidden_layers.** V3.2 keeps its multi-token-prediction head
   as `model.layers.61.*` (num_hidden_layers = 61, so index 61 is one past the end) with its
   own embed_tokens copy, enorm/hnorm, eh_proj (7168x14336 — concat projection), and
   shared_head. Layer-count-driven iteration either misses it or crashes on it.
9. **Tied embeddings mean absent lm_head.** 8 of 19 models (Qwen2.5-3B, all Qwen3
   embedders/rerankers, jina x2, zerank-2) carry no `lm_head.weight`; the profile's
   `LmHead = "lm_head.weight"` must be nullable-by-tie, keyed on `tie_word_embeddings`.
10. **Heads beyond the LM head.** MiniLM `pooler.dense` (+bias), jina-reranker-v3
    `projector.{0,2}` MLP. Also phi-2's rare `lm_head.bias`, and MiniLM's I64
    `position_ids` buffer stored as a tensor.
11. **Config-shape quirks worth a parser note:** Llama-4 nests everything under
    `text_config`/`vision_config`; zerank-2 uses `dtype` instead of `torch_dtype` and
    materializes `layer_types[]`; jina-code carries retrieval metadata
    (`matryoshka_dims`, `task_names`) in config.json; DSV2-Lite/V3.2 use yarn rope scaling
    with mscale; Llama-4 has `no_rope_layers` + `attention_chunk_size` (NoPE/chunked
    attention); phi-2 has `partial_rotary_factor 0.4` (only 32 of 80 dims rotary).

Skipped/absent: none of the covered dirs lacked snapshots or safetensors; every targeted
model had readable headers. `deepseek-coder-33b`, `Qwen3-Coder-480B` and `DeepSeek-V3.2`
were read header-only (no data touched) despite size.
