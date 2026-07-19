# 27b — Primitive Index: Checkpoint Containers, Metadata, and the Gate Matrix

Companion to `27a_Primitive_Index_Attention.md` (ATT-\*, POS-\*) and
`27c_Primitive_Index_FFN_Norms_Embeddings.md` (FFN-\*, NRM-\*, EMB-\*), written in
parallel. This doc owns the **CON-\*** namespace (containers and metadata) and the
**master gate matrix** that spans all three docs. Campaign context:
`26_Uncracked_List_Campaign.md` (lanes A–J). Current decomposer coverage:
`app/Laplace.Decomposers/Model/ArchitectureProfile.cs` (llama, phi, qwen2, bert;
unknown model_type silently falls back to Llama — a matrix row of its own, CON-CFG-CORE).

ID convention across the three docs:

| Prefix | Owner doc | Domain |
|--------|-----------|--------|
| `ATT-*` | Attention doc | Q/K/V/O projections, score, softmax, mask, mix |
| `POS-*` | Attention doc | RoPE, rope-scaling, absolute/learned positions |
| `FFN-*` | FFN/Norms/Embeddings doc | gate/up/down, activations, MoE |
| `NRM-*` | FFN/Norms/Embeddings doc | RMSNorm, LayerNorm, qk-norm, final norm |
| `EMB-*` | FFN/Norms/Embeddings doc | token/position/segment embeddings, lm_head |
| `TOK-*` | this doc (tokenizer containers double as replay primitives) | tokenize/detokenize |
| `CON-*` | this doc | container formats and metadata scalars |

Where a companion doc lands on a different ID for the same primitive, the companion
doc's ID wins and the matrix row is renamed; the matrix **structure** is the contract.

--------------------------------------------------------------------------------
## The spine and everything hanging off it
--------------------------------------------------------------------------------

One forward pass, one page, every primitive ID in firing order. Operator shorthand
"Q/K/V/O" maps to ATT-QPROJ / ATT-KPROJ / ATT-VPROJ / ATT-OPROJ below.

**Before the spine — the containers.** The checkpoint arrives as a directory:
weights in one or more `.safetensors` files (CON-ST-FILE, CON-ST-SHARD), the
architecture recipe in `config.json` (CON-CFG-\*), the tokenizer in `tokenizer.json`
or its older split forms (CON-TOK-\*), and decode-time scalars in
`generation_config.json` (CON-GEN-CFG). Every dtype in the weight files decodes to
f32 exactly (CON-DTYPE-EXACT) — that exactness is what makes a bit-level replay gate
possible at all.

1. **Tokenize** (TOK-BPE / TOK-WORDPIECE / TOK-SPM): prompt bytes → token ids, using
   the vocab, merges, and special tokens the containers witnessed. Byte-level BPE and
   byte-fallback (CON-TOK-BYTELEVEL) decide what happens to bytes no token covers.
2. **Embed** (EMB-TOK): each token id indexes one row of `embed_tokens` — a lookup,
   not math. BERT adds two more lookups summed in: absolute position (EMB-POS) and
   segment (EMB-SEG). RoPE families skip both; their position enters later, at step 5.
3. **Pre-attention norm** (NRM-RMS for Llama/Qwen2, NRM-LN for BERT/Phi): the residual
   stream is rescaled so every layer sees inputs of tame magnitude. LayerNorm subtracts
   the mean and applies beta; RMSNorm only rescales — folding them the same way is
   BERT defect (b).
4. **Q/K/V projections** (ATT-QPROJ, ATT-KPROJ, ATT-VPROJ, plus ATT-BIAS where
   HasBiases): three GEMMs turn the normed stream into per-head query, key, value
   vectors. GQA (ATT-GQA) makes K/V narrower — several query heads share one KV head.
   Optional per-head q/k norm (NRM-QK) rescales q and k before scoring.
5. **Position rotation** (POS-ROPE, POS-ROPE-SCALE): q and k are rotated by an angle
   that depends on token offset and `rope_theta`; scaling dicts (llama3/yarn/longrope)
   remap the frequencies for long context. BERT passes `offset=NULL` — its position
   already entered at step 2.
6. **Score** (ATT-SCORE): q·k / sqrt(head_dim) for every (query token, key token) pair —
   the bilinear form the factor payloads (campaign lane A) must reconstruct exactly.
7. **Mask** (ATT-MASK-CAUSAL / ATT-MASK-SWA): future tokens (and, with
   `sliding_window`, tokens beyond the window) are struck from the score table.
8. **Softmax** (ATT-SOFTMAX): each query row's surviving scores become a probability
   mixture over key tokens.
9. **Mix and output** (ATT-MIX, ATT-OPROJ): the mixture weights average the V vectors;
   the O projection writes the result back into residual-stream coordinates. V∘O
   composed is the OV_RELATES plane. Residual add follows.
10. **Post-attention norm** (NRM-POST): same norm law as step 3, second copy.
11. **FFN** (FFN-GATE, FFN-UP, FFN-ACT, FFN-DOWN): up-project, apply the nonlinearity
    (SiLU with gating for Llama/Qwen2, GELU without for Phi/BERT — GELU is part of
    BERT coverage), down-project. MoE variants (FFN-MOE-ROUTER) route each token to
    top-k experts first. Residual add follows. This is COMPLETES_TO.
