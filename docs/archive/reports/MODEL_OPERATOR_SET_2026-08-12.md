# The finite operator set — what a checkpoint actually contains, and what survives ingest

**Date:** 2026-08-12
**Scope:** decoder-only transformer (Llama family and its descendants)
**Relates to:** `docs/plan/MODEL_INGESTION_DESIGN.md` Phase 3, Phase 4, Phase 7

Companion to the ingestion design. That document says *what* to do; this one
enumerates the operator set exhaustively and states, per operator, what the
substrate keeps and what it can hand back on export.

## 1. The complete list

Every learned parameter in a Llama-family decoder. There is nothing else — no
hidden state is stored, RoPE has no parameters, and attention masks are
structural.

**Per layer, ×L:**

| tensor | shape | kind |
|---|---|---|
| `q_proj` | `d_model × (n_heads · d_head)` | matrix |
| `k_proj` | `d_model × (n_kv · d_head)` | matrix (GQA: `n_kv ≤ n_heads`) |
| `v_proj` | `d_model × (n_kv · d_head)` | matrix |
| `o_proj` | `(n_heads · d_head) × d_model` | matrix |
| `gate_proj` | `d_model × d_ffn` | matrix |
| `up_proj` | `d_model × d_ffn` | matrix |
| `down_proj` | `d_ffn × d_model` | matrix |
| `input_layernorm.weight` | `d_model` | **vector** (RMSNorm gain) |
| `post_attention_layernorm.weight` | `d_model` | **vector** |

**Global, ×1:**

| tensor | shape | kind |
|---|---|---|
| `embed_tokens` | `V × d_model` | matrix |
| `model.norm.weight` | `d_model` | vector |
| `lm_head` | `d_model × V` | matrix, frequently **tied** to `embed_tokens` |

Seven matrices and two vectors per layer; two matrices and one vector globally.
Non-embedding parameter count is `L·(4·d_model² + 3·d_model·d_ffn)` for MHA,
falling to `2·d_model²·(1 + n_kv/n_heads)` on the attention half under GQA.
Embedding and head are `V·d_model` each, or one of them if tied.

That is the whole finite list. It is finite per architecture *and* the set of
architectures is small.

## 2. Seven of those tensors are not separately observable

This is the load-bearing fact and it is not a Laplace claim — it is the standard
decomposition from Anthropic's own interpretability work.

> *"Keys, queries and values are not the fundamental objects — they are
> intermediate results in computing these two low-rank matrices."*
> — Elhage et al., *A Mathematical Framework for Transformer Circuits* (2021)

Per head, the function depends on the weights **only** through two products:

```
W_QK = W_Qᵀ W_K      d_model × d_model, rank ≤ d_head    "where to look"
W_OV = W_O  W_V      d_model × d_model, rank ≤ d_head    "what gets written"
```

Four tensors collapse to two objects, and the collapse is exact rather than an
approximation. `W_Q` and `W_K` individually are **gauge**: substituting
`W_Q → R W_Q`, `W_K → R W_K` for any invertible `R` leaves `W_QK` and therefore
the model unchanged. The rotation form is documented and empirically validated —
transform `W_q, W_k, W_v → R₁ᵀ(·)` and `W_o → W_o R₁` and the hidden features
inside multi-head attention are identical.

**Consequence for MODEL_INGESTION_DESIGN Phase 3.** The doc lists rotation as
"harder, and the open design question." It has a published answer: gauge-fixing
by QR orthonormalization, setting `W_Vᵀ W_V = I_dv`, which selects one
representative from the equivalence class. Permutation canonicalization is the
easy half and the doc already says so; the rotation half is not open research, it
is implementation.

**Consequence for Phase 4.** The contraction table lists QK, FFN and lm_head. It
does **not** list OV. That is half of each attention head — the half that decides
what a coupling *writes* rather than where it looks. A lane that contracts QK
only records which tokens attend and never which tokens get copied.

## 3. The MLP does not collapse the same way, and the design doc's row is an approximation