12. **Repeat** steps 3–11 for `num_hidden_layers` layers — the residual stream is the
    only thing that survives between them.
13. **Final norm** (NRM-FINAL): one last rescale of the residual stream.
14. **Unembed** (EMB-LMHEAD): one GEMM against `lm_head` (or the transposed embedding
    when `tie_word_embeddings` — EMB-TIE) → logits over the vocab. Decode-time
    sampling scalars live in CON-GEN-CFG and are witnessed, not replayed.

Everything the ETL scrapes is a slice of this spine: SIMILAR_TO reads step 2's table
against itself, ATTENDS freezes step 6, OV_RELATES composes step 9, COMPLETES_TO
contracts step 11, CONTINUES_TO rides step 14. The gate matrix below asks, per
primitive: did we witness its bytes, can we replay its math, and is the replay exact.

--------------------------------------------------------------------------------
## PART 1 — Containers and metadata (CON-\*, TOK-\*)
--------------------------------------------------------------------------------

Schema per entry: **ID** / What/Why / Description / What ETL must witness / Gate.

### CON-ST-FILE — safetensors single-file container

- **What/Why:** The standard weights file: a tiny JSON table of contents followed by
  one flat byte buffer. It exists so tensors can be read by name and byte-range
  without executing code (unlike pickle).
- **Description:** Layout = 8-byte little-endian u64 header length **N**, then N bytes
  of UTF-8 JSON, then the data buffer starting at offset 8+N. Each JSON key is a
  tensor name mapping to `{"dtype", "shape", "data_offsets": [start, end]}`;
  offsets are **relative to the buffer start**, not the file. Buffer is contiguous,
  no gaps, no overlaps. A special `"__metadata__"` key holds string→string metadata
  (commonly `{"format": "pt"}`).
- **What ETL must witness:** every tensor's (name, dtype, shape, absolute byte range)
  — this is the step-1 per-checkpoint tensor/slice entity (`ModelCheckpoint.cs`
  byte-range ids). Implemented: `SafetensorsContainerParser.ParseHeader` (header-length
  sanity cap 256MB, `__metadata__` skipped, refs sorted by DataStart).
- **Gate:** header roundtrip — parsed (name, dtype, shape, offsets) matches a
  kernel-direct re-read; every buffer byte is covered by exactly one tensor or the
  parse fails loudly.

### CON-ST-SHARD — sharded checkpoints and `model.safetensors.index.json`

- **What/Why:** Big models split weights across `model-00001-of-000NN.safetensors`
  shards; the index file says which tensor lives in which shard. It exists because
  single files have practical size/upload limits.
- **Description:** `model.safetensors.index.json` = `{"metadata": {"total_size": …},
  "weight_map": {"tensor.name": "model-00002-of-00007.safetensors", …}}`. Single-file
  models have no index.
- **What ETL must witness:** the shard→tensor assignment as part of checkpoint
  identity. **Current code does not read the index** — `ParseModel` globs
  `*.safetensors` ordinally and unions headers. That is correct for well-formed
  snapshots but silently tolerates a missing shard and cannot detect an
  index/directory mismatch.
- **Gate:** when the index exists, every `weight_map` entry resolves to a parsed
  tensor in the named shard and no parsed tensor is absent from the map; sum of
  buffers ≈ `metadata.total_size`. Missing shard = hard fail, not partial scrape.

### CON-ST-DTYPE — dtype codes

- **What/Why:** The header's dtype string tells you how to interpret each tensor's
  raw bytes. Getting one code wrong corrupts every number downstream, silently.
- **Description:** Spec codes: `F64 F32 F16 BF16 F8_E5M2 F8_E4M3 I64 I32 I16 I8 U8
  BOOL` (newer spec revisions add U16/U32/U64 and sub-byte F4/quantized codes — treat
  any unknown code as a hard fail, never a skip). All little-endian, C-contiguous,
  row-major.
- **What ETL must witness:** dtype per tensor (already on the TensorReference). The
  decode table lives in `WeightTensorETL` (`F32` memcpy, `F16` via `UInt16BitsToHalf`,
  `BF16` via native `laplace_bf16_decode`, `F8_E5M2`/`F8_E4M3` via C# decoders;
  unknown dtype throws).
- **Gate:** decoder-vs-spec bit tables — for each supported dtype, all bit patterns
  (exhaustive for 8/16-bit) decode to the reference f32 value including
  ±0/±inf/NaN/subnormals.

### CON-ST-META — `__metadata__` block

- **What/Why:** Free-form string map in the header (producer, format tag). Exists for
  provenance; it is the only place a safetensors file says anything about itself.
- **Description:** String→string only, per spec. Commonly `{"format": "pt"}`.
- **What ETL must witness:** transcribe verbatim as checkpoint-scoped attestations
  (witnessed layer — a source assertion about itself). Currently skipped by the parser.
- **Gate:** metadata keys present in the file appear as attestations on the checkpoint
  entity; absent block = no rows (never fabricate).

### CON-CFG-CORE — `config.json` canonical scalars