Phase 4 gives FFN as `E·W_upᵀ · W_downᵀ·Eᵀ`. Two things are dropped:

- **`gate_proj`.** SwiGLU is `down(silu(gate(x)) ⊙ up(x))`. The gate is a third
  matrix and it is elementwise-multiplied, not summed.
- **The nonlinearity.** `silu` sits between `up` and `down`, so the composite is
  not bilinear and there is no exact two-sided contraction.

`E·W_upᵀ · W_downᵀ·Eᵀ` is therefore the **linearized** FFN — correct as a
first-order coupling estimate and not an identity. It should be labelled as such,
because everything else in the pipeline is exact and this is the one place a
silent approximation enters that is not `coord`/`hilbert_index`.

Bilinear-layer interpretability treats this directly: a *bilinear* MLP variant
(no elementwise nonlinearity) is exactly analyzable, which is why it gets studied
as an interpretability-friendly substitute rather than as a description of
SwiGLU.

## 4. What Laplace stores per operator

| operator | stored as | bytes retained |
|---|---|---|
| `embed_tokens` | **not stored** — S³ placement, computed closed-form per atom | 0 |
| `lm_head` | not stored; unembed is a contraction against the same placement | 0 |
| `W_QK` (per head) | Glicko consensus over token-pair couplings, `context_id = (plane, layer, head)` | attestation rows only |
| `W_OV` (per head) | **not currently contracted** — gap, §2 | — |
| `W_gate/up/down` | Glicko consensus over linearized token→token couplings | attestation rows only |
| RMSNorm γ vectors | per-channel gains, not couplings between entities | see §6 |
| tensor names, byte ranges, shapes | AST scrape → composition on the trajectory | kilobytes |
| the floats themselves | **discarded** — Phase 7 | **0** |

The embedding row is the one that makes the rest work. In `E·M·Eᵀ`, `E` is the
free variable that every other checkpoint would have to store and align. Here it
is `super_fibonacci_point(i, N)` with `N = laplace.atom_window()` fixed by the
Unicode standard, so `E` is the same matrix for every model, computed rather than
loaded, and cross-model comparison needs no Procrustes alignment step at all.

Measured consequence, from the audit: 25 tokenizers, ~2.7M raw tokens, casefolded
surfaces union to **151,016**, and twelve consecutive Qwen-family models added
**zero**. Growth saturates per domain because the atoms were fixed before the
first model arrived.

## 5. Half the list is surplus, and the architecture cannot say so

The operator set is finite. It is also, measurably, mostly unnecessary — and the
reason it is still shipped is architectural rather than empirical.

**Measured by others, on frontier-scale models:**

- **Wanda**: 50%-sparse LLaMA-65B matches the zero-shot performance of its dense
  counterpart. No retraining, no weight update, no second-order information — the
  sparse subnetwork *already exists inside the pretrained weights*.
- **SparseGPT**: 50–60% unstructured sparsity on OPT-175B and BLOOM-176B in under
  4.5 hours, minimal perplexity increase.
- Both report that pruning gets **more** effective as model size grows.

So roughly half of every checkpoint is carried, stored, transferred and multiplied
without contributing measurable behaviour, and the fraction rises with scale.

**Why it cannot be removed in place.** `exp(x) > 0` for every finite `x`, so
softmax is structurally incapable of emitting an exact zero — the literature
states it plainly: *"softmax output is nonzero for any input."* Three consequences,
and the third is a correctness cost rather than an efficiency one:

1. Every key receives mass, so no key can be skipped. This is where the `O(n²)`
   actually comes from; sparse attention is always an approximation bolted on
   afterward.
2. The distribution sums to 1 over whatever is in the window, so the mechanism
   cannot abstain — only redistribute among bad options.
3. **The signal is diluted.** In long sequences the impactful pairwise
   connections are drowned by nonzero weight spread across everything else.

`sparsemax` — Euclidean projection onto the simplex — exists precisely to give
exact zeros back. That it had to be invented is the admission.