- **What/Why:** The architecture recipe: how wide, how deep, how many heads. The ETL
  cannot slice a single tensor correctly without these numbers.
- **Description:** Canonical HF keys: `model_type`, `architectures[]`, `vocab_size`,
  `hidden_size`, `num_hidden_layers`, `num_attention_heads`, `num_key_value_heads`
  (defaults to num_attention_heads → MHA), `head_dim` (defaults to
  hidden_size/num_heads — Gemma3 and others override it explicitly),
  `intermediate_size`, `rms_norm_eps` / `layer_norm_eps`, `tie_word_embeddings`,
  `max_position_embeddings`, `torch_dtype`, `bos_token_id`/`eos_token_id`.
- **What ETL must witness:** every scalar as a recipe attestation; the whole
  canonicalized JSON hashed to the recipe entity id. Implemented:
  `ModelConfigReader.Read` (key-sorted canonical JSON → BLAKE3 recipe id; alias
  fallbacks; MLA keys; qk-norm flags). **Known gap:** `ArchitectureProfile.For`
  defaults unknown model_type to Llama instead of refusing — an unwitnessed guess.
- **Gate:** parsed scalars equal a direct JSON re-read for the gate models; unknown
  model_type must surface as Coverage != Full, never a silent Llama fallback into
  the emit path.

### CON-CFG-ALIAS — per-family key aliases

- **What/Why:** Older/other families name the same scalars differently (GPT-2 says
  `n_embd`, T5 says `d_model`). One alias table exists so one reader serves all
  families.
- **Description:** Verified alias sets in `ModelConfigReader`:
  hidden_size ← `n_embd`/`d_model`/`model_dim`; layers ←
  `n_layer`/`num_layers`/`n_layers`; heads ← `n_head`/`num_heads`; kv heads ←
  `num_kv_heads`/`n_kv_heads`; intermediate ← `ffn_dim`/`n_inner`/`encoder_ffn_dim`;
  head_dim ← `attention_head_dim`; experts ←
  `num_local_experts`/`num_experts`/`n_routed_experts`; eps ←
  `rms_norm_eps`/`layer_norm_eps`/`layer_norm_epsilon`; rope ←
  `rope_theta`/`rotary_emb_base`.
- **What ETL must witness:** the value under the key **the source actually used**
  (record-vs-calculate: the alias resolution is calculated, the raw key/value is
  witnessed via the canonical JSON hash).
- **Gate:** per new family, an alias-resolution unit test against that family's real
  config.json; resolved scalar equals hand-read value.

### CON-CFG-ROPE — `rope_theta` and the `rope_scaling` dict

- **What/Why:** RoPE's base frequency and any long-context remapping of it. Replay of
  ATT-SCORE at nonzero offset is wrong without these — they parameterize POS-ROPE.
- **Description:** `rope_theta` scalar (default 10000.0; Llama-3 uses 500000.0).
  `rope_scaling` dict: `rope_type`/`type` ∈ {default, linear, dynamic, yarn, longrope,
  llama3}; `factor` (all types except default); llama3 adds `low_freq_factor`,
  `high_freq_factor`, `original_max_position_embeddings`; yarn adds
  `beta_fast`/`beta_slow` (interpolation ramp bounds); longrope adds
  short/long factor arrays. Phi additionally has `partial_rotary_factor` (only part
  of head_dim rotates).
- **What ETL must witness:** theta (implemented) **and the full rope_scaling dict
  (NOT currently parsed — verified `rope_scaling` appears nowhere under `app/`)**;
  partial_rotary_factor also unread. All are recipe scalars for lane H (RoPE
  read-side rotation, doc 26 §B).
- **Gate:** `gate_pos_rope_scale` — native rotation with witnessed
  theta+scaling reproduces kernel-direct q·k at offsets straddling
  original_max_position_embeddings; a llama3-scaled model must FAIL the unscaled
  path (proves the dict is load-bearing).

### CON-CFG-ATTN — attention-shape extras

- **What/Why:** Flags that change which score cells exist at all: sliding windows,
  bias presence, qk-norm. Wrong flags = replaying a different model.
- **Description:** `sliding_window` (+ Qwen2's `use_sliding_window` /
  `max_window_layers` — window applies only to some layers), `attention_bias` /
  `attention_dropout`, `use_qk_norm`/`qk_layernorm`, Gemma-style
  `query_pre_attn_scalar` (score divisor override), `attn_logit_softcapping`.
- **What ETL must witness:** each present key verbatim. Implemented: qk-norm flag
  triplet only; sliding_window and the rest ride the canonical JSON hash but are not
  first-class scalars yet.
- **Gate:** ATT-MASK-SWA row in the matrix; scalar readback test per key on a model
  that has it (e.g. Qwen2 7B sliding window).

### CON-CFG-MOE — mixture-of-experts scalars

- **What/Why:** MoE models have N parallel FFNs and a router; these keys say how many
  and how many fire per token. Without them the FFN tensor names don't even resolve.
- **Description:** `num_local_experts` (Mixtral) / `num_experts` (Qwen3-MoE) /
  `n_routed_experts` (DeepSeek), `num_experts_per_tok`, `moe_intermediate_size`,
  `shared_expert_intermediate_size`, `norm_topk_prob`, DeepSeek's `n_shared_experts`.
- **What ETL must witness:** expert count (implemented as `NumExperts`), the rest on
  the canonical hash. No ArchitectureProfile has MoE tensor templates yet — MoE is
  witnessed-at-recipe-grain only, FFN-MOE-ROUTER row = open.
- **Gate:** deferred until an MoE profile lands; gate model Mixtral-tiny or
  Qwen1.5-MoE-A2.7B; router replay top-k agreement vs kernel-direct.

### CON-CFG-MLA — DeepSeek multi-head-latent-attention keys

- **What/Why:** DeepSeek compresses K/V through low-rank latents; five extra scalars
  describe the factorized shapes. They exist to make the KV cache small.
- **Description:** `q_lora_rank`, `kv_lora_rank`, `qk_rope_head_dim`,
  `qk_nope_head_dim`, `v_head_dim`.
- **What ETL must witness:** all five — **implemented** in `ModelConfigReader`
  (MlaQLoraRank etc.); no MLA ArchitectureProfile/paths yet, so scoring replay is open.
- **Gate:** deferred with the MLA profile; scalars-readback test exists as part of
  CON-CFG-CORE gate.

### CON-CFG-DTYPE — `torch_dtype`

- **What/Why:** The dtype the producer trained/saved in, asserted at config grain. It
  can disagree with per-tensor dtypes (mixed-precision checkpoints) — the
  disagreement is itself signal.
- **Description:** String: `float32`/`float16`/`bfloat16`/`float8_e4m3fn`….
- **What ETL must witness:** verbatim (implemented in `LlamaRecipeExtractor`, default
  "bfloat16"). Per-tensor dtype (CON-ST-DTYPE) is authoritative for decode; this is
  provenance only.
- **Gate:** attestation present; decode never consults it (asserted by code review /
  no read path from recipe dtype into WeightTensorETL).

### CON-GEN-CFG — `generation_config.json`

- **What/Why:** Decode-time defaults the producer ships: sampling temperature, eos
  ids, penalties. Not part of the forward pass, but part of what the checkpoint
  asserts about how to run itself.
- **Description:** `bos_token_id`, `eos_token_id` (int or array), `pad_token_id`,
  `temperature`, `top_p`, `top_k`, `repetition_penalty`, `do_sample`,
  `max_length`/`max_new_tokens`.
- **What ETL must witness:** each present scalar as recipe-adjacent attestations
  under the checkpoint source. **Not currently read anywhere** (verified: no
  `generation_config` reference under the Model decomposer).
- **Gate:** readback test — every key present in the file has exactly one attestation;
  the file being absent produces zero rows. Inference-relevant only for the substrate's
  own realize/converse defaults, never for replay exactness.

### CON-TOK-JSON — `tokenizer.json` (HF tokenizers container)

- **What/Why:** The single-file modern tokenizer: vocab, merges, normalization, and
  pre-tokenization rules in one JSON. It exists so tokenization is reproducible
  without Python code.
- **Description:** Top-level: `model.type` ∈ {BPE, WordPiece, Unigram};
  `model.vocab` (map token→id for BPE/WordPiece; array of [piece, logprob] for
  Unigram); `model.merges` (BPE only; array of "left right" strings or [l,r] pairs);
  `normalizer` (NFC/NFKC/lowercase/strip chains); `pre_tokenizer` (ByteLevel /
  Whitespace / Metaspace); `decoder`; `added_tokens[]` (id, content, `special` flag);
  `post_processor` (template with [CLS]/[SEP] etc.).
- **What ETL must witness:** full vocab (token id → canonical bytes → content entity),
  merges (TOKEN_MAPS_TO / merge-rank records), added/special tokens. Implemented:
  `LlamaTokenizerParser.Parse` (model.vocab or bare vocab; added_tokens special ids;
  byte-level and metaspace canonicalization) + `ParseMerges` (model.merges only).
  **Gaps:** Unigram vocab shape (array-of-pairs) unhandled; normalizer/pre_tokenizer
  law not witnessed, so encode replay is not yet derivable for arbitrary text.
- **Gate:** TOK-\* rows; vocab roundtrip — every id in `embed_tokens` row-space has
  exactly one token record; canonical bytes re-encode to the same id via the witnessed
  merge table (BPE) on the gate models.

### CON-TOK-WORDPIECE — `vocab.txt` (BERT WordPiece)

- **What/Why:** The old BERT container: one token per line, line number = id,
  `##` prefix marks continuation pieces. MiniLM's tokenizer — the MiniLM-first gate
  cannot pass without it.