**What this is NOT.** It is not dead neurons, and the audit already measured that:
per token, 2,799 of 5,632 neurons carry 90% of the mass and **0 of 5,632 are
dead**. Nothing in the neuron basis is idle. The surplus lives in the *couplings*,
which is exactly what superposition predicts and why `MODEL_INGESTION_DESIGN.md`
lists "whether the couplings are recoverable in the neuron basis at all" as open.

**Why it matters here.** An absent edge in this substrate is absent — not
epsilon. `converse.attention` ranks on `eff_mu(rating, rd)`, which is exactly zero
for an unattested pair, so the surplus is never recorded rather than recorded and
ignored. Phase 5b makes that concrete on the intake side: a coupling with no
corroboration is not admitted at all. The 50% that pruning removes after training
is the 50% this design declines to write in the first place — and it declines by
adjudication (does anything corroborate it?) rather than by magnitude threshold,
which is the part pruning cannot do because a threshold has no notion of evidence.

## 5b. The forward pass ends in a database query, and it cannot return empty

Worth stating precisely, because it is an established equivalence rather than an
analogy:

> *"The softmax layer is equivalent to the classical Maximum Inner Product Search
> (MIPS) problem — given a query, finding k vectors in a database that have the
> largest inner product values with the query. In neural language model
> prediction, **context vectors are equivalent to queries, and weight vectors are
> equivalent to the database**."*

`logits = h · W_Uᵀ` is `V` inner products; softmax sorts and normalizes them. The
final act of generation is a top-k similarity search over a collection, and the
efficiency literature already substitutes real ANN indexes for it — screening,
clustering, inverted multi-index, anisotropic quantization — precisely because it
is one.

So "the capital of California is ___" places a query vector in a region and
returns the ranked members near it. That is a `SELECT … ORDER BY score LIMIT k`.
The differences from an actual database query are that it is unindexed (every one
of `V` scored), approximate by construction, and — per §5 — **structurally unable
to return the empty set**. When the query points somewhere with nothing in it, a
ranked list comes back anyway. That is not a failure mode bolted onto the
mechanism; it is the mechanism working as specified.

**And there is no claim object anywhere in it.** Train the model that the capital
of California is any arbitrary string, and that string *is* the answer — not a
false claim the model holds, the answer itself. Nothing records that a source
asserted it, nothing records how many sources disagreed, and nothing can lose an
argument, because there is no argument to lose. Weights carry frequency, and
frequency has no sign.

`MODEL_INGESTION_DESIGN.md` already names the same defect from the other
direction: the web mentions flat earth constantly and overwhelmingly to debunk it,
and co-occurrence reads every one of those as support. Corpus poisoning and the
debunking problem are the same hole — a magnitude with no claimant.

The substrate's difference is not that its retrieval is better. It is that the
retrieval returns *rows*: `subject, type, object, source, context, outcome,
observation_count, rating, rd`. The same poisoned assertion enters as one
attestation from one source at `AIModelProbe` trust, adjudicated against curated
witnesses, with `outcome` able to record a loss. It can be refuted because it
exists as an object separate from the geometry that found it — which is the
entire content of "geometry proposes candidates; typed evidence governs
selection."

## 6. Export: Glicko → weights

The design doc does not cover the inverse. It is more tractable than it looks,
and the reason is §2.

Given the consensus edges for one head and the placement matrix `E` — which is
computed, not recovered — the couplings are `C ≈ E M Eᵀ`. Recovering `M` is a
linear least-squares problem in `d_model²` unknowns against however many attested
pairs exist for that `context_id`. Then:

- **`M` is what you need.** `W_QK` and `W_OV` *are* `M`. There is nothing further
  to recover.
- **Factoring `M` into `W_Q`, `W_K` is not necessary and not well posed.** Any
  rank-`d_head` factorization `M = AᵀB` reproduces the model exactly, and the
  choice among them is precisely the gauge freedom of §2. Take the SVD, split the
  singular values, and the result is as correct as the original weights — and by
  gauge-fixing convention, more canonical.

So the honest statement of export is: **you cannot recover the original `W_Q`,
and you do not want to.** The original was one arbitrary representative of an
equivalence class. What is recoverable is the class itself, which is the part
that was ever functional.

What genuinely limits fidelity is not the factorization but the **admission
policy** — Phase 5b admits novel couplings only on corroboration, so couplings
that were present in the checkpoint and never corroborated were deliberately not
recorded. Export reconstructs the *adjudicated* model, not the original. That is
a design property, not a loss of information the system failed to capture.

## 6. Genuinely open

- **RMSNorm gains.** A `d_model` vector of per-channel scalars is not a coupling
  between two entities, so it has no natural attestation form. Options: fold into
  the adjacent matrix (mathematically clean — `RMSNorm(x)·γ` then `W` equals
  `RMSNorm(x)` then `diag(γ)W`), or carry as a small typed payload. Folding is
  preferred and is not yet decided anywhere.
- **OV contraction shape.** QK is `(E·W_q)(E·W_k)ᵀ` and directly a token-pair
  form. OV maps residual→residual; expressing it as a token-pair coupling needs
  the unembed, i.e. `E W_OV Eᵀ`, and whether that is the right object or whether
  OV belongs at a different tier is undecided.
- **The linearized FFN's error.** Nobody has measured how far
  `E·W_upᵀ·W_downᵀ·Eᵀ` sits from the true SwiGLU coupling on a real checkpoint.
  That is a measurable quantity and it bounds what the FFN half of the ingest can
  claim.
- **Least-squares conditioning on export.** `d_model²` unknowns against the
  attested pair count for one head — whether that system is well conditioned at
  realistic edge densities is unmeasured.

## 7. Not established here

Every architectural claim above is standard and cited. The Laplace-side mapping
in §4 and §5 is derived from `MODEL_INGESTION_DESIGN.md` plus §2, and the export
path in §5 **has not been implemented or tested** — it is an argument that the
inverse is well posed, not a demonstration that it runs.

## Sources

- [A Mathematical Framework for Transformer Circuits — Elhage et al., Anthropic (2021)](https://transformer-circuits.pub/2021/framework/index.html)
- [A technical note on bilinear layers for interpretability (arXiv 2305.03452)](https://arxiv.org/pdf/2305.03452)
- [Empirical validation of gauge symmetry for Transformers, GPT-2 124M–1.5B, NeurReps 2025](https://github.com/kellywang2030/transformer-gauge-symmetry)
- [DFRot — rotation invariance of W_q/W_k/W_v/W_o (arXiv 2412.00648)](https://arxiv.org/pdf/2412.00648)
- [ProcrustesGPT — orthogonal transformations of LLM weights (arXiv 2506.02818)](https://arxiv.org/pdf/2506.02818)
- [Parameter Count — AI Engineering Academy](https://aiengineering.academy/LLM/LLMArchitecture/ParameterCount/)
- [Transformer Math — Counting Model Parameters](https://michaelwornow.net/2024/01/18/counting-params-in-transformer)
- [Wanda — A Simple and Effective Pruning Approach for LLMs, ICLR 2024 (arXiv 2306.11695)](https://arxiv.org/pdf/2306.11695)
- [SparseGPT / one-shot LLM pruning at 50–60% without retraining](https://www.spheron.network/blog/llm-pruning-sparsegpt-wanda-gpu-cloud/)
- [Softpick — rectified softmax, on softmax's inability to emit zero (arXiv 2504.20966)](https://www.alphaxiv.org/overview/2504.20966v1)
- [Sparsemax-based channel selection — "softmax output is nonzero for any input" (arXiv 2103.15305)](https://arxiv.org/pdf/2103.15305)
- [Learning to Screen for Fast Softmax Inference — softmax layer as MIPS (arXiv 1810.12406)](https://arxiv.org/pdf/1810.12406)
- [Adaptive Sampled Softmax with Inverted Multi-Index (arXiv 2501.08563)](https://arxiv.org/html/2501.08563)
- [Accelerating Large-Scale Inference with Anisotropic Vector Quantization (arXiv 1908.10396)](https://arxiv.org/pdf/1908.10396)