- **Description:** Plain text, LF-separated; specials `[PAD] [UNK] [CLS] [SEP]
  [MASK]` are ordinary lines; `tokenizer_config.json` carries `do_lower_case` and
  `strip_accents`. Greedy longest-match-first encoding, per-word, unknown → `[UNK]`.
  Note MiniLM snapshots ALSO ship a `tokenizer.json` (WordPiece model.type), which is
  the path the current parser would take — **doc 26 defect (d): that path is
  unverified for WordPiece** (## continuation vs ▁/Ġ canonicalization differ).
- **What ETL must witness:** id→piece table, continuation flag as TokenRole, specials,
  the lowercase/strip flags (they change the witnessed surface law).
- **Gate:** `ModelGate_TOK_WORDPIECE_MiniLM` — parser output over MiniLM's tokenizer
  matches a hand-verified sample (specials, ## pieces, cased handling); encode of the
  probe corpus matches reference token ids recorded once from the checkpoint's own
  assets (no Python at gate time).

### CON-TOK-SPM — sentencepiece `.model` (protobuf)

- **What/Why:** Llama-1/2, T5, Gemma ship the tokenizer as a compiled sentencepiece
  protobuf instead of JSON. It exists because SPM predates HF tokenizers and trains
  its own Unigram/BPE models.
- **Description:** Binary protobuf (`ModelProto`): pieces[] with (piece, score, type ∈
  {NORMAL, UNKNOWN, CONTROL, USER_DEFINED, BYTE}), trainer/normalizer specs inside.
  `▁` (U+2581) encodes the word boundary; byte-fallback pieces are `<0xXX>`.
  Most such repos also ship an equivalent `tokenizer.json`.
- **What ETL must witness:** piece table with types and scores. Current strategy:
  ride the sibling `tokenizer.json` (the parser's metaspace/byte-fallback handling
  exists); native protobuf parse is open — required only when a snapshot ships ONLY
  `.model` (no Python converters allowed by law).
- **Gate:** vocab parity — `.model` piece table == `tokenizer.json` vocab on a model
  shipping both (TinyLlama); byte-fallback pieces land as byte atoms.

### CON-TOK-BPE-GPT2 — `vocab.json` + `merges.txt` (split GPT-2 BPE)

- **What/Why:** The pre-tokenizer.json container: vocab map and merge list as two
  files. GPT-2/RoBERTa/CLIP-era checkpoints still ship it, sometimes without a
  tokenizer.json.
- **Description:** `vocab.json` = token→id map (byte-level alphabet, Ġ = space);
  `merges.txt` = one merge per line in rank order, `#version` header line.
- **What ETL must witness:** same records as CON-TOK-JSON. **Not currently read** —
  parser requires tokenizer.json. Acceptable until a gate model needs it; then the
  two-file reader feeds the same TokenRecord/merge pipeline.
- **Gate:** parity with tokenizer.json on a model shipping both (gpt2 snapshot).

### CON-TOK-BYTELEVEL — byte-level BPE vs byte-fallback semantics

- **What/Why:** Two different answers to "what about bytes with no token": GPT-2
  byte-level BPE remaps ALL 256 bytes to printable unicode surrogates before merging;
  SPM byte-fallback emits literal `<0xXX>` tokens only when a piece is missing.
  Confusing them corrupts the canonical bytes of every token.
- **Description:** Byte-level: the 256-entry byte↔unicode table (Ġ=0x20, Ċ=0x0A, …);
  decode = inverse-map then UTF-8. Byte-fallback: `<0xXX>` pieces of type BYTE;
  `▁` metaspace for word starts.
- **What ETL must witness:** the canonical BYTES of each token, post-inverse-map —
  implemented in `LlamaTokenizerParser.Canonicalize` (TokenRole.ByteLevel flag; single
  bytes ≥ ByteAtoms.First get byte-atom coords).
- **Gate:** roundtrip — canonical bytes of the full vocab re-serialize to the raw
  token strings under the detected scheme; a mixed-scheme vocab (specials + byte
  pieces + normal pieces) partitions with zero unclassified tokens.

### CON-TOK-SPECIAL — `special_tokens_map.json`, `added_tokens.json`, `tokenizer_config.json`

- **What/Why:** The sidecar files naming bos/eos/pad/unk, extra added tokens, and the
  chat template. They exist because specials were bolted on after the base vocab was
  trained.
- **Description:** `special_tokens_map.json` (role→token string);
  `added_tokens.json` (token→id, legacy); `tokenizer_config.json`
  (`chat_template` Jinja string, `model_max_length`, per-token
  `add_bos_token`/`add_eos_token`, `do_lower_case`).
- **What ETL must witness:** role bindings (bos/eos/pad ids) and chat_template
  verbatim (it is the checkpoint's asserted prompt grammar — witnessed text, tier by
  content law). Implemented: only `added_tokens[].special` inside tokenizer.json;
  the three sidecars are **not read**.
- **Gate:** role-binding readback on gate models; chat_template attestation
  byte-identical to the file.

### CON-DTYPE-EXACT — precision semantics for exact replay

- **What/Why:** The whole gate philosophy rests on one fact: every storage dtype
  decodes to f32 without loss, so "replay == checkpoint math" can be tested for
  equality instead of tolerance.
- **Description:** bf16→f32 is exact (bf16 = truncated f32; decode is `<<16`).
  f16→f32 is exact (every f16 value is representable in f32). f8 (E5M2/E4M3)→f32 is
  exact for the same reason. f32→f64 exact. The LOSSY directions (f32→bf16 rounding)
  occur only at foundry export, never in the scrape.
- **What "bit-exact gate" means per dtype:** the decoded f32 bit pattern equals the
  reference decode, including NaN payloads, ±inf, subnormals, and negative zero
  (E4M3fn quirk: no inf, S.1111.111 = NaN). Downstream ACCUMULATION is where
  exactness ends: dot products are order-sensitive, so gates on composed values state
  a tolerance (T1 7.6e-06, T2 5.1e-07, B ≤1e-6) with Neumaier summation as the
  reference order, while gates on decode/factor-storage state bit equality (FACTOR
  vertices store f32 verbatim — 6×f32 per mantissa vertex, doc 26 §A).
- **What ETL must witness:** nothing extra — this entry is the LAW the other gates
  cite.
- **Gate:** `gate_con_dtype_exact` — exhaustive 16-bit/8-bit decode tables (see
  CON-ST-DTYPE); plus one composed check: q·k computed from bf16-decoded f32 via
  Neumaier == the same computed in f64 within stated tolerance on gate models.

### CON-QUANT-GGUF — quantized containers (GGUF, GPTQ/AWQ safetensors)

- **What/Why:** Block-quantized formats (GGUF Q4_K etc., GPTQ/AWQ int4 tensors) store
  weights lossily with per-block scales. They exist for inference RAM; they are NOT a
  witness of the model's trained values.
- **Description:** GGUF = self-contained single file (its own KV metadata + tokenizer
  + quantized tensors). GPTQ/AWQ = safetensors with qweight/qzeros/scales triples.
- **What ETL must witness:** **out of scope for the scrape.** The scrape witnesses
  full-precision checkpoints only (parser error message already states GGUF is not a
  safetensors witness). GGUF is FOUNDRY-side vocabulary: `engine/synthesis/gguf_writer`
  EXPORTS molded models to GGUF. Never ingest a quantized container as a checkpoint
  witness; dequantized values would be calculated, not witnessed, and the precision
  gates above become unstateable.
- **Gate:** negative gate — `ingest model` over a GGUF/GPTQ directory refuses with a
  clear error; no partial rows deposited.

**Part 1 entry count: 19.**

--------------------------------------------------------------------------------
## PART 2 — The gate matrix
--------------------------------------------------------------------------------

### Row schema (the contract)

| Column | Meaning |
|--------|---------|
| **ID** | Primitive id from the three index docs (namespace table above) |
| **Witnessed?** | Does the ETL transcribe this primitive's bytes/scalars into the substrate verbatim? `yes` / `partial` / `no` — with the code site when yes |
| **Replay op implemented?** | Does a native (C/C++/SPI) op reproduce this primitive's math from stored payloads? `yes` / `no` / `n/a` (pure-witness rows) |
| **Exactness gate test** | `ModelGate_<ID>_<Model>` (xunit) and/or `gate_<id>` (pg_regress); named even when not yet written — the name IS the work item |
| **Perf gate** | Wall-clock or complexity bound, if the primitive is on the scrape/score hot path; `—` otherwise |
| **Status** | `GREEN` (gate passing) / `RED` (gate exists or defect known, failing) / `OPEN` (not built) / `N/A` |

Test naming convention (binding):
- xunit: `ModelGate_<ID>_<Model>` — ID with hyphens as underscores, e.g.
  `ModelGate_ATT_SCORE_MiniLM`, `ModelGate_TOK_WORDPIECE_MiniLM`.
- pg_regress: `gate_<id>` — lowercase, underscores, model-agnostic (the SQL gate runs
  against whatever checkpoint the seed step deposited), e.g. `gate_att_score`.
- Order law (operator directive 2026-07-15): **MiniLM first, then TinyLlama.** A gate
  is not GREEN until it passes on MiniLM (where BERT coverage applies) or the smallest
  covered model, bare run, no Python anywhere.

### Standing campaign gates (apply to every row)

1. **MiniLM-first correctness gate** — ETL must obey `ArchitectureProfile.Bert`; four
   verified defects to clear (doc 26 §A): (a) biases never applied despite
   HasBiases/`BiasOf()`; (b) NormFold treats LayerNorm as column scale — correct is
   x_t = LN(E[t]+P[0]+S[0]; gamma,beta) per token natively, then project; (c)
   position/segment embedding roles absent from the profile; (d) WordPiece tokenizer
   path through LlamaTokenizerParser unverified.
2. **TinyLlama perf gate** — full factor scrape ≤60s target, 120s hard ceiling,
   wall-clock from the seed-step log, bare run. Scalar Neumaier qk kernels are gate
   reference only, never the bulk path.
3. **Arena-deposit requirement** — trajectory vertex 0 carries the arena; score-law
   inversion (T3, 2.3e-9) holds IFF the arena is deposited. Any row whose replay
   inverts the score law inherits this requirement.
4. **No-Python law** — no Python instruments anywhere in any gate. ETL or nothing.
5. **Relation-declaration law** — any new relation a row's emit path produces must be
   declared in `InitializeAsync relationNodeNames` before the gate can run (undeclared
   relation = native 0xC0000005).

### The matrix

Rows below are filled for primitives verifiable today from code and doc 26; companion
docs add/rename ATT/POS/FFN/NRM/EMB rows under the same schema.

| ID | Witnessed? | Replay op implemented? | Exactness gate test | Perf gate | Status |
|----|-----------|------------------------|---------------------|-----------|--------|
| CON-ST-FILE | yes — SafetensorsContainerParser | n/a | ModelGate_CON_ST_FILE_MiniLM | header parse O(header) | GREEN (in production use) |
| CON-ST-SHARD | partial — glob union, index.json unread | n/a | ModelGate_CON_ST_SHARD_TinyLlama | — | RED (missing-shard undetected) |
| CON-ST-DTYPE | yes — WeightTensorETL decode table | yes (bf16 native, f16/f8 C#) | ModelGate_CON_ST_DTYPE_All / gate_con_dtype_exact | decode ≥ GB/s memcpy-class | GREEN for F32/F16/BF16/F8; unknown dtype hard-fails |
| CON-ST-META | no — `__metadata__` skipped | n/a | ModelGate_CON_ST_META_MiniLM | — | OPEN |
| CON-CFG-CORE | yes — ModelConfigReader + canonical-JSON hash | n/a | ModelGate_CON_CFG_CORE_MiniLM | — | RED (unknown model_type falls back to Llama) |
| CON-CFG-ALIAS | yes — FirstInt alias chains | n/a | ModelGate_CON_CFG_ALIAS_&lt;Family&gt; | — | GREEN for covered families |
| CON-CFG-ROPE | partial — theta yes, rope_scaling dict NOT parsed | no (lane H) | ModelGate_CON_CFG_ROPE_TinyLlama / gate_pos_rope_scale | — | RED (scaling dict unread) |
| CON-CFG-ATTN | partial — qk-norm flags only; sliding_window unread | no | ModelGate_CON_CFG_ATTN_Qwen2 | — | OPEN |
| CON-CFG-MOE | partial — expert count only; no MoE profile | no | ModelGate_CON_CFG_MOE_Mixtral | — | OPEN |
| CON-CFG-MLA | yes — 5 MLA scalars read | no (no MLA profile) | ModelGate_CON_CFG_MLA_DeepSeek | — | OPEN (witness GREEN, replay OPEN) |
| CON-GEN-CFG | no — file never read | n/a | ModelGate_CON_GEN_CFG_MiniLM | — | OPEN |
| CON-DTYPE-EXACT | law row | yes (Neumaier reference kernels) | gate_con_dtype_exact | — | GREEN (T1/T2 evidence) |
| CON-QUANT-GGUF | out of scope (foundry export only) | n/a | ModelGate_CON_QUANT_Refusal | — | GREEN if refusal path proven, else OPEN |
| TOK-BPE | yes — LlamaTokenizerParser (tokenizer.json vocab+merges) | partial (encode replay from witnessed merges not proven) | ModelGate_TOK_BPE_TinyLlama | vocab parse seconds | GREEN witness / OPEN encode-replay |
| TOK-WORDPIECE | partial — path exists, unverified (defect d) | no | ModelGate_TOK_WORDPIECE_MiniLM | — | RED (doc 26 defect d) |
| TOK-SPM | partial — via sibling tokenizer.json only | no (native protobuf open) | ModelGate_TOK_SPM_TinyLlama | — | OPEN |
| TOK-BYTELEVEL | yes — Canonicalize byte-map + byte atoms | n/a | ModelGate_TOK_BYTELEVEL_TinyLlama | — | GREEN |
| TOK-SPECIAL | partial — added_tokens.special only; sidecars unread | n/a | ModelGate_TOK_SPECIAL_MiniLM | — | OPEN |
| EMB-TOK | yes — SelfSimilarityPath / step-1 tensor entities | yes (ProjectEmbedding) | ModelGate_EMB_TOK_MiniLM (T1 factorization 7.6e-06) | inside scrape budget | GREEN |
| EMB-POS | no — role absent from Bert profile (defect c) | no | ModelGate_EMB_POS_MiniLM | — | RED |
| EMB-SEG | no — role absent (defect c) | no | ModelGate_EMB_SEG_MiniLM | — | RED |
| EMB-LMHEAD | yes — LmHead in profiles; CONTINUES_TO plane | yes (projection) | ModelGate_EMB_LMHEAD_TinyLlama | — | GREEN |
| EMB-TIE | yes — tie_word_embeddings scalar | yes (reuse embed) | ModelGate_EMB_TIE_TinyLlama | — | GREEN (witness) |
| NRM-RMS | yes — PerLayerNorms/FinalNorm tensors | yes (NormFold column scale — correct for RMS) | ModelGate_NRM_RMS_TinyLlama (T2 5.1e-07) | — | GREEN |
| NRM-LN | witnessed tensors yes; replay WRONG (defect b) | RED — LayerNorm folded as column scale, no mean/beta | ModelGate_NRM_LN_MiniLM | — | RED |
| NRM-FINAL | yes | yes (per family law) | ModelGate_NRM_FINAL_TinyLlama | — | GREEN for RMS, RED for LN |
| NRM-QK | flag witnessed; tensors/replay no | no | ModelGate_NRM_QK_Qwen3 | — | OPEN |
| ATT-QPROJ / ATT-KPROJ | yes — BilinearPath L/R patterns | yes (SliceHead/ProjectEmbedding) | ModelGate_ATT_QK_MiniLM | GEMM bulk path (MKL) | GREEN |
| ATT-VPROJ / ATT-OPROJ | yes — ProjectionPath (OV_RELATES) | yes | ModelGate_ATT_OV_MiniLM | GEMM bulk path | GREEN |
| ATT-BIAS | tensors present, NEVER APPLIED (defect a) | RED | ModelGate_ATT_BIAS_MiniLM | — | RED |
| ATT-GQA | yes — RightIsKv per profile | yes (kv fan-out) | ModelGate_ATT_GQA_TinyLlama | — | GREEN |
| ATT-SCORE | factors: lane A increment 1 (FACTOR vertices live) | partial — qk kernels exist; SPI scorer (lane B) open | ModelGate_ATT_SCORE_MiniLM / gate_model_pair_score | row_topk = CS prune + exact re-score; no V² tiles | RED→ lane A/B in flight |
| ATT-SOFTMAX | n/a (calculated at replay) | no (lane D) | ModelGate_ATT_SOFTMAX_MiniLM | — | OPEN |
| ATT-MASK-CAUSAL | n/a (structural) | no (lane D) | ModelGate_ATT_MASK_TinyLlama | — | OPEN |
| ATT-MASK-SWA | no (sliding_window unread) | no | ModelGate_ATT_SWA_Qwen2 | — | OPEN |
| ATT-MIX | n/a (calculated at replay) | no (lane D residual composition) | ModelGate_ATT_MIX_MiniLM (T4 yardstick: L5 top-1 ≥90%) | — | OPEN |
| POS-ROPE | theta witnessed; rotation replay = lane H | no | ModelGate_POS_ROPE_TinyLlama / gate_pos_rope | rotation inside scorer budget | OPEN |
| POS-ROPE-SCALE | no (dict unread) | no | gate_pos_rope_scale | — | OPEN |
| FFN-GATE / FFN-UP / FFN-DOWN | yes — ContractionPath (COMPLETES_TO) | yes (contraction natives) | ModelGate_FFN_GUD_TinyLlama | GEMM bulk path | GREEN |
| FFN-ACT-SILU | recipe-implied (HasGate) | yes | ModelGate_FFN_SILU_TinyLlama | — | GREEN |
| FFN-ACT-GELU | needed for BERT/Phi coverage | no (doc 26 §B: real product code owed) | ModelGate_FFN_GELU_MiniLM | — | RED |
| FFN-MOE-ROUTER | no | no | ModelGate_FFN_MOE_Mixtral | — | OPEN |

**Matrix row count: 41** (13 CON, 5 TOK, 5 EMB, 4 NRM, 10 ATT/POS-grouped rows, 4 FFN).
Companion docs may split grouped rows (ATT-QPROJ/ATT-KPROJ etc.) into singletons —
the schema permits it; keep one gate per row after the split.

### How to add a new architecture (checklist)

1. **Profile entry** — add the `ArchitectureProfile` static (tensor name templates
   for embed/norms/Q/K/V/O/gate/up/down, HasGate/HasBiases/RmsNorm, Paths). Wire it
   into `For()`; the unknown-type fallback must FAIL, not default to Llama.
2. **Role classification** — every tensor name in the checkpoint must classify in
   `TensorRoleClassifier`; zero orphan tensors (an unclassified tensor is an
   unwitnessed assertion).
3. **Witnessed scalars** — add the family's config keys/aliases to
   `ModelConfigReader`; confirm the canonical-JSON recipe hash covers everything else;
   tokenizer container variant covered (CON-TOK-\* rows).
4. **Replay ops** — any primitive the family adds (new activation, norm variant,
   rope scaling type, MoE router) gets a native kernel; C#/SQL orchestrate only.
5. **Relation declaration** — every relation the emit path produces declared in
   `InitializeAsync relationNodeNames` (undeclared = native fault); new relation
   types owe a reseed (highway bits renumber alphabetically).
6. **Gates** — add one matrix row per new primitive, name the tests
   (`ModelGate_<ID>_<Model>`, `gate_<id>`), smallest covered model first
   (MiniLM-order law), bare-run wall-clock recorded in the seed-step log, arena
   deposit verified where score-law inversion applies, no Python anywhere.

--------------------------------------------------------------------------------
## References

- Doc 26 (campaign): `.scratchpad/26_Uncracked_List_Campaign.md`
- Profile: `app/Laplace.Decomposers/Model/ArchitectureProfile.cs`;
  parser: `SafetensorsContainerParser.cs`; config: `ModelConfigReader.cs`;
  dtype decode: `WeightTensorETL.cs`; tokenizer: `LlamaTokenizerParser.cs`
- safetensors format: [DeepWiki — safetensors file format](https://deepwiki.com/huggingface/safetensors/2.1-file-format),
  [safetensors header structure](https://zenn.dev/platina/articles/e65c73cb01a900?locale=en),
  [file structure notes](https://malcolm-mill.github.io/LLM/safetensors_file_structure/)
- rope_scaling keys: [HF transformers RoPE utilities](https://huggingface.co/docs/transformers/main/en/internal/rope_utils),
  [Llama-3.1 rope_scaling discussion](https://huggingface.co/meta-llama/Llama-3.1-8B-Instruct/discussions/15),
  [Qwen2.5 YaRN usage](https://huggingface.co/Qwen/Qwen2.5-32B-Instruct/discussions/5)
